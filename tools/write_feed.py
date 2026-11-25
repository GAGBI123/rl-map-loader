"""Writes latest.json (the update feed installed apps read) and release-notes.md for a release.

    python write_feed.py <folder with the downloads> <version> [notes file]

latest.json lists each download by key (the app picks its own: self-contained Windows, .NET 9 Windows, Linux) with
its size and SHA-256, computed from the final (signed) files.
"""
import datetime
import hashlib
import json
import os
import sys

REPO = "https://github.com/GAGBI123/rl-map-loader"
FILES = {
    "windows": "RLMapLoader-Windows.exe",
    "windows-net9": "RLMapLoader-Windows-NET9.exe",
    "linux-x64": "rl-map-loader-linux-x64",
}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def main():
    out, version = sys.argv[1], sys.argv[2].lstrip("vV")
    notes = ""
    if len(sys.argv) > 3 and os.path.exists(sys.argv[3]):
        with open(sys.argv[3], encoding="utf-8-sig") as f:
            notes = f.read().strip()

    assets = {}
    for key, name in FILES.items():
        path = os.path.join(out, name)
        assets[key] = {"file": name, "size": os.path.getsize(path), "sha256": sha256(path)}
    feed = {
        "version": version,
        "tag": "v" + version,
        "date": datetime.date.today().isoformat(),
        "notes": notes,
        "assets": assets,
    }
    with open(os.path.join(out, "latest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(feed, f, indent=2, ensure_ascii=False)

    body = f"""{notes}

### Download
- **Windows:** `RLMapLoader-Windows.exe` – just run it. (`RLMapLoader-Windows-NET9.exe` is the small version for PCs that have the .NET 9 Desktop Runtime.)
- **Linux:** `RLMapLoader-Linux.tar.gz` – extract it and open `rl-map-loader` once; it adds RL Map Loader to your app menu.

Already installed? The app updates itself (Settings → Updates).

[Code signing policy]({REPO}#code-signing-policy) · [Privacy]({REPO}#privacy)
"""
    with open(os.path.join(out, "release-notes.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(body.strip() + "\n")
    print(json.dumps(feed, indent=2))


if __name__ == "__main__":
    main()
