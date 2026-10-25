#!/bin/sh
# Runs inside the builder container. Uses the unmodified upstream gbos-x86
# build.sh, then exports the image as non-zero blocks so the Windows side never
# has to touch a 72 GB sparse file over the Docker file share.
set -eu

export PATH=/opt/gbos86/shim:$PATH
cd /src

# /src/dist is the named build volume. Remove images left by an interrupted
# run so the volume does not slowly fill up; the packed copy in /images is
# written only after a complete build.
rm -f dist/gbOS86-looper-*.img dist/gbOS86-looper-*.img.sha256
rm -rf dist/*.stage

DATA_GB=${GBOS_DATA_GB:-64}
case "$DATA_GB" in
  ''|*[!0-9]*) echo "invalid GBOS_DATA_GB: $DATA_GB" >&2; exit 1 ;;
esac

echo "gbOS86 builder: looper image, ${DATA_GB} GB userdata partition"
sh build.sh --looper --data-size "$DATA_GB" -y

IMG=$(ls -t dist/gbOS86-looper-*.img 2>/dev/null | head -n 1)
if [ -z "$IMG" ]; then
  echo "gbOS86 builder: build.sh finished but produced no looper image" >&2
  exit 1
fi

BASE=$(basename "$IMG" .img)
SIZE=$(stat -c%s "$IMG")
SHA=""
if [ -f "dist/$BASE.img.sha256" ]; then
  SHA=$(cut -d' ' -f1 "dist/$BASE.img.sha256")
fi

# Report the identity first so the Windows side can associate the following
# GBOS86_PACK line with it.
echo "GBOS86_RESULT|$BASE|$SIZE|$SHA"

python3 /opt/gbos86/pack_image.py "$IMG" "/images/$BASE.pack" "/images/$BASE.pack.map" 1048576

if [ -f "dist/$BASE.img.sha256" ]; then
  cp "dist/$BASE.img.sha256" "/images/$BASE.img.sha256"
fi

# The pack is complete and verified by pack_image.py. Drop the 72 GB logical
# image from the build volume so the next build does not run out of space.
rm -f "$IMG" "dist/$BASE.img.sha256"
echo "gbOS86 builder: packed image is in /images; build volume cleaned."
