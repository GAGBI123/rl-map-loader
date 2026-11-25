"""Packs the Linux download: RLMapLoader/rl-map-loader (+x), install.sh, uninstall.sh, README.txt.

Built on Windows, so file modes are set here (tar keeps the executable bit, a browser download of the bare binary
doesn't).    python make_linux_tar.py <rl-map-loader binary> <linux dir> <out.tar.gz>
"""
import io
import sys
import tarfile
import time

README = """RL Map Loader for Linux

1. Open "rl-map-loader" in this folder (double-click it, or run ./rl-map-loader in a terminal).
   The first time, it copies itself to ~/.local/bin and adds "RL Map Loader" to your app menu.
2. From then on, open RL Map Loader from your app menu. You can delete this folder.

It keeps itself up to date (Settings -> Updates).
Uninstall: ./uninstall.sh
"""


def add_bytes(t, name, data, mode):
    ti = tarfile.TarInfo(name)
    ti.size = len(data)
    ti.mode = mode
    ti.mtime = int(time.time())
    t.addfile(ti, io.BytesIO(data))


def main():
    binary, linux_dir, out = sys.argv[1:4]
    with tarfile.open(out, "w:gz") as t:
        d = tarfile.TarInfo("RLMapLoader")
        d.type = tarfile.DIRTYPE
        d.mode = 0o755
        d.mtime = int(time.time())
        t.addfile(d)
        with open(binary, "rb") as f:
            add_bytes(t, "RLMapLoader/rl-map-loader", f.read(), 0o755)
        for script in ("install.sh", "uninstall.sh"):
            with open(f"{linux_dir}/{script}", "rb") as f:
                add_bytes(t, f"RLMapLoader/{script}", f.read().replace(b"\r\n", b"\n"), 0o755)
        add_bytes(t, "RLMapLoader/README.txt", README.encode(), 0o644)
    print(out)


if __name__ == "__main__":
    main()
