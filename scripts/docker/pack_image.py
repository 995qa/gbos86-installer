#!/usr/bin/env python3
"""pack_image.py IMAGE OUT_PACK OUT_MAP [BLOCK]

Write only the non-zero blocks of a (usually sparse) disk image to OUT_PACK and
a map that lets the Windows installer rebuild the image as an NTFS sparse file.
The map also carries the SHA-256 of the packed data so the transfer can be
verified end to end.
"""
import hashlib
import os
import sys

MAGIC = "GBOS86-PACK 1"


def main():
    if len(sys.argv) < 4:
        sys.stderr.write("usage: pack_image.py IMAGE OUT_PACK OUT_MAP [BLOCK]\n")
        return 2
    image = sys.argv[1]
    pack_path = sys.argv[2]
    map_path = sys.argv[3]
    block = int(sys.argv[4]) if len(sys.argv) > 4 else (1 << 20)
    if block < 4096:
        block = 4096

    size = os.path.getsize(image)
    zero = bytes(block)
    entries = []
    pack_offset = 0
    scanned = 0
    last_report = 0
    digest = hashlib.sha256()

    pack_dir = os.path.dirname(pack_path)
    if pack_dir:
        os.makedirs(pack_dir, exist_ok=True)

    with open(image, "rb", buffering=0) as src, open(pack_path, "wb", buffering=1 << 20) as out:
        while True:
            chunk = src.read(block)
            if not chunk:
                break
            n = len(chunk)
            if chunk != (zero if n == block else zero[:n]):
                out.write(chunk)
                digest.update(chunk)
                entries.append((scanned, n, pack_offset))
                pack_offset += n
            scanned += n
            if scanned - last_report >= (4 << 30):
                last_report = scanned
                print("PACK_PROGRESS|%d|%d" % (scanned, size), flush=True)
        out.flush()
        # Some Docker Desktop bind-mount filesystems do not implement fsync.
        # The data is still written and the map records the SHA-256, so a
        # missing fsync is not a reason to fail the build.
        try:
            os.fsync(out.fileno())
        except OSError:
            pass

    sha = digest.hexdigest()
    with open(map_path, "w", encoding="ascii", newline="\n") as m:
        m.write(MAGIC + "\n")
        m.write("size=%d\n" % size)
        m.write("block=%d\n" % block)
        m.write("packsize=%d\n" % pack_offset)
        m.write("sha256=%s\n" % sha)
        for off, length, packed in entries:
            m.write("%d %d %d\n" % (off, length, packed))

    base = os.path.basename(pack_path)
    if base.endswith(".pack"):
        base = base[:-5]
    print("GBOS86_PACK|%s|%d|%d|%d|%s" % (base, size, block, pack_offset, sha), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
