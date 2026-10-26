// Raw physical-disk / volume access. Used to read filesystem labels and to
// flash partition payloads without going through a mounted volume.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GbOs86
{
    /// <summary>
    /// An open, locked volume handle. Windows must not mount a filesystem on a
    /// volume while this is alive: a volume that becomes mounted (recognised
    /// filesystem) rejects raw writes to its sectors with ERROR_ACCESS_DENIED.
    /// Keep the lock until the raw writes are finished, then dispose it.
    /// </summary>
    internal sealed class VolumeLock : IDisposable
    {
        private SafeFileHandle _handle;

        public string Path { get; private set; }

        internal VolumeLock(SafeFileHandle handle, string path)
        {
            _handle = handle;
            Path = path;
        }

        public void Dispose()
        {
            if (_handle != null)
            {
                try { _handle.Dispose(); } catch { }
                _handle = null;
            }
        }
    }

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

        /// <summary>
        /// Test hook: wrap an already open seekable handle so the read/write
        /// paths can be exercised against a file instead of a physical disk.
        /// </summary>
        internal RawDisk(SafeFileHandle handle, bool writable)
        {
            Number = -1;
            _handle = handle;
            _writable = writable;
        }

        public void ReadAt(long offset, byte[] buffer, int count)
        {
            if (count <= 0) return;
            if (count == buffer.Length)
            {
                ReadRaw(offset, buffer, count);
                return;
            }
            // ReadFile always fills an array from index 0, so a short read needs
            // its own buffer. The bytes must be copied back afterwards: filling
            // a copy and leaving `buffer` untouched makes every short read look
            // like the disk returned whatever the buffer held before (this broke
            // verification of a partition's final, partial block).
            byte[] temp = new byte[count];
            ReadRaw(offset, temp, count);
            Buffer.BlockCopy(temp, 0, buffer, 0, count);
        }

        private void ReadRaw(long offset, byte[] buffer, int count)
        {
            Seek(offset);
            int done = 0;
            while (done < count)
            {
                int left = count - done;
                byte[] target = done == 0 ? buffer : new byte[left];
                uint got;
                if (!Native.ReadFile(_handle, target, (uint)left, out got, IntPtr.Zero))
                {
                    int code = Marshal.GetLastWin32Error();
                    throw new Win32Exception(code, "Read failed at offset " + (offset + done) + Win32(code));
                }
                if (got == 0) throw new EndOfStreamException("Unexpected end of disk at offset " + (offset + done) + ".");
                if (!ReferenceEquals(target, buffer))
                    Buffer.BlockCopy(target, 0, buffer, done, (int)got);
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
                {
                    int code = Marshal.GetLastWin32Error();
                    throw new Win32Exception(code, "Write failed at offset " + (offset + done) + Win32(code));
                }
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
            {
                int code = Marshal.GetLastWin32Error();
                throw new Win32Exception(code, "The disk did not flush its write cache" + Win32(code));
            }
        }

        /// <summary>" (error 5: Access is denied.)" for log and dialog text.</summary>
        private static string Win32(int code)
        {
            string text = "";
            try { text = new Win32Exception(code).Message; } catch (Exception) { }
            return text.Length > 0 ? " (error " + code + ": " + text + ")" : " (error " + code + ")";
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
            // For writes only: WriteFile cannot write from the middle of an
            // array, so the block is copied out first. Reads must not use this
            // (the copy would swallow the data); see ReadAt.
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

        /// <summary>
        /// Opens a volume, locks it and dismounts its filesystem. The returned
        /// handle keeps the lock: while it is open Windows cannot mount a
        /// filesystem on the volume, so raw writes to those sectors stay legal.
        /// Returns null when the volume could not be opened or locked (the
        /// install can still proceed; the write then relies on the volume not
        /// being mounted).
        /// </summary>
        public static VolumeLock LockAndDismountVolume(string accessPath)
        {
            if (string.IsNullOrEmpty(accessPath)) return null;
            string path = NormalizeVolumePath(accessPath);

            SafeFileHandle h = Native.CreateFile(path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int openError = Marshal.GetLastWin32Error();
                Log.Write("Could not open " + path + " to lock it" + Win32(openError) + ".");
                return null;
            }

            uint returned;
            if (!Native.DeviceIoControl(h, Native.FSCTL_LOCK_VOLUME,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero))
            {
                int lockError = Marshal.GetLastWin32Error();
                Log.Write("Could not lock " + path + Win32(lockError) +
                          ". Windows may mount the new volume while it is being written; " +
                          "close Explorer and Disk Management and retry.");
                h.Dispose();
                return null;
            }
            if (!Native.DeviceIoControl(h, Native.FSCTL_DISMOUNT_VOLUME,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                Log.Write("Could not dismount " + path + Win32(Marshal.GetLastWin32Error()) +
                          "; the volume is locked anyway.");

            Log.Write("Locked " + path + ".");
            return new VolumeLock(h, path);
        }

        /// <summary>
        /// Turns a volume path from the Storage module into one CreateFile
        /// accepts. A drive letter must be opened as a volume, not as the
        /// current directory on that drive; volume GUID paths lose the trailing
        /// backslash so they can be compared.
        /// </summary>
        public static string NormalizeVolumePath(string accessPath)
        {
            if (string.IsNullOrEmpty(accessPath)) return "";
            string path = accessPath;
            if (path.EndsWith("\\") && path.StartsWith(@"\\?\"))
                path = path.Substring(0, path.Length - 1);
            if (path.Length == 2 && path[1] == ':' &&
                ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')))
                path = @"\\.\" + path;
            return path;
        }
    }
}
