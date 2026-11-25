# Development

## Project layout

- `RLMapLoader.Core` – platform-neutral logic: settings, map library, Underpass swapping, install detection, updater
- `RLMapLoader` – Windows app (WPF)
- `RLMapLoader.Linux` – Linux app ([Avalonia](https://avaloniaui.net/))
- `tests/SwapTest` – swap-logic test harness (Windows and Linux)
- `tests/UpdateTest` – self-update test harness
- `tests/linux-session` – runs the Linux app in an X11 desktop session (update restart from the tray and from the
  button, log out, SIGTERM); run it with *Linux session test* in the Actions tab
- `linux/` – install scripts, desktop entry, PKGBUILD
- `tools/airdribble-patch` – the Air Dribble Challenge map fix ([README](../tools/airdribble-patch/README.md))

Icons in the Linux app are from Material Design Icons (Apache 2.0).

## How it works

- **Maps**: `.upk` / `.udk` in the maps folder and its sub-folders. Pictures come from `preview.jpg` in the map's
  folder or an image with the map's name; `info.json` (or `MapName.json`) gives `author` and `desc`.
  Zips in the maps folder that contain maps are unpacked next to themselves (pictures and json included, entries
  can't escape the folder) unless their maps are already present; the zip is kept.
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

Releases are built by GitHub Actions ([release.yml](../.github/workflows/release.yml)) from the code in this repo.
To ship an update, bump `<Version>` in `Directory.Build.props`, commit, push, then tag it:

```powershell
git tag -a v1.2.0 -m "What changed"
git push origin v1.2.0
```

The workflow builds the downloads, signs the Windows ones (once SignPath is set up), writes `latest.json`
([tools/write_feed.py](../tools/write_feed.py)) and publishes the release; the tag's message becomes the release notes.
*Run workflow* in the Actions tab with *publish* off makes a test build whose files are kept as a workflow artifact.
`release.ps1` does the same locally, unsigned, as a fallback.

Downloads: `RLMapLoader-Windows.exe` (self-contained), `RLMapLoader-Windows-NET9.exe` (needs the .NET 9 Desktop
Runtime), `RLMapLoader-Linux.tar.gz` (first install) and `rl-map-loader-linux-x64` (used by the updater).

## Code signing (SignPath)

After the SignPath Foundation accepts the project:

1. In SignPath, create the project (slug e.g. `rl-map-loader`) with a GitHub trusted build system, a release signing
   policy that needs manual approval, and this artifact configuration (the GitHub artifact is a zip of the two exes):

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <zip-file>
       <pe-file path="RLMapLoader-Windows.exe" product-name="RL Map Loader" product-version="1.*">
         <authenticode-sign/>
       </pe-file>
       <pe-file path="RLMapLoader-Windows-NET9.exe" product-name="RL Map Loader" product-version="1.*">
         <authenticode-sign/>
       </pe-file>
     </zip-file>
   </artifact-configuration>
   ```

2. In this repo (Settings → Secrets and variables → Actions) add the secret `SIGNPATH_API_TOKEN` (a SignPath CI user's
   token) and the variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG` and `SIGNPATH_SIGNING_POLICY_SLUG`.

From then on every release waits for the signing request to be approved in SignPath (up to an hour), then publishes
the signed files.
