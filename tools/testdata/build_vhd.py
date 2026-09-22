"""Makes a fixed-size VHD holding a test card, for trying the app in a Windows VM.

    python3 tools/testdata/build_vhd.py testdata/cards/three-sites viltkamera-test.vhd

Windows mounts a VHD by double-clicking it (or Disk Management > Attach VHD): it turns up as a
writable drive with a DCIM folder, so copying, emptying and ejecting can all be tried. An ISO
cannot: it mounts as a read-only CD, which the app skips when looking for cards.

Run on macOS: it builds the FAT32 volume with hdiutil, then adds the VHD footer.
"""

import os, struct, subprocess, sys, tempfile, uuid
from pathlib import Path

def megabytes(path: Path) -> int:
    total = sum(f.stat().st_size for f in path.rglob("*") if f.is_file())
    return int(total / 1_000_000 * 1.25) + 64  # room for the filesystem and slack

def footer(size: int) -> bytes:
    # Fixed-disk footer, VHD spec 1.0. Windows checks the cookie, the type and the checksum.
    cylinders = min(size // 512 // (16 * 63), 65535)
    fields = struct.pack(
        ">8sIIQI4sI4sQQHBBII16sB",
        b"conectix", 2, 0x00010000, 0xFFFFFFFFFFFFFFFF, 0,          # cookie, features, version, offset, time
        b"eiiv", 0x00010000, b"Wi2k",                               # creator app, version, host OS
        size, size,                                                 # original and current size
        cylinders, 16, 63,                                          # geometry: cylinders, heads, sectors
        2, 0,                                                       # disk type: fixed, checksum (filled below)
        uuid.uuid4().bytes, 0)
    fields = fields.ljust(512, b"\x00")
    checksum = (~sum(fields)) & 0xFFFFFFFF
    return fields[:64] + struct.pack(">I", checksum) + fields[68:]

def main(card: str, out: str) -> None:
    card_path, out_path = Path(card), Path(out)
    with tempfile.TemporaryDirectory() as tmp:
        dmg = Path(tmp) / "card.dmg"
        subprocess.run(["hdiutil", "create", "-size", f"{megabytes(card_path)}m", "-fs", "MS-DOS FAT32",
                        "-volname", "VILTKAM", "-layout", "MBRSPUD", "-ov", "-quiet", str(dmg)], check=True)
        attached = subprocess.run(["hdiutil", "attach", str(dmg), "-nobrowse"],
                                  check=True, capture_output=True, text=True).stdout
        mount = next(line.split("\t")[-1].strip() for line in attached.splitlines() if "/Volumes/" in line)
        try:
            subprocess.run(["cp", "-R", str(card_path / "DCIM"), mount], check=True)
            subprocess.run(["dot_clean", mount], check=False)  # drop the ._ files macOS leaves
        finally:
            subprocess.run(["hdiutil", "detach", mount, "-quiet"], check=True)

        raw = Path(tmp) / "card.img"
        subprocess.run(["hdiutil", "convert", str(dmg), "-format", "UDRW", "-o", str(raw), "-quiet"], check=True)
        raw = raw.with_suffix(".img.dmg") if not raw.exists() else raw
        size = raw.stat().st_size
        with open(raw, "rb") as src, open(out_path, "wb") as dst:
            while chunk := src.read(1 << 20):
                dst.write(chunk)
            dst.write(footer(size))
    print(f"{out_path} ({out_path.stat().st_size / 1_000_000:.0f} MB) — double-click it in Windows to mount")

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
