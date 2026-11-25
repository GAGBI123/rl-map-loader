# RL Map Loader

<img src="RLMapLoader/Assets/logo.png" width="72" align="right" alt="">

Play Rocket League workshop maps. Pick a map, the app loads it into **Underpass**, and you start Free Play on
Underpass. Press **F3** in game to open a quick map picker. Works on Windows and Linux.

## Install

Download from the **[latest release](https://github.com/GAGBI123/rl-map-loader/releases/latest)**.

**Windows**

1. Download `RLMapLoader-Windows.exe`.
2. Run it. If Windows says *"Windows protected your PC"*, click **More info → Run anyway**.

**Linux**

1. Download `RLMapLoader-Linux.tar.gz` and extract it.
2. Open `rl-map-loader` (double-click it, or run `./rl-map-loader`).
3. It adds **RL Map Loader** to your app menu. Open it from there from now on.

## How to use

1. On first start, set your **Rocket League maps folder** (`rocketleague/TAGame/CookedPCConsole`, usually found
   automatically) and the folder with your **downloaded maps** (`.upk`, `.udk`, or `.zip` files, which are
   unpacked automatically).
2. Click a map → **Load map**, or press **F3** in game.
3. In Rocket League: **Training → Free Play → Underpass**.
4. Click **Restore original Underpass** before you play online.

> **Windows overlay:** set Rocket League to **Borderless** (Settings → Video → Display Mode). Windows can't show
> anything on top of Fullscreen.

## Updates

The app updates itself. You can turn automatic updates off, or check for updates manually, in
**Settings → Updates**.

## Privacy

RL Map Loader doesn't collect or send any personal data. It only goes online to check this repository for new
versions and download them ([GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)
applies to those requests). You can turn that off in **Settings → Updates**.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org)
(applied for – Windows downloads are unsigned until the project is accepted).

- Committers and reviewers: [GAGBI123](https://github.com/GAGBI123)
- Approvers: [GAGBI123](https://github.com/GAGBI123)

Releases are built from this repository's source by [GitHub Actions](.github/workflows/release.yml), and every signing
request is approved by hand. Changes from other people (pull requests) are reviewed before they're merged.

---

[MIT License](LICENSE) · Building from source and how releases work: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)
