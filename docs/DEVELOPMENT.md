# Development

## Project layout

- `RLMapLoader.Core` – platform-neutral logic: settings, map library, Underpass swapping, install detection, updater
- `RLMapLoader` – Windows app (WPF)
- `RLMapLoader.Linux` – Linux app ([Avalonia](https://avaloniaui.net/))
- `tests/SwapTest` – swap-logic test harness (Windows and Linux)
- `tests/UpdateTest` – self-update test harness
- `linux/` – install scripts, desktop entry, PKGBUILD
- `tools/airdribble-patch` – the Air Dribble Challenge map fix ([README](../tools/airdribble-patch/README.md))

Icons in the Linux app are from Material Design Icons (Apache 2.0).

## How it works

- **Maps**: `.upk` / `.udk` in the maps folder and its sub-folders. Pictures come from `preview.jpg` in the map's
  folder or an image with the map's name; `info.json` (or `MapName.json`) gives `author` and `desc`.
- **Swapping**: the original `Labs_Underpass_P.upk` is kept as `Labs_Underpass_P.upk.bak` (the convention other
  loaders use) plus a private safety copy. Originals are recognised by their package header (Psyonix packages have a
  non-zero licensee version, UDK-built maps have 0). The map is copied to a temp file first and swapped in at the end.
- **Windows overlay**: always-on-top window over Borderless/Windowed. In exclusive Fullscreen the game is minimized
  while the overlay is open.
- **Linux overlay**: F3 is grabbed through X11 (works on X11, and on Wayland while an X11/XWayland window such as the
  game has focus). The overlay is an override-redirect window that never takes focus; the keyboard is grabbed while
  it's open, so the game isn't tabbed out. Bind `rl-map-loader --toggle-overlay` to a shortcut to open it from
  anywhere. Rocket League is found under Heroic/Legendary, Steam/Proton (native or Flatpak), Lutris and Bottles.
- **Linux install**: the first start copies the binary to `~/.local/bin/rl-map-loader` and writes an app-menu entry
  (`--install` does just that, `--no-install` runs in place).

## Build

```powershell
dotnet build RLMapLoader.sln -c Release
```

Linux from source (`sudo pacman -S dotnet-sdk-9.0`): `cd linux && ./build.sh && ./install.sh`, or `makepkg -si`.

Swap-logic tests (against a sandbox folder, never the real game folder):

```powershell
dotnet run --project tests\SwapTest -- <workDir> <original Labs_Underpass_P.upk> <mapA.upk> <mapB.upk> <mapsFolder>
```

## Releases and updates

Installed apps read `latest.json` from the newest release of this repo. It lists each download (`windows`,
`windows-net9`, `linux-x64`) with its size and SHA-256; the app checks both before replacing itself, then restarts.
Automatic updates install while the app is in the tray and Rocket League is closed, or when the app quits.

To ship an update, bump `<Version>` in `Directory.Build.props`, then:

```powershell
.\release.ps1 -Notes "What changed" -Publish
```

It builds all downloads into `release\<version>`, writes `latest.json` and creates the GitHub release with `gh`.
Downloads: `RLMapLoader-Windows.exe` (self-contained), `RLMapLoader-Windows-NET9.exe` (needs the .NET 9 Desktop
Runtime), `RLMapLoader-Linux.tar.gz` (first install) and `rl-map-loader-linux-x64` (used by the updater).
