// Small shared helpers: process execution, argument quoting, logging and text
// formatting. C# 5 / .NET Framework 4.8 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GbOs86
{
    internal sealed class ProcessResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;
        public bool TimedOut;
        public bool Cancelled;

        public bool Success { get { return ExitCode == 0 && !TimedOut && !Cancelled; } }

        public string Combined
        {
            get
            {
                string o = StdOut == null ? "" : StdOut.Trim();
                string e = StdErr == null ? "" : StdErr.Trim();
                if (o.Length == 0) return e;
                if (e.Length == 0) return o;
                return o + Environment.NewLine + e;
            }
        }
    }

    internal static class ProcessRunner
    {
        public static string QuoteArg(string arg)
        {
            if (arg == null) return "\"\"";
            if (arg.Length == 0) return "\"\"";
            bool need = false;
            foreach (char c in arg)
            {
                if (c == ' ' || c == '\t' || c == '"') { need = true; break; }
            }
            if (!need) return arg;

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    if (backslashes > 0) sb.Append('\\', backslashes);
                    backslashes = 0;
                    sb.Append(c);
                }
            }
            if (backslashes > 0) sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string JoinArgs(IEnumerable<string> args)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(QuoteArg(a));
            }
            return sb.ToString();
        }

        public static ProcessResult Run(string fileName, string arguments, int timeoutMs)
        {
            return Run(fileName, arguments, timeoutMs, null);
        }

        public static ProcessResult Run(string fileName, string arguments, int timeoutMs, Action<string> onLine)
        {
            return Run(fileName, arguments, timeoutMs, onLine, null);
        }

        public static ProcessResult Run(string fileName, string arguments, int timeoutMs,
            Action<string> onLine, Func<bool> cancelled)
        {
            ProcessResult result = new ProcessResult();
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Environment.CurrentDirectory;

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                StringBuilder output = new StringBuilder();
                StringBuilder error = new StringBuilder();
                object sync = new object();

                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sync) output.AppendLine(e.Data);
                    if (onLine != null) onLine(e.Data);
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sync) error.AppendLine(e.Data);
                    if (onLine != null) onLine(e.Data);
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (timeoutMs <= 0 && cancelled == null)
                {
                    p.WaitForExit();
                }
                else
                {
                    DateTime started = DateTime.UtcNow;
                    bool stopped = false;
                    while (!p.WaitForExit(250))
                    {
                        if (cancelled != null && cancelled())
                        {
                            result.Cancelled = true;
                            stopped = true;
                            break;
                        }
                        if (timeoutMs > 0 && (DateTime.UtcNow - started).TotalMilliseconds > timeoutMs)
                        {
                            result.TimedOut = true;
                            stopped = true;
                            break;
                        }
                    }
                    if (stopped)
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(5000); } catch { }
                    }
                }

                // WaitForExit(int) does not guarantee that the asynchronous
                // stdout/stderr handlers have drained. Without this, the last
                // lines (bcdedit's GUID, the build's GBOS86_PACK marker) can be
                // missing from the result.
                if (p.HasExited)
                {
                    try { p.WaitForExit(); } catch { }
                }

                lock (sync)
                {
                    result.StdOut = output.ToString();
                    result.StdErr = error.ToString();
                }
                try { result.ExitCode = p.ExitCode; }
                catch { result.ExitCode = -1; }
            }
            return result;
        }

        public static ProcessResult RunPowerShell(string script, int timeoutMs)
        {
            return RunPowerShell(script, timeoutMs, null);
        }

        public static ProcessResult RunPowerShell(string script, int timeoutMs, Action<string> onLine)
        {
            string file = Path.Combine(Path.GetTempPath(),
                "gbos86-" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                // -File keeps quoting predictable even when the script is long.
                string header = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false" +
                                Environment.NewLine +
                                "$ProgressPreference = 'SilentlyContinue'" + Environment.NewLine;
                File.WriteAllText(file, header + script, new UTF8Encoding(true));
                string args = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                              QuoteArg(file);
                return Run(PowerShellPath, args, timeoutMs, onLine);
            }
            finally
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
        }

        public static string PowerShellPath
        {
            get
            {
                string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
                if (File.Exists(sys)) return sys;
                return "powershell.exe";
            }
        }

        public static string CmdPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"); }
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static event Action<string> Line;

        public static string FilePath { get { return _path; } }

        public static void Start(string path)
        {
            lock (Gate)
            {
                _path = path;
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(path, "=== gbOS86 installer " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===" + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        public static void Write(string message)
        {
            if (message == null) return;
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            lock (Gate)
            {
                try
                {
                    if (_path != null)
                        File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
                Action<string> handler = Line;
                if (handler != null)
                {
                    try { handler(line); } catch { }
                }
            }
        }

        public static void WriteBlank()
        {
            Write("");
        }
    }

    internal static class Fmt
    {
        public static string Bytes(ulong bytes)
        {
            double b = bytes;
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            int u = 0;
            while (b >= 1024.0 && u < units.Length - 1) { b /= 1024.0; u++; }
            if (u == 0) return ((ulong)b).ToString(CultureInfo.InvariantCulture) + " " + units[u];
            return b.ToString(b >= 100 ? "0" : b >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + units[u];
        }

        public static string Gb(ulong bytes)
        {
            return (bytes / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        }

        public static string Hex(byte[] data)
        {
            if (data == null) return "";
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalHours >= 1) return ((int)t.TotalHours) + "h " + t.Minutes + "m";
            if (t.TotalMinutes >= 1) return ((int)t.TotalMinutes) + "m " + t.Seconds + "s";
            return Math.Max(0, (int)t.TotalSeconds) + "s";
        }

        public static string Trim(string s)
        {
            return s == null ? "" : s.Trim();
        }

        public static string OneLine(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (max > 0 && s.Length > max) return s.Substring(0, max - 3) + "...";
            return s;
        }
    }

    internal static class Paths
    {
        public static string AppDataDir
        {
            get
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gbOS86");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string ProgramDataDir
        {
            get
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "gbOS86");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string ImagesDir
        {
            get
            {
                string p = Path.Combine(AppDataDir, "images");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string LogsDir
        {
            get
            {
                string p = Path.Combine(AppDataDir, "logs");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string StateFile { get { return Path.Combine(ProgramDataDir, "install-state.ini"); } }
        public static string SettingsFile { get { return Path.Combine(AppDataDir, "settings.ini"); } }

        public static string AppDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        public static string ScriptsDir
        {
            get
            {
                string beside = Path.Combine(AppDir, "scripts");
                if (Directory.Exists(beside)) return beside;
                // Development layout: <repo>\dist\gbOS86-Installer.exe + <repo>\scripts
                string parent = Path.GetFullPath(Path.Combine(AppDir, ".."));
                string dev = Path.Combine(parent, "scripts");
                if (Directory.Exists(dev)) return dev;
                return beside;
            }
        }

        public static string FindDefaultGbOsSource()
        {
            List<string> candidates = new List<string>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, "gbos-x86"));
            candidates.Add(Path.Combine(home, "Documents", "gbos-x86"));
            candidates.Add(Path.Combine(home, "gbos-86"));
            candidates.Add(@"C:\gbos-x86");
            string parent = Path.GetFullPath(Path.Combine(AppDir, ".."));
            candidates.Add(Path.Combine(parent, "gbos-x86"));
            candidates.Add(Path.Combine(parent, "..", "gbos-x86"));
            foreach (string c in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(c, "build.sh")) &&
                        Directory.Exists(Path.Combine(c, "usb")))
                        return Path.GetFullPath(c);
                }
                catch { }
            }
            return "";
        }

        public static bool IsGbOsSource(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) &&
                       File.Exists(Path.Combine(path, "build.sh")) &&
                       File.Exists(Path.Combine(path, "usb", "make_super.py")) &&
                       File.Exists(Path.Combine(path, "usb", "out", "BOOTX64.EFI"));
            }
            catch { return false; }
        }
    }

    internal sealed class IniFile
    {
        private readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Path { get; private set; }

        public IniFile() { }

        public IniFile(string path)
        {
            Path = path;
        }

        public static IniFile Load(string path)
        {
            IniFile ini = new IniFile(path);
            ini.Path = path;
            try
            {
                if (File.Exists(path))
                {
                    foreach (string raw in File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        ini._map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }
            return ini;
        }

        public string Get(string key, string fallback)
        {
            string v;
            return _map.TryGetValue(key, out v) ? v : fallback;
        }

        public int GetInt(string key, int fallback)
        {
            int v;
            return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            string v = Get(key, "").ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes") return true;
            if (v == "0" || v == "false" || v == "no") return false;
            return fallback;
        }

        public void Set(string key, string value)
        {
            _map[key] = value == null ? "" : value;
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(Path))
                throw new InvalidOperationException("IniFile.Path is not set.");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# gbOS86 installer settings");
            foreach (KeyValuePair<string, string> kv in _map)
                sb.AppendLine(kv.Key + "=" + kv.Value);

            // Replace the file atomically so a crash cannot leave a truncated
            // state file behind.
            string temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(Path))
                {
                    try { File.Replace(temp, Path, null); }
                    catch
                    {
                        try { File.SetAttributes(Path, FileAttributes.Normal); } catch { }
                        File.Delete(Path);
                        File.Move(temp, Path);
                    }
                }
                else
                {
                    File.Move(temp, Path);
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }

    internal static class JsonText
    {
        // The manifest/state formats deliberately use simple key=value lines, so
        // the only JSON we ever touch is PowerShell's ConvertTo-Json output.
        // This tiny extractor is enough for a single flat object.
        public static string Value(string json, string key)
        {
            if (json == null) return "";
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return "";
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return "";
            if (json[i] == '"')
            {
                i++;
                StringBuilder sb = new StringBuilder();
                while (i < json.Length)
                {
                    char c = json[i++];
                    if (c == '\\' && i < json.Length)
                    {
                        char e = json[i++];
                        if (e == '"' || e == '\\' || e == '/') sb.Append(e);
                        else if (e == 'n') sb.Append('\n');
                        else if (e == 'r') sb.Append('\r');
                        else if (e == 't') sb.Append('\t');
                        else sb.Append(e);
                    }
                    else if (c == '"') break;
                    else sb.Append(c);
                }
                return sb.ToString();
            }
            int start = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']') i++;
            return json.Substring(start, i - start).Trim();
        }
    }

    internal static class Hash
    {
        public static string Sha256File(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (SHA256 sha = SHA256.Create())
                return Fmt.Hex(sha.ComputeHash(fs));
        }

        public static string Sha256Bytes(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return Fmt.Hex(sha.ComputeHash(data));
        }
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(byte[] data, int offset, int count)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }

    internal static class Sync
    {
        public static void Sleep(int ms)
        {
            Thread.Sleep(ms);
        }
    }
}
