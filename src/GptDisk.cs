// Minimal GPT reader and FAT32/ext4 label reader. The installer only ever
// handles GPT disk images, so this is deliberately small and strict.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GbOs86
{
    internal enum FileSystemKind
    {
        Unknown = 0,
        Fat32 = 1,
        Ext4 = 2
    }

    internal sealed class GptPartition
    {
        public int Index;
        public Guid TypeGuid;
        public Guid UniqueGuid;
        public ulong FirstLba;
        public ulong LastLba;
        public ulong Attributes;
        public string Name = "";

        public ulong Offset { get { return FirstLba * 512UL; } }
        public ulong Size { get { return (LastLba - FirstLba + 1) * 512UL; } }

        public override string ToString()
        {
            return (Index + 1) + " " + (Name.Length == 0 ? "(unnamed)" : Name) +
                   " " + Fmt.Bytes(Size);
        }
    }

    internal sealed class GptDisk : IDisposable
    {
        public static readonly Guid TypeEfi = new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
        public static readonly Guid TypeMicrosoftBasic = new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
        public static readonly Guid TypeLinuxFilesystem = new Guid("0fc63daf-8483-4772-8e79-3d69d8477de4");

        private FileStream _stream;

        public string Path { get; private set; }
        public long FileSize { get; private set; }
        public ulong DiskSize { get; private set; }
        public Guid DiskGuid { get; private set; }
        public ulong FirstUsableLba { get; private set; }
        public ulong LastUsableLba { get; private set; }
        public ulong BackupLba { get; private set; }
        public List<GptPartition> Partitions { get; private set; }

        private GptDisk()
        {
            Partitions = new List<GptPartition>();
        }

        public static GptDisk Open(string path)
        {
            GptDisk disk = new GptDisk();
            disk.Path = System.IO.Path.GetFullPath(path);
            disk._stream = new FileStream(disk.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.RandomAccess);
            try
            {
                disk.FileSize = disk._stream.Length;
                disk.Parse();
                return disk;
            }
            catch
            {
                disk.Dispose();
                throw;
            }
        }

        private byte[] ReadAt(ulong offset, int count)
        {
            if (offset > (ulong)long.MaxValue || offset + (ulong)count > (ulong)FileSize)
                throw new InvalidDataException("The image ends before the requested GPT structure.");
            byte[] buf = new byte[count];
            _stream.Seek((long)offset, SeekOrigin.Begin);
            int done = 0;
            while (done < count)
            {
                int n = _stream.Read(buf, done, count - done);
                if (n <= 0) throw new EndOfStreamException("Unexpected end of image.");
                done += n;
            }
            return buf;
        }

        private void Parse()
        {
            if (FileSize < 1024) throw new InvalidDataException("The image is too small to contain a GPT.");

            byte[] mbr = ReadAt(0, 512);
            if (mbr[510] != 0x55 || mbr[511] != 0xAA)
                throw new InvalidDataException("The image does not start with a disk signature.");

            byte[] hdr = ReadAt(512, 512);
            if (Encoding.ASCII.GetString(hdr, 0, 8) != "EFI PART")
                throw new InvalidDataException("No GPT header at LBA 1. This is not a GPT disk image.");

            uint headerSize = BitConverter.ToUInt32(hdr, 12);
            if (headerSize < 92 || headerSize > 512)
                throw new InvalidDataException("Unsupported GPT header size " + headerSize + ".");
            uint expectedCrc = BitConverter.ToUInt32(hdr, 16);
            byte[] copy = new byte[headerSize];
            Buffer.BlockCopy(hdr, 0, copy, 0, (int)headerSize);
            for (int i = 16; i < 20; i++) copy[i] = 0;
            uint actualCrc = Crc32.Compute(copy, 0, copy.Length);
            if (actualCrc != expectedCrc)
                throw new InvalidDataException("GPT header checksum mismatch (the image is damaged).");

            BackupLba = BitConverter.ToUInt64(hdr, 32);
            FirstUsableLba = BitConverter.ToUInt64(hdr, 40);
            LastUsableLba = BitConverter.ToUInt64(hdr, 48);
            byte[] diskGuidBytes = new byte[16];
            Buffer.BlockCopy(hdr, 56, diskGuidBytes, 0, 16);
            DiskGuid = new Guid(diskGuidBytes);
            ulong entriesLba = BitConverter.ToUInt64(hdr, 72);
            uint entryCount = BitConverter.ToUInt32(hdr, 80);
            uint entrySize = BitConverter.ToUInt32(hdr, 84);
            uint entriesCrc = BitConverter.ToUInt32(hdr, 88);

            if (entrySize < 128 || entrySize > 4096)
                throw new InvalidDataException("Unsupported GPT entry size " + entrySize + ".");
            if (entryCount == 0 || entryCount > 4096)
                throw new InvalidDataException("Unsupported GPT entry count " + entryCount + ".");

            long tableBytes = (long)entryCount * entrySize;
            if (tableBytes > 64L * 1024 * 1024)
                throw new InvalidDataException("GPT partition table is unreasonably large.");
            byte[] table = ReadAt(entriesLba * 512UL, (int)tableBytes);
            uint tableCrc = Crc32.Compute(table, 0, table.Length);
            if (tableCrc != entriesCrc)
                throw new InvalidDataException("GPT partition table checksum mismatch (the image is damaged).");

            for (uint i = 0; i < entryCount; i++)
            {
                int at = (int)(i * entrySize);
                bool used = false;
                for (int b = 0; b < 16; b++) if (table[at + b] != 0) { used = true; break; }
                if (!used) continue;

                GptPartition p = new GptPartition();
                byte[] type = new byte[16];
                byte[] unique = new byte[16];
                Buffer.BlockCopy(table, at, type, 0, 16);
                Buffer.BlockCopy(table, at + 16, unique, 0, 16);
                p.TypeGuid = new Guid(type);
                p.UniqueGuid = new Guid(unique);
                p.FirstLba = BitConverter.ToUInt64(table, at + 32);
                p.LastLba = BitConverter.ToUInt64(table, at + 40);
                p.Attributes = BitConverter.ToUInt64(table, at + 48);
                p.Index = (int)i;
                p.Name = ReadUtf16(table, at + 56, 72);
                if (p.LastLba < p.FirstLba)
                    throw new InvalidDataException("GPT partition " + (i + 1) + " has an invalid size.");
                Partitions.Add(p);
            }

            if (Partitions.Count == 0)
                throw new InvalidDataException("The GPT contains no partitions.");

            Partitions.Sort(delegate(GptPartition a, GptPartition b) { return a.FirstLba.CompareTo(b.FirstLba); });
            for (int i = 0; i < Partitions.Count; i++)
            {
                Partitions[i].Index = i;
                if (Partitions[i].FirstLba < FirstUsableLba || Partitions[i].LastLba > LastUsableLba)
                    throw new InvalidDataException("Partition " + (i + 1) + " lies outside the usable GPT area.");
                if (i > 0 && Partitions[i].FirstLba <= Partitions[i - 1].LastLba)
                    throw new InvalidDataException("GPT partitions overlap.");
            }

            // A GPT without a valid backup header is not safe to flash.
            if (BackupLba == 0 || BackupLba >= (ulong)(FileSize / 512))
                throw new InvalidDataException("The image's backup GPT is missing or outside the file.");
            byte[] backup = ReadAt(BackupLba * 512UL, 512);
            if (Encoding.ASCII.GetString(backup, 0, 8) != "EFI PART")
                throw new InvalidDataException("The image's backup GPT header is missing (truncated download?).");
            uint bsize = BitConverter.ToUInt32(backup, 12);
            if (bsize < 92 || bsize > 512)
                throw new InvalidDataException("The backup GPT header is invalid.");
            byte[] bcopy = new byte[bsize];
            Buffer.BlockCopy(backup, 0, bcopy, 0, (int)bsize);
            for (int i = 16; i < 20; i++) bcopy[i] = 0;
            if (Crc32.Compute(bcopy, 0, bcopy.Length) != BitConverter.ToUInt32(backup, 16))
                throw new InvalidDataException("The backup GPT header checksum is invalid.");

            DiskSize = (BackupLba + 1) * 512UL;
            if ((ulong)FileSize < DiskSize)
                throw new InvalidDataException("The image file is shorter than its GPT says it should be.");
        }

        private static string ReadUtf16(byte[] data, int offset, int byteCount)
        {
            string s = Encoding.Unicode.GetString(data, offset, byteCount);
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s.Substring(0, nul);
            return s.Trim();
        }

        public byte[] ReadPartitionBytes(GptPartition p, int count)
        {
            int available = (int)Math.Min((ulong)count, p.Size);
            return ReadAt(p.Offset, available);
        }

        public FileSystemKind DetectFileSystem(GptPartition p, out string label)
        {
            label = "";
            if (p.Size < 4096) return FileSystemKind.Unknown;
            byte[] buf;
            try { buf = ReadPartitionBytes(p, 4096); }
            catch { return FileSystemKind.Unknown; }
            return FsLabel.Detect(buf, out label);
        }

        public void Dispose()
        {
            if (_stream != null)
            {
                try { _stream.Dispose(); } catch { }
                _stream = null;
            }
        }
    }
}
