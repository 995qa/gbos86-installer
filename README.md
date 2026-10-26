# gbOS86 Windows Installer

A native Windows GUI installer that builds the gbOS86 **looper** (persistent
loopback) image and flashes it into unallocated space on a GPT disk, next to
Windows. It never formats an existing partition. When a disk has no free space,
it can shrink a Windows NTFS volume for you (opt-in, and restored on uninstall)
so gbOS86 and Windows can share one drive.

With a second disk:

```
Windows disk:  [ EFI ][ MSR ][ Windows C: ][ Recovery ][ ... unallocated ... ]
                                                       ^
                                                       +-- gbOS86 EFI / GBOSSYS /
                                                           GBOS_META / GBOS_DATA
```

With a single disk, the installer frees the space first:

```
Windows disk:  [ EFI ][ MSR ][ Windows C: ][ free (shrunk from C:) ][ Recovery ]
                                                ^
                                                +-- gbOS86 EFI / GBOSSYS /
                                                    GBOS_META / GBOS_DATA
```

## Requirements

| | |
|---|---|
| Windows | Windows 10 or 11, 64-bit, **UEFI** firmware |
| Rights | Administrator (the manifest requests elevation) |
| Runtime | .NET Framework 4.8 (included with Windows 10/11) |
| Target disk | GPT only. Any internal, non-removable disk of at least 1 GiB, including the disk Windows boots from |
| Free space | About 9 GB + the data-partition size (64 GB by default). Taken from unallocated space, or by shrinking an NTFS volume on the selected disk |
| Shrinking | A same-drive install needs a shrinkable NTFS volume (normally `C:`); Windows' `SizeMin` limit must allow freeing enough space |
| Secure Boot | Must be **off**: gbOS86's `BOOTX64.EFI` is unsigned |
| Building only | Docker Desktop for Windows with Linux containers; ~35 GB free in the `gbos-x86` folder, ~20 GB in `%LOCALAPPDATA%\gbOS86\images`, and ~15 GB in Docker's disk image |

You can install an image that was built elsewhere without Docker. Docker is
only needed for the **Build image** button.

## Build the installer

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

This produces `dist\gbOS86-Installer.exe` and `dist\scripts\` (the Docker build
files). The C# compiler that ships with .NET Framework 4.8 is used, so there is
no SDK/runtime to install.

## Use

1. Start `dist\gbOS86-Installer.exe` (accept the UAC prompt).
2. Choose the image source:
   - **Build from gbos-x86 source**: select your `gbos-x86` checkout, pick the
     data-partition size, and click **Build image**. The first build downloads
     about 7.8 GB from Google and the project's GitHub releases and can take
     20-60 minutes.
   - **Use an existing looper image**: browse to a `gbOS86-looper-*.img`.
     The image must be the four-partition looper image, not the standard live
     USB image.
3. Select the target disk. Any internal, non-removable disk of at least 1 GiB
   is listed, including the disk Windows boots from. Keep **Make room by
   shrinking the Windows volume** ticked (the default) to let the installer
   shrink an NTFS volume when the disk has no free space.
4. Check the summary. The installer shows exactly how much unallocated space
   will be used, how much any volume will be shrunk, and warns about Secure
   Boot or an existing gbOS86 install.
5. Click **Install gbOS86** and confirm.
6. Reboot, press **F12** (or your machine's boot-menu key), and choose
   **gbOS86**. Windows remains the default boot option.

To remove gbOS86 later, click **Uninstall**. It removes only the partitions
recorded by the installer (matched by partition GUID), never Windows
partitions, and grows back a volume the installer shrank.

## What the installer does

1. **Validates** the image: strict GPT parse (both headers and CRCs), four
   partitions, FAT32 on `GBOS_EFI`/`GBOSSYS`, ext4 on `GBOS_META`/`GBOS_DATA`,
   and the expected filesystem labels.
2. **Finds free space** on the selected GPT disk. The 1 MiB area at each end of
   the disk is reserved for the GPT itself; every existing partition is treated
   as used.
3. **Makes room if necessary**: when the largest free extent is too small and
   **Make room** is ticked, queries `Get-PartitionSupportedSize` for the NTFS
   volumes on the disk, picks the running Windows volume when it can free
   enough space (otherwise the volume that can free the most), and shrinks it
   with `Resize-Partition`. The exact amount is computed offline first, so the
   summary shows the resulting size and free space before anything changes. A
   volume is never shrunk below Windows' `SizeMin`. This step is skipped when
   the disk already has enough unallocated space.
4. **Creates four exact-sized GPT partitions** in one unallocated extent with
   `New-Partition`/`Set-Partition`, in the image's own order and with the image's
   GPT type GUIDs.
5. **Clears stale filesystem headers** at the start of each new partition
   (32 MiB for the ESP, 128 MiB for GBOSSYS, the whole 64 MiB metadata
   partition, 512 MiB for DATA) so old FAT/ext4 metadata cannot confuse the new
   filesystems.
6. **Writes only allocated image extents** to `\\.\PhysicalDriveN`. The 64 GB
   DATA partition is mostly sparse, so a 72 GB logical image typically writes
   about 9 GB. The new volumes are locked and their filesystems dismounted for
   the whole write, and each partition's first sector is written last, so
   Windows cannot mount a half-written FAT32 volume and reject the remaining
   raw writes with "access denied".
7. **Verifies** every written byte by reading it back and comparing, then checks
   the filesystem type and label of each target partition.
8. **Rolls back** the new partitions if anything fails, and grows a shrunk
   volume back to its original size.

No boot entry is added. The firmware's own boot menu lists the new `GBOS_EFI`
partition (`\EFI\BOOT\BOOTX64.EFI`), so Windows stays the default boot option
and there is no menu entry to clean up on uninstall.

## Image layout

The looper image built by `gbos-x86` has this fixed layout:

| # | GPT name | Size | Filesystem | Contents |
|---|---|---|---|---|
| 1 | `GBOS_EFI` | 512 MiB | FAT32 | `EFI/BOOT/BOOTX64.EFI` (kernel + stage0) |
| 2 | `GBOSSYS` | 8 GiB | FAT32 | `super.img`, patches, `gbos.cfg` (`MODE=boot`) |
| 3 | `GBOS_META` | 64 MiB | ext4 | Android `/metadata` |
| 4 | `GBOS_DATA` | N GiB | ext4 | Android `/data` (sparse, grows as used) |

The installer reads the exact sizes and type GUIDs from the image, so a
different data size (or a future layout change) works without editing the
installer. It only insists on the four partition roles above.

## Building details

The **Build image** path runs the unmodified upstream `gbos-x86/build.sh`
inside a Debian container:

- `scripts/docker/Dockerfile` installs the tools the upstream build expects
  (python3, curl, xz, mtools, dosfstools, sfdisk, e2fsprogs, busybox-static).
- `scripts/docker/shim/docker` translates the upstream build's nested
  `docker run alpine ...` steps into local commands, because a Docker socket is
  not available inside the build container.
- The image is assembled in a named Docker volume (`gbos86-build-dist`) so the
  72 GB sparse file never has to cross Docker Desktop's Windows file sharing.
  Stale images are removed at the start of a build and the volume is cleaned
  after the pack is written.
- `scripts/docker/pack_image.py` exports only non-zero 1 MiB blocks plus a map.
  The Windows side rebuilds an NTFS sparse image from those blocks
  (`PackedImage.cs`) and verifies the pack's SHA-256. The pack and the rebuilt
  image coexist until unpacking finishes, so `%LOCALAPPDATA%\gbOS86\images`
  needs about twice the pack size.
- The upstream `.img.sha256` is copied next to the rebuilt image and recorded in
  the installation state when available.

Downloads and extracted Google partitions are cached in the `gbos-x86` folder
(`lapis/parts`, `build/dl`, `usb/modules`, ...), all of which are already in the
upstream `.gitignore`.

## Tests

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
```

The tests cover the strict GPT parser (including corruption rejection), FAT32/
ext4 label detection, unallocated-space math (including shrink planning around a
recovery partition), NTFS sparse extent queries, extent-map round trips
(including rejection of a map for a replaced image), and the pack/materialize
path. They do not touch a physical disk.

## Troubleshooting

**"Docker Desktop is installed but its engine is not running"**  
Start Docker Desktop and wait until it says "Engine running".

**"Not enough unallocated space"**  
Tick **Make room by shrinking the Windows volume** (it is on by default) and
refresh. The installer then tries to shrink an NTFS volume on the selected disk;
for a single-drive PC that is normally `C:`. Windows limits how far a volume can
shrink when unmovable files (page file, hibernation file, MFT, the image itself)
sit near its end, so the installer reports the maximum it can free.

To need less space, build the image with a smaller data partition (for example
16 GB instead of 64 GB), or free space in Windows and shrink `C:` in Disk
Management first. The installer never shrinks a volume unless you leave the
checkbox ticked.

**"Windows cannot shrink this volume that far"**  
`Resize-Partition` rejected the size because of `SizeMin`. Run Disk Cleanup,
disable hibernation (`powercfg /h off`), or delete the image from `C:` and retry,
then extend `C:` back in Disk Management. Nothing was changed if this appears
before "Creating four GPT partitions".

**"The target disk must use GPT"**  
gbOS86 boots via UEFI, so the disk must be GPT. Converting an MBR disk that
contains Windows is out of scope for this installer.

**"Verification failed"**  
The disk returned different bytes than were written. This usually means a
failing disk, a USB bridge lying about flushes, or antivirus/another program
holding the volume. The installer removes the new partitions; check the disk
with the manufacturer's tool.

**"Write failed at offset ..."**  
The raw write was refused part-way through. The message includes the Win32
error code: `error 5: Access is denied` means Windows mounted a filesystem on a
new volume while it was being written, so close Explorer, Disk Management and
anything else that uses the disk and run the installer again; `error 1117: The
request could not be performed because of an I/O device error` points at the
disk itself. The installer removes the new partitions before you retry.

**Secure Boot**  
gbOS86's boot loader is not signed. Turn Secure Boot off in the UEFI settings.
Windows itself will still boot with Secure Boot off.

**gbOS86 is not in the boot menu**  
The firmware lists the new `GBOS_EFI` partition by itself; some firmware only
refreshes its boot menu after a full power cycle (shut down, not restart). If it
still does not appear, make sure the PC boots in UEFI mode (not CSM/legacy),
Secure Boot is off, and the disk holding gbOS86 is in the boot order.

## Notes

- The installer never downloads or bundles Google's OS image; it builds from
  your local `gbos-x86` checkout or uses an image you provide.
- The image file may live on the target disk (it usually does, in
  `%LOCALAPPDATA%`); it is only read. Keep a copy elsewhere in case the install
  is interrupted.
- Raw writes require administrator rights. Close Disk Management, Explorer
  windows on the target disk, and anything else that might hold a volume handle.
- Shrinking writes a new GPT and changes the size of one existing partition.
  Back up important files before any partition change, as with any installer.
- The log is written to `%LOCALAPPDATA%\gbOS86\logs\`. The **Open logs** button
  opens that folder.
- The installation state is recorded in
  `%ProgramData%\gbOS86\install-state.ini` for the Uninstall button. It includes
  the shrink record so Uninstall can restore the original volume size.
