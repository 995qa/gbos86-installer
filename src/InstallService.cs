// The destructive half: validate the target, create the four partitions in
// unallocated GPT space, write the image payload, verify it. The firmware's
// boot menu picks up the new ESP on its own, so no boot entry is added. Every
// failure after partition creation rolls the disk back.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace GbOs86
{
    internal sealed class PreflightResult
    {
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<PartitionInfo> ExistingGbOs;
        public bool HasExisting;
        public FreeExtent Free;
        /// <summary>Free area that will exist after the planned shrink, if any.</summary>
        public FreeExtent PlannedFree;
        public ShrinkCandidate Shrink;
        public ulong ShrinkBytes;
        public bool MakeRoom;
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
        public List<PartitionInfo> Partitions = new List<PartitionInfo>();
        public ulong BytesWritten;
    }

    internal sealed class ShrinkUndo
    {
        public int PartitionNumber;
        public string PartitionGuid = "";
        public ulong OriginalSize;
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
        /// <summary>Windows volume the installer shrank, so Uninstall can restore it.</summary>
        public int ShrinkPartition = -1;
        public string ShrinkPartitionGuid = "";
        public ulong ShrinkOriginalSize;

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
            s.ShrinkPartition = ini.GetInt("ShrinkPartition", -1);
            s.ShrinkPartitionGuid = ini.Get("ShrinkPartitionGuid", "");
            s.ShrinkOriginalSize = ini.GetU64("ShrinkOriginalSize", 0);
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
            ini.Set("ShrinkPartition", ShrinkPartition.ToString(CultureInfo.InvariantCulture));
            ini.Set("ShrinkPartitionGuid", ShrinkPartitionGuid);
            ini.Set("ShrinkOriginalSize", ShrinkOriginalSize.ToString(CultureInfo.InvariantCulture));
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

        public static PreflightResult Preflight(DiskInfo disk, LooperImage image, bool makeRoom)
        {
            PreflightResult r = new PreflightResult();
            r.MakeRoom = makeRoom;
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
                r.Warnings.Add("Disk " + disk.Number + " also holds the running Windows installation. " +
                               "gbOS86 will be added to this disk; Windows is kept.");
            if (disk.IsReadOnly)
                r.Errors.Add("Disk " + disk.Number + " is read-only.");
            if (disk.IsOffline)
                r.Errors.Add("Disk " + disk.Number + " is offline.");
            if (!disk.LabelsRead && !DiskService.ReadLabels(disk))
                r.Errors.Add("Cannot read the partition table on disk " + disk.Number +
                             ". Close Disk Management and any other program that uses the disk, then refresh.");

            // The image is read through the normal file system, so it may live on
            // the target disk; only the partitions being replaced would be a problem.
            string imageLetter = "";
            bool imageOnTarget = false;
            try
            {
                string root = Path.GetPathRoot(image.Path);
                if (!string.IsNullOrEmpty(root) && root.Length >= 2)
                {
                    imageLetter = root.Substring(0, 1);
                    foreach (PartitionInfo p in disk.Partitions)
                    {
                        if (string.Equals(p.DriveLetter, imageLetter, StringComparison.OrdinalIgnoreCase))
                        {
                            imageOnTarget = true;
                            break;
                        }
                    }
                }
            }
            catch { }

            List<PartitionInfo> existing = DiskService.FindGbOsInstall(disk);
            bool partial = existing == null && DiskService.HasPartialGbOs(disk);
            bool imageOnExisting = false;
            if (partial)
                r.Errors.Add("Disk " + disk.Number + " already has some gbOS86 partitions, but not a complete set. " +
                             "Remove them with Disk Management (or the installer's Uninstall button) first.");
            if (existing != null)
            {
                r.HasExisting = true;
                r.ExistingGbOs = existing;
                r.Warnings.Add("An existing gbOS86 installation was found on disk " + disk.Number +
                               ". It will be removed and replaced.");
                foreach (PartitionInfo p in existing)
                    if (imageLetter.Length > 0 &&
                        string.Equals(p.DriveLetter, imageLetter, StringComparison.OrdinalIgnoreCase))
                        imageOnExisting = true;
                if (imageOnExisting)
                    r.Errors.Add("The image file is stored on a gbOS86 partition that will be replaced. " +
                                 "Move it to another disk before installing.");
            }
            if (imageOnTarget && !imageOnExisting)
                r.Warnings.Add("The image file is stored on the target disk (" + imageLetter + ":). It is not " +
                               "modified, but keep a copy elsewhere in case the install is interrupted.");

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
                string shortfall = "Not enough unallocated space on disk " + disk.Number + ".\n" +
                    "gbOS86 needs one contiguous free area of " + Fmt.Bytes(r.Required) +
                    " (EFI 512 MiB + system 8 GiB + metadata 64 MiB + data " +
                    image.DataGb.ToString("0.#", CultureInfo.InvariantCulture) + " GB).\n" +
                    "The largest free area is " + Fmt.Bytes(have) + ".";

                if (!makeRoom)
                {
                    r.Errors.Add(shortfall + "\n\n" +
                        "Tick \"Make room by shrinking the Windows volume\" to let the installer shrink an " +
                        "NTFS volume on this disk, or free up space on the disk yourself and refresh.");
                }
                else
                {
                    List<ShrinkCandidate> candidates = DiskService.ProbeShrinkCandidates(disk);
                    ShrinkCandidate candidate = DiskService.PickShrinkCandidate(candidates, r.Required);
                    if (candidate == null)
                    {
                        r.Errors.Add(shortfall + "\n\n" +
                            "No NTFS volume on this disk can be shrunk automatically. Free up space in " +
                            "Windows (or shrink the volume in Disk Management) and refresh. Building the " +
                            "image with a smaller data partition also needs less space.");
                    }
                    else
                    {
                        FreeExtent after;
                        ulong shrink = DiskService.PlanShrink(disk, candidate.PartitionNumber, r.Required,
                            candidate.MaxShrink, exclude, out after);
                        if (shrink == 0)
                        {
                            string limit = candidate.MaxShrink == 0
                                ? candidate.DisplayName + " cannot be shrunk further."
                                : candidate.DisplayName + " can be shrunk by at most " +
                                  Fmt.Bytes(candidate.MaxShrink) + ", which would give one free area of " +
                                  Fmt.Bytes(after.Length) + ".";
                            r.Errors.Add(shortfall + "\n\n" + limit + "\n" +
                                "Free up space in Windows (or shrink the volume in Disk Management) and refresh. " +
                                "Building the image with a smaller data partition also needs less space.");
                        }
                        else
                        {
                            r.Shrink = candidate;
                            r.ShrinkBytes = shrink;
                            r.PlannedFree = after;
                            r.Warnings.Add(candidate.DisplayName + " will be shrunk by " + Fmt.Bytes(shrink) +
                                " (from " + Fmt.Bytes(candidate.VolumeSize) + " to " +
                                Fmt.Bytes(candidate.VolumeSize - shrink) + ") to make room for gbOS86. " +
                                "Uninstalling gbOS86 restores its size when possible.");
                            ulong staysFree = candidate.VolumeFree > shrink ? candidate.VolumeFree - shrink : 0;
                            if (staysFree < 10 * DiskService.GiB)
                                r.Warnings.Add("Only " + Fmt.Bytes(staysFree) + " will stay free on " +
                                    candidate.DisplayName + ". Free up space in Windows first if it needs more.");
                            if (imageLetter.Length > 0 &&
                                string.Equals(imageLetter, candidate.DriveLetter, StringComparison.OrdinalIgnoreCase))
                                r.Warnings.Add("The image file is on " + candidate.DisplayName + ", which limits how " +
                                    "far Windows can shrink. If Windows cannot move it, the install stops before " +
                                    "any partition is changed; move the image to another disk in that case.");
                        }
                    }
                }
            }

            if (r.Required > 0 && disk.Size < r.Required)
                r.Errors.Add("The image is larger than the selected disk.");

            bool? secureBoot = DiskService.SecureBootEnabled;
            if (secureBoot == true)
                r.Warnings.Add("Secure Boot is enabled. gbOS86's BOOTX64.EFI is not signed, so the firmware will " +
                               "refuse to boot it. Turn Secure Boot off in the UEFI settings after installing, or " +
                               "Windows and gbOS86 will not both boot.");
            foreach (string w in image.Warnings) r.Warnings.Add(w);

            if (r.Shrink != null && r.PlannedFree != null && r.Required > 0)
                r.Warnings.Add("After the shrink, new partitions will be created at disk offset " +
                               Fmt.Bytes(r.PlannedFree.Offset) + ", using " + Fmt.Bytes(r.Required) + " of the " +
                               Fmt.Bytes(r.PlannedFree.Length) + " free area.");
            else if (best != null && r.Required > 0)
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
            List<PartitionInfo> created = new List<PartitionInfo>();
            List<VolumeLock> locks = new List<VolumeLock>();
            ShrinkUndo shrink = null;
            bool destructive = false;
            bool makeRoom = pre.MakeRoom;
            InstallState previousState = InstallState.Load();
            try
            {
                try { Native.EnablePrivilege("SeManageVolumePrivilege"); }
                catch (Exception ex) { Log.Write("Note: SeManageVolumePrivilege: " + ex.Message); }

                Step(progress, 2, "Checking the target disk...");
                DiskInfo disk = Reload(selectedDisk);
                PreflightResult fresh = Preflight(disk, image, makeRoom);
                if (!fresh.Ok) throw new InvalidOperationException(fresh.Summary);
                pre = fresh;
                DescribeTarget(disk, pre);

                if (pre.HasExisting)
                {
                    Step(progress, 4, "Removing the previous gbOS86 installation...");
                    // A previous install may have used the bcdedit fallback
                    // instead of a native Boot#### entry. Remove the entry the
                    // installer recorded, but only if it belongs to this disk.
                    if (previousState != null && !previousState.BootNative && previousState.BcdGuid.Length > 0 &&
                        SameDisk(previousState, disk))
                    {
                        UefiBootEntry oldBcd = new UefiBootEntry();
                        oldBcd.Native = false;
                        oldBcd.BcdGuid = previousState.BcdGuid;
                        oldBcd.Description = "gbOS86";
                        try { UefiBoot.Remove(oldBcd); }
                        catch (Exception ex) { Log.Write("Could not remove the old BCD entry: " + ex.Message); }
                    }
                    // Older installers added a "gbOS86" entry to the firmware
                    // boot menu or the Windows Boot Manager. Those point at the
                    // partitions being replaced, so clear them; this installer
                    // does not add one.
                    UefiBootEntry stale = UefiBoot.Find("gbOS86");
                    if (stale != null)
                    {
                        try { UefiBoot.Remove(stale); }
                        catch (Exception ex) { Log.Write("Could not remove the old boot entry: " + ex.Message); }
                    }
                    List<int> old = new List<int>();
                    foreach (PartitionInfo p in pre.ExistingGbOs) old.Add(p.Number);
                    old.Sort();
                    old.Reverse();
                    DiskService.RemovePartitions(disk.Number, old);
                    DiskService.Refresh();
                    disk = Reload(selectedDisk);
                    pre = Preflight(disk, image, makeRoom);
                    if (!pre.Ok) throw new InvalidOperationException(pre.Summary);
                }

                // Same-drive installs usually start with no unallocated space.
                // Shrink a Windows volume, then re-read the disk so the new
                // partitions go into the space that was just freed.
                CheckCancel(cancelled);
                if (pre.Shrink != null && pre.ShrinkBytes > 0)
                {
                    PartitionInfo target = disk.FindPartition(pre.Shrink.PartitionNumber);
                    if (target == null)
                        throw new InvalidOperationException(
                            "The volume that was going to be shrunk is no longer on disk " + disk.Number +
                            ". Refresh and try again.");
                    ulong originalSize = target.Size;
                    ulong newSize = originalSize > pre.ShrinkBytes ? originalSize - pre.ShrinkBytes : 0;
                    Step(progress, 6, "Shrinking " + pre.Shrink.DisplayName + " by " +
                        Fmt.Bytes(pre.ShrinkBytes) + " to make room...");
                    Log.Write("  " + pre.Shrink.DisplayName + ": " + Fmt.Bytes(originalSize) + " -> " +
                              Fmt.Bytes(newSize));
                    DiskService.ResizePartition(disk.Number, target.Number, newSize);
                    shrink = new ShrinkUndo();
                    shrink.PartitionNumber = target.Number;
                    shrink.PartitionGuid = target.Guid;
                    shrink.OriginalSize = originalSize;
                    DiskService.Refresh();
                    disk = Reload(selectedDisk);
                    pre = Preflight(disk, image, makeRoom);
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
                // Keep these locks for the whole write. If Windows mounts a
                // filesystem on one of the new volumes (GBOSSYS is a Basic Data
                // partition and will look like FAT32 as soon as its boot sector
                // lands), raw writes to its sectors fail with access denied.
                foreach (PartitionInfo p in created)
                    LockPartition(p, locks);
                Log.Write("Locked " + locks.Count + " volume(s) for the raw write.");

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
                report.BytesWritten = Flash(disk, image, created, progress, cancelled,
                    delegate(PartitionInfo p) { return LockPartition(p, locks); });

                CheckCancel(cancelled);
                Step(progress, 82, "Verifying the written data...");
                Verify(disk, image, created, progress, cancelled);

                // Release the volume locks before saving state or deleting
                // anything: a locked volume cannot be removed by a rollback.
                ReleaseLocks(locks);

                Step(progress, 90, "Saving installation state...");
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
                foreach (PartitionInfo p in created)
                {
                    state.Partitions.Add(p.Number);
                    state.PartitionGuids.Add(p.Guid);
                }
                if (shrink != null)
                {
                    state.ShrinkPartition = shrink.PartitionNumber;
                    state.ShrinkPartitionGuid = shrink.PartitionGuid;
                    state.ShrinkOriginalSize = shrink.OriginalSize;
                }
                else if (previousState != null && previousState.ShrinkOriginalSize > 0 &&
                         SameDisk(previousState, disk))
                {
                    // A previous install shrank Windows and this reinstall reused
                    // the freed space. Keep the record so Uninstall can still
                    // restore the original volume size.
                    state.ShrinkPartition = previousState.ShrinkPartition;
                    state.ShrinkPartitionGuid = previousState.ShrinkPartitionGuid;
                    state.ShrinkOriginalSize = previousState.ShrinkOriginalSize;
                }
                state.Save();
                report.Partitions = created;

                Step(progress, 100, "Done.");
                Log.Write("gbOS86 installed on disk " + disk.Number + " (" + disk.Model + ").");
                Log.Write("No boot entry was added; pick gbOS86 in the firmware boot menu (F12).");
                Log.Write("Leave Secure Boot off: gbOS86's boot loader is not signed.");
                return report;
            }
            catch (OperationCanceledException)
            {
                ReleaseLocks(locks);
                Rollback(selectedDisk, created, destructive, shrink);
                throw;
            }
            catch
            {
                ReleaseLocks(locks);
                Rollback(selectedDisk, created, destructive, shrink);
                throw;
            }
        }

        private static void Rollback(DiskInfo disk, List<PartitionInfo> created,
            bool destructive, ShrinkUndo shrink)
        {
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
            if (shrink != null && disk != null)
            {
                try
                {
                    DiskInfo current = Reload(disk);
                    PartitionInfo target = FindByGuid(current, shrink.PartitionGuid);
                    if (target == null) target = current.FindPartition(shrink.PartitionNumber);
                    if (target != null && target.Size < shrink.OriginalSize)
                    {
                        Log.Write("Rollback: restoring partition " + target.Number + " to " +
                                  Fmt.Bytes(shrink.OriginalSize) + "...");
                        DiskService.ResizePartition(current.Number, target.Number, shrink.OriginalSize);
                        DiskService.Refresh();
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Rollback: could not restore the shrunk volume to its original size. " +
                              "Extend it in Disk Management. " + ex.Message);
                }
            }
        }

        private static PartitionInfo FindByGuid(DiskInfo disk, string guid)
        {
            if (disk == null || string.IsNullOrEmpty(guid)) return null;
            foreach (PartitionInfo p in disk.Partitions)
                if (string.Equals(p.Guid, guid, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>Logs what the install is about to change, for support logs.</summary>
        private static void DescribeTarget(DiskInfo disk, PreflightResult pre)
        {
            Log.Write("Target disk " + disk.Number + ": " + disk.Model + ", serial " + disk.SerialOrUnknown +
                      ", " + Fmt.Bytes(disk.Size) + ", " + disk.PartitionStyle +
                      (disk.IsSystem || disk.IsBoot ? " (holds the running Windows)" : ""));
            foreach (PartitionInfo p in disk.Partitions)
                Log.Write("  partition " + p.Number + " at " + Fmt.Bytes(p.Offset) + ", " + Fmt.Bytes(p.Size) +
                          ", " + p.Type + (p.DriveLetter.Length > 0 ? " (" + p.DriveLetter + ":)" : ""));
            if (pre.Shrink != null)
                Log.Write("  plan: shrink " + pre.Shrink.DisplayName + " by " + Fmt.Bytes(pre.ShrinkBytes));
            if (pre.Free != null)
                Log.Write("  free area: " + Fmt.Bytes(pre.Free.Length) + " at " + Fmt.Bytes(pre.Free.Offset));
        }

        /// <summary>
        /// Locks every volume path of a freshly created partition and keeps the
        /// locks in <paramref name="locks"/> until the raw write is done.
        /// </summary>
        private static bool LockPartition(PartitionInfo p, List<VolumeLock> locks)
        {
            bool any = false;
            string paths = p == null || p.AccessPaths == null ? "" : p.AccessPaths;
            foreach (string ap in paths.Split(';'))
            {
                string path = RawDisk.NormalizeVolumePath(ap.Trim());
                if (path.Length == 0) continue;
                if (IsLocked(locks, path)) { any = true; continue; }
                VolumeLock held = RawDisk.LockAndDismountVolume(path);
                if (held != null)
                {
                    locks.Add(held);
                    any = true;
                }
            }
            return any;
        }

        private static bool IsLocked(List<VolumeLock> locks, string path)
        {
            foreach (VolumeLock held in locks)
                if (string.Equals(held.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void ReleaseLocks(List<VolumeLock> locks)
        {
            foreach (VolumeLock held in locks)
            {
                try { held.Dispose(); } catch { }
            }
            locks.Clear();
        }

        private static ulong Flash(DiskInfo disk, LooperImage image, List<PartitionInfo> created,
            Action<int, string> progress, Func<bool> cancelled, Func<PartitionInfo, bool> relock)
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
                              extents.Count + " extent(s) -> partition " + tp.Number + " at " +
                              Fmt.Bytes(tp.Offset));
                    // Hold back the first sector, which carries the FAT32/ext4
                    // signature. Writing it last means Windows never sees a
                    // complete filesystem while the raw write is in progress.
                    byte[] boot = null;
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
                            byte[] block = buffer;
                            if (boot == null && dstAt == (long)tp.Offset && n >= 512)
                            {
                                boot = new byte[512];
                                Buffer.BlockCopy(buffer, 0, boot, 0, 512);
                                block = (byte[])buffer.Clone();
                                Array.Clear(block, 0, 512);
                            }
                            WriteBlock(dst, ip, tp, dstAt, block, n, relock);
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
                    if (boot != null)
                        WriteBlock(dst, ip, tp, (long)tp.Offset, boot, boot.Length, relock);
                    dst.Flush();
                }
                dst.Flush();
            }
            return done;
        }

        /// <summary>
        /// Writes one block, retrying when Windows refuses the raw write because
        /// it mounted a filesystem on the new partition (ERROR_ACCESS_DENIED).
        /// Each retry locks that partition, which dismounts the filesystem.
        /// </summary>
        private static void WriteBlock(RawDisk dst, ImagePartition ip, PartitionInfo tp, long dstAt,
            byte[] buffer, int count, Func<PartitionInfo, bool> relock)
        {
            int blocked = 0;
            while (true)
            {
                try
                {
                    dst.WriteAt(dstAt, buffer, count);
                    return;
                }
                catch (Win32Exception ex)
                {
                    bool accessDenied = ex.NativeErrorCode == Native.ERROR_ACCESS_DENIED;
                    string context = "Writing " + ip.DisplayName + " to partition " + tp.Number +
                                     " failed. " + ex.Message;
                    if (!accessDenied || relock == null || blocked >= 4)
                        throw new InvalidOperationException(context + MountHint(accessDenied), ex);
                    blocked++;
                    Log.Write("  " + context + " Re-locking the volume and retrying (" + blocked + " of 4).");
                    if (!relock(tp))
                        throw new InvalidOperationException(context + MountHint(accessDenied), ex);
                    Thread.Sleep(250);
                }
            }
        }

        private static string MountHint(bool accessDenied)
        {
            return accessDenied
                ? " Windows mounted a filesystem on the new volume during the write. Close Explorer, " +
                  "Disk Management and anything else that uses the disk, then run the installer again."
                : "";
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
            int repaired = 0;
            using (FileStream src = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.RandomAccess))
            using (RawDisk raw = new RawDisk(disk.Number, true))
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
                            int at = FirstDiff(want, got, n);
                            if (at >= 0)
                            {
                                long rel = dstAt - (long)tp.Offset + at;
                                int diff = CountDiff(want, got, n);
                                Log.Write("  partition " + tp.Number + " differs at " + rel + " (" + diff +
                                          " byte(s) in a " + Fmt.Bytes((ulong)n) + " block at disk offset " +
                                          Fmt.Bytes((ulong)dstAt) + ").");
                                Log.Write("    image: " + Hex(want, at) + "  disk: " + Hex(got, at));
                                // A read that races a flush is not a disk failure;
                                // check once more before writing again.
                                raw.ReadAt(dstAt, got, n);
                                if (FirstDiff(want, got, n) < 0)
                                {
                                    Log.Write("  the block matched on a second read.");
                                }
                                else
                                {
                                    Log.Write("  rewriting the block and checking it again...");
                                    raw.WriteAt(dstAt, want, n);
                                    raw.Flush();
                                    raw.ReadAt(dstAt, got, n);
                                    if (FirstDiff(want, got, n) >= 0)
                                    {
                                        Log.Write("    image: " + Hex(want, at) + "  disk: " + Hex(got, at));
                                        throw new InvalidDataException(
                                            "Verification failed: partition " + tp.Number + " still differs at offset " +
                                            rel + " after rewriting " + Fmt.Bytes((ulong)n) + ". The disk may be failing.");
                                    }
                                    repaired++;
                                    Log.Write("  the block matched after one rewrite. The disk dropped " +
                                              repaired + " block(s) so far; check it with the manufacturer's tool.");
                                }
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
            if (repaired > 0)
                Log.Write("WARNING: " + repaired + " block(s) did not survive the first write and had to be " +
                          "rewritten. The disk may be failing; back up important data and check it.");
        }

        private static int FirstDiff(byte[] a, byte[] b, int count)
        {
            for (int i = 0; i < count; i++)
                if (a[i] != b[i]) return i;
            return -1;
        }

        private static int CountDiff(byte[] a, byte[] b, int count)
        {
            int diff = 0;
            for (int i = 0; i < count; i++)
                if (a[i] != b[i]) diff++;
            return diff;
        }

        private static string Hex(byte[] data, int at)
        {
            StringBuilder sb = new StringBuilder();
            int end = Math.Min(data.Length, at + 16);
            for (int i = at; i < end; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
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

            // Older installers added a "gbOS86" entry to the firmware boot menu
            // or the Windows Boot Manager; remove the recorded one if this
            // installation has it. Current installs add no entry.
            if (state.BootIndex >= 0 || state.BootVariable.Length > 0 || state.BcdGuid.Length > 0)
            {
                Report(progress, 10, "Removing the old boot entry...");
                UefiBootEntry entry = new UefiBootEntry();
                entry.Native = state.BootNative;
                entry.Index = state.BootIndex;
                entry.VariableName = state.BootVariable;
                entry.BcdGuid = state.BcdGuid;
                entry.Description = "gbOS86";
                try { UefiBoot.Remove(entry); }
                catch (Exception ex) { Log.Write("Could not remove the boot entry: " + ex.Message); }
            }

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

            // If the install shrank a Windows volume, give the space back now
            // that the gbOS86 partitions are gone. A failure here is not fatal:
            // the user can extend the volume in Disk Management.
            if (state.ShrinkOriginalSize > 0)
            {
                Report(progress, 80, "Restoring the shrunk volume...");
                try
                {
                    DiskInfo current = null;
                    foreach (DiskInfo d in DiskService.LoadDisks())
                    {
                        if (state.DiskSerial.Length > 0 &&
                            string.Equals(d.Serial, state.DiskSerial, StringComparison.OrdinalIgnoreCase))
                            current = d;
                        else if (current == null && d.Number == state.DiskNumber)
                            current = d;
                    }
                    PartitionInfo target = FindByGuid(current, state.ShrinkPartitionGuid);
                    if (target == null && current != null && state.ShrinkPartition > 0)
                        target = current.FindPartition(state.ShrinkPartition);
                    if (target == null)
                    {
                        Log.Write("The volume that was shrunk is no longer present; leaving its size alone.");
                    }
                    else if (target.Size >= state.ShrinkOriginalSize)
                    {
                        Log.Write("The shrunk volume already has its original size.");
                    }
                    else
                    {
                        Log.Write("Restoring partition " + target.Number + " to " +
                                  Fmt.Bytes(state.ShrinkOriginalSize) + "...");
                        DiskService.ResizePartition(current.Number, target.Number, state.ShrinkOriginalSize);
                        DiskService.Refresh();
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Could not restore the shrunk volume to its original size. " +
                              "Extend it in Disk Management if you want the space back. " + ex.Message);
                }
            }

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
