// FAT32 / ext4 boot-sector sniffing. Used both on image files and on raw
// partitions (to recognise an existing gbOS86 install).
using System;

namespace GbOs86
{
    internal static class FsLabel
    {
        public static FileSystemKind Detect(byte[] buf, out string label)
        {
            label = "";
            if (buf == null || buf.Length < 512) return FileSystemKind.Unknown;

            if (buf[510] == 0x55 && buf[511] == 0xAA)
            {
                int bps = BitConverter.ToUInt16(buf, 0x0B);
                int fats = buf[0x10];
                int rootEntries = BitConverter.ToUInt16(buf, 0x11);
                int fatSize16 = BitConverter.ToUInt16(buf, 0x16);
                int fatSize32 = (int)BitConverter.ToUInt32(buf, 0x24);
                if ((bps == 512 || bps == 1024 || bps == 2048 || bps == 4096) &&
                    fats > 0 && (fatSize16 > 0 || fatSize32 > 0))
                {
                    // FAT32 is the only FAT variant gbOS86 uses: BPB_FATSz16
                    // must be zero and the 16-bit root-entry count unused.
                    if (fatSize16 != 0 || fatSize32 <= 0 || rootEntries != 0)
                        return FileSystemKind.Unknown;
                    label = ReadFatLabel(buf);
                    return FileSystemKind.Fat32;
                }
            }

            if (buf.Length >= 1024 + 0x100 && BitConverter.ToUInt16(buf, 1024 + 0x38) == 0xEF53)
            {
                label = ReadExt4Label(buf, 1024);
                return FileSystemKind.Ext4;
            }
            return FileSystemKind.Unknown;
        }

        private static string ReadFatLabel(byte[] boot)
        {
            char[] chars = new char[11];
            for (int i = 0; i < 11; i++)
            {
                byte b = boot[0x47 + i];
                chars[i] = (b >= 32 && b < 127) ? (char)b : ' ';
            }
            return new string(chars).Trim();
        }

        private static string ReadExt4Label(byte[] data, int superblock)
        {
            char[] chars = new char[16];
            for (int i = 0; i < 16; i++)
            {
                byte b = data[superblock + 0x78 + i];
                chars[i] = b == 0 ? ' ' : (b >= 32 && b < 127 ? (char)b : ' ');
            }
            return new string(chars).Trim();
        }
    }
}
