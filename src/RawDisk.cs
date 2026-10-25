// Raw physical-disk / volume access. Used to read filesystem labels and to
// flash partition payloads without going through a mounted volume.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GbOs86
{
    internal sealed class RawDisk : IDisposable
    {
        private SafeFileHandle _handle;
        private readonly bool _writable;

        public int Number { get; private set; }

        public RawDisk(int number, bool writable)
        {
            Number = number;
            _writable = writable;
            uint access = Native.GENERIC_READ | (writable ? Native.GENERIC_WRITE : 0);
            uint flags = writable ? Native.FILE_FLAG_WRITE_THROUGH : 0;
            _handle = Native.CreateFile(@"\\.\PhysicalDrive" + number, access,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
                IntPtr.Zero, Native.OPEN_EXISTING, flags, IntPtr.Zero);
            if (_handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Cannot open physical disk " + number + ". Run the installer as administrator.");
        }

        public void ReadAt(long offset, byte[] buffer, int count)
        {
            Seek(offset);
            int done = 0;
            while (done < count)
            {
                uint got;
                if (!Native.ReadFile(_handle, Slice(buffer, done, count - done), (uint)(count - done), out got, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Read failed at offset " + (offset + done) + ".");
                if (got == 0) throw new EndOfStreamException("Unexpected end of disk at offset " + (offset + done) + ".");
                done += (int)got;
            }
        }

        public void WriteAt(long offset, byte[] buffer, int count)
        {
            if (!_writable) throw new InvalidOperationException("Disk handle was opened read-only.");
            Seek(offset);
            int done = 0;
            while (done < count)
            {
                uint wrote;
                if (!Native.WriteFile(_handle, Slice(buffer, done, count - done), (uint)(count - done), out wrote, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Write failed at offset " + (offset + done) + ".");
                if (wrote == 0) throw new System.IO.IOException("The disk accepted zero bytes at offset " + (offset + done) + ".");
                done += (int)wrote;
            }
        }

        public void WriteZerosAt(long offset, long count)
        {
            byte[] zero = new byte[4 << 20];
            long left = count;
            long pos = offset;
            while (left > 0)
            {
                int n = (int)Math.Min((long)zero.Length, left);
                WriteAt(pos, zero, n);
                pos += n;
                left -= n;
            }
        }

        public void Flush()
        {
            if (!Native.FlushFileBuffers(_handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The disk did not flush its write cache.");
        }

        public void UpdateProperties()
        {
            uint returned;
            Native.DeviceIoControl(_handle, Native.IOCTL_DISK_UPDATE_PROPERTIES,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
        }

        private void Seek(long offset)
        {
            long newPos;
            if (!Native.SetFilePointerEx(_handle, offset, out newPos, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot seek to offset " + offset + ".");
        }

        private static byte[] Slice(byte[] buffer, int offset, int count)
        {
            if (offset == 0 && count == buffer.Length) return buffer;
            byte[] temp = new byte[count];
            Buffer.BlockCopy(buffer, offset, temp, 0, count);
            return temp;
        }

        public void Dispose()
        {
            if (_handle != null)
            {
                try { _handle.Dispose(); } catch { }
                _handle = null;
            }
        }

        public static void LockAndDismountVolume(string accessPath)
        {
            if (string.IsNullOrEmpty(accessPath)) return;
            try
            {
                string path = accessPath;
                if (path.EndsWith("\\") && path.StartsWith(@"\\?\"))
                    path = path.Substring(0, path.Length - 1);
                // A drive letter must be opened as a volume, not as the
                // current directory on that drive.
                if (path.Length == 2 && path[1] == ':' &&
                    ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')))
                    path = @"\\.\" + path;
                SafeFileHandle h = Native.CreateFile(path,
                    Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return;
                try
                {
                    uint returned;
                    Native.DeviceIoControl(h, Native.FSCTL_LOCK_VOLUME,
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
                    Native.DeviceIoControl(h, Native.FSCTL_DISMOUNT_VOLUME,
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
                }
                finally { h.Dispose(); }
            }
            catch { }
        }
    }
}
