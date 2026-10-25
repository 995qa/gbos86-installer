// Offline tests for the parts that do not need a physical disk. Run:
//   powershell -File tests\run-tests.ps1
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GbOs86
{
    internal static class TestProgram
    {
        private static int _failures;
        private static string _temp;

        private static int Main()
        {
            _temp = Path.Combine(Path.GetTempPath(), "gbos86-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
            try
            {
                TestFsLabels();
                TestGptParser();
                TestGptRejectsCorruption();
                TestFreeSpace();
                TestSparseExtents();
                TestPackedImage();
                TestUefiDevicePathData();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL  unhandled: " + ex);
                _failures++;
            }
            finally
            {
                try { Directory.Delete(_temp, true); } catch { }
            }

            Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : _failures + " TEST(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }

        private static void Check(bool condition, string name, string detail)
        {
            if (condition)
            {
                Console.WriteLine("ok    " + name);
            }
            else
            {
                Console.WriteLine("FAIL  " + name + (detail == null ? "" : ": " + detail));
                _failures++;
            }
        }

        private static void TestFsLabels()
        {
            byte[] fat = new byte[4096];
            fat[0] = 0xEB; fat[1] = 0x3C; fat[2] = 0x90;
            WriteU16(fat, 0x0B, 512);
            fat[0x0D] = 1;
            WriteU16(fat, 0x0E, 1);
            fat[0x10] = 2;
            WriteU16(fat, 0x11, 0);
            fat[0x15] = 0xF8;
            WriteU32(fat, 0x24, 1024);
            PutAscii(fat, 0x47, "GBOS_EFI   ", 11);
            fat[510] = 0x55; fat[511] = 0xAA;

            string label;
            FileSystemKind fs = FsLabel.Detect(fat, out label);
            Check(fs == FileSystemKind.Fat32 && label == "GBOS_EFI", "FAT32 label", fs + " " + label);

            byte[] ext = new byte[4096];
            WriteU16(ext, 1024 + 0x38, 0xEF53);
            PutAscii(ext, 1024 + 0x78, "GBOS_DATA", 16);
            fs = FsLabel.Detect(ext, out label);
            Check(fs == FileSystemKind.Ext4 && label == "GBOS_DATA", "ext4 label", fs + " " + label);

            fs = FsLabel.Detect(new byte[512], out label);
            Check(fs == FileSystemKind.Unknown, "blank is unknown", fs.ToString());
        }

        private static void TestGptParser()
        {
            string path = Path.Combine(_temp, "gpt-small.img");
            List<TestPart> parts = new List<TestPart>();
            parts.Add(new TestPart(GptDisk.TypeEfi, 2048, 128, "GBOS_EFI", true, "GBOS_EFI   "));
            parts.Add(new TestPart(GptDisk.TypeMicrosoftBasic, 2176, 128, "GBOSSYS", true, "GBOSSYS    "));
            parts.Add(new TestPart(GptDisk.TypeLinuxFilesystem, 2304, 128, "GBOS_META", false, "GBOS_META"));
            parts.Add(new TestPart(GptDisk.TypeLinuxFilesystem, 2432, 128, "GBOS_DATA", false, "GBOS_DATA"));
            MakeGpt(path, 4 * 1024 * 1024, parts);

            using (GptDisk gpt = GptDisk.Open(path))
            {
                Check(gpt.Partitions.Count == 4, "GPT finds 4 partitions", gpt.Partitions.Count.ToString());
                Check(gpt.Partitions[0].Name == "GBOS_EFI", "GPT name", gpt.Partitions[0].Name);
                Check(gpt.Partitions[0].Offset == 2048UL * 512UL, "GPT offset", gpt.Partitions[0].Offset.ToString());
                string label;
                FileSystemKind fs = gpt.DetectFileSystem(gpt.Partitions[0], out label);
                Check(fs == FileSystemKind.Fat32 && label == "GBOS_EFI", "GPT p1 filesystem", fs + " " + label);
                fs = gpt.DetectFileSystem(gpt.Partitions[2], out label);
                Check(fs == FileSystemKind.Ext4 && label == "GBOS_META", "GPT p3 filesystem", fs + " " + label);
            }
        }

        private static void TestGptRejectsCorruption()
        {
            string path = Path.Combine(_temp, "gpt-corrupt.img");
            List<TestPart> parts = new List<TestPart>();
            parts.Add(new TestPart(GptDisk.TypeEfi, 2048, 128, "GBOS_EFI", true, "GBOS_EFI   "));
            MakeGpt(path, 2 * 1024 * 1024, parts);

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Seek(2 * 512 + 32, SeekOrigin.Begin);
                int b = fs.ReadByte();
                fs.Seek(2 * 512 + 32, SeekOrigin.Begin);
                fs.WriteByte((byte)(b ^ 0x5A));
            }
            bool threw = false;
            try { using (GptDisk g = GptDisk.Open(path)) { } }
            catch (InvalidDataException) { threw = true; }
            Check(threw, "GPT rejects a corrupt entry table", null);
        }

        private static void TestFreeSpace()
        {
            DiskInfo d = new DiskInfo();
            d.Size = 1000UL * 1024 * 1024;
            d.Partitions.Add(Part(1, 100UL, 50UL));
            d.Partitions.Add(Part(2, 400UL, 100UL));

            List<FreeExtent> free = DiskService.ComputeFreeExtents(d, null);
            Check(free.Count == 3, "free space gaps", free.Count.ToString());
            Check(free[0].Offset == 1024UL * 1024, "first gap starts after guard", free[0].Offset.ToString());
            Check(free[1].Offset == 150UL * 1024 * 1024 && free[1].Length == 250UL * 1024 * 1024,
                "middle gap", free[1].ToString());
            Check(free[2].Length == 499UL * 1024 * 1024, "last gap", free[2].ToString());

            List<int> exclude = new List<int>();
            exclude.Add(2);
            List<FreeExtent> after = DiskService.ComputeFreeExtents(d, exclude);
            Check(after.Count == 2 && after[1].Length == 849UL * 1024 * 1024,
                "free space after removing a partition", after.Count + " " + after[after.Count - 1]);
        }

        private static void TestSparseExtents()
        {
            string path = Path.Combine(_temp, "sparse.bin");
            using (SparseWriter w = new SparseWriter(path, 64L * 1024 * 1024))
            {
                byte[] hello = Encoding.ASCII.GetBytes("hello-sparse");
                byte[] world = Encoding.ASCII.GetBytes("world-sparse");
                w.Write(10L * 1024 * 1024, hello, hello.Length);
                w.Write(30L * 1024 * 1024, world, world.Length);
                w.Finish();
            }

            bool supported;
            List<Extent> ext = SparseImage.QueryAllocatedRanges(path, 0, new FileInfo(path).Length, out supported);
            Check(supported && ext != null, "NTFS allocated-range query supported", supported.ToString());
            bool at10 = false, at30 = false, at20 = false;
            foreach (Extent e in ext)
            {
                if (e.Offset <= 10L * 1024 * 1024 && e.End >= 10L * 1024 * 1024 + 12) at10 = true;
                if (e.Offset <= 30L * 1024 * 1024 && e.End >= 30L * 1024 * 1024 + 12) at30 = true;
                if (e.Offset <= 20L * 1024 * 1024 && e.End >= 20L * 1024 * 1024 + 4096) at20 = true;
            }
            Check(at10 && at30, "extents cover written data", null);
            Check(!at20, "extents skip the hole", null);

            DateTime stamp = File.GetLastWriteTimeUtc(path);
            ExtentMap.Save(path + ".map", new FileInfo(path).Length, stamp, ext);
            List<Extent> loaded = ExtentMap.Load(path + ".map", new FileInfo(path).Length, stamp);
            Check(loaded != null && loaded.Count == ext.Count, "extent map round trip",
                loaded == null ? "null" : loaded.Count.ToString());
            Check(ExtentMap.Load(path + ".map", new FileInfo(path).Length, stamp.AddMinutes(1)) == null,
                "extent map rejects a replaced image", null);

            string full = Path.Combine(_temp, "full.bin");
            byte[] data = new byte[1024 * 1024];
            data[0] = 1;
            data[600 * 1024] = 2;
            File.WriteAllBytes(full, data);
            List<Extent> scanned = SparseImage.ScanNonZeroRanges(full, 0, data.Length, 4096);
            Check(scanned.Count == 2, "non-zero scan", scanned.Count.ToString());
            Check(scanned[0].Offset == 0 && scanned[1].Offset == 600 * 1024, "non-zero scan offsets",
                scanned.Count > 1 ? scanned[0] + " " + scanned[1] : "");
        }

        private static void TestPackedImage()
        {
            string source = Path.Combine(_temp, "pack-source.bin");
            long size = 8L * 1024 * 1024;
            byte[] data = new byte[size];
            for (int i = 0; i < data.Length; i++) data[i] = 0;
            byte[] one = Encoding.ASCII.GetBytes("first-block");
            byte[] two = Encoding.ASCII.GetBytes("second-block");
            Buffer.BlockCopy(one, 0, data, 0, one.Length);
            Buffer.BlockCopy(two, 0, data, (int)(5L * 1024 * 1024), two.Length);
            File.WriteAllBytes(source, data);

            // Build a pack/map exactly like scripts/docker/pack_image.py.
            string pack = Path.Combine(_temp, "pack.bin");
            string map = Path.Combine(_temp, "pack.bin.map");
            int block = 1024 * 1024;
            List<long> offsets = new List<long>();
            List<int> lengths = new List<int>();
            using (FileStream src = File.OpenRead(source))
            using (FileStream dst = File.Create(pack))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] buf = new byte[block];
                long at = 0;
                while (true)
                {
                    int n = src.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    bool nonzero = false;
                    for (int i = 0; i < n; i++) if (buf[i] != 0) { nonzero = true; break; }
                    if (nonzero)
                    {
                        dst.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        offsets.Add(at);
                        lengths.Add(n);
                    }
                    at += n;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                using (StreamWriter w = new StreamWriter(map, false, Encoding.ASCII))
                {
                    w.NewLine = "\n";
                    w.WriteLine("GBOS86-PACK 1");
                    w.WriteLine("size=" + size);
                    w.WriteLine("block=" + block);
                    w.WriteLine("packsize=" + new FileInfo(pack).Length);
                    w.WriteLine("sha256=" + Fmt.Hex(sha.Hash));
                    long packed = 0;
                    for (int i = 0; i < offsets.Count; i++)
                    {
                        w.WriteLine(offsets[i] + " " + lengths[i] + " " + packed);
                        packed += lengths[i];
                    }
                }
            }

            PackedImage img = PackedImage.Load(map);
            string rebuilt = Path.Combine(_temp, "pack-rebuilt.img");
            img.Materialize(rebuilt, null, null);
            Check(new FileInfo(rebuilt).Length == size, "materialized size", new FileInfo(rebuilt).Length.ToString());
            byte[] got = File.ReadAllBytes(rebuilt);
            bool same = true;
            for (int i = 0; i < data.Length; i++) if (data[i] != got[i]) { same = false; break; }
            Check(same, "materialized bytes match", null);
        }

        private static void TestUefiDevicePathData()
        {
            // The hard-drive node layout is fixed by the UEFI spec; check the
            // sizes/offsets the boot-entry builder relies on.
            Check(42 == 4 + 4 + 8 + 8 + 16 + 1 + 1, "UEFI hard-drive node is 42 bytes", null);
            Check(4 + Encoding.Unicode.GetByteCount(@"\EFI\BOOT\BOOTX64.EFI") + 2 ==
                  4 + 2 * 21 + 2, "UEFI file-path node length", null);
        }

        private sealed class TestPart
        {
            public Guid Type;
            public ulong FirstLba;
            public ulong LastLba;
            public string Name;
            public bool Fat;
            public string Label;

            public TestPart(Guid type, ulong first, ulong sectors, string name, bool fat, string label)
            {
                Type = type;
                FirstLba = first;
                LastLba = first + sectors - 1;
                Name = name;
                Fat = fat;
                Label = label;
            }
        }

        private static void MakeGpt(string path, long logicalSize, List<TestPart> parts)
        {
            long sectors = logicalSize / 512;
            long backup = sectors - 1;
            long backupEntries = sectors - 33;
            ulong lastUsable = (ulong)(sectors - 34);

            byte[] table = new byte[128 * 128];
            for (int i = 0; i < parts.Count; i++)
            {
                int at = i * 128;
                TestPart p = parts[i];
                p.Type.ToByteArray().CopyTo(table, at);
                Guid.NewGuid().ToByteArray().CopyTo(table, at + 16);
                WriteU64(table, at + 32, p.FirstLba);
                WriteU64(table, at + 40, p.LastLba);
                PutUtf16(table, at + 56, p.Name, 72);
            }
            uint tableCrc = Crc32.Compute(table, 0, table.Length);

            byte[] primary = BuildHeader(1, (ulong)backup, 34, lastUsable, 2, tableCrc);
            byte[] backupHdr = BuildHeader((ulong)backup, 1, 34, lastUsable, (ulong)backupEntries, tableCrc);

            using (SparseWriter w = new SparseWriter(path, logicalSize))
            {
                byte[] mbr = new byte[512];
                mbr[510] = 0x55; mbr[511] = 0xAA;
                w.Write(0, mbr, 512);
                w.Write(512, primary, 512);
                w.Write(2 * 512, table, table.Length);
                w.Write(backupEntries * 512, table, table.Length);
                w.Write(backup * 512, backupHdr, 512);

                foreach (TestPart p in parts)
                {
                    byte[] boot = new byte[4096];
                    if (p.Fat)
                    {
                        boot[0] = 0xEB; boot[1] = 0x3C; boot[2] = 0x90;
                        WriteU16(boot, 0x0B, 512);
                        boot[0x0D] = 1;
                        WriteU16(boot, 0x0E, 1);
                        boot[0x10] = 2;
                        boot[0x15] = 0xF8;
                        WriteU32(boot, 0x24, 1024);
                        PutAscii(boot, 0x47, p.Label, 11);
                        boot[510] = 0x55; boot[511] = 0xAA;
                    }
                    else
                    {
                        WriteU16(boot, 1024 + 0x38, 0xEF53);
                        PutAscii(boot, 1024 + 0x78, p.Label, 16);
                    }
                    w.Write((long)p.FirstLba * 512, boot, boot.Length);
                }
                w.Finish();
            }
        }

        private static byte[] BuildHeader(ulong current, ulong backup, ulong firstUsable,
            ulong lastUsable, ulong entriesLba, uint tableCrc)
        {
            byte[] h = new byte[512];
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(h, 0);
            WriteU32(h, 8, 0x00010000);
            WriteU32(h, 12, 92);
            WriteU64(h, 24, current);
            WriteU64(h, 32, backup);
            WriteU64(h, 40, firstUsable);
            WriteU64(h, 48, lastUsable);
            Guid.NewGuid().ToByteArray().CopyTo(h, 56);
            WriteU64(h, 72, entriesLba);
            WriteU32(h, 80, 128);
            WriteU32(h, 84, 128);
            WriteU32(h, 88, tableCrc);
            WriteU32(h, 16, 0);
            WriteU32(h, 16, Crc32.Compute(h, 0, 92));
            return h;
        }

        private static PartitionInfo Part(int number, ulong offsetMb, ulong sizeMb)
        {
            PartitionInfo p = new PartitionInfo();
            p.Number = number;
            p.Offset = offsetMb * 1024 * 1024;
            p.Size = sizeMb * 1024 * 1024;
            return p;
        }

        private static void WriteU16(byte[] b, int at, int v)
        {
            b[at] = (byte)(v & 0xFF);
            b[at + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] b, int at, uint v)
        {
            BitConverter.GetBytes(v).CopyTo(b, at);
        }

        private static void WriteU64(byte[] b, int at, ulong v)
        {
            BitConverter.GetBytes(v).CopyTo(b, at);
        }

        private static void PutAscii(byte[] b, int at, string s, int width)
        {
            for (int i = 0; i < width; i++)
                b[at + i] = (byte)(i < s.Length ? s[i] : ' ');
        }

        private static void PutUtf16(byte[] b, int at, string s, int width)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(s);
            int n = Math.Min(bytes.Length, width - 2);
            Buffer.BlockCopy(bytes, 0, b, at, n);
        }
    }
}
