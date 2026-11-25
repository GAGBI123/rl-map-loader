#!/usr/bin/env bash
# Runs the real Linux app in an X11 desktop session and checks what players do with it:
#   tray    - an update (from a local feed) installs itself while the app is in the tray, and the app restarts into it
#   button  - the same through the "Restart to update" button with the window open; the new window shows up
#   logout  - logging out of the session isn't cancelled by the app, and the app quits
#   sigterm - the app quits on SIGTERM
# The session is Xvfb + xfce4-session, an XSMP session manager: KDE and GNOME ask X11 apps to quit the same way.
# Used by .github/workflows/linux-session-test.yml (needs: xvfb xfce4-session xfwm4 dbus-x11 xdotool imagemagick python3-pil).
#
#   session-test.sh <app binary to test> <newer binary for the update feed> <its version> <results folder>
set -u
OLD=$(readlink -f "$1"); NEW=$(readlink -f "$2"); NEWVER=$3; OUT=$4
mkdir -p "$OUT"; OUT=$(readlink -f "$OUT")
HERE=$(dirname "$(readlink -f "$0")")
RESULTS=()
FAILED=0
say() { echo "[$(date +%T)] $*"; }
pass() { RESULTS+=("PASS  $1"); say "PASS $1"; }
fail() { RESULTS+=("FAIL  $1"); say "FAIL $1"; FAILED=$((FAILED + 1)); }

# ------------------------------------------------------------ local update feed
FEED=$(mktemp -d)
cp "$NEW" "$FEED/rl-map-loader-linux-x64"
SIZE=$(stat -c %s "$FEED/rl-map-loader-linux-x64")
SHA=$(sha256sum "$FEED/rl-map-loader-linux-x64" | cut -d' ' -f1)
cat > "$FEED/latest.json" <<EOF
{ "version": "$NEWVER", "tag": "v$NEWVER", "notes": "test update",
  "assets": { "linux-x64": { "file": "rl-map-loader-linux-x64", "size": $SIZE, "sha256": "$SHA" } } }
EOF
(cd "$FEED" && exec python3 -m http.server 8765 --bind 127.0.0.1 >/dev/null 2>&1) &
FEED_PID=$!
export RLMAPLOADER_UPDATE_FEED=http://127.0.0.1:8765/latest.json

Xvfb :99 -screen 0 1600x1000x24 -nolisten tcp >/dev/null 2>&1 &
XVFB_PID=$!
export DISPLAY=:99
sleep 2

# ------------------------------------------------------------ helpers
new_home() { # <check for updates true|false>
    export HOME; HOME=$(mktemp -d)
    export XDG_RUNTIME_DIR=$HOME/run XDG_CONFIG_HOME=$HOME/.config XDG_DATA_HOME=$HOME/.local/share XDG_CACHE_HOME=$HOME/.cache
    mkdir -p "$XDG_RUNTIME_DIR" "$HOME/game/TAGame/CookedPCConsole" "$HOME/maps" "$HOME/.config/RLMapLoader" "$HOME/.local/bin"
    chmod 700 "$XDG_RUNTIME_DIR"
    # looks like the game's own Underpass (Unreal package with a licensee version)
    { printf '\xC1\x83\x2A\x9E\x64\x03\x20\x00'; head -c 4088 /dev/zero; } > "$HOME/game/TAGame/CookedPCConsole/Labs_Underpass_P.upk"
    cat > "$HOME/.config/RLMapLoader/settings.json" <<EOF
{ "CookedPath": "$HOME/game/TAGame/CookedPCConsole", "MapsPath": "$HOME/maps", "CloseToTray": true, "TrayHintShown": true,
  "RestoreOnExit": true, "CheckForUpdates": $1, "AutoUpdate": true }
EOF
    install -m 755 "$OLD" "$HOME/.local/bin/rl-map-loader"   # where the app installs itself
    APPLOG=$HOME/.local/share/RLMapLoader/log.txt
    # keep the session small: no autostart programs (they keep it "starting", which refuses log out)
    mkdir -p "$HOME/.config/autostart"
    for f in /etc/xdg/autostart/*.desktop; do printf '[Desktop Entry]\nHidden=true\n' > "$HOME/.config/autostart/$(basename "$f")"; done
}

start_session() {
    eval "$(dbus-launch --sh-syntax)"
    xfce4-session > "$OUT/$SCEN-xfce4-session.txt" 2>&1 &
    SESSION_PID=$!
    SM=""
    for _ in $(seq 60); do
        for p in $(pgrep -P "$SESSION_PID"); do
            SM=$(tr '\0' '\n' < "/proc/$p/environ" 2>/dev/null | grep '^SESSION_MANAGER=' | head -1)
            [ -n "$SM" ] && break
        done
        [ -n "$SM" ] && break
        sleep 0.5
    done
    if [ -z "$SM" ]; then say "no SESSION_MANAGER from xfce4-session"; return 1; fi
    export "${SM?}"
    say "session up ($SM)"
    sleep 2
}

stop_session() {
    cp "$APPLOG" "$OUT/$SCEN-app-log.txt" 2>/dev/null
    pkill -f "$HOME/.local/bin/rl-map-loader" 2>/dev/null
    kill "$SESSION_PID" 2>/dev/null
    pkill -x xfwm4 2>/dev/null; pkill -x xfsettingsd 2>/dev/null; pkill -x xfconfd 2>/dev/null
    kill "$DBUS_SESSION_BUS_PID" 2>/dev/null
    sleep 2
    pkill -9 -f "$HOME/.local/bin/rl-map-loader" 2>/dev/null
    unset SESSION_MANAGER
}

start_app() {
    "$HOME/.local/bin/rl-map-loader" "$@" >> "$OUT/$SCEN-app-output.txt" 2>&1 &
    APP_PID=$!
    say "started app pid $APP_PID ($*)"
}

wait_log() { # <pattern> <seconds>
    for _ in $(seq $(($2 * 2))); do
        grep -q -- "$1" "$APPLOG" 2>/dev/null && return 0
        sleep 0.5
    done
    return 1
}

alive() { [ -d "/proc/$1" ] && ! grep -q '^State:.*Z' "/proc/$1/status" 2>/dev/null; }
app_pids() { pgrep -f "$HOME/.local/bin/rl-map-loader" | tr '\n' ' '; }
main_window() { xdotool search --onlyvisible --name '^RL Map Loader$' 2>/dev/null | head -1; }
shot() { import -window root "$OUT/$SCEN-$1.png" 2>/dev/null; }

# ------------------------------------------------------------ tray: automatic update while in the tray
SCEN=tray
say "=== $SCEN"
new_home true
if start_session; then
    start_app --tray
    if ! wait_log "Downloaded update $NEWVER" 90; then
        fail "$SCEN: update wasn't downloaded"
    elif wait_log "Updated to version $NEWVER" 120; then
        sleep 2
        if alive "$APP_PID"; then fail "$SCEN: old process still running"; else pass "$SCEN: restarted into $NEWVER (old process gone, running: $(app_pids))"; fi
    else
        fail "$SCEN: no restart into $NEWVER (running: $(app_pids))"
    fi
    shot after
    stop_session
fi

# ------------------------------------------------------------ button: "Restart to update" with the window open
SCEN=button
say "=== $SCEN"
new_home true
if start_session; then
    start_app
    if ! wait_log "Downloaded update $NEWVER" 90; then
        fail "$SCEN: update wasn't downloaded"
    else
        sleep 3
        shot before
        WIN=$(main_window)
        if [ -z "$WIN" ]; then
            fail "$SCEN: no app window"
        else
            eval "$(xdotool getwindowgeometry --shell "$WIN")"   # X Y WIDTH HEIGHT
            if POS=$(python3 "$HERE/find_button.py" "$OUT/$SCEN-before.png" "$X" "$Y" "$WIDTH"); then
                say "clicking the update button at $POS"
                # shellcheck disable=SC2086
                xdotool mousemove $POS sleep 0.3 click 1
                if wait_log "Updated to version $NEWVER" 60; then
                    sleep 4
                    shot after
                    if alive "$APP_PID"; then fail "$SCEN: old process still running"
                    elif [ -z "$(main_window)" ]; then fail "$SCEN: restarted, but no window is showing"
                    else pass "$SCEN: restarted into $NEWVER and the window is back (running: $(app_pids))"; fi
                else
                    sleep 2; shot after
                    fail "$SCEN: no restart into $NEWVER after the click (running: $(app_pids))"
                fi
            else
                fail "$SCEN: couldn't find the update button"
            fi
        fi
    fi
    stop_session
fi

# ------------------------------------------------------------ logout: the session manager asks every app to quit
SCEN=logout
say "=== $SCEN"
new_home false
if start_session; then
    start_app
    for _ in $(seq 40); do [ -n "$(main_window)" ] && break; sleep 0.5; done
    sleep 3
    shot before
    # log out (no dialog); the session manager refuses while it's still starting up, so ask again until it takes it
    for _ in $(seq 30); do
        if gdbus call --session --dest org.xfce.SessionManager --object-path /org/xfce/SessionManager \
               --method org.xfce.Session.Manager.Logout false true >> "$OUT/$SCEN-logout-command.txt" 2>&1; then
            say "log out requested"; break
        fi
        sleep 2
    done
    for _ in $(seq 60); do alive "$SESSION_PID" || break; sleep 0.5; done
    sleep 1
    shot after
    if alive "$SESSION_PID"; then
        fail "$SCEN: logout was cancelled (session still running, app running: $(alive "$APP_PID" && echo yes || echo no))"
    elif alive "$APP_PID"; then
        fail "$SCEN: logged out, but the app is still running"
    else
        pass "$SCEN: logged out and the app quit"
    fi
    stop_session
fi

# ------------------------------------------------------------ sigterm: e.g. the session being torn down
SCEN=sigterm
say "=== $SCEN"
new_home false
if start_session; then
    start_app --tray
    sleep 6
    kill -TERM "$APP_PID"
    for _ in $(seq 20); do alive "$APP_PID" || break; sleep 0.5; done
    if alive "$APP_PID"; then fail "$SCEN: still running 10 s after SIGTERM"; else pass "$SCEN: quit on SIGTERM"; fi
    stop_session
fi

kill "$FEED_PID" "$XVFB_PID" 2>/dev/null
echo
printf '%s\n' "${RESULTS[@]}" | tee "$OUT/summary.txt"
exit "$FAILED"
