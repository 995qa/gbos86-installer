// Win32 interop used by the disk/image/boot-entry code. Kept in one place so the
// rest of the app never has to guess a P/Invoke signature.
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GbOs86
{
    internal static class Native
    {
        // CreateFile / disk handles
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint FILE_SHARE_DELETE = 0x00000004;
        public const uint OPEN_EXISTING = 3;
        public const uint CREATE_NEW = 1;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

        // File-system control codes (winioctl.h)
        public const uint FSCTL_SET_SPARSE = 0x000900C4;
        public const uint FSCTL_QUERY_ALLOCATED_RANGES = 0x000940CF;
        public const uint FSCTL_LOCK_VOLUME = 0x00090018;
        public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
        public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
        public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

        // Win32 errors
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_INVALID_FUNCTION = 1;
        public const int ERROR_NOT_SUPPORTED = 50;
        public const int ERROR_INVALID_PARAMETER = 87;
        public const int ERROR_INSUFFICIENT_BUFFER = 122;
        public const int ERROR_ENVVAR_NOT_FOUND = 203;
        public const int ERROR_HANDLE_EOF = 38;
        public const int ERROR_NOT_ALL_ASSIGNED = 1300;
        public const int ERROR_ALREADY_EXISTS = 183;

        // Firmware environment (UEFI variables)
        public const string EfiGlobalVariable = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";
        public const uint EFI_VARIABLE_NON_VOLATILE = 0x00000001;
        public const uint EFI_VARIABLE_BOOTSERVICE_ACCESS = 0x00000002;
        public const uint EFI_VARIABLE_RUNTIME_ACCESS = 0x00000004;
        public const uint EFI_VARIABLE_ATTRIBUTES = 7;

        public const uint LOAD_OPTION_ACTIVE = 0x00000001;

        // Firmware types
        public const uint FirmwareTypeUnknown = 0;
        public const uint FirmwareTypeBios = 1;
        public const uint FirmwareTypeUefi = 2;

        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_ALLOCATED_RANGE_BUFFER
        {
            public long FileOffset;
            public long Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode, ref FILE_ALLOCATED_RANGE_BUFFER lpInBuffer,
            uint nInBufferSize, [Out] byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFilePointerEx(
            SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(
            SafeFileHandle hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(
            SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlushFileBuffers(SafeFileHandle hFile);

        [DllImport("kernel32.dll", SetLastError = false)]
        public static extern void SetLastError(uint dwErrCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFirmwareType(out uint firmwareType);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFirmwareEnvironmentVariableExW")]
        public static extern uint GetFirmwareEnvironmentVariableEx(
            string lpName, string lpGuid, [Out] byte[] pBuffer, uint nSize, out uint pdwAttributes);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetFirmwareEnvironmentVariableExW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFirmwareEnvironmentVariableEx(
            string lpName, string lpGuid, byte[] pBuffer, uint nSize, uint dwAttributes);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetFirmwareEnvironmentVariableExW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFirmwareEnvironmentVariableExPtr(
            string lpName, string lpGuid, IntPtr pBuffer, uint nSize, uint dwAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(
            IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupPrivilegeValue(
            string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        public static void EnablePrivilege(string name)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Privileges.Luid = luid;
                tp.Privileges.Attributes = SE_PRIVILEGE_ENABLED;

                SetLastError(0);
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                // AdjustTokenPrivileges returns TRUE even when it only adjusted some
                // privileges; the error is the reliable signal.
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_NOT_ALL_ASSIGNED)
                    throw new Win32Exception(err, "The current account does not hold the " + name + " privilege.");
                if (err == ERROR_ACCESS_DENIED)
                    throw new Win32Exception(err, "This operation requires administrator rights.");
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        public static uint GetFirmwareTypeOrUnknown()
        {
            uint type;
            if (GetFirmwareType(out type)) return type;
            return FirmwareTypeUnknown;
        }

        public static bool IsUefi()
        {
            return GetFirmwareTypeOrUnknown() == FirmwareTypeUefi;
        }

        public static byte[] GetEfiVariable(string name)
        {
            uint attributes;
            SetLastError(0);
            uint size = GetFirmwareEnvironmentVariableEx(name, EfiGlobalVariable, null, 0, out attributes);
            int err = Marshal.GetLastWin32Error();
            if (size == 0)
            {
                if (err == ERROR_ENVVAR_NOT_FOUND) return null;
                // A few firmware implementations do not answer a zero-size query.
                // Probe with a generous fixed buffer (boot entries are tiny).
                byte[] probe = new byte[65536];
                uint got = GetFirmwareEnvironmentVariableEx(name, EfiGlobalVariable, probe,
                    (uint)probe.Length, out attributes);
                if (got == 0)
                {
                    int e = Marshal.GetLastWin32Error();
                    if (e == ERROR_ENVVAR_NOT_FOUND) return null;
                    throw new Win32Exception(e, "Cannot read UEFI variable " + name + ".");
                }
                byte[] result = new byte[got];
                Buffer.BlockCopy(probe, 0, result, 0, (int)got);
                return result;
            }

            byte[] buffer = new byte[size];
            uint read = GetFirmwareEnvironmentVariableEx(name, EfiGlobalVariable, buffer, size, out attributes);
            if (read == 0)
            {
                int e = Marshal.GetLastWin32Error();
                if (e == ERROR_ENVVAR_NOT_FOUND) return null;
                throw new Win32Exception(e, "Cannot read UEFI variable " + name + ".");
            }
            if (read < size)
            {
                byte[] trimmed = new byte[read];
                Buffer.BlockCopy(buffer, 0, trimmed, 0, (int)read);
                return trimmed;
            }
            return buffer;
        }

        public static bool SetEfiVariable(string name, byte[] data)
        {
            bool ok = SetFirmwareEnvironmentVariableEx(name, EfiGlobalVariable, data,
                data == null ? 0u : (uint)data.Length, EFI_VARIABLE_ATTRIBUTES);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot write UEFI variable " + name + ".");
            return true;
        }

        public static bool DeleteEfiVariable(string name)
        {
            // Passing a zero-length buffer deletes a non-volatile variable.
            return SetFirmwareEnvironmentVariableEx(name, EfiGlobalVariable, new byte[0], 0, EFI_VARIABLE_ATTRIBUTES);
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity identity =
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal principal =
                        new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static string Win32Message(int code)
        {
            try { return new Win32Exception(code).Message; }
            catch { return "Win32 error " + code; }
        }
    }
}
