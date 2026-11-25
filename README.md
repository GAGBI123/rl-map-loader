# RL Map Loader

A Windows app for playing Rocket League workshop maps: pick a map from a gallery (or from an in-game
overlay) and it's swapped into **Underpass** — then just start Free Play on Underpass.

![icon](RLMapLoader/Assets/logo.png)

## Features

- **Gallery** of your custom maps (`.upk` / `.udk`, sub-folders included) with search, sort and favorites.
- **Map pictures**: uses the image that came with the map — `preview.jpg` in the map's folder, or an image with
  the same name as the map. Maps without a picture show their name instead. Right-click → *Choose image…* (or
  drop an image on a card) to set your own.
- **Map details** from the common download format (`MapFolder/map.udk + preview.jpg + info.json`): the folder
  name is used as the title, plus `author` and `desc`. A `MapName.json` next to a map works too.
- **In-game overlay** (default **F3**, changeable): a small picker on top of the game. Type to search,
  ↑/↓ to pick, Enter to load, Esc to close. On Windows it shows over Rocket League in *Borderless* or *Windowed*
  display mode. Windows can't draw anything over exclusive *Fullscreen*, so in that mode the overlay minimizes the game
  (like Alt+Tab) and brings it back when you close the overlay.
- **Safe swapping**
  - The original `Labs_Underpass_P.upk` is kept as `Labs_Underpass_P.upk.bak` (same convention as other
    loaders, so they interoperate) plus a private safety copy in `%LOCALAPPDATA%\RLMapLoader\Backup`.
  - Originals are recognised by their package header (Psyonix packages have a non-zero licensee version,
    UDK-built maps have 0), so game updates and other loaders can't confuse it.
  - The map is copied to a temp file first and swapped in at the end; nothing changes if Rocket League has the
    file open or no backup of the original exists.
  - *Restore original* any time; optionally restored automatically when you exit.
- Drag & drop `.upk` / `.udk` / `.zip` files to add maps, tray icon, start with Windows.
- **Updates itself** from GitHub Releases (Settings → Updates). New versions download in the background and install
  with a quick restart while the app sits in the tray and Rocket League is closed (or when you quit). Turn off
  *Install updates automatically* to get an **Update** button instead.

## Download

Get the newest version from the [releases page](https://github.com/GAGBI123/rl-map-loader-releases/releases/latest):

- **Windows:** `RLMapLoader-Windows.exe` – just run it (`RLMapLoader-Windows-NET9.exe` is the small build for PCs with
  the .NET 9 Desktop Runtime).
- **Linux:** `RLMapLoader-Linux.tar.gz` – extract it and open `rl-map-loader` once. It copies itself to
  `~/.local/bin`, adds *RL Map Loader* to your app menu and starts; from then on open it from the menu.

## Use

1. Run `RLMapLoader-Windows.exe` (or the NET9 build, which needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)).
2. Settings (gear icon): set
   - **Rocket League maps folder** — `rocketleague\TAGame\CookedPCConsole` (auto-detected for Epic and Steam)
   - **Custom maps folder** — where you keep downloaded maps
3. Click a map → **Load map** (or press F3 in game).
4. In Rocket League: **Training → Free Play → Underpass**.
5. **Restore original** before playing online.

## Linux (CachyOS / Arch and other distros)

`RLMapLoader.Linux` is the same app built with [Avalonia](https://avaloniaui.net/). It finds Rocket League
installed through **Heroic** or **Legendary** (Epic), **Steam/Proton** (native or Flatpak), and **Lutris**/**Bottles**
Wine prefixes, and handles Linux's case-sensitive file names.

Install the prebuilt app (self-contained, no .NET needed): extract `RLMapLoader-Linux.tar.gz` and open
`rl-map-loader` (double-click, or `./rl-map-loader`). The first start installs it to `~/.local/bin` with an app-menu
entry and icon, then runs that copy; `./install.sh` does the same from a terminal. Updates replace
`~/.local/bin/rl-map-loader` in place. (`rl-map-loader --no-install` runs it from where it is.)

Or build from source (`sudo pacman -S dotnet-sdk-9.0`):

```bash
cd linux && ./build.sh && ./install.sh  # or: makepkg -si   (uses linux/PKGBUILD)
```

Overlay hotkey on Linux:

- **F3** (configurable) is grabbed through X11. It works on X11 sessions, and on Wayland while an X11/XWayland
  window has focus — which is the case for Rocket League under Proton/Wine.
- Over the game the overlay opens **without taking focus**, so the game isn't tabbed out. The overlay is an
  override-redirect window (like a tooltip: the window manager never focuses it, and it's drawn above fullscreen
  windows), and while it's open the keyboard is grabbed so typing, ↑/↓, Enter and Esc go to it. Wine ignores focus
  changes caused by keyboard grabs, so Rocket League stays active. The grab ends when the overlay closes, and after
  2 idle minutes at the latest.
- To open the overlay from anywhere (e.g. KDE Plasma on Wayland), bind `rl-map-loader --toggle-overlay` to a key in
  *System Settings → Shortcuts*. It also opens without taking focus when an X11/XWayland window (the game) is active.
  `rl-map-loader --restore` puts the original Underpass back.

## Project layout

- `RLMapLoader.Core` — platform-neutral logic: settings, map library, Underpass swapping, install detection
- `RLMapLoader` — Windows app (WPF)
- `RLMapLoader.Linux` — Linux app (Avalonia)
- `tests/SwapTest` — swap-logic test harness (Windows and Linux)
- `linux/` — install scripts, desktop entry, PKGBUILD
- `tools/airdribble-patch` — the Air Dribble Challenge map fix

Icons in the Linux app are from Material Design Icons (Apache 2.0).

## Releases and updates

Installed apps read `latest.json` from the newest release of the **public** repo
[`GAGBI123/rl-map-loader-releases`](https://github.com/GAGBI123/rl-map-loader-releases), which holds only the downloads –
this source repo stays private, and nobody needs a GitHub account to update. `latest.json` lists each download
(`windows`, `windows-net9`, `linux-x64`) with its size and SHA-256; the app verifies both before swapping itself.

To ship an update: bump `<Version>` in `Directory.Build.props`, then

```powershell
.\release.ps1 -Notes "What changed" -Publish
```

It builds all downloads into `release\<version>`, writes `latest.json` and creates the GitHub release with `gh`.
`tests/UpdateTest` exercises the update mechanics (check, download, checksum, swap, restart) against a local feed.

## Build

```powershell
dotnet build RLMapLoader\RLMapLoader.csproj -c Release
dotnet publish RLMapLoader\RLMapLoader.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o RLMapLoader-App
```

Swap-logic tests (run against a sandbox folder, never the real game folder):

```powershell
dotnet run --project tests\SwapTest -- <workDir> <original Labs_Underpass_P.upk> <mapA.upk> <mapB.upk> <mapsFolder>
```

## tools/airdribble-patch

Python scripts used to fix the *Air Dribble Challenge* workshop map in place (every object keeps its exact size
and offset). `upk.py` reads uncompressed UDK (v868) packages, `kgraph.py` prints the Kismet graph,
`ptree.py` is a lossless tagged-property editor, and `patch_airdribble.py` applies the fix:

- sets `MaxTriggerCount = 0` on the *Car Spawned* event and the *Reset* remote events. Kismet events default to
  firing once, so the map's own reset logic stopped after the first reset.
- when the car spawns, also runs the map's existing Teleport action (players → the map's start), in the same
  moment. Freeplay resets were spawning the car somewhere else every other time; now every reset lands at the start
  with no visible second reset.
- links the final-time / best-time texts to the player like the live timer

```powershell
python patch_airdribble.py AirDribble_Challenge.upk AirDribble_Challenge_Fixed.upk
python kgraph.py AirDribble_Challenge_Fixed.upk
```
