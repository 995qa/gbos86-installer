// Docker Desktop integration: build the Linux-side looper image inside a
// container that already has the tools the upstream build expects, then export
// only its non-zero blocks to Windows.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GbOs86
{
    internal sealed class BuildResult
    {
        public string BaseName = "";
        public string PackPath = "";
        public string MapPath = "";
        public string ImagePath = "";
        public string ImageSha256 = "";
        public string PackSha256 = "";
        public long LogicalSize;
        public long PackedSize;
        public int BlockSize;
    }

    internal static class DockerService
    {
        public const string BuilderImage = "gbos86-builder:1";
        public const string DistVolume = "gbos86-build-dist";

        public static string FindDocker()
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string dir in path.Split(';'))
                {
                    try
                    {
                        string exe = Path.Combine(dir.Trim(), "docker.exe");
                        if (File.Exists(exe)) return exe;
                    }
                    catch { }
                }
            }
            string[] common =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"Docker\Docker\resources\bin\docker.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Docker\Docker\resources\bin\docker.exe"),
                @"C:\ProgramData\DockerDesktop\version-bin\docker.exe"
            };
            foreach (string p in common)
                if (File.Exists(p)) return p;
            return "";
        }

        public static bool IsInstalled { get { return FindDocker().Length > 0; } }

        public static ProcessResult CheckDaemon()
        {
            string docker = FindDocker();
            if (docker.Length == 0)
                return null;
            return ProcessRunner.Run(docker, "info --format \"{{.ServerVersion}}\"", 30000);
        }

        public static void EnsureBuilderImage(string scriptsDir, Action<string> log, Func<bool> cancelled)
        {
            string docker = RequireDocker();
            ProcessResult inspect = ProcessRunner.Run(docker, "image inspect " + BuilderImage, 30000);
            if (inspect.Success)
            {
                log("Docker builder image is ready.");
                return;
            }

            string dockerDir = Path.Combine(scriptsDir, "docker");
            string dockerfile = Path.Combine(dockerDir, "Dockerfile");
            if (!File.Exists(dockerfile))
                throw new FileNotFoundException("The Docker build files are missing: " + dockerfile);

            log("Building the gbOS86 builder container (one-time, needs internet)...");
            List<string> args = new List<string>();
            args.Add("build");
            // The image is for x86-64 PCs; force the builder platform so an
            // ARM Docker Desktop does not fetch an ARM busybox.
            args.Add("--platform");
            args.Add("linux/amd64");
            args.Add("-t");
            args.Add(BuilderImage);
            args.Add("-f");
            args.Add(dockerfile);
            args.Add(dockerDir);
            ProcessResult r = ProcessRunner.Run(docker, ProcessRunner.JoinArgs(args), 0,
                delegate(string line) { log("  " + line); }, cancelled);
            if (r.Cancelled) throw new OperationCanceledException();
            if (!r.Success)
                throw new InvalidOperationException("Could not build the Docker builder image.\n\n" + r.Combined);
            log("Builder container built.");
        }

        public static BuildResult BuildLooper(string sourceDir, int dataGb, string imagesDir,
            string scriptsDir, Action<string> log, Func<bool> cancelled)
        {
            string docker = RequireDocker();
            EnsureBuilderImage(scriptsDir, log, cancelled);

            string containerName = "gbos86-build-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string buildScript = "/opt/gbos86/build-in-container.sh";

            List<string> args = new List<string>();
            args.Add("run");
            args.Add("--rm");
            args.Add("--name");
            args.Add(containerName);
            args.Add("--platform");
            args.Add("linux/amd64");
            args.Add("-v");
            args.Add(sourceDir + ":/src");
            args.Add("-v");
            args.Add(DistVolume + ":/src/dist");
            args.Add("-v");
            args.Add(imagesDir + ":/images");
            args.Add("-w");
            args.Add("/src");
            args.Add("-e");
            args.Add("GBOS_DATA_GB=" + dataGb.ToString(CultureInfo.InvariantCulture));
            args.Add(BuilderImage);
            args.Add("sh");
            args.Add(buildScript);

            log("Building the gbOS86 loopback image in Docker.");
            log("  source: " + sourceDir);
            log("  data partition: " + dataGb + " GB");
            log("The first build downloads about 7.8 GB from Google and the project releases.");

            BuildResult result = null;
            ProcessResult r = ProcessRunner.Run(docker, ProcessRunner.JoinArgs(args), 0,
                delegate(string line)
                {
                    if (line.StartsWith("GBOS86_RESULT|", StringComparison.Ordinal))
                    {
                        result = ParseResult(line, imagesDir);
                        log("Image build finished.");
                        return;
                    }
                    if (line.StartsWith("GBOS86_PACK|", StringComparison.Ordinal))
                    {
                        ParsePack(line, imagesDir, result);
                        return;
                    }
                    log("  " + line);
                }, cancelled);

            if (r.Cancelled)
            {
                KillContainer(docker, containerName);
                throw new OperationCanceledException();
            }
            if (!r.Success)
                throw new InvalidOperationException("The gbOS86 image build failed.\n\n" + Tail(r.Combined, 4000));
            if (result == null)
                throw new InvalidOperationException("The build finished but did not report an image.\n\n" +
                    Tail(r.Combined, 4000));
            if (!IsSafeBaseName(result.BaseName))
                throw new InvalidOperationException("The build reported an invalid image name: " + result.BaseName);
            if (result.PackedSize <= 0)
                throw new InvalidOperationException("The build finished but did not report the exported image size.\n\n" +
                    Tail(r.Combined, 4000));
            if (!File.Exists(result.PackPath) || !File.Exists(result.MapPath))
                throw new InvalidOperationException("The build reported an image but the exported files are missing.");

            result.ImagePath = Path.Combine(imagesDir, result.BaseName + ".img");
            return result;
        }

        private static BuildResult ParseResult(string line, string imagesDir)
        {
            string[] f = line.Split('|');
            BuildResult r = new BuildResult();
            if (f.Length >= 2) r.BaseName = f[1].Trim();
            if (f.Length >= 3)
            {
                long size;
                long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                r.LogicalSize = size;
            }
            if (f.Length >= 4) r.ImageSha256 = f[3].Trim();
            r.PackPath = Path.Combine(imagesDir, r.BaseName + ".pack");
            r.MapPath = Path.Combine(imagesDir, r.BaseName + ".pack.map");
            return r;
        }

        private static void ParsePack(string line, string imagesDir, BuildResult result)
        {
            if (result == null) return;
            string[] f = line.Split('|');
            // GBOS86_PACK|base|logical|block|packed|sha256
            if (f.Length >= 6)
            {
                long logical, packed;
                int block;
                long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out logical);
                int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out block);
                long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out packed);
                if (logical > 0) result.LogicalSize = logical;
                if (block > 0) result.BlockSize = block;
                if (packed > 0) result.PackedSize = packed;
                result.PackSha256 = f[5].Trim();
            }
        }

        private static void KillContainer(string docker, string name)
        {
            try { ProcessRunner.Run(docker, "rm -f " + name, 60000); } catch { }
        }

        // The name comes from the build container's output and is used to form
        // file paths on Windows, so reject anything that is not a plain file
        // name (no separators, no "..").
        private static bool IsSafeBaseName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 200) return false;
            if (name == "." || name == "..") return false;
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
                if (!ok) return false;
            }
            return true;
        }

        private static string RequireDocker()
        {            string docker = FindDocker();
            if (docker.Length == 0)
                throw new InvalidOperationException(
                    "Docker Desktop is not installed.\n\n" +
                    "Building the gbOS86 image needs Docker Desktop for Windows (Linux containers). " +
                    "Install it from https://www.docker.com/products/docker-desktop/ and start it, " +
                    "then click Build again.\n\n" +
                    "You can also browse to a gbOS86-looper-*.img that was built elsewhere.");
            ProcessResult check = ProcessRunner.Run(docker, "info --format \"{{.ServerVersion}}\"", 30000);
            if (!check.Success)
                throw new InvalidOperationException(
                    "Docker Desktop is installed but its engine is not running.\n\n" +
                    "Start Docker Desktop, wait for \"Engine running\", then try again.");
            return docker;
        }

        private static string Tail(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Trim();
            if (text.Length <= max) return text;
            return "..." + text.Substring(text.Length - max);
        }
    }
}
