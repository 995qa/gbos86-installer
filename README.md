# gbOS86 Windows Installer

A native Windows GUI installer that builds the gbOS86 **looper** (persistent
loopback) image and flashes it into unallocated space on a GPT disk, next to
Windows. It does not format, shrink, or otherwise touch existing partitions.

```
Windows disk:  [ EFI ][ MSR ][ Windows C: ][ Recovery ][ ... unallocated ... ]
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
| Target disk | GPT, not the running Windows disk, with one contiguous unallocated area large enough for the image |
| Free space | About 9 GB + the data-partition size (64 GB by default) of unallocated space on the target disk |
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
3. Select the target disk. Only internal, non-removable disks of at least 1 GiB
   are listed. The running Windows disk is rejected.
4. Check the summary. The installer shows exactly how much unallocated space
   will be used and warns about Secure Boot or an existing gbOS86 install.
5. Click **Install gbOS86** and confirm.
6. Reboot, press **F12** (or your machine's boot-menu key), and choose
   **gbOS86**. Windows remains the default boot option.

To remove gbOS86 later, click **Uninstall**. It removes only the partitions and
boot entry recorded by the installer (matched by partition GUID), never Windows
partitions.

## What the installer does

1. **Validates** the image: strict GPT parse (both headers and CRCs), four
   partitions, FAT32 on `GBOS_EFI`/`GBOSSYS`, ext4 on `GBOS_META`/`GBOS_DATA`,
   and the expected filesystem labels.
2. **Finds free space** on the selected GPT disk. The 1 MiB area at each end of
   the disk is reserved for the GPT itself; every existing partition is treated
   as used.
3. **Creates four exact-sized GPT partitions** in one unallocated extent with
   `New-Partition`/`Set-Partition`, in the image's own order and with the image's
   GPT type GUIDs.
4. **Clears stale filesystem headers** at the start of each new partition
   (32 MiB for the ESP, 128 MiB for GBOSSYS, the whole 64 MiB metadata
   partition, 512 MiB for DATA) so old FAT/ext4 metadata cannot confuse the new
   filesystems.
5. **Writes only allocated image extents** to `\\.\PhysicalDriveN`. The 64 GB
   DATA partition is mostly sparse, so a 72 GB logical image typically writes
   about 9 GB.
6. **Verifies** every written byte by reading it back and comparing, then checks
   the filesystem type and label of each target partition.
7. **Adds a UEFI boot entry** (`Boot####`/`BootOrder`, description `gbOS86`)
   pointing at `\EFI\BOOT\BOOTX64.EFI` on the new ESP, appended after Windows so
   Windows stays the default. If the firmware refuses variable writes, it falls
   back to a `bcdedit` Windows Boot Manager entry.
8. **Rolls back** the new partitions and boot entry if anything fails.

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
ext4 label detection, unallocated-space math, NTFS sparse extent queries,
extent-map round trips (including rejection of a map for a replaced image), and
the pack/materialize path. They do not touch a physical disk.

## Troubleshooting

**"Docker Desktop is installed but its engine is not running"**  
Start Docker Desktop and wait until it says "Engine running".

**"Not enough unallocated space"**  
The image needs one contiguous free area. Shrink a data partition in Disk
Management, or build a smaller data partition (the default is 64 GB). The
installer never shrinks Windows for you.

**"The target disk must use GPT"**  
gbOS86 boots via UEFI, so the disk must be GPT. Converting an MBR disk that
contains Windows is out of scope for this installer.

**"Verification failed"**  
The disk returned different bytes than were written. This usually means a
failing disk, a USB bridge lying about flushes, or antivirus/another program
holding the volume. The installer removes the new partitions; check the disk
with the manufacturer's tool.

**Secure Boot**  
gbOS86's boot loader is not signed. Turn Secure Boot off in the UEFI settings.
Windows itself will still boot with Secure Boot off.

**Boot entry did not appear**  
Some firmware only refreshes its boot menu after a full power cycle. If the
native `Boot####` write was refused, the installer adds a `gbOS86` entry to the
Windows Boot Manager instead. That entry is only visible when the Windows boot
menu is enabled; you can turn it on with
`bcdedit /set {bootmgr} displaybootmenu yes` (and set a timeout with
`bcdedit /timeout 5`), or add `\EFI\BOOT\BOOTX64.EFI` manually in the firmware
setup.

## Notes

- The installer never downloads or bundles Google's OS image; it builds from
  your local `gbos-x86` checkout or uses an image you provide.
- Raw writes require administrator rights. Close Disk Management, Explorer
  windows on the target disk, and anything else that might hold a volume handle.
- The log is written to `%LOCALAPPDATA%\gbOS86\logs\`. The **Open logs** button
  opens that folder.
- The installation state is recorded in
  `%ProgramData%\gbOS86\install-state.ini` for the Uninstall button.
