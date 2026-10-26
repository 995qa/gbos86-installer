// UEFI boot entry management. The installer no longer adds an entry: firmware
// boot menus list the new EFI partition on their own. What remains here is used
// to find and remove entries written by earlier versions of the installer.
// Add() (Boot####/BootOrder, with the bcdedit Windows Boot Manager fallback) is
// kept for reference and covered by the offline tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GbOs86
{
    internal sealed class GptEntryInfo
    {
        public int PartitionNumber;
        public ulong FirstLba;
        public ulong LastLba;
        public byte[] UniqueGuid;
        public Guid TypeGuid;
        public string Name = "";
    }

    internal sealed class UefiBootEntry
    {
        public int Index = -1;
        public string Description = "gbOS86";
        public string VariableName = "";
        public bool Native = true;
        public string BcdGuid = "";

        public override string ToString()
        {
            if (Native) return VariableName + " (" + Description + ")";
            return "BCD " + BcdGuid + " (" + Description + ")";
        }
    }

    internal static class UefiBoot
    {
        private const int MaxBootEntries = 0x100;
        private const string DefaultDescription = "gbOS86";
        private const string EfiBootPath = @"\EFI\BOOT\BOOTX64.EFI";

        public static UefiBootEntry Add(int diskNumber, PartitionInfo efiPartition, string description)
        {
            if (description == null || description.Trim().Length == 0) description = DefaultDescription;
            if (!Native.IsUefi())
                throw new InvalidOperationException("gbOS86 needs a UEFI system. This PC booted in legacy BIOS mode.");
            EnsureEfiPrivilege();

            GptEntryInfo gpt = ReadGptEntry(diskNumber, efiPartition.Offset, efiPartition.Size);
            if (gpt == null)
                throw new InvalidOperationException("Could not find the new EFI partition in the disk's GPT.");

            try
            {
                return AddNative(gpt, description);
            }
            catch (Exception ex)
            {
                Log.Write("UEFI variable write failed: " + ex.Message);
                Log.Write("Falling back to a Windows Boot Manager entry (bcdedit).");
                UefiBootEntry fallback = AddBcd(diskNumber, efiPartition, description);
                fallback.Description = description;
                return fallback;
            }
        }

        public static void Remove(UefiBootEntry entry)
        {
            if (entry == null) return;
            EnsureEfiPrivilege();
            if (entry.Native) RemoveNative(entry);
            else RemoveBcd(entry);
        }

        public static UefiBootEntry Find(string description)
        {
            if (!Native.IsUefi()) return null;
            EnsureEfiPrivilege();
            try
            {
                List<int> order = ReadBootOrder();
                if (order.Count > 0)
                {
                    foreach (int i in order)
                    {
                        byte[] data = Native.GetEfiVariable(BootName(i));
                        if (data == null) continue;
                        string desc;
                        if (TryGetDescription(data, out desc) &&
                            string.Equals(desc, description, StringComparison.OrdinalIgnoreCase))
                        {
                            UefiBootEntry e = new UefiBootEntry();
                            e.Index = i;
                            e.VariableName = BootName(i);
                            e.Description = desc;
                            e.Native = true;
                            return e;
                        }
                    }
                }
                for (int i = 0; i < 256; i++)
                {
                    if (order.Contains(i)) continue;
                    byte[] data = Native.GetEfiVariable(BootName(i));
                    if (data == null) continue;
                    string desc;
                    if (TryGetDescription(data, out desc) &&
                        string.Equals(desc, description, StringComparison.OrdinalIgnoreCase))
                    {
                        UefiBootEntry e = new UefiBootEntry();
                        e.Index = i;
                        e.VariableName = BootName(i);
                        e.Description = desc;
                        e.Native = true;
                        return e;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Cannot inspect UEFI boot entries: " + ex.Message);
            }
            return null;
        }

        private static void EnsureEfiPrivilege()
        {
            try { Native.EnablePrivilege("SeSystemEnvironmentPrivilege"); }
            catch (Exception ex) { Log.Write("Cannot enable SeSystemEnvironmentPrivilege: " + ex.Message); }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static UefiBootEntry AddNative(GptEntryInfo gpt, string description)
        {
            List<int> order = ReadBootOrder();

            // Reuse an existing gbOS86 entry so repeated installs do not fill NVRAM.
            int index = -1;
            UefiBootEntry previous = Find(description);
            if (previous != null) index = previous.Index;

            bool isNew = index < 0;
            if (isNew)
            {
                for (int i = 0; i < MaxBootEntries; i++)
                {
                    if (Native.GetEfiVariable(BootName(i)) == null) { index = i; break; }
                }
            }
            if (index < 0)
                throw new InvalidOperationException("The firmware has no free Boot#### slot.");

            byte[] loadOption = BuildLoadOption(description, gpt);
            string name = BootName(index);
            Native.SetEfiVariable(name, loadOption);

            List<int> newOrder = new List<int>(order);
            if (!newOrder.Contains(index)) newOrder.Add(index);
            try
            {
                WriteBootOrder(newOrder);
            }
            catch
            {
                if (isNew)
                {
                    try { Native.DeleteEfiVariable(name); } catch { }
                }
                throw;
            }

            byte[] check = Native.GetEfiVariable(name);
            if (!SameBytes(check, loadOption))
            {
                // Do not leave a broken entry behind if the firmware accepted
                // the variable but did not store it correctly.
                if (isNew)
                {
                    try { Native.DeleteEfiVariable(name); } catch { }
                    try
                    {
                        List<int> cleanup = ReadBootOrder();
                        if (cleanup.Remove(index)) WriteBootOrder(cleanup);
                    }
                    catch { }
                }
                throw new InvalidOperationException("The firmware did not keep the gbOS86 boot entry.");
            }

            UefiBootEntry entry = new UefiBootEntry();
            entry.Index = index;
            entry.VariableName = name;
            entry.Description = description;
            entry.Native = true;
            Log.Write("Added UEFI boot entry " + name + " -> " + EfiBootPath +
                      " on partition " + gpt.PartitionNumber + ".");
            return entry;
        }

        private static void RemoveNative(UefiBootEntry entry)
        {
            List<int> order = ReadBootOrder();
            if (order.Contains(entry.Index))
            {
                order.Remove(entry.Index);
                WriteBootOrder(order);
            }
            try { Native.DeleteEfiVariable(entry.VariableName); }
            catch (Exception ex) { Log.Write("Could not delete " + entry.VariableName + ": " + ex.Message); }
            Log.Write("Removed UEFI boot entry " + entry.VariableName + ".");
        }

        private static List<int> ReadBootOrder()
        {
            List<int> list = new List<int>();
            byte[] data = Native.GetEfiVariable("BootOrder");
            if (data == null) return list;
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                int v = data[i] | (data[i + 1] << 8);
                if (!list.Contains(v)) list.Add(v);
            }
            return list;
        }

        private static void WriteBootOrder(List<int> order)
        {
            if (order.Count == 0)
            {
                try { Native.DeleteEfiVariable("BootOrder"); } catch { }
                return;
            }
            byte[] data = new byte[order.Count * 2];
            for (int i = 0; i < order.Count; i++)
            {
                data[i * 2] = (byte)(order[i] & 0xFF);
                data[i * 2 + 1] = (byte)((order[i] >> 8) & 0xFF);
            }
            Native.SetEfiVariable("BootOrder", data);
        }

        private static string BootName(int index)
        {
            return "Boot" + index.ToString("X4", CultureInfo.InvariantCulture);
        }

        private static bool TryGetDescription(byte[] loadOption, out string description)
        {
            description = "";
            if (loadOption == null || loadOption.Length < 6) return false;
            int filePathLength = loadOption[4] | (loadOption[5] << 8);
            int max = loadOption.Length - 6 - filePathLength;
            if (max <= 0) return false;
            int end = 6;
            while (end + 1 < loadOption.Length && end < 6 + max)
            {
                if (loadOption[end] == 0 && loadOption[end + 1] == 0) break;
                end += 2;
            }
            description = Encoding.Unicode.GetString(loadOption, 6, end - 6);
            return true;
        }

        private static byte[] BuildLoadOption(string description, GptEntryInfo gpt)
        {
            byte[] filePath = BuildFilePathNode(EfiBootPath);
            byte[] hd = BuildHardDriveNode(gpt);
            byte[] end = new byte[] { 0x7F, 0xFF, 0x04, 0x00 };
            int pathLength = hd.Length + filePath.Length + end.Length;

            byte[] desc = Encoding.Unicode.GetBytes(description);
            int total = 4 + 2 + desc.Length + 2 + pathLength;
            byte[] data = new byte[total];
            int at = 0;
            BitConverter.GetBytes(Native.LOAD_OPTION_ACTIVE).CopyTo(data, at); at += 4;
            data[at] = (byte)(pathLength & 0xFF);
            data[at + 1] = (byte)((pathLength >> 8) & 0xFF);
            at += 2;
            Buffer.BlockCopy(desc, 0, data, at, desc.Length); at += desc.Length;
            data[at] = 0; data[at + 1] = 0; at += 2;
            Buffer.BlockCopy(hd, 0, data, at, hd.Length); at += hd.Length;
            Buffer.BlockCopy(filePath, 0, data, at, filePath.Length); at += filePath.Length;
            Buffer.BlockCopy(end, 0, data, at, end.Length);
            return data;
        }

        private static byte[] BuildHardDriveNode(GptEntryInfo gpt)
        {
            byte[] node = new byte[42];
            node[0] = 0x04; // media device path
            node[1] = 0x01; // hard drive
            node[2] = 42;
            node[3] = 0;
            BitConverter.GetBytes((uint)gpt.PartitionNumber).CopyTo(node, 4);
            BitConverter.GetBytes(gpt.FirstLba).CopyTo(node, 8);
            BitConverter.GetBytes(gpt.LastLba - gpt.FirstLba + 1).CopyTo(node, 16);
            Buffer.BlockCopy(gpt.UniqueGuid, 0, node, 24, 16);
            node[40] = 0; // MBR type (unused for GPT)
            node[41] = 2; // signature type: GPT
            return node;
        }

        private static byte[] BuildFilePathNode(string path)
        {
            byte[] chars = Encoding.Unicode.GetBytes(path);
            int length = 4 + chars.Length + 2;
            byte[] node = new byte[length];
            node[0] = 0x04;
            node[1] = 0x04; // file path
            node[2] = (byte)(length & 0xFF);
            node[3] = (byte)((length >> 8) & 0xFF);
            Buffer.BlockCopy(chars, 0, node, 4, chars.Length);
            return node;
        }

        private static GptEntryInfo ReadGptEntry(int diskNumber, ulong partitionOffset, ulong partitionSize)
        {
            using (RawDisk raw = new RawDisk(diskNumber, false))
            {
                byte[] header = new byte[512];
                raw.ReadAt(512, header, 512);
                if (Encoding.ASCII.GetString(header, 0, 8) != "EFI PART")
                    throw new InvalidOperationException("The selected disk does not have a GPT header.");

                ulong entriesLba = BitConverter.ToUInt64(header, 72);
                uint count = BitConverter.ToUInt32(header, 80);
                uint entrySize = BitConverter.ToUInt32(header, 84);
                if (count == 0 || count > 4096 || entrySize < 128 || entrySize > 4096)
                    throw new InvalidOperationException("The selected disk has an unsupported GPT.");

                int bytes = (int)(count * entrySize);
                byte[] table = new byte[bytes];
                raw.ReadAt((long)(entriesLba * 512UL), table, bytes);

                uint expected = BitConverter.ToUInt32(header, 88);
                if (Crc32.Compute(table, 0, table.Length) != expected)
                    throw new InvalidOperationException("The selected disk's GPT table is damaged.");

                ulong firstLba = partitionOffset / 512UL;
                ulong lastLba = (partitionOffset + partitionSize) / 512UL - 1;

                for (uint i = 0; i < count; i++)
                {
                    int at = (int)(i * entrySize);
                    bool used = false;
                    for (int b = 0; b < 16; b++) if (table[at + b] != 0) { used = true; break; }
                    if (!used) continue;
                    ulong first = BitConverter.ToUInt64(table, at + 32);
                    ulong last = BitConverter.ToUInt64(table, at + 40);
                    if (first != firstLba || last != lastLba) continue;

                    GptEntryInfo info = new GptEntryInfo();
                    info.PartitionNumber = (int)i + 1;
                    info.FirstLba = first;
                    info.LastLba = last;
                    info.UniqueGuid = new byte[16];
                    Buffer.BlockCopy(table, at + 16, info.UniqueGuid, 0, 16);
                    byte[] type = new byte[16];
                    Buffer.BlockCopy(table, at, type, 0, 16);
                    info.TypeGuid = new Guid(type);
                    string name = Encoding.Unicode.GetString(table, at + 56, 72);
                    int nul = name.IndexOf('\0');
                    if (nul >= 0) name = name.Substring(0, nul);
                    info.Name = name.Trim();
                    return info;
                }
            }
            return null;
        }

        private static UefiBootEntry AddBcd(int diskNumber, PartitionInfo efiPartition, string description)
        {
            string volumePath = PickVolumePath(efiPartition.AccessPaths);
            string letter = "";
            bool addedLetter = false;
            string guid = "";
            try
            {
                // A temporary drive letter is the most portable bcdedit partition=
                // form. Fall back to the volume GUID path if no letter is free.
                letter = AssignTemporaryLetter(diskNumber, efiPartition.Number);
                addedLetter = letter.Length > 0;
                if (!addedLetter && volumePath.Length == 0)
                    throw new InvalidOperationException("The EFI partition has no volume path.");

                string device = addedLetter ? letter + ":" : volumePath;

                ProcessResult copy = ProcessRunner.Run(PathBcdEdit(),
                    "/copy {bootmgr} /d " + ProcessRunner.QuoteArg(description), 60000);
                if (!copy.Success)
                    throw new InvalidOperationException("bcdedit /copy failed.\n\n" + copy.Combined);
                Match m = Regex.Match(copy.StdOut + copy.StdErr, @"\{[0-9a-fA-F\-]{36}\}");
                if (!m.Success)
                    throw new InvalidOperationException("bcdedit did not return a new entry GUID.\n\n" + copy.Combined);
                guid = m.Value;

                try
                {
                    RunBcd("/set " + guid + " device partition=" + device);
                    RunBcd("/set " + guid + " path " + EfiBootPath);
                    RunBcd("/displayorder " + guid + " /addlast");
                }
                catch
                {
                    try { RunBcd("/delete " + guid + " /f"); } catch { }
                    throw;
                }

                UefiBootEntry entry = new UefiBootEntry();
                entry.Native = false;
                entry.BcdGuid = guid;
                entry.Description = description;
                Log.Write("Added Windows Boot Manager entry " + guid + " -> " + EfiBootPath + ".");
                return entry;
            }
            finally
            {
                if (addedLetter && letter.Length > 0)
                {
                    try { RemoveLetter(diskNumber, efiPartition.Number, letter); } catch { }
                }
            }
        }

        private static void RemoveBcd(UefiBootEntry entry)
        {
            if (entry == null || entry.BcdGuid.Length == 0) return;
            try { RunBcd("/displayorder " + entry.BcdGuid + " /remove"); } catch { }
            try { RunBcd("/delete " + entry.BcdGuid + " /f"); }
            catch (Exception ex) { Log.Write("Could not delete BCD entry " + entry.BcdGuid + ": " + ex.Message); }
            Log.Write("Removed Windows Boot Manager entry " + entry.BcdGuid + ".");
        }

        private static void RunBcd(string arguments)
        {
            ProcessResult r = ProcessRunner.Run(PathBcdEdit(), arguments, 60000);
            if (!r.Success)
                throw new InvalidOperationException("bcdedit " + arguments + " failed.\n\n" + r.Combined);
        }

        private static string PathBcdEdit()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string p = System.IO.Path.Combine(sys, "bcdedit.exe");
            return System.IO.File.Exists(p) ? p : "bcdedit.exe";
        }

        private static string PickVolumePath(string accessPaths)
        {
            if (string.IsNullOrEmpty(accessPaths)) return "";
            string[] parts = accessPaths.Split(';');
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (t.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
                    return t.EndsWith("\\") ? t : t + "\\";
            }
            return "";
        }

        private static string AssignTemporaryLetter(int diskNumber, int partitionNumber)
        {
            string script =
                "$ErrorActionPreference='Stop'\n" +
                "Add-PartitionAccessPath -DiskNumber " + diskNumber + " -PartitionNumber " + partitionNumber +
                " -AssignDriveLetter -ErrorAction Stop\n" +
                "$p = Get-Partition -DiskNumber " + diskNumber + " -PartitionNumber " + partitionNumber + "\n" +
                "Write-Output ('LETTER|' + $p.DriveLetter)\n";
            ProcessResult r = ProcessRunner.RunPowerShell(script, 60000);
            if (!r.Success) return "";
            foreach (string line in r.StdOut.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("LETTER|", StringComparison.Ordinal))
                    return line.Substring(7).Trim();
            }
            return "";
        }

        private static void RemoveLetter(int diskNumber, int partitionNumber, string letter)
        {
            string script =
                "$ErrorActionPreference='SilentlyContinue'\n" +
                "Remove-PartitionAccessPath -DiskNumber " + diskNumber + " -PartitionNumber " + partitionNumber +
                " -AccessPath '" + letter.Replace("'", "''") + ":'\n";
            ProcessRunner.RunPowerShell(script, 60000);
        }
    }
}
