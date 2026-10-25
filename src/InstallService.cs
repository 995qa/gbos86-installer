// The destructive half: validate the target, create the four partitions in
// unallocated GPT space, write the image payload, verify it, and add the UEFI
// boot entry. Every failure after partition creation rolls the disk back.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GbOs86
{
    internal sealed class PreflightResult
    {
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<PartitionInfo> ExistingGbOs;
        public bool HasExisting;
        public FreeExtent Free;
        public ulong Required;
        public ulong FreeAfterRemoval;

        public bool Ok { get { return Errors.Count == 0; } }

        public string Summary
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                foreach (string e in Errors) sb.AppendLine("ERROR: " + e);
                foreach (string w in Warnings) sb.AppendLine("WARNING: " + w);
                return sb.ToString().Trim();
            }
        }
    }

    internal sealed class InstallReport
    {
        public UefiBootEntry BootEntry;
        public List<PartitionInfo> Partitions = new List<PartitionInfo>();
        public ulong BytesWritten;
    }

    internal sealed class InstallState
    {
        public int DiskNumber = -1;
        public string DiskSerial = "";
        public string DiskModel = "";
        public string ImagePath = "";
        public string ImageSha = "";
        public string InstalledAt = "";
        public bool BootNative = true;
        public int BootIndex = -1;
        public string BootVariable = "";
        public string BcdGuid = "";
        public List<int> Partitions = new List<int>();
        public List<string> PartitionGuids = new List<string>();

        public static InstallState Load()
        {
            if (!File.Exists(Paths.StateFile)) return null;
            IniFile ini = IniFile.Load(Paths.StateFile);
            InstallState s = new InstallState();
            s.DiskNumber = ini.GetInt("DiskNumber", -1);
            s.DiskSerial = ini.Get("DiskSerial", "");
            s.DiskModel = ini.Get("DiskModel", "");
            s.ImagePath = ini.Get("Image", "");
            s.ImageSha = ini.Get("ImageSha", "");
            s.InstalledAt = ini.Get("InstalledAt", "");
            s.BootNative = ini.GetBool("BootNative", true);
            s.BootIndex = ini.GetInt("BootIndex", -1);
            s.BootVariable = ini.Get("BootVariable", "");
            s.BcdGuid = ini.Get("BcdGuid", "");
            foreach (string n in ini.Get("Partitions", "").Split(','))
            {
                int v;
                if (int.TryParse(n.Trim(), out v)) s.Partitions.Add(v);
            }
            foreach (string g in ini.Get("PartitionGuids", "").Split(','))
            {
                string t = g.Trim();
                if (t.Length > 0) s.PartitionGuids.Add(t);
            }
            // Ignore an empty or truncated state file; there is nothing to
            // uninstall unless the disk number and at least one partition were
            // recorded.
            if (s.DiskNumber < 0 || s.Partitions.Count == 0) return null;
            return s;
        }

        public void Save()
        {
            IniFile ini = new IniFile(Paths.StateFile);
            ini.Set("Version", "1");
            ini.Set("DiskNumber", DiskNumber.ToString(CultureInfo.InvariantCulture));
            ini.Set("DiskSerial", DiskSerial);
            ini.Set("DiskModel", DiskModel);
            ini.Set("Image", ImagePath);
            ini.Set("ImageSha", ImageSha);
            ini.Set("InstalledAt", InstalledAt);
            ini.Set("BootNative", BootNative ? "1" : "0");
            ini.Set("BootIndex", BootIndex.ToString(CultureInfo.InvariantCulture));
            ini.Set("BootVariable", BootVariable);
            ini.Set("BcdGuid", BcdGuid);
            ini.Set("Partitions", JoinInts(Partitions));
            ini.Set("PartitionGuids", string.Join(",", PartitionGuids.ToArray()));
            ini.Save();
        }

        public void Delete()
        {
            try { if (File.Exists(Paths.StateFile)) File.Delete(Paths.StateFile); } catch { }
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
    }

    internal static class InstallService
    {
        private const ulong MiB = 1024UL * 1024UL;

        public static PreflightResult Preflight(DiskInfo disk, LooperImage image)
        {
            PreflightResult r = new PreflightResult();
            if (disk == null) { r.Errors.Add("No target disk was selected."); return r; }
            if (image == null) { r.Errors.Add("No gbOS86 image was selected."); return r; }
            if (!Native.IsAdministrator())
                r.Errors.Add("The installer is not running as administrator.");
            if (!Native.IsUefi())
                r.Errors.Add("This PC booted in legacy BIOS mode. gbOS86 needs UEFI.");

            r.Required = image.RequiredTargetBytes;
            if (!disk.IsGpt)
                r.Errors.Add("The target disk must use GPT. " + disk.Describe + " uses " + disk.PartitionStyle + ".");
            if (disk.IsSystem || disk.IsBoot)
                r.Errors.Add("Disk " + disk.Number + " contains the running Windows installation. Pick another disk.");
            if (disk.IsReadOnly)
                r.Errors.Add("Disk " + disk.Number + " is read-only.");
            if (disk.IsOffline)
                r.Errors.Add("Disk " + disk.Number + " is offline.");
            if (!disk.LabelsRead && !DiskService.ReadLabels(disk))
                r.Errors.Add("Cannot read the partition table on disk " + disk.Number +
                             ". Close Disk Management and any other program that uses the disk, then refresh.");
            // Never read the source image from the disk being repartitioned.
            try
            {
                string root = Path.GetPathRoot(image.Path);
                if (!string.IsNullOrEmpty(root) && root.Length >= 2)
                {
                    string letter = root.Substring(0, 1);
                    foreach (PartitionInfo p in disk.Partitions)
                    {
                        if (string.Equals(p.DriveLetter, letter, StringComparison.OrdinalIgnoreCase))
                        {
                            r.Errors.Add("The image file is stored on the target disk (" + root +
                                         "). Move it to another disk before installing.");
                            break;
                        }
                    }
                }
            }
            catch { }

            List<PartitionInfo> existing = DiskService.FindGbOsInstall(disk);
            bool partial = existing == null && DiskService.HasPartialGbOs(disk);
            if (partial)
                r.Errors.Add("Disk " + disk.Number + " already has some gbOS86 partitions, but not a complete set. " +
                             "Remove them with Disk Management (or the installer's Uninstall button) first.");
            if (existing != null)
            {
                r.HasExisting = true;
                r.ExistingGbOs = existing;
                r.Warnings.Add("An existing gbOS86 installation was found on disk " + disk.Number +
                               ". It will be removed and replaced.");
            }

            List<int> exclude = new List<int>();
            if (existing != null)
                foreach (PartitionInfo p in existing) exclude.Add(p.Number);

            List<FreeExtent> free = DiskService.ComputeFreeExtents(disk, exclude);
            FreeExtent best = null;
            foreach (FreeExtent f in free)
                if (best == null || f.Length > best.Length) best = f;
            r.Free = best;
            r.FreeAfterRemoval = best == null ? 0 : best.Length;

            if (best == null || best.Length < r.Required)
            {
                ulong have = best == null ? 0 : best.Length;
                r.Errors.Add("Not enough unallocated space on disk " + disk.Number + ".\n" +
                             "gbOS86 needs one contiguous free area of " + Fmt.Bytes(r.Required) +
                             " (EFI 512 MiB + system 8 GiB + metadata 64 MiB + data " +
                             image.DataGb.ToString("0.#", CultureInfo.InvariantCulture) + " GB).\n" +
                             "The largest free area is " + Fmt.Bytes(have) + ".");
            }

            if (r.Required > 0 && disk.Size < r.Required)
                r.Errors.Add("The image is larger than the selected disk.");

            bool? secureBoot = DiskService.SecureBootEnabled;
            if (secureBoot == true)
                r.Warnings.Add("Secure Boot is enabled. gbOS86's BOOTX64.EFI is not signed, so the firmware will " +
                               "refuse to boot it. Turn Secure Boot off in the UEFI settings after installing, or " +
                               "Windows and gbOS86 will not both boot.");
            foreach (string w in image.Warnings) r.Warnings.Add(w);

            if (best != null && r.Required > 0)
                r.Warnings.Add("New partitions will be created at disk offset " + Fmt.Bytes(best.Offset) +
                               ", using " + Fmt.Bytes(r.Required) + " of the " + Fmt.Bytes(best.Length) +
                               " free area.");
            return r;
        }

        public static InstallReport Install(DiskInfo selectedDisk, LooperImage image, PreflightResult pre,
            Action<int, string> progress, Func<bool> cancelled)
        {
            if (!pre.Ok) throw new InvalidOperationException(pre.Summary);
            if (selectedDisk == null || image == null) throw new InvalidOperationException("Nothing to install.");

            InstallReport report = new InstallReport();
            UefiBootEntry boot = null;
            List<PartitionInfo> created = new List<PartitionInfo>();
            bool destructive = false;
            try
            {
                try { Native.EnablePrivilege("SeManageVolumePrivilege"); }
                catch (Exception ex) { Log.Write("Note: SeManageVolumePrivilege: " + ex.Message); }

                Step(progress, 2, "Checking the target disk...");
                DiskInfo disk = Reload(selectedDisk);
                PreflightResult fresh = Preflight(disk, image);
                if (!fresh.Ok) throw new InvalidOperationException(fresh.Summary);
                pre = fresh;

                if (pre.HasExisting)
                {
                    Step(progress, 4, "Removing the previous gbOS86 installation...");
                    // A previous install may have used the bcdedit fallback
                    // instead of a native Boot#### entry. Remove the entry the
                    // installer recorded, but only if it belongs to this disk.
                    InstallState oldState = InstallState.Load();
                    if (oldState != null && !oldState.BootNative && oldState.BcdGuid.Length > 0 &&
                        SameDisk(oldState, disk))
                    {
                        UefiBootEntry oldBcd = new UefiBootEntry();
                        oldBcd.Native = false;
                        oldBcd.BcdGuid = oldState.BcdGuid;
                        oldBcd.Description = "gbOS86";
                        try { UefiBoot.Remove(oldBcd); }
                        catch (Exception ex) { Log.Write("Could not remove the old BCD entry: " + ex.Message); }
                    }
                    boot = UefiBoot.Find("gbOS86");
                    if (boot != null)
                    {
                        try { UefiBoot.Remove(boot); }
                        catch (Exception ex) { Log.Write("Could not remove the old boot entry: " + ex.Message); }
                        boot = null;
                    }
                    List<int> old = new List<int>();
                    foreach (PartitionInfo p in pre.ExistingGbOs) old.Add(p.Number);
                    old.Sort();
                    old.Reverse();
                    DiskService.RemovePartitions(disk.Number, old);
                    DiskService.Refresh();
                    disk = Reload(selectedDisk);
                    pre = Preflight(disk, image);
                    if (!pre.Ok) throw new InvalidOperationException(pre.Summary);
                }

                CheckCancel(cancelled);
                FreeExtent free = pre.Free;
                if (free == null || free.Length < pre.Required)
                    throw new InvalidOperationException("The free space changed while preparing the install. Refresh and try again.");

                // Build the exact partition list from the image.
                List<PartitionSpec> specs = new List<PartitionSpec>();
                ulong cursor = free.Offset;
                foreach (ImagePartition p in image.Partitions)
                {
                    ulong size = LooperImage.AlignUp(p.Size, MiB);
                    PartitionSpec s = new PartitionSpec();
                    s.Name = p.Name;
                    s.Offset = cursor;
                    s.Size = size;
                    s.TypeGuid = p.TypeGuid;
                    s.NoDefaultLetter = true;
                    specs.Add(s);
                    cursor += size;
                }
                if (cursor > free.End)
                    throw new InvalidOperationException("The image does not fit in the selected free space.");

                Step(progress, 8, "Creating four GPT partitions in unallocated space...");
                destructive = true;
                created = DiskService.CreatePartitions(disk.Number, specs);
                Log.Write("Created partitions " + DescribeNumbers(created) + ".");
                DiskService.Refresh();

                Step(progress, 12, "Locking the new volumes...");
                foreach (PartitionInfo p in created)
                {
                    foreach (string ap in p.AccessPaths.Split(';'))
                    {
                        string path = ap.Trim();
                        if (path.Length == 0) continue;
                        RawDisk.LockAndDismountVolume(path);
                    }
                }

                Step(progress, 14, "Clearing old filesystem headers...");
                using (RawDisk raw = new RawDisk(disk.Number, true))
                {
                    raw.UpdateProperties();
                    for (int i = 0; i < created.Count; i++)
                    {
                        ulong prefix = ZeroPrefix(i, created[i].Size);
                        if (prefix == 0) continue;
                        Log.Write("  zeroing " + Fmt.Bytes(prefix) + " at partition " + created[i].Number);
                        raw.WriteZerosAt((long)created[i].Offset, (long)prefix);
                    }
                    raw.Flush();
                }

                Step(progress, 18, "Writing gbOS86 to disk...");
                report.BytesWritten = Flash(disk, image, created, progress, cancelled);

                CheckCancel(cancelled);
                Step(progress, 82, "Verifying the written data...");
                Verify(disk, image, created, progress, cancelled);

                Step(progress, 90, "Adding the UEFI boot entry...");
                boot = UefiBoot.Add(disk.Number, created[0], "gbOS86");
                report.BootEntry = boot;

                Step(progress, 95, "Saving installation state...");
                InstallState state = new InstallState();
                state.DiskNumber = disk.Number;
                state.DiskSerial = disk.Serial;
                state.DiskModel = disk.Model;
                state.ImagePath = image.Path;
                // Use the upstream .sha256 when the build exported one. Hashing
                // the 72 GB logical image here would stall the install for no
                // benefit: every written byte was already verified above.
                state.ImageSha = ReadImageSha(image.Path);
                state.InstalledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                state.BootNative = boot.Native;
                state.BootIndex = boot.Index;
                state.BootVariable = boot.VariableName;
                state.BcdGuid = boot.BcdGuid;
                foreach (PartitionInfo p in created)
                {
                    state.Partitions.Add(p.Number);
                    state.PartitionGuids.Add(p.Guid);
                }
                state.Save();
                report.Partitions = created;

                Step(progress, 100, "Done.");
                Log.Write("gbOS86 installed on disk " + disk.Number + " (" + disk.Model + ").");
                Log.Write("Boot entry: " + boot);
                Log.Write("Leave Secure Boot off and pick gbOS86 in the firmware boot menu (F12) to start it.");
                return report;
            }
            catch (OperationCanceledException)
            {
                Rollback(selectedDisk, boot, created, destructive);
                throw;
            }
            catch
            {
                Rollback(selectedDisk, boot, created, destructive);
                throw;
            }
        }

        private static void Rollback(DiskInfo disk, UefiBootEntry boot, List<PartitionInfo> created, bool destructive)
        {
            if (boot != null)
            {
                try { UefiBoot.Remove(boot); }
                catch (Exception ex) { Log.Write("Rollback: could not remove the boot entry: " + ex.Message); }
            }
            if (destructive && created.Count > 0 && disk != null)
            {
                try
                {
                    List<int> numbers = new List<int>();
                    foreach (PartitionInfo p in created) numbers.Add(p.Number);
                    numbers.Sort();
                    numbers.Reverse();
                    Log.Write("Rollback: removing the new partitions " + DescribeNumbers(created) + "...");
                    DiskService.RemovePartitions(disk.Number, numbers);
                    DiskService.Refresh();
                }
                catch (Exception ex)
                {
                    Log.Write("Rollback failed. Remove the gbOS86 partitions manually in Disk Management. " + ex.Message);
                }
            }
        }

        private static ulong Flash(DiskInfo disk, LooperImage image, List<PartitionInfo> created,
            Action<int, string> progress, Func<bool> cancelled)
        {
            ulong total = 0;
            for (int i = 0; i < image.Partitions.Count; i++)
                foreach (Extent e in image.GetPartitionExtents(image.Partitions[i])) total += (ulong)e.Length;
            if (total == 0) throw new InvalidOperationException("The image contains no data to write.");

            byte[] buffer = new byte[4 << 20];
            ulong done = 0;
            int lastPercent = -1;
            using (FileStream src = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.RandomAccess))
            using (RawDisk dst = new RawDisk(disk.Number, true))
            {
                dst.UpdateProperties();
                for (int i = 0; i < image.Partitions.Count; i++)
                {
                    ImagePartition ip = image.Partitions[i];
                    PartitionInfo tp = created[i];
                    List<Extent> extents = image.GetPartitionExtents(ip);
                    Log.Write("  " + ip.DisplayName + ": " + Fmt.Bytes((ulong)Sum(extents)) + " in " +
                              extents.Count + " extent(s) -> partition " + tp.Number);
                    foreach (Extent e in extents)
                    {
                        CheckCancel(cancelled);
                        long srcAt = (long)ip.Offset + e.Offset;
                        long dstAt = (long)tp.Offset + e.Offset;
                        long left = e.Length;
                        while (left > 0)
                        {
                            CheckCancel(cancelled);
                            int n = (int)Math.Min((long)buffer.Length, left);
                            ReadExact(src, srcAt, buffer, n);
                            dst.WriteAt(dstAt, buffer, n);
                            srcAt += n;
                            dstAt += n;
                            left -= n;
                            done += (ulong)n;
                            int percent = 18 + (int)(64.0 * done / total);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                Report(progress, percent, "Writing gbOS86 to disk... " +
                                    Fmt.Bytes(done) + " / " + Fmt.Bytes(total));
                            }
                        }
                    }
                    dst.Flush();
                }
                dst.Flush();
            }
            return done;
        }

        private static void Verify(DiskInfo disk, LooperImage image, List<PartitionInfo> created,
            Action<int, string> progress, Func<bool> cancelled)
        {
            ulong total = 0;
            for (int i = 0; i < image.Partitions.Count; i++)
                foreach (Extent e in image.GetPartitionExtents(image.Partitions[i])) total += (ulong)e.Length;

            byte[] want = new byte[4 << 20];
            byte[] got = new byte[4 << 20];
            ulong done = 0;
            int lastPercent = -1;
            using (FileStream src = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.RandomAccess))
            using (RawDisk raw = new RawDisk(disk.Number, false))
            {
                for (int i = 0; i < image.Partitions.Count; i++)
                {
                    ImagePartition ip = image.Partitions[i];
                    PartitionInfo tp = created[i];
                    foreach (Extent e in image.GetPartitionExtents(ip))
                    {
                        CheckCancel(cancelled);
                        long srcAt = (long)ip.Offset + e.Offset;
                        long dstAt = (long)tp.Offset + e.Offset;
                        long left = e.Length;
                        while (left > 0)
                        {
                            CheckCancel(cancelled);
                            int n = (int)Math.Min((long)want.Length, left);
                            ReadExact(src, srcAt, want, n);
                            raw.ReadAt(dstAt, got, n);
                            for (int b = 0; b < n; b++)
                            {
                                if (want[b] != got[b])
                                    throw new InvalidDataException(
                                        "Verification failed: partition " + tp.Number + " differs at offset " +
                                        (dstAt - (long)tp.Offset + b) + ". The disk may be failing.");
                            }
                            srcAt += n;
                            dstAt += n;
                            left -= n;
                            done += (ulong)n;
                            int percent = 82 + (int)(10.0 * done / total);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                Report(progress, percent, "Verifying the written data... " +
                                    Fmt.Bytes(done) + " / " + Fmt.Bytes(total));
                            }
                        }
                    }

                    byte[] head = new byte[4096];
                    raw.ReadAt((long)tp.Offset, head, head.Length);
                    string label;
                    FileSystemKind fs = FsLabel.Detect(head, out label);
                    if (fs != ip.Fs || !string.Equals(label, ip.Label, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Verification failed: partition " + tp.Number +
                            " has filesystem " + fs + " / label \"" + label + "\", expected " +
                            ip.Fs + " / \"" + ip.Label + "\".");
                    Log.Write("  verified partition " + tp.Number + " (" + label + ").");
                }
            }
        }

        private static void ReadExact(FileStream fs, long offset, byte[] buffer, int count)
        {
            fs.Seek(offset, SeekOrigin.Begin);
            int done = 0;
            while (done < count)
            {
                int n = fs.Read(buffer, done, count - done);
                if (n <= 0) throw new EndOfStreamException("The image ended unexpectedly.");
                done += n;
            }
        }

        private static string ReadImageSha(string imagePath)
        {
            try
            {
                string sidecar = imagePath + ".sha256";
                if (!File.Exists(sidecar)) return "";
                string text = File.ReadAllText(sidecar).Trim();
                if (text.Length < 64) return "";
                string token = text.Substring(0, 64);
                foreach (char c in token)
                {
                    bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                    if (!hex) return "";
                }
                return token.ToLowerInvariant();
            }
            catch { return ""; }
        }

        private static bool SameDisk(InstallState state, DiskInfo disk)
        {
            if (state.DiskNumber == disk.Number) return true;
            return state.DiskSerial.Length > 0 && disk.Serial.Length > 0 &&
                   string.Equals(state.DiskSerial, disk.Serial, StringComparison.OrdinalIgnoreCase);
        }

        private static DiskInfo Reload(DiskInfo selected)
        {
            List<DiskInfo> disks = DiskService.LoadDisks();
            foreach (DiskInfo d in disks)
            {
                if (!string.IsNullOrEmpty(selected.Serial) && !string.IsNullOrEmpty(d.Serial))
                {
                    if (string.Equals(d.Serial, selected.Serial, StringComparison.OrdinalIgnoreCase))
                        return d;
                }
                else if (d.Number == selected.Number && d.Size == selected.Size)
                {
                    return d;
                }
            }
            throw new InvalidOperationException("The target disk disappeared or was changed. Refresh the disk list.");
        }

        public static void Uninstall(InstallState state, Action<int, string> progress)
        {
            if (state == null) throw new InvalidOperationException("There is no recorded gbOS86 installation.");
            List<DiskInfo> disks = DiskService.LoadDisks();
            DiskInfo disk = null;
            foreach (DiskInfo d in disks)
            {
                if (state.DiskSerial.Length > 0 && string.Equals(d.Serial, state.DiskSerial, StringComparison.OrdinalIgnoreCase))
                    disk = d;
                else if (disk == null && d.Number == state.DiskNumber && d.Size > 0)
                    disk = d;
            }
            if (disk == null)
                throw new InvalidOperationException("The disk that holds gbOS86 is not present.");

            Report(progress, 10, "Removing the boot entry...");
            UefiBootEntry entry = new UefiBootEntry();
            entry.Native = state.BootNative;
            entry.Index = state.BootIndex;
            entry.VariableName = state.BootVariable;
            entry.BcdGuid = state.BcdGuid;
            entry.Description = "gbOS86";
            try { UefiBoot.Remove(entry); }
            catch (Exception ex) { Log.Write("Could not remove the boot entry: " + ex.Message); }

            Report(progress, 30, "Removing gbOS86 partitions...");
            List<int> numbers = new List<int>();
            int stillPresent = 0;
            foreach (int n in state.Partitions)
            {
                PartitionInfo p = disk.FindPartition(n);
                if (p == null) continue;
                stillPresent++;
                // Only remove a partition if its GUID still matches the one we
                // installed; otherwise Windows may have reused the number.
                if (state.PartitionGuids.Count > 0)
                {
                    bool match = false;
                    foreach (string g in state.PartitionGuids)
                        if (string.Equals(g, p.Guid, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                    if (!match) continue;
                }
                numbers.Add(n);
            }
            numbers.Sort();
            numbers.Reverse();
            if (numbers.Count == 0 && stillPresent > 0)
                throw new InvalidOperationException(
                    "The partitions recorded for gbOS86 are no longer the ones on disk " + disk.Number +
                    ". Windows may have reused their partition numbers, so nothing was removed.\n\n" +
                    "Remove the gbOS86 partitions manually in Disk Management, then click Uninstall again.");
            if (numbers.Count > 0) DiskService.RemovePartitions(disk.Number, numbers);
            DiskService.Refresh();
            state.Delete();
            Report(progress, 100, "gbOS86 removed.");
            Log.Write("Removed gbOS86 from disk " + disk.Number + ".");
        }

        private static ulong ZeroPrefix(int index, ulong partitionSize)
        {
            ulong prefix;
            if (index == 0) prefix = 32 * MiB;
            else if (index == 1) prefix = 128 * MiB;
            else if (index == 2) prefix = partitionSize;
            else prefix = 512 * MiB;
            return Math.Min(prefix, partitionSize);
        }

        private static long Sum(List<Extent> extents)
        {
            long total = 0;
            foreach (Extent e in extents) total += e.Length;
            return total;
        }

        private static string DescribeNumbers(List<PartitionInfo> list)
        {
            StringBuilder sb = new StringBuilder();
            foreach (PartitionInfo p in list)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(p.Number);
            }
            return sb.ToString();
        }

        private static void CheckCancel(Func<bool> cancelled)
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
        }

        private static void Step(Action<int, string> progress, int percent, string text)
        {
            Log.Write(text);
            Report(progress, percent, text);
        }

        private static void Report(Action<int, string> progress, int percent, string text)
        {
            if (progress != null) progress(percent, text);
        }
    }
}
