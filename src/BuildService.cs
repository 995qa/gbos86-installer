// Orchestrates image acquisition: either build through Docker or validate a
// user-supplied looper image.
using System;
using System.IO;

namespace GbOs86
{
    internal static class BuildService
    {
        public static string BuildViaDocker(string sourceDir, int dataGb, string imagesDir,
            string scriptsDir, Action<string> log, Func<bool> cancelled)
        {
            if (!Paths.IsGbOsSource(sourceDir))
                throw new InvalidOperationException(
                    "This does not look like a gbos-x86 checkout:\n" + sourceDir +
                    "\n\nExpected build.sh, usb/make_super.py and usb/out/BOOTX64.EFI.");

            BuildResult result = DockerService.BuildLooper(sourceDir, dataGb, imagesDir, scriptsDir, log, cancelled);
            if (cancelled != null && cancelled()) throw new OperationCanceledException();

            // The pack and the rebuilt sparse image coexist until Materialize
            // finishes, so peak usage is about twice the pack. Remove an older
            // image first (the pack is already complete and verified, so a
            // failed rebuild can simply be retried).
            TryDelete(result.ImagePath);
            TryDelete(result.ImagePath + ".extents");

            long need = (2L * result.PackedSize) + (256L << 20);
            if (File.Exists(result.ImagePath)) need += result.PackedSize; // still locked
            string root = Path.GetPathRoot(imagesDir);
            try
            {
                DriveInfo drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < need)
                    throw new InvalidOperationException(
                        "Not enough free space for the rebuilt image.\n\nNeed about " +
                        Fmt.Bytes((ulong)need) + " on " + root + ", have " +
                        Fmt.Bytes((ulong)drive.AvailableFreeSpace) + ".");
            }
            catch (ArgumentException) { }

            log("Unpacking the image onto NTFS (only non-zero blocks)...");
            PackedImage packed = PackedImage.Load(result.MapPath);
            if (packed.LogicalSize != result.LogicalSize && result.LogicalSize > 0)
                log("Warning: the image map reports a different logical size than the build.");
            if (result.PackSha256.Length > 0 &&
                !string.Equals(result.PackSha256, packed.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The exported image map does not match the build's SHA-256.");

            long lastReport = 0;
            packed.Materialize(result.ImagePath, delegate(long done, long total)
            {
                if (total <= 0) return;
                if (done < total && done - lastReport < (256L << 20)) return;
                lastReport = done;
                log("  unpacked " + Fmt.Bytes((ulong)done) + " of " + Fmt.Bytes((ulong)total));
            }, cancelled);

            if (cancelled != null && cancelled()) throw new OperationCanceledException();

            using (LooperImage image = LooperImage.Open(result.ImagePath))
            {
                image.SaveExtentMap();
                LogPartitions(image);
            }

            TryDelete(result.PackPath);
            TryDelete(result.MapPath);
            return result.ImagePath;
        }

        public static LooperImage OpenExisting(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                throw new FileNotFoundException("Image not found: " + imagePath);
            LooperImage image = LooperImage.Open(imagePath);
            try
            {
                image.SaveExtentMap();
                LogPartitions(image);
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        public static void LogPartitions(LooperImage image)
        {
            Log.Write("Image: " + image.Path);
            Log.Write("  logical size " + Fmt.Bytes((ulong)image.FileSize) +
                      ", data written " + Fmt.Bytes((ulong)image.AllocatedBytes) +
                      (image.IsSparse ? " (sparse)" : ""));
            foreach (ImagePartition p in image.Partitions)
            {
                Log.Write("  p" + p.Number + " " + p.DisplayName + "  " + Fmt.Bytes(p.Size) +
                          "  " + p.Fs + (p.Label.Length > 0 ? "  label " + p.Label : ""));
            }
            foreach (string w in image.Warnings) Log.Write("  warning: " + w);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
                File.Delete(path);
            }
            catch { }
        }
    }
}
