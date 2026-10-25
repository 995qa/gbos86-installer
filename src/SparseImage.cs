// Sparse-file helpers. The loopback image is mostly holes (a 64 GB userdata
// partition only has ~150 MB of real ext4 metadata), so the installer transfers
// and writes only allocated ranges. That keeps flashing a few GB instead of 70.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GbOs86
{
    internal sealed class Extent
    {
        public long Offset;
        public long Length;
        public long End { get { return Offset + Length; } }

        public Extent(long offset, long length)
        {
            Offset = offset;
            Length = length;
        }

        public override string ToString()
        {
            return Offset + "+" + Length;
        }
    }

    internal static class SparseImage
    {
        private const int QueryBuffer = 64 * 1024;

        public static List<Extent> QueryAllocatedRanges(string path, long start, long end, out bool supported)
        {
            supported = false;
            List<Extent> result = new List<Extent>();
            if (end <= start) return result;

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.RandomAccess))
            {
                long pos = start;
                while (pos < end)
                {
                    Native.FILE_ALLOCATED_RANGE_BUFFER input =
                        new Native.FILE_ALLOCATED_RANGE_BUFFER();
                    input.FileOffset = pos;
                    input.Length = end - pos;
                    byte[] outBuf = new byte[QueryBuffer];
                    uint returned;
                    bool ok;
                    try
                    {
                        ok = Native.DeviceIoControl(fs.SafeFileHandle, Native.FSCTL_QUERY_ALLOCATED_RANGES,
                            ref input, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.FILE_ALLOCATED_RANGE_BUFFER)),
                            outBuf, (uint)outBuf.Length, out returned, IntPtr.Zero);
                    }
                    catch (EntryPointNotFoundException) { return null; }

                    if (!ok)
                    {
                        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        if (err == Native.ERROR_INVALID_FUNCTION || err == Native.ERROR_NOT_SUPPORTED ||
                            err == Native.ERROR_INVALID_PARAMETER)
                            return null;
                        if (err == Native.ERROR_ACCESS_DENIED)
                            return null;
                        // No allocated ranges in the requested region. This is
                        // success with an empty result, not a failure.
                        if (err == Native.ERROR_HANDLE_EOF)
                        {
                            supported = true;
                            break;
                        }
                        throw new Win32Exception(err, "Cannot query the allocated ranges of " + path + ".");
                    }

                    supported = true;
                    int count = (int)(returned / 16);
                    if (count == 0) break;
                    long lastEnd = pos;
                    for (int i = 0; i < count; i++)
                    {
                        long off = BitConverter.ToInt64(outBuf, i * 16);
                        long len = BitConverter.ToInt64(outBuf, i * 16 + 8);
                        if (len <= 0) continue;
                        long a = Math.Max(off, start);
                        long b = Math.Min(off + len, end);
                        if (b <= a) continue;
                        result.Add(new Extent(a, b - a));
                        if (off + len > lastEnd) lastEnd = off + len;
                    }
                    if (lastEnd <= pos) break;
                    pos = lastEnd;
                    if (pos >= end) break;
                }
            }
            return Coalesce(result);
        }

        public static List<Extent> ScanNonZeroRanges(string path, long start, long end, int blockSize)
        {
            List<Extent> result = new List<Extent>();
            if (blockSize < 4096) blockSize = 4096;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                blockSize, FileOptions.SequentialScan))
            {
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[blockSize];
                long pos = start;
                bool inData = false;
                long dataStart = 0;
                bool allZero = true;
                for (int i = 0; i < buf.Length; i++) buf[i] = 0;

                while (pos < end)
                {
                    int want = (int)Math.Min((long)blockSize, end - pos);
                    int got = 0;
                    while (got < want)
                    {
                        int n = fs.Read(buf, got, want - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got <= 0) break;

                    allZero = true;
                    for (int i = 0; i < got; i++)
                    {
                        if (buf[i] != 0) { allZero = false; break; }
                    }

                    if (!allZero)
                    {
                        if (!inData) { inData = true; dataStart = pos; }
                    }
                    else if (inData)
                    {
                        result.Add(new Extent(dataStart, pos - dataStart));
                        inData = false;
                    }
                    pos += got;
                }
                if (inData) result.Add(new Extent(dataStart, pos - dataStart));
            }
            return Coalesce(result);
        }

        public static List<Extent> Coalesce(List<Extent> input)
        {
            List<Extent> sorted = new List<Extent>(input);
            sorted.Sort(delegate(Extent a, Extent b) { return a.Offset.CompareTo(b.Offset); });
            List<Extent> result = new List<Extent>();
            foreach (Extent e in sorted)
            {
                if (e.Length <= 0) continue;
                if (result.Count > 0)
                {
                    Extent last = result[result.Count - 1];
                    if (e.Offset <= last.End)
                    {
                        long newEnd = Math.Max(last.End, e.End);
                        last.Length = newEnd - last.Offset;
                        continue;
                    }
                }
                result.Add(new Extent(e.Offset, e.Length));
            }
            return result;
        }

        public static List<Extent> Intersect(List<Extent> extents, long start, long end)
        {
            List<Extent> result = new List<Extent>();
            foreach (Extent e in extents)
            {
                long a = Math.Max(e.Offset, start);
                long b = Math.Min(e.End, end);
                if (b > a) result.Add(new Extent(a, b - a));
            }
            return result;
        }
    }

    internal sealed class SparseWriter : IDisposable
    {
        private FileStream _fs;
        private byte[] _copy = new byte[4 << 20];
        public string Path { get; private set; }
        public long LogicalSize { get; private set; }
        public bool IsSparse { get; private set; }

        public SparseWriter(string path, long logicalSize)
        {
            Path = path;
            LogicalSize = logicalSize;
            _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                1 << 20, FileOptions.RandomAccess);
            uint returned;
            IsSparse = Native.DeviceIoControl(_fs.SafeFileHandle, Native.FSCTL_SET_SPARSE,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
            _fs.SetLength(logicalSize);
        }

        public void Write(long offset, byte[] buffer, int count)
        {
            _fs.Seek(offset, SeekOrigin.Begin);
            _fs.Write(buffer, 0, count);
        }

        public void WriteZeros(long offset, long count)
        {
            byte[] zero = new byte[4 << 20];
            _fs.Seek(offset, SeekOrigin.Begin);
            long left = count;
            while (left > 0)
            {
                int n = (int)Math.Min((long)zero.Length, left);
                _fs.Write(zero, 0, n);
                left -= n;
            }
        }

        public void Finish()
        {
            if (_fs == null) return;
            _fs.Flush(true);
            _fs.Dispose();
            _fs = null;
        }

        public void Dispose()
        {
            if (_fs != null)
            {
                try { _fs.Dispose(); } catch { }
                _fs = null;
            }
        }
    }

    internal static class ExtentMap
    {
        private const string Magic = "GBOS86-EXTENTS 1";

        public static void Save(string path, long fileSize, DateTime imageWriteTimeUtc, List<Extent> extents)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Magic);
            sb.AppendLine("size=" + fileSize);
            // The timestamp makes the sidecar invalid when the image is
            // replaced, even if the replacement has the same size.
            sb.AppendLine("mtime=" + imageWriteTimeUtc.Ticks);
            foreach (Extent e in extents)
                sb.AppendLine(e.Offset + " " + e.Length);

            // Write-and-rename so a concurrent read never sees a half-written map.
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, sb.ToString(), Encoding.ASCII);
                if (File.Exists(path))
                {
                    try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
                    File.Delete(path);
                }
                File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        public static List<Extent> Load(string path, long expectedFileSize, DateTime expectedWriteTimeUtc)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 3 || lines[0].Trim() != Magic) return null;

                long size = -1;
                long mtime = -1;
                int first = -1;
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("size=", StringComparison.Ordinal))
                    {
                        if (!long.TryParse(line.Substring(5), out size)) return null;
                    }
                    else if (line.StartsWith("mtime=", StringComparison.Ordinal))
                    {
                        if (!long.TryParse(line.Substring(6), out mtime)) return null;
                    }
                    else
                    {
                        first = i;
                        break;
                    }
                }
                if (first < 0) return null;
                if (size != expectedFileSize || mtime != expectedWriteTimeUtc.Ticks) return null;

                List<Extent> list = new List<Extent>();
                for (int i = first; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    string[] parts = line.Split(' ');
                    if (parts.Length != 2) return null;
                    long o, l;
                    if (!long.TryParse(parts[0], out o) || !long.TryParse(parts[1], out l)) return null;
                    if (o < 0 || l <= 0 || o + l > expectedFileSize) return null;
                    list.Add(new Extent(o, l));
                }
                return SparseImage.Coalesce(list);
            }
            catch { return null; }
        }
    }
}
