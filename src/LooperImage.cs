// Validation and sparse-range access for a gbOS86 "looper" disk image.
using System;
using System.Collections.Generic;
using System.IO;

namespace GbOs86
{
    internal sealed class ImagePartition
    {
        public int Number;              // 1..4
        public string Name = "";        // GPT name
        public string Label = "";       // filesystem label
        public FileSystemKind Fs;
        public ulong Offset;
        public ulong Size;
        public Guid TypeGuid;
        public Guid UniqueGuid;
        public GptPartition Gpt;

        public string DisplayName
        {
            get { return Name.Length > 0 ? Name : Label; }
        }
    }

    internal sealed class LooperImage : IDisposable
    {
        public string Path { get; private set; }
        public long FileSize { get; private set; }
        public ulong DiskSize { get; private set; }
        public List<ImagePartition> Partitions { get; private set; }
        public List<string> Warnings { get; private set; }
        public bool ExtentsKnown { get; private set; }
        public bool IsSparse { get; private set; }
        public long AllocatedBytes { get; private set; }
        private List<Extent> _extents;
        private GptDisk _gpt;

        private LooperImage()
        {
            Partitions = new List<ImagePartition>();
            Warnings = new List<string>();
        }

        public ulong DataBytes
        {
            get
            {
                if (Partitions.Count < 4) return 0;
                return Partitions[3].Size;
            }
        }

        public double DataGb
        {
            get { return DataBytes / 1073741824.0; }
        }

        public ulong RequiredTargetBytes
        {
            get
            {
                ulong total = 0;
                foreach (ImagePartition p in Partitions)
                    total += AlignUp(p.Size, 1048576UL);
                return total;
            }
        }

        public static ulong AlignUp(ulong value, ulong alignment)
        {
            if (alignment == 0) return value;
            ulong rem = value % alignment;
            return rem == 0 ? value : value + (alignment - rem);
        }

        public static LooperImage Open(string path)
        {
            LooperImage img = new LooperImage();
            img.Path = System.IO.Path.GetFullPath(path);
            if (!File.Exists(img.Path))
                throw new FileNotFoundException("Image not found: " + img.Path);

            img._gpt = GptDisk.Open(img.Path);
            img.FileSize = img._gpt.FileSize;
            img.DiskSize = img._gpt.DiskSize;

            List<GptPartition> used = new List<GptPartition>(img._gpt.Partitions);
            if (used.Count != 4)
            {
                throw new InvalidDataException(
                    "This is not a gbOS86 loopback image: expected 4 partitions (GBOS_EFI, GBOSSYS, GBOS_META, GBOS_DATA), found " +
                    used.Count + ".");
            }

            foreach (GptPartition gp in used)
            {
                ImagePartition p = new ImagePartition();
                p.Gpt = gp;
                p.Number = img.Partitions.Count + 1;
                p.Name = gp.Name;
                p.Offset = gp.Offset;
                p.Size = gp.Size;
                p.TypeGuid = gp.TypeGuid;
                p.UniqueGuid = gp.UniqueGuid;
                string label;
                p.Fs = img._gpt.DetectFileSystem(gp, out label);
                p.Label = label;
                img.Partitions.Add(p);
            }

            img.Validate();
            img.LoadExtents();
            return img;
        }

        private void Validate()
        {
            string[] expectedLabels = { "GBOS_EFI", "GBOSSYS", "GBOS_META", "GBOS_DATA" };
            string[] expectedNames = { "GBOS_EFI", "GBOSSYS", "GBOS_META", "GBOS_DATA" };

            for (int i = 0; i < 4; i++)
            {
                ImagePartition p = Partitions[i];
                string expected = expectedLabels[i];
                bool nameOk = string.Equals(p.Name, expectedNames[i], StringComparison.OrdinalIgnoreCase);
                bool labelOk = string.Equals(p.Label, expected, StringComparison.OrdinalIgnoreCase);
                if (!nameOk && !labelOk)
                {
                    throw new InvalidDataException(
                        "Partition " + (i + 1) + " is not " + expected + " (GPT name \"" + p.Name +
                        "\", filesystem label \"" + p.Label + "\").\n\n" +
                        "Use the loopback image built by gbos-x86/build.sh --looper, not the standard live image.");
                }
                if (i < 2 && p.Fs != FileSystemKind.Fat32)
                    throw new InvalidDataException("Partition " + (i + 1) + " (" + expected + ") is not FAT32.");
                if (i >= 2 && p.Fs != FileSystemKind.Ext4)
                    throw new InvalidDataException("Partition " + (i + 1) + " (" + expected + ") is not ext4.");
            }

            if (Partitions[0].TypeGuid != GptDisk.TypeEfi)
                Warnings.Add("The EFI partition does not have the standard EFI System type GUID.");
            if (Partitions[1].TypeGuid != GptDisk.TypeMicrosoftBasic &&
                Partitions[1].TypeGuid != GptDisk.TypeLinuxFilesystem)
                Warnings.Add("The GBOSSYS partition has an unusual GPT type.");
            if (Partitions[0].Size < 32UL * 1024 * 1024)
                throw new InvalidDataException("The EFI partition is too small.");
            if (Partitions[1].Size < 512UL * 1024 * 1024)
                throw new InvalidDataException("The GBOSSYS partition is too small.");
            if (Partitions[2].Size < 8UL * 1024 * 1024)
                throw new InvalidDataException("The GBOS_META partition is too small.");
            if (Partitions[3].Size < 256UL * 1024 * 1024)
                throw new InvalidDataException("The GBOS_DATA partition is too small.");

            if (Difference(Partitions[0].Size, 512UL * 1024 * 1024) > 1024UL * 1024)
                Warnings.Add("The EFI partition is " + Fmt.Bytes(Partitions[0].Size) + " instead of 512 MiB.");
            if (Difference(Partitions[1].Size, 8192UL * 1024 * 1024) > 1024UL * 1024)
                Warnings.Add("The GBOSSYS partition is " + Fmt.Bytes(Partitions[1].Size) + " instead of 8 GiB.");
            if (Difference(Partitions[2].Size, 64UL * 1024 * 1024) > 1024UL * 1024)
                Warnings.Add("The GBOS_META partition is " + Fmt.Bytes(Partitions[2].Size) + " instead of 64 MiB.");
        }

        private static ulong Difference(ulong a, ulong b)
        {
            return a >= b ? a - b : b - a;
        }

        private void LoadExtents()
        {
            string sidecar = Path + ".extents";
            List<Extent> fromFile = null;
            DateTime imageTime = GetImageWriteTimeUtc();
            // Only trust the sidecar if it was written after the image. A newly
            // copied image can reuse an old sidecar's size while its contents
            // (and therefore its allocated ranges) are different, so the map
            // also records the image's timestamp.
            if (File.Exists(sidecar) && IsSidecarFresh(sidecar))
                fromFile = ExtentMap.Load(sidecar, FileSize, imageTime);
            if (fromFile != null)
            {
                _extents = fromFile;
                ExtentsKnown = true;
                IsSparse = true;
            }
            else
            {
                // Querying "allocated ranges" of a non-sparse file returns the
                // whole file, which would make the installer write every zero
                // byte of a 72 GB image. Only use the query for sparse files.
                bool sparse = false;
                try
                {
                    sparse = (File.GetAttributes(Path) & FileAttributes.SparseFile) != 0;
                }
                catch { }

                bool supported = false;
                if (sparse)
                    _extents = SparseImage.QueryAllocatedRanges(Path, 0, FileSize, out supported);

                if (_extents != null && supported)
                {
                    ExtentsKnown = true;
                    IsSparse = true;
                }
                else
                {
                    // A normal (non-sparse) copy of the image: scan for zero blocks
                    // so we do not write tens of GB of holes to the target.
                    _extents = SparseImage.ScanNonZeroRanges(Path, 0, FileSize, 8 << 20);
                    ExtentsKnown = true;
                    IsSparse = false;
                    Warnings.Add("The image is not stored as a sparse file; its zero ranges were scanned and will be skipped.");
                }
            }

            long total = 0;
            foreach (Extent e in _extents) total += e.Length;
            AllocatedBytes = total;
        }

        private DateTime GetImageWriteTimeUtc()
        {
            try { return File.GetLastWriteTimeUtc(Path); }
            catch { return DateTime.MinValue; }
        }

        public List<Extent> GetPartitionExtents(ImagePartition p)
        {
            List<Extent> list = SparseImage.Intersect(_extents, (long)p.Offset, (long)(p.Offset + p.Size));
            for (int i = 0; i < list.Count; i++)
                list[i] = new Extent(list[i].Offset - (long)p.Offset, list[i].Length);
            return list;
        }

        public List<Extent> GetAllExtents()
        {
            return new List<Extent>(_extents);
        }

        private bool IsSidecarFresh(string sidecar)
        {
            try
            {
                DateTime imageTime = File.GetLastWriteTimeUtc(Path);
                DateTime mapTime = File.GetLastWriteTimeUtc(sidecar);
                // Allow for the coarse timestamps some filesystems report.
                return mapTime >= imageTime.AddSeconds(-2);
            }
            catch { return false; }
        }

        public bool SaveExtentMap()
        {
            try
            {
                ExtentMap.Save(Path + ".extents", FileSize, GetImageWriteTimeUtc(), _extents);
                return true;
            }
            catch
            {
                // A read-only image folder is not a reason to reject an
                // otherwise valid image; the extents are already in memory.
                return false;
            }
        }

        public void Dispose()
        {
            if (_gpt != null)
            {
                _gpt.Dispose();
                _gpt = null;
            }
        }
    }
}
