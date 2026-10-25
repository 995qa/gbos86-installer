// Disk enumeration, free-space calculation, partition creation/removal and
// volume shrinking. All privileged operations go through PowerShell's Storage
// module; raw reads/writes use RawDisk.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace GbOs86
{
    internal sealed class FreeExtent
    {
        public ulong Offset;
        public ulong Length;
        public ulong End { get { return Offset + Length; } }

        public FreeExtent(ulong offset, ulong length)
        {
            Offset = offset;
            Length = length;
        }

        public override string ToString()
        {
            return "offset " + Offset + ", " + Fmt.Bytes(Length);
        }
    }

    internal sealed class PartitionInfo
    {
        public int DiskNumber;
        public int Number;
        public string DriveLetter = "";
        public ulong Offset;
        public ulong Size;
        public string Type = "";
        public string GptType = "";
        public string Guid = "";
        public string AccessPaths = "";
        public string Label = "";
        public FileSystemKind Fs;
        public bool LabelRead;

        public string DriveText
        {
            get { return DriveLetter.Length == 0 ? "" : DriveLetter + ":"; }
        }

        public bool IsGbOsLabel
        {
            get
            {
                return Label == "GBOS_EFI" || Label == "GBOSSYS" ||
                       Label == "GBOS_META" || Label == "GBOS_DATA";
            }
        }

        public override string ToString()
        {
            return "Partition " + Number + " " + Fmt.Bytes(Size);
        }
    }

    internal sealed class DiskInfo
    {
        public int Number;
        public string Model = "";
        public string Serial = "";
        public ulong Size;
        public string InterfaceType = "";
        public string MediaType = "";
        public string PartitionStyle = "";
        public string BusType = "";
        public bool IsSystem;
        public bool IsBoot;
        public bool IsReadOnly;
        public bool IsOffline;
        public List<PartitionInfo> Partitions = new List<PartitionInfo>();
        public List<FreeExtent> FreeExtents = new List<FreeExtent>();
        public FreeExtent LargestFree;
        public bool LabelsRead;

        public bool IsGpt { get { return string.Equals(PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase); } }
        public string SerialOrUnknown { get { return Serial.Length == 0 ? "unknown" : Serial; } }

        public string Describe
        {
            get
            {
                return "Disk " + Number + ": " + Model + " (" + Fmt.Bytes(Size) + ", " +
                       (IsGpt ? "GPT" : PartitionStyle) + ")";
            }
        }

        public PartitionInfo FindPartition(int number)
        {
            foreach (PartitionInfo p in Partitions)
                if (p.Number == number) return p;
            return null;
        }
    }

    internal sealed class PartitionSpec
    {
        public string Name = "";
        public ulong Offset;
        public ulong Size;
        public Guid TypeGuid;
        public bool NoDefaultLetter;
    }

    internal static class DiskService
    {
        public const ulong MiB = 1024UL * 1024UL;
        public const ulong GiB = 1024UL * 1024UL * 1024UL;

        public static List<DiskInfo> LoadDisks()
        {
            // The Storage WMI provider is the fastest source on Windows 8+:
            // one query for disks and one for partitions. Get-Disk/Get-Partition
            // start PowerShell and initialize the volume enumerator, which can
            // take several seconds on some machines.
            Dictionary<int, DiskInfo> byNumber = LoadStorageDisks();
            if (byNumber.Count == 0)
                byNumber = LoadLegacyDisks();

            List<DiskInfo> all = new List<DiskInfo>(byNumber.Values);
            all.Sort(delegate(DiskInfo a, DiskInfo b) { return a.Number.CompareTo(b.Number); });

            List<DiskInfo> result = new List<DiskInfo>();
            foreach (DiskInfo d in all)
            {
                if (d.Size == 0) continue;
                if (d.Model.Length == 0) d.Model = "(unknown disk)";
                d.Partitions.Sort(delegate(PartitionInfo a, PartitionInfo b) { return a.Offset.CompareTo(b.Offset); });
                ComputeFree(d);
                if (IsUsb(d)) continue;
                if (d.MediaType.IndexOf("Removable", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                result.Add(d);
            }
            return result;
        }

        private static Dictionary<int, DiskInfo> LoadStorageDisks()
        {
            Dictionary<int, DiskInfo> byNumber = new Dictionary<int, DiskInfo>();
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM MSFT_Disk")))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            int number = Number(mo["Number"]);
                            ulong size = U64(mo["Size"]);
                            if (size < GiB) continue;
                            DiskInfo d = new DiskInfo();
                            d.Number = number;
                            d.Model = Text(mo["FriendlyName"]);
                            if (d.Model.Length == 0) d.Model = Text(mo["Model"]);
                            d.Serial = Text(mo["SerialNumber"]);
                            d.Size = size;
                            d.PartitionStyle = PartitionStyleName(Number(mo["PartitionStyle"]));
                            d.BusType = BusTypeName(Number(mo["BusType"]));
                            d.InterfaceType = d.BusType;
                            d.IsBoot = Flag(mo["IsBoot"]);
                            d.IsSystem = Flag(mo["IsSystem"]);
                            d.IsReadOnly = Flag(mo["IsReadOnly"]);
                            d.IsOffline = Flag(mo["IsOffline"]);
                            byNumber[number] = d;
                        }
                        catch { }
                    }
                }

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM MSFT_Partition")))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            int diskNo = Number(mo["DiskNumber"]);
                            DiskInfo d;
                            if (!byNumber.TryGetValue(diskNo, out d)) continue;
                            PartitionInfo p = new PartitionInfo();
                            p.DiskNumber = diskNo;
                            p.Number = Number(mo["PartitionNumber"]);
                            p.Offset = U64(mo["Offset"]);
                            p.Size = U64(mo["Size"]);
                            p.DriveLetter = Text(mo["DriveLetter"]);
                            p.GptType = Text(mo["GptType"]);
                            p.Guid = Text(mo["Guid"]);
                            p.AccessPaths = JoinPaths(mo["AccessPaths"]);
                            p.Type = p.GptType.Length > 0 ? p.GptType : MbrTypeName(Number(mo["MbrType"]));
                            d.Partitions.Add(p);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return byNumber;
        }

        // Fallback for systems where the Storage provider is unavailable.
        private static Dictionary<int, DiskInfo> LoadLegacyDisks()
        {
            Dictionary<int, DiskInfo> byNumber = new Dictionary<int, DiskInfo>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Index,Model,SerialNumber,Size,InterfaceType,MediaType FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            int index = Number(mo["Index"]);
                            ulong size = U64(mo["Size"]);
                            if (size < GiB) continue;
                            DiskInfo d = new DiskInfo();
                            d.Number = index;
                            d.Model = Text(mo["Model"]);
                            d.Serial = Text(mo["SerialNumber"]);
                            d.Size = size;
                            d.InterfaceType = Text(mo["InterfaceType"]);
                            d.MediaType = Text(mo["MediaType"]);
                            byNumber[index] = d;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            LoadWmiPartitions(byNumber);
            foreach (DiskInfo d in byNumber.Values)
            {
                if (d.Partitions.Count == 0) continue;
                d.PartitionStyle = "MBR";
                foreach (PartitionInfo p in d.Partitions)
                {
                    if (p.Type.StartsWith("GPT:", StringComparison.OrdinalIgnoreCase))
                    {
                        d.PartitionStyle = "GPT";
                        break;
                    }
                }
            }
            return byNumber;
        }

        /// <summary>
        /// Fallback partition enumeration: Win32_DiskPartition has offsets and
        /// sizes and Win32_LogicalDiskToPartition maps drive letters to them.
        /// </summary>
        private static void LoadWmiPartitions(Dictionary<int, DiskInfo> byNumber)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT DiskIndex,Index,StartingOffset,Size,Type FROM Win32_DiskPartition"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            int diskNo = Convert.ToInt32(mo["DiskIndex"], CultureInfo.InvariantCulture);
                            int index = Convert.ToInt32(mo["Index"], CultureInfo.InvariantCulture);
                            ulong offset = Convert.ToUInt64(mo["StartingOffset"], CultureInfo.InvariantCulture);
                            ulong size = Convert.ToUInt64(mo["Size"], CultureInfo.InvariantCulture);
                            DiskInfo d;
                            if (!byNumber.TryGetValue(diskNo, out d)) continue;
                            PartitionInfo p = new PartitionInfo();
                            p.DiskNumber = diskNo;
                            // WMI's partition Index is zero-based; Windows
                            // partition numbers (and GPT entries) start at 1.
                            p.Number = index + 1;
                            p.Offset = offset;
                            p.Size = size;
                            p.Type = Convert.ToString(mo["Type"], CultureInfo.InvariantCulture).Trim();
                            d.Partitions.Add(p);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Antecedent,Dependent,StartingAddress FROM Win32_LogicalDiskToPartition"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            ulong start = Convert.ToUInt64(mo["StartingAddress"], CultureInfo.InvariantCulture);
                            string letter = ExtractDeviceId(Convert.ToString(mo["Dependent"], CultureInfo.InvariantCulture));
                            if (letter.Length == 0) continue;
                            foreach (DiskInfo d in byNumber.Values)
                            {
                                foreach (PartitionInfo p in d.Partitions)
                                {
                                    if (p.Offset == start) p.DriveLetter = letter;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // The association class exposes paths such as
        // \\PC\root\cimv2:Win32_LogicalDisk.DeviceID="C:" rather than objects.
        private static string ExtractDeviceId(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            const string marker = "DeviceID=";
            int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            i += marker.Length;
            while (i < path.Length && (path[i] == ' ' || path[i] == '"')) i++;
            int start = i;
            while (i < path.Length && path[i] != '"' && path[i] != '\\' && path[i] != '}') i++;
            return path.Substring(start, i - start).Trim();
        }

        private static string Text(object value)
        {
            if (value == null) return "";
            string s = Convert.ToString(value, CultureInfo.InvariantCulture);
            return s == null ? "" : s.Trim('\0', ' ', '\t', '\r', '\n');
        }

        private static int Number(object value)
        {
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static ulong U64(object value)
        {
            try { return Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static bool Flag(object value)
        {
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        private static string JoinPaths(object value)
        {
            string[] paths = value as string[];
            if (paths == null) return Text(value);
            StringBuilder sb = new StringBuilder();
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(path);
            }
            return sb.ToString();
        }

        private static string PartitionStyleName(int value)
        {
            if (value == 1) return "MBR";
            if (value == 2) return "GPT";
            return "RAW";
        }

        private static string BusTypeName(int value)
        {
            switch (value)
            {
                case 0: return "";
                case 1: return "SCSI";
                case 2: return "ATAPI";
                case 3: return "ATA";
                case 4: return "IEEE 1394";
                case 5: return "SSA";
                case 6: return "Fibre Channel";
                case 7: return "USB";
                case 8: return "RAID";
                case 9: return "iSCSI";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 14: return "Virtual";
                case 15: return "File Backed Virtual";
                case 16: return "Storage Spaces";
                case 17: return "NVMe";
                case 18: return "SCM";
                case 19: return "UFS";
                default: return "Bus " + value.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string MbrTypeName(int value)
        {
            switch (value)
            {
                case 0x01: return "FAT12";
                case 0x04:
                case 0x06:
                case 0x0E: return "FAT16";
                case 0x05:
                case 0x0F: return "Extended";
                case 0x07: return "NTFS/IFS";
                case 0x0B:
                case 0x0C: return "FAT32";
                case 0x27: return "Windows RE";
                case 0x42: return "Dynamic";
                case 0xEF: return "EFI System";
                default: return value == 0 ? "" : "MBR 0x" + value.ToString("X2", CultureInfo.InvariantCulture);
            }
        }

        private static bool IsUsb(DiskInfo d)
        {
            return string.Equals(d.InterfaceType, "USB", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(d.BusType, "USB", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(d.BusType, "SD", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(d.BusType, "MMC", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> Lines(string text)
        {
            if (text == null) yield break;
            foreach (string raw in text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length > 0) yield return line;
            }
        }

        private static void ComputeFree(DiskInfo d)
        {
            d.FreeExtents = ComputeFreeExtents(d, null);
            d.LargestFree = new FreeExtent(0, 0);
            foreach (FreeExtent f in d.FreeExtents)
                if (f.Length > d.LargestFree.Length) d.LargestFree = f;
        }

        /// <summary>
        /// Unallocated, 1 MiB-aligned areas of a disk. Partitions listed in
        /// <paramref name="exclude"/> are treated as already removed (used to
        /// simulate replacing an existing gbOS86 install).
        /// </summary>
        public static List<FreeExtent> ComputeFreeExtents(DiskInfo d, List<int> exclude)
        {
            const ulong guard = 1024UL * 1024UL;
            List<FreeExtent> used = new List<FreeExtent>();
            used.Add(new FreeExtent(0, Math.Min(guard, d.Size)));
            if (d.Size > guard)
                used.Add(new FreeExtent(d.Size - guard, guard));
            foreach (PartitionInfo p in d.Partitions)
            {
                if (p.Size == 0) continue;
                if (exclude != null && exclude.Contains(p.Number)) continue;
                used.Add(new FreeExtent(p.Offset, p.Size));
            }
            used.Sort(delegate(FreeExtent a, FreeExtent b) { return a.Offset.CompareTo(b.Offset); });

            List<FreeExtent> merged = new List<FreeExtent>();
            foreach (FreeExtent e in used)
            {
                if (merged.Count > 0 && e.Offset <= merged[merged.Count - 1].End)
                {
                    FreeExtent last = merged[merged.Count - 1];
                    ulong end = Math.Max(last.End, e.End);
                    last.Length = end - last.Offset;
                }
                else
                {
                    merged.Add(new FreeExtent(e.Offset, e.Length));
                }
            }

            List<FreeExtent> result = new List<FreeExtent>();
            for (int i = 0; i < merged.Count; i++)
            {
                ulong gapStart = merged[i].End;
                ulong gapEnd = (i + 1 < merged.Count) ? merged[i + 1].Offset : d.Size;
                if (gapEnd <= gapStart) continue;
                gapStart = AlignUp(gapStart, MiB);
                gapEnd = AlignDown(gapEnd, MiB);
                if (gapEnd <= gapStart) continue;
                result.Add(new FreeExtent(gapStart, gapEnd - gapStart));
            }
            return result;
        }

        public static ulong AlignUp(ulong value, ulong alignment)
        {
            if (alignment == 0) return value;
            ulong rem = value % alignment;
            return rem == 0 ? value : value + (alignment - rem);
        }

        public static ulong AlignDown(ulong value, ulong alignment)
        {
            return alignment == 0 ? value : value - (value % alignment);
        }

        public static void Refresh()
        {
            ProcessRunner.RunPowerShell(
                "Get-Disk -ErrorAction SilentlyContinue | ForEach-Object { Update-Disk -Number $_.Number -ErrorAction SilentlyContinue }",
                30000);
            System.Threading.Thread.Sleep(500);
        }

        public static bool ReadLabels(DiskInfo disk)
        {
            bool opened = false;
            try
            {
                using (RawDisk raw = new RawDisk(disk.Number, false))
                {
                    opened = true;
                    foreach (PartitionInfo p in disk.Partitions)
                    {
                        p.Label = "";
                        p.Fs = FileSystemKind.Unknown;
                        p.LabelRead = false;
                        if (p.Size < 4096) continue;
                        byte[] buf = new byte[4096];
                        try
                        {
                            raw.ReadAt((long)p.Offset, buf, buf.Length);
                            string label;
                            p.Fs = FsLabel.Detect(buf, out label);
                            p.Label = label;
                            p.LabelRead = true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            // Only claim the labels were read when the disk could actually be
            // opened; otherwise an existing gbOS86 install could be missed.
            disk.LabelsRead = opened;
            return opened;
        }

        public static List<PartitionInfo> FindGbOsInstall(DiskInfo disk)
        {
            if (!disk.LabelsRead) ReadLabels(disk);
            Dictionary<string, PartitionInfo> byLabel = new Dictionary<string, PartitionInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (PartitionInfo p in disk.Partitions)
            {
                if (p.Label.Length > 0 && p.IsGbOsLabel && !byLabel.ContainsKey(p.Label))
                    byLabel[p.Label] = p;
            }
            PartitionInfo efi, sys, meta, data;
            if (!byLabel.TryGetValue("GBOS_EFI", out efi)) return null;
            if (!byLabel.TryGetValue("GBOSSYS", out sys)) return null;
            if (!byLabel.TryGetValue("GBOS_META", out meta)) return null;
            if (!byLabel.TryGetValue("GBOS_DATA", out data)) return null;

            List<PartitionInfo> list = new List<PartitionInfo> { efi, sys, meta, data };
            for (int i = 1; i < 4; i++)
            {
                if (list[i].Offset != list[i - 1].Offset + list[i - 1].Size)
                    return null;
            }
            return list;
        }

        public static bool HasPartialGbOs(DiskInfo disk)
        {
            if (!disk.LabelsRead) ReadLabels(disk);
            foreach (PartitionInfo p in disk.Partitions)
                if (p.IsGbOsLabel) return true;
            return false;
        }

        public static void RemovePartitions(int diskNumber, List<int> numbers)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference='Stop'");
            sb.AppendLine("$disk=" + diskNumber.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("$parts=@(" + JoinInts(numbers) + ")");
            sb.AppendLine("foreach ($n in $parts) {");
            sb.AppendLine("  $p = Get-Partition -DiskNumber $disk -PartitionNumber $n -ErrorAction SilentlyContinue");
            sb.AppendLine("  if (-not $p) { continue }");
            sb.AppendLine("  try { foreach ($ap in @($p.AccessPaths)) { if ($ap) { Remove-PartitionAccessPath -DiskNumber $disk -PartitionNumber $n -AccessPath $ap -ErrorAction SilentlyContinue } } } catch {}");
            sb.AppendLine("  try { Set-Partition -DiskNumber $disk -PartitionNumber $n -IsReadOnly $false -ErrorAction SilentlyContinue } catch {}");
            sb.AppendLine("  Remove-Partition -DiskNumber $disk -PartitionNumber $n -Confirm:$false -ErrorAction Stop");
            sb.AppendLine("  Write-Output (\"REMOVED|\" + $n)");
            sb.AppendLine("}");
            sb.AppendLine("Update-Disk -Number $disk -ErrorAction SilentlyContinue");
            sb.AppendLine("Write-Output 'OK'");

            ProcessResult r = ProcessRunner.RunPowerShell(sb.ToString(), 120000);
            if (!r.Success || r.StdOut.IndexOf("OK", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("Could not remove the gbOS86 partitions.\n\n" + r.Combined);
        }

        private static string JoinInts(List<int> values)
        {
            StringBuilder sb = new StringBuilder();
            foreach (int v in values)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(v.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static List<PartitionInfo> CreatePartitions(int diskNumber, List<PartitionSpec> specs)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference='Stop'");
            sb.AppendLine("$disk=" + diskNumber.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("$specs=@(");
            for (int i = 0; i < specs.Count; i++)
            {
                PartitionSpec s = specs[i];
                sb.AppendLine("  [pscustomobject]@{Name='" + EscapePs(s.Name) + "'; Offset=[uint64]" +
                    s.Offset.ToString(CultureInfo.InvariantCulture) + "; Size=[uint64]" +
                    s.Size.ToString(CultureInfo.InvariantCulture) + "; Type='" + s.TypeGuid.ToString() +
                    "'; NoLetter=$" + (s.NoDefaultLetter ? "true" : "false") + "}" +
                    (i + 1 < specs.Count ? "," : ""));
            }
            sb.AppendLine(")");
            sb.AppendLine("$created=New-Object System.Collections.ArrayList");
            sb.AppendLine("try {");
            sb.AppendLine("  foreach ($s in $specs) {");
            sb.AppendLine("    $type = '{' + $s.Type + '}'");
            sb.AppendLine("    $p = New-Partition -DiskNumber $disk -Offset $s.Offset -Size $s.Size -GptType $type -ErrorAction Stop");
            sb.AppendLine("    [void]$created.Add($p)");
            sb.AppendLine("    try { Set-Partition -DiskNumber $disk -PartitionNumber $p.PartitionNumber -NoDefaultDriveLetter $true -ErrorAction Stop } catch {}");
            sb.AppendLine("    if ($s.Name) { try { Set-Partition -DiskNumber $disk -PartitionNumber $p.PartitionNumber -NewName $s.Name -ErrorAction Stop } catch {} }");
            sb.AppendLine("  }");
            sb.AppendLine("  # Remove drive letters so Explorer cannot touch the raw partitions, but");
            sb.AppendLine("  # keep volume GUID paths for locking/dismounting before the raw write.");
            sb.AppendLine("  for ($i=0; $i -lt $created.Count; $i++) {");
            sb.AppendLine("    $p = $created[$i]");
            sb.AppendLine("    $paths = @()");
            sb.AppendLine("    try { $paths = @($p.AccessPaths) } catch {}");
            sb.AppendLine("    foreach ($ap in $paths) {");
            sb.AppendLine("      if ($ap -match '^[A-Za-z]:\\\\?$') {");
            sb.AppendLine("        try { Remove-PartitionAccessPath -DiskNumber $disk -PartitionNumber $p.PartitionNumber -AccessPath $ap -ErrorAction SilentlyContinue } catch {}");
            sb.AppendLine("      }");
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("  Update-Disk -Number $disk -ErrorAction SilentlyContinue");
            sb.AppendLine("  foreach ($p in $created) {");
            sb.AppendLine("    $cur = Get-Partition -DiskNumber $disk -PartitionNumber $p.PartitionNumber -ErrorAction SilentlyContinue");
            sb.AppendLine("    if ($cur) { $p = $cur }");
            sb.AppendLine("    $ap = ''");
            sb.AppendLine("    try { $ap = (@($p.AccessPaths) -join ';') } catch {}");
            sb.AppendLine("    Write-Output (\"CREATED|\" + $p.PartitionNumber + \"|\" + $p.Offset + \"|\" + $p.Size + \"|\" + $p.Guid.ToString() + \"|\" + $ap)");
            sb.AppendLine("  }");
            sb.AppendLine("  Write-Output 'OK'");
            sb.AppendLine("} catch {");
            sb.AppendLine("  $msg = $_.Exception.Message -replace \"`r|`n\", ' '");
            sb.AppendLine("  Write-Output (\"ERROR|\" + $msg)");
            sb.AppendLine("  foreach ($p in $created) {");
            sb.AppendLine("    try { foreach ($ap in @($p.AccessPaths)) { if ($ap) { Remove-PartitionAccessPath -DiskNumber $disk -PartitionNumber $p.PartitionNumber -AccessPath $ap -ErrorAction SilentlyContinue } } } catch {}");
            sb.AppendLine("    try { Remove-Partition -DiskNumber $disk -PartitionNumber $p.PartitionNumber -Confirm:$false -ErrorAction SilentlyContinue } catch {}");
            sb.AppendLine("  }");
            sb.AppendLine("  Update-Disk -Number $disk -ErrorAction SilentlyContinue");
            sb.AppendLine("  exit 1");
            sb.AppendLine("}");

            ProcessResult r = ProcessRunner.RunPowerShell(sb.ToString(), 120000);
            if (!r.Success || r.StdOut.IndexOf("OK", StringComparison.Ordinal) < 0)
            {
                string msg = r.Combined;
                int idx = msg.IndexOf("ERROR|", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int end = msg.IndexOf('\n', idx);
                    msg = end > idx ? msg.Substring(idx + 6, end - idx - 6) : msg.Substring(idx + 6);
                }
                throw new InvalidOperationException("Could not create the gbOS86 partitions.\n\n" + msg.Trim());
            }

            List<PartitionInfo> result = new List<PartitionInfo>();
            foreach (string line in Lines(r.StdOut))
            {
                if (!line.StartsWith("CREATED|", StringComparison.Ordinal)) continue;
                string[] f = line.Split('|');
                if (f.Length < 6) continue;
                PartitionInfo p = new PartitionInfo();
                p.DiskNumber = diskNumber;
                int n;
                ulong off, size;
                if (!int.TryParse(f[1], out n)) continue;
                if (!ulong.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out off)) continue;
                if (!ulong.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) continue;
                p.Number = n;
                p.Offset = off;
                p.Size = size;
                p.Guid = f[4].Trim();
                p.AccessPaths = f[5].Trim();
                result.Add(p);
            }
            if (result.Count != specs.Count)
                throw new InvalidOperationException("Windows created " + result.Count + " of " + specs.Count +
                    " partitions. The disk was left unchanged where possible; refresh and try again.");
            return result;
        }

        private static string EscapePs(string s)
        {
            return (s == null ? "" : s).Replace("'", "''");
        }

        public static void ShrinkPartition(int diskNumber, int partitionNumber, ulong shrinkBytes)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference='Stop'");
            sb.AppendLine("$d=" + diskNumber);
            sb.AppendLine("$n=" + partitionNumber);
            sb.AppendLine("$shrink=[uint64]" + shrinkBytes.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("$p = Get-Partition -DiskNumber $d -PartitionNumber $n -ErrorAction Stop");
            sb.AppendLine("$sup = Get-PartitionSupportedSize -DiskNumber $d -PartitionNumber $n -ErrorAction Stop");
            sb.AppendLine("$new = [uint64]$p.Size - $shrink");
            sb.AppendLine("if ($new -lt [uint64]$sup.SizeMin) { throw \"This volume cannot be shrunk that far. The smallest it can be is \" + [math]::Round([uint64]$sup.SizeMin/1GB,1) + \" GB.\" }");
            sb.AppendLine("Resize-Partition -DiskNumber $d -PartitionNumber $n -Size $new -ErrorAction Stop");
            sb.AppendLine("Update-Disk -Number $d -ErrorAction SilentlyContinue");
            sb.AppendLine("Write-Output (\"OK|\" + $new)");
            ProcessResult r = ProcessRunner.RunPowerShell(sb.ToString(), 0);
            if (!r.Success || r.StdOut.IndexOf("OK|", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("Could not shrink the volume.\n\n" + r.Combined);
        }

        public static bool? SecureBootEnabled
        {
            get
            {
                try
                {
                    object v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State",
                        "UEFISecureBootEnabled", null);
                    if (v == null) return null;
                    return Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0;
                }
                catch { return null; }
            }
        }

        public static int? SystemDiskNumber(List<DiskInfo> disks)
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (sys.Length >= 2)
            {
                string letter = sys.Substring(0, 1);
                foreach (DiskInfo d in disks)
                {
                    foreach (PartitionInfo p in d.Partitions)
                        if (string.Equals(p.DriveLetter, letter, StringComparison.OrdinalIgnoreCase))
                            return d.Number;
                }
            }
            foreach (DiskInfo d in disks)
                if (d.IsSystem) return d.Number;
            return null;
        }

        public static string DescribePartitions(DiskInfo disk)
        {
            StringBuilder sb = new StringBuilder();
            foreach (PartitionInfo p in disk.Partitions)
            {
                sb.AppendLine("  " + p.Number + "  " + Fmt.Bytes(p.Size) + "  " +
                    (p.DriveText.Length > 0 ? p.DriveText : "no letter") + "  " +
                    (p.Label.Length > 0 ? p.Label : p.Type));
            }
            return sb.ToString();
        }
    }
}
