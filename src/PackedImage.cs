// Rebuilds a sparse image on NTFS from the non-zero blocks exported by the
// Docker build. Keeps the 72 GB looper image from becoming a 72 GB write
// through Docker Desktop's file sharing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace GbOs86
{
    internal sealed class PackedBlock
    {
        public long Offset;
        public int Length;
        public long PackedOffset;
    }

    internal sealed class PackedImage
    {
        public string PackPath { get; private set; }
        public string MapPath { get; private set; }
        public long LogicalSize { get; private set; }
        public int BlockSize { get; private set; }
        public long PackedSize { get; private set; }
        public string Sha256 { get; private set; }
        public List<PackedBlock> Blocks { get; private set; }

        private PackedImage()
        {
            Blocks = new List<PackedBlock>();
        }

        public static PackedImage Load(string mapPath)
        {
            if (!File.Exists(mapPath))
                throw new FileNotFoundException("The image map is missing: " + mapPath);
            PackedImage img = new PackedImage();
            img.MapPath = Path.GetFullPath(mapPath);
            img.PackPath = mapPath.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
                ? mapPath.Substring(0, mapPath.Length - 4)
                : mapPath + ".pack";

            string[] lines = File.ReadAllLines(mapPath);
            if (lines.Length < 2 || lines[0].Trim() != "GBOS86-PACK 1")
                throw new InvalidDataException("The image map is not a gbOS86 pack map.");

            bool entriesStarted = false;
            long expectedPacked = 0;
            long lastBlockEnd = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (!entriesStarted)
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        string key = line.Substring(0, eq);
                        string value = line.Substring(eq + 1);
                        if (key == "size") img.LogicalSize = ParseLong(value);
                        else if (key == "block") img.BlockSize = (int)ParseLong(value);
                        else if (key == "packsize") img.PackedSize = ParseLong(value);
                        else if (key == "sha256") img.Sha256 = value.Trim();
                        continue;
                    }
                    entriesStarted = true;
                }

                string[] f = line.Split(' ');
                if (f.Length != 3)
                    throw new InvalidDataException("Malformed block entry in " + mapPath + ": " + line);
                PackedBlock b = new PackedBlock();
                b.Offset = ParseLong(f[0]);
                b.Length = (int)ParseLong(f[1]);
                b.PackedOffset = ParseLong(f[2]);
                if (b.Offset < 0 || b.Length <= 0 || b.PackedOffset != expectedPacked)
                    throw new InvalidDataException("Invalid block entry in " + mapPath + ": " + line);
                if (b.Offset + b.Length > img.LogicalSize)
                    throw new InvalidDataException("Block entry lies outside the image: " + line);
                if (b.Offset < lastBlockEnd)
                    throw new InvalidDataException("Block entries overlap or are out of order: " + line);
                lastBlockEnd = b.Offset + b.Length;
                expectedPacked += b.Length;
                img.Blocks.Add(b);
            }

            if (img.LogicalSize <= 0 || img.BlockSize <= 0)
                throw new InvalidDataException("The image map is incomplete.");
            if (img.PackedSize != expectedPacked)
                throw new InvalidDataException("The image map's packed size does not match its blocks.");
            if (img.Sha256 == null || img.Sha256.Length != 64)
                throw new InvalidDataException("The image map has no SHA-256.");
            if (!File.Exists(img.PackPath))
                throw new FileNotFoundException("The packed image data is missing: " + img.PackPath);
            if (new FileInfo(img.PackPath).Length != img.PackedSize)
                throw new InvalidDataException("The packed image is truncated.");
            return img;
        }

        public void Materialize(string targetPath, Action<long, long> progress, Func<bool> cancelled)
        {
            string part = targetPath + ".part";
            try
            {
                using (FileStream input = new FileStream(PackPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                using (SparseWriter writer = new SparseWriter(part, LogicalSize))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] buffer = new byte[4 << 20];
                    long done = 0;
                    foreach (PackedBlock b in Blocks)
                    {
                        if (cancelled != null && cancelled()) throw new OperationCanceledException();
                        input.Seek(b.PackedOffset, SeekOrigin.Begin);
                        int left = b.Length;
                        long at = b.Offset;
                        while (left > 0)
                        {
                            int n = Math.Min(buffer.Length, left);
                            int got = 0;
                            while (got < n)
                            {
                                int r = input.Read(buffer, got, n - got);
                                if (r <= 0) throw new EndOfStreamException("The packed image ended early.");
                                got += r;
                            }
                            sha.TransformBlock(buffer, 0, n, null, 0);
                            writer.Write(at, buffer, n);
                            at += n;
                            left -= n;
                        }
                        done += b.Length;
                        if (progress != null) progress(done, PackedSize);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    string actual = Fmt.Hex(sha.Hash);
                    if (!string.Equals(actual, Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The packed image failed its SHA-256 check.");
                    writer.Finish();
                }

                if (File.Exists(targetPath))
                {
                    try { File.SetAttributes(targetPath, FileAttributes.Normal); } catch { }
                    File.Delete(targetPath);
                }
                File.Move(part, targetPath);
                long allocated = 0;
                foreach (PackedBlock b in Blocks) allocated += b.Length;
                Log.Write("Rebuilt sparse image " + targetPath + " (" + Fmt.Bytes((ulong)LogicalSize) +
                          " logical, " + Fmt.Bytes((ulong)allocated) + " allocated).");
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                throw;
            }
        }

        private static long ParseLong(string s)
        {
            long v;
            if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                throw new InvalidDataException("Invalid number in image map: " + s);
            return v;
        }
    }
}
