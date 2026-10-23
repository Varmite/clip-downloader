// YT Downloader - a small, fast Windows front-end for yt-dlp + ffmpeg.
// Built with the C# compiler that ships with Windows (.NET Framework 4.8), so it starts instantly.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("YT Downloader")]
[assembly: AssemblyProduct("YT Downloader")]
[assembly: AssemblyDescription("Download YouTube, TikTok and Instagram videos (H.264 MP4/MKV) and audio (MP3/M4A)")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace YTDownloader
{
    static class Program
    {
        public static readonly DateTime StartedUtc = DateTime.UtcNow;

        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => MessageBox.Show(e.Exception.Message, "YT Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Settings.Load();
            Engine.Resolve();
            Application.Run(new MainForm());
        }
    }

    static class Paths
    {
        public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string Bin = Path.Combine(AppDir, "bin");
        public static readonly string FFmpeg = Path.Combine(Bin, "ffmpeg.exe");
        public static readonly string FFprobe = Path.Combine(Bin, "ffprobe.exe");
        public static readonly string Deno = Path.Combine(Bin, "deno.exe");
        public static readonly string UserData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YTDownloader");
        public static readonly string EngineRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YTDownloader", "engine");
        public static readonly string Temp = Path.Combine(Path.GetTempPath(), "YTDownloader");
    }

    static class Settings
    {
        static readonly Dictionary<string, string> values = new Dictionary<string, string>();
        static readonly string file = Path.Combine(Paths.UserData, "settings.txt");

        public static void Load()
        {
            try
            {
                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) values[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch { }
        }

        public static string Get(string key, string fallback)
        {
            lock (values) { string v; return values.TryGetValue(key, out v) ? v : fallback; }
        }

        public static int GetInt(string key, int fallback, int max)
        {
            int v;
            return int.TryParse(Get(key, null), out v) && v >= 0 && v <= max ? v : fallback;
        }

        public static void Set(string key, string value) { lock (values) values[key] = value; }

        public static void Remove(string key) { lock (values) values.Remove(key); }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.UserData);
                lock (values) File.WriteAllLines(file, values.Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // Finds the newest yt-dlp: the copy bundled with the app or an auto-updated one.
    static class Engine
    {
        public static string Exe { get; private set; }
        public static string Version { get; private set; }

        public static void Resolve()
        {
            string bundled = Path.Combine(Paths.AppDir, "yt-dlp");
            string bestExe = Path.Combine(bundled, "yt-dlp.exe");
            string bestVer = ReadVersion(bundled) ?? "";
            try
            {
                if (Directory.Exists(Paths.EngineRoot))
                {
                    foreach (var dir in Directory.GetDirectories(Paths.EngineRoot))
                    {
                        if (dir.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                        string ver = ReadVersion(dir);
                        string exe = Path.Combine(dir, "yt-dlp.exe");
                        if (ver != null && string.CompareOrdinal(ver, bestVer) > 0 && File.Exists(exe)) { bestVer = ver; bestExe = exe; }
                    }
                }
            }
            catch { }
            Exe = bestExe;
            Version = bestVer;
        }

        static string ReadVersion(string dir)
        {
            try { return File.ReadAllText(Path.Combine(dir, "version.txt")).Trim(); } catch { return null; }
        }
    }

    static class Updater
    {
        static int running;

        public static bool IsDue()
        {
            long ticks;
            return !long.TryParse(Settings.Get("lastUpdateCheck", null), out ticks) || DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromHours(12);
        }

        // Installs the latest yt-dlp release if it is newer than ours. Returns true if it did.
        public static bool CheckAndUpdate()
        {
            if (Interlocked.Exchange(ref running, 1) == 1) return false;
            string zip = null, tmp = null;
            try
            {
                Net.Init();
                Settings.Set("lastUpdateCheck", DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                Settings.Save();
                string tag = LatestTag();
                if (tag == null || string.CompareOrdinal(tag, Engine.Version) <= 0) return false;

                string url = "https://github.com/yt-dlp/yt-dlp/releases/download/" + tag + "/";
                Directory.CreateDirectory(Paths.EngineRoot);
                zip = Path.Combine(Paths.EngineRoot, tag + ".zip");
                tmp = Path.Combine(Paths.EngineRoot, tag + ".tmp");
                string dest = Path.Combine(Paths.EngineRoot, tag);
                string expected = null;
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "YTDownloader";
                    foreach (var line in wc.DownloadString(url + "SHA2-256SUMS").Split('\n'))
                    {
                        var parts = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2 && parts[1] == "yt-dlp_win.zip") expected = parts[0];
                    }
                    if (expected == null) return false;
                    wc.Headers[HttpRequestHeader.UserAgent] = "YTDownloader";
                    wc.DownloadFile(url + "yt-dlp_win.zip", zip);
                }
                string actual;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(zip))
                    actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return false;

                Tools.TryDeleteDir(tmp);
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, tmp);
                File.WriteAllText(Path.Combine(tmp, "version.txt"), tag);
                Tools.TryDeleteDir(dest);
                Directory.Move(tmp, dest);
                Engine.Resolve();
                return true;
            }
            catch { return false; }
            finally
            {
                if (zip != null) Tools.TryDelete(zip);
                if (tmp != null) Tools.TryDeleteDir(tmp);
                running = 0;
            }
        }

        static string LatestTag()
        {
            var req = (HttpWebRequest)WebRequest.Create("https://github.com/yt-dlp/yt-dlp/releases/latest");
            req.Method = "HEAD";
            req.AllowAutoRedirect = false;
            req.UserAgent = "YTDownloader";
            req.Timeout = 15000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                var m = Regex.Match(resp.Headers["Location"] ?? "", @"/tag/(\d{4}\.\d{2}\.\d{2}(?:\.\d+)?)$");
                return m.Success ? m.Groups[1].Value : null;
            }
        }
    }

    static class Net
    {
        static bool done;
        public static void Init()
        {
            if (done) return;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 + 1.3
            done = true;
        }
    }

    // A Windows job object: every process in it dies when we cancel or when the app exits.
    sealed class Job : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        IntPtr handle;
        bool killed;

        public Job()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            var info = new ExtendedLimits();
            info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            SetInformationJobObject(handle, 9, ref info, Marshal.SizeOf(typeof(ExtendedLimits)));
        }

        public void Add(Process p)
        {
            lock (this)
            {
                if (handle == IntPtr.Zero) return;
                AssignProcessToJobObject(handle, p.Handle);
                if (killed) TerminateJobObject(handle, 1);
            }
        }

        public void Kill()
        {
            lock (this)
            {
                killed = true;
                if (handle != IntPtr.Zero) TerminateJobObject(handle, 1);
            }
        }

        public void Dispose()
        {
            lock (this)
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }
    }

    sealed class JobException : Exception
    {
        public JobException(string message) : base(message) { }
    }

    static class Tools
    {
        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                sb.Append(c);
            }
            return sb.Append('\\', slashes * 2).Append('"').ToString();
        }

        public static string Join(IEnumerable<string> args) { return string.Join(" ", args.Select(Quote)); }

        // Runs a console tool without a window, streaming output lines to the callbacks. Returns the exit code.
        public static int Run(string exe, IEnumerable<string> args, Action<string> onOut, Action<string> onErr, Job job)
        {
            var psi = new ProcessStartInfo(exe, Join(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { if (e.Data != null && onOut != null) onOut(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null && onErr != null) onErr(e.Data); };
                p.Start();
                if (job != null) job.Add(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        public static List<string> YtDlpArgs(bool skipHls)
        {
            var a = new List<string> { "--ignore-config", "--ffmpeg-location", Paths.Bin };
            if (File.Exists(Paths.Deno)) a.AddRange(new[] { "--js-runtimes", "deno:" + Paths.Deno });
            a.AddRange(new[] { "--js-runtimes", "node" });
            // HLS manifests are only needed for live streams; skipping them saves about a second.
            if (skipHls) a.AddRange(new[] { "--extractor-args", "youtube:skip=hls" });
            return a;
        }

        static bool Has(string s, string part) { return s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }

        public static bool IsFormatError(string e)
        {
            return e != null && (Has(e, "Requested format is not available") || Has(e, "No video formats found") || Has(e, "Only images are available"));
        }

        public static bool IsPermanentError(string e)
        {
            return e != null && (Has(e, "Private video") || Has(e, "members-only") || Has(e, "confirm your age") || Has(e, "Video unavailable")
                || Has(e, "video is unavailable") || Has(e, "video is not available") || Has(e, "has been removed") || Has(e, "Unsupported URL")
                || Has(e, "live event will begin") || Has(e, "Premieres in"));
        }

        public static string FriendlyError(string e)
        {
            if (string.IsNullOrEmpty(e)) return "Download failed. Click \"Show log\" for details.";
            if (Has(e, "not a bot")) return "YouTube wants to confirm you're not a bot. Wait a few minutes and try again.";
            if (Has(e, "login required") || Has(e, "rate-limit reached") || Has(e, "empty media response") || Has(e, "log in"))
                return "The site wants you to sign in to see this. It may be private, or try again in a few minutes.";
            if (Has(e, "confirm your age") || Has(e, "age-restricted")) return "This video is age-restricted and needs a signed-in YouTube account.";
            if (Has(e, "Private video") || Has(e, "account is private")) return "This video is private.";
            if (Has(e, "IP address is blocked") || Has(e, "not available in your")) return "This video isn't available in your country.";
            if (Has(e, "members-only") || Has(e, "Join this channel")) return "This video is only for channel members.";
            if (IsFormatError(e)) return "No downloadable version of this video was found.";
            if (Has(e, "live event will begin") || Has(e, "Premieres in")) return "This live stream or premiere hasn't started yet.";
            if (Has(e, "HTTP Error 403")) return "The site refused the download (HTTP 403). Try again in a moment.";
            if (Has(e, "Video unavailable") || Has(e, "has been removed") || Has(e, "not available")) return "This video is unavailable.";
            if (Has(e, "Unsupported URL") || Has(e, "is not a valid URL")) return "That link isn't a supported video link.";
            if (Has(e, "No space left") || Has(e, "Errno 28")) return "The disk is full.";
            if (Has(e, "Permission denied") || Has(e, "Errno 13")) return "Can't write to the save folder. Pick another folder.";
            if (Has(e, "getaddrinfo") || Has(e, "Failed to resolve") || Has(e, "timed out") || Has(e, "Unable to download") || Has(e, "Connection"))
                return "Network problem. Check your internet connection and try again.";
            e = Regex.Replace(e, @"^ERROR:\s*(\[[^\]]+\]\s*)?([\w-]{11}:\s*)?", "");
            return e.Length > 160 ? e.Substring(0, 160) + "…" : e;
        }

        public static string FormatSpeed(double bytesPerSec)
        {
            return bytesPerSec >= 1048576 ? (bytesPerSec / 1048576).ToString("0.0") + " MB/s" : (bytesPerSec / 1024).ToString("0") + " KB/s";
        }

        public static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Round(seconds));
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
        }

        public static double Num(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        public static void TryDelete(string file) { try { File.Delete(file); } catch { } }

        public static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
    }

    static class Links
    {
        public static string Normalize(string text)
        {
            if (text == null) return null;
            text = text.Trim();
            if (text.Length == 0 || text.Length > 2000 || text.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0) return null;
            if (!Regex.IsMatch(text, "^https?://", RegexOptions.IgnoreCase)) text = "https://" + text;
            Uri u;
            if (!Uri.TryCreate(text, UriKind.Absolute, out u) || u.Host.IndexOf('.') < 0) return null;
            return text;
        }

        public const string YouTube = "YouTube", TikTok = "TikTok", Instagram = "Instagram";

        static bool IsHost(string h, string domain) { return h == domain || h.EndsWith("." + domain); }

        // Works out which site a link is from: YouTube, TikTok, Instagram, or null for anything else.
        public static string Platform(string url)
        {
            Uri u;
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out u)) return null;
            string h = u.Host.ToLowerInvariant();
            if (h == "youtu.be" || IsHost(h, "youtube.com") || IsHost(h, "youtube-nocookie.com")) return YouTube;
            if (IsHost(h, "tiktok.com")) return TikTok;
            if (IsHost(h, "instagram.com") || h == "instagr.am") return Instagram;
            return null;
        }

        public static bool IsSupported(string url) { return Platform(url) != null; }

        public static bool IsYouTube(string url) { return Platform(url) == YouTube; }

        public static string VideoId(string url)
        {
            if (!IsYouTube(url)) return null;
            var u = new Uri(url);
            Match m;
            if (u.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) m = Regex.Match(u.AbsolutePath, "^/([A-Za-z0-9_-]{11})$");
            else
            {
                m = Regex.Match(u.Query, "[?&]v=([A-Za-z0-9_-]{11})(?:&|$)");
                if (!m.Success) m = Regex.Match(u.AbsolutePath, "^/(?:shorts|live|embed|v|e)/([A-Za-z0-9_-]{11})(?:/|$)");
            }
            return m.Success ? m.Groups[1].Value : null;
        }

        // A link that isn't a single video: a YouTube playlist or channel, or a TikTok profile.
        public static bool IsCollection(string url)
        {
            switch (Platform(url))
            {
                case YouTube: return VideoId(url) == null;
                case TikTok: return Regex.IsMatch(new Uri(url).AbsolutePath, "^/@[^/]+/?$");
                default: return false;
            }
        }
    }

    // Fetches video info in the background as soon as a link is pasted, so Download can start right away.
    sealed class Prefetch
    {
        public readonly string Url;
        public readonly DateTime Started = DateTime.UtcNow;
        public readonly Task Task;
        public string JsonPath, Title, Channel, Thumbnail, Error;
        public double Duration;
        public bool IsLive;
        public int MaxHeight, MaxH264Height; // tallest SDR video stream up to 4K, and tallest native H.264 one
        public volatile bool Canceled;
        readonly Job job = new Job();

        public Prefetch(string url)
        {
            Url = url;
            Task = Task.Run(new Action(Run));
        }

        void Run()
        {
            try
            {
                for (int attempt = 0; attempt < 2 && !Canceled; attempt++)
                {
                    var json = new StringBuilder();
                    string lastError = null;
                    var args = Tools.YtDlpArgs(attempt == 0);
                    args.AddRange(new[] { "-J", "--no-playlist", "--", Url });
                    int code = Tools.Run(Engine.Exe, args, line => json.Append(line), line => { if (line.StartsWith("ERROR:")) lastError = line; }, job);
                    if (Canceled) return;
                    if (code == 0 && json.Length > 0)
                    {
                        Directory.CreateDirectory(Paths.Temp);
                        string path = Path.Combine(Paths.Temp, "info-" + Guid.NewGuid().ToString("N") + ".json");
                        File.WriteAllText(path, json.ToString(), new UTF8Encoding(false));
                        InfoParser.Fill(this, json.ToString());
                        JsonPath = path;
                        return;
                    }
                    Error = lastError ?? "yt-dlp exited with code " + code;
                    if (!Tools.IsFormatError(Error)) return;
                }
            }
            catch (Exception ex) { Error = ex.Message; }
            finally { job.Dispose(); }
        }

        public void Cancel()
        {
            Canceled = true;
            job.Kill();
            if (JsonPath != null) Tools.TryDelete(JsonPath);
        }
    }

    static class InfoParser
    {
        public static void Fill(Prefetch p, string json)
        {
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var d = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (d == null) return;
            p.Title = Str(d, "title") ?? "(untitled)";
            p.Channel = Str(d, "channel") ?? Str(d, "uploader");
            p.Thumbnail = Str(d, "thumbnail");
            object v;
            if (d.TryGetValue("duration", out v) && v != null) p.Duration = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            p.IsLive = d.TryGetValue("is_live", out v) && v is bool && (bool)v;
            var formats = d.TryGetValue("formats", out v) ? v as object[] : null;
            if (formats == null) return;
            foreach (var f in formats.OfType<Dictionary<string, object>>())
            {
                string vcodec = Str(f, "vcodec") ?? "none", range = Str(f, "dynamic_range") ?? "SDR";
                object h;
                if (vcodec == "none" || range != "SDR" || !f.TryGetValue("height", out h) || !(h is int)) continue;
                int height = (int)h;
                if (height > 2160) continue;
                p.MaxHeight = Math.Max(p.MaxHeight, height);
                if (vcodec.StartsWith("avc1") || vcodec.StartsWith("h264")) p.MaxH264Height = Math.Max(p.MaxH264Height, height);
            }
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is string && ((string)v).Length > 0 ? (string)v : null;
        }
    }

    static class Encoders
    {
        static readonly string[] Hardware = { "h264_nvenc", "h264_qsv", "h264_amf" };
        static string cached;

        // Picks the fastest working H.264 encoder (GPU first) and remembers it.
        public static string Detect()
        {
            if (cached != null) return cached;
            string saved = Settings.Get("encoder", null);
            if (saved != null) return cached = saved;
            string found = "libx264";
            foreach (var enc in Hardware)
            {
                var args = new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=320x240:d=0.1", "-frames:v", "2", "-c:v", enc, "-f", "null", "-" };
                if (Tools.Run(Paths.FFmpeg, args, null, null, null) == 0) { found = enc; break; }
            }
            Settings.Set("encoder", found);
            Settings.Save();
            return cached = found;
        }

        public static void Forget()
        {
            cached = null;
            Settings.Remove("encoder");
            Settings.Save();
        }

        public static string[] Args(string enc, bool gpuFrames)
        {
            switch (enc)
            {
                case "h264_nvenc":
                    var a = new List<string> { "-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "20", "-b:v", "0", "-profile:v", "high" };
                    if (!gpuFrames) a.AddRange(new[] { "-pix_fmt", "yuv420p" });
                    return a.ToArray();
                case "h264_qsv":
                    return new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "20", "-profile:v", "high", "-pix_fmt", "nv12" };
                case "h264_amf":
                    return new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "22", "-qp_b", "24", "-profile:v", "high", "-pix_fmt", "yuv420p" };
                default:
                    return new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p" };
            }
        }

        public static string Describe(string enc)
        {
            switch (enc)
            {
                case "h264_nvenc": return "NVIDIA GPU";
                case "h264_qsv": return "Intel GPU";
                case "h264_amf": return "AMD GPU";
                default: return "CPU";
            }
        }
    }

    sealed class JobOptions
    {
        public string Url, Folder;
        public int Format, Quality;
    }

    sealed class MediaInfo
    {
        public string Video;
        public List<string> Audio = new List<string>();
        public double Duration;
    }

    sealed class DownloadJob
    {
        public const int Mp4 = 0, Mkv = 1, Mp3 = 2, M4a = 3;
        public const int QBest = 0, QMax = 1, Q720 = 2, Q480 = 3, Q360 = 4;

        const string ProgressTemplate = "download:PROG|%(info.vcodec)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|"
            + "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(progress.fragment_index)s|%(progress.fragment_count)s";
        static readonly string[] Mp4Audio = { "aac", "mp3", "ac3", "eac3", "alac" };

        readonly JobOptions o;
        readonly Action<string, int> report;
        readonly Job job = new Job();
        readonly StringBuilder log = new StringBuilder();
        volatile bool canceled;
        string lastError, prefix = "";
        int maxHeight, maxH264Height;

        public bool PartialFailure { get; private set; }

        public DownloadJob(JobOptions options, Action<string, int> report)
        {
            o = options;
            this.report = report;
        }

        bool IsVideo { get { return o.Format == Mp4 || o.Format == Mkv; } }

        public void Cancel()
        {
            canceled = true;
            job.Kill();
        }

        void CheckCanceled() { if (canceled) throw new OperationCanceledException(); }

        void Log(string line) { lock (log) log.AppendLine(line); }

        public string SaveLog()
        {
            try
            {
                Directory.CreateDirectory(Paths.Temp);
                string path = Path.Combine(Paths.Temp, "last-log.txt");
                lock (log) File.WriteAllText(path, log.ToString(), Encoding.UTF8);
                return path;
            }
            catch { return null; }
        }

        public List<string> Execute(Prefetch pf)
        {
            try
            {
                Log("YT Downloader 1.0 | yt-dlp " + Engine.Version + " | " + DateTime.Now);
                if (!File.Exists(Engine.Exe)) throw new JobException("yt-dlp is missing. Please reinstall YT Downloader.");
                string tmp = CreateTempDir();
                try
                {
                    var downloaded = Download(pf, tmp);
                    var files = new List<string>();
                    for (int i = 0; i < downloaded.Count; i++)
                    {
                        CheckCanceled();
                        prefix = downloaded.Count > 1 ? "(" + (i + 1) + "/" + downloaded.Count + ") " : "";
                        string path = IsVideo ? EnsureH264(downloaded[i]) : downloaded[i];
                        files.Add(MoveToFolder(path, tmp));
                    }
                    return files;
                }
                finally { Tools.TryDeleteDir(tmp); }
            }
            finally { job.Dispose(); }
        }

        // Work happens in a temp folder on the same drive as the destination, so the final move is instant.
        string CreateTempDir()
        {
            string tempRoot = Path.GetPathRoot(Path.GetFullPath(Paths.Temp));
            bool sameDrive = string.Equals(tempRoot, Path.GetPathRoot(Path.GetFullPath(o.Folder)), StringComparison.OrdinalIgnoreCase);
            string stamp = DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
            string dir = sameDrive ? Path.Combine(Paths.Temp, "job-" + stamp) : Path.Combine(o.Folder, ".ytdownloader-" + stamp);
            var info = Directory.CreateDirectory(dir);
            if (!sameDrive) info.Attributes |= FileAttributes.Hidden;
            return dir;
        }

        List<string> Download(Prefetch pf, string tmp)
        {
            string infoJson = null;
            if (pf != null)
            {
                report("Getting video info…", -1);
                while (!pf.Task.Wait(100)) CheckCanceled();
                if (pf.JsonPath != null && File.Exists(pf.JsonPath) && DateTime.UtcNow - pf.Started < TimeSpan.FromHours(4)) infoJson = pf.JsonPath;
                maxHeight = pf.MaxHeight;
                maxH264Height = pf.MaxH264Height;
            }
            // Mode 0 reuses the prefetched info, 1 extracts fresh (fast, no HLS), 2 extracts fresh with everything.
            int mode = infoJson != null ? 0 : 1;
            bool updated = false;
            while (true)
            {
                CheckCanceled();
                Directory.CreateDirectory(Paths.Temp);
                string resultFile = Path.Combine(Paths.Temp, "result-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    int code = RunYtDlp(mode, infoJson, tmp, resultFile);
                    CheckCanceled();
                    var files = File.Exists(resultFile) ? File.ReadAllLines(resultFile, Encoding.UTF8).Where(l => l.Length > 0 && File.Exists(l)).ToList() : new List<string>();
                    if (code == 0 || files.Count > 0)
                    {
                        PartialFailure = code != 0;
                        return files;
                    }
                }
                finally { Tools.TryDelete(resultFile); }

                string err = lastError ?? "";
                if (mode == 0) mode = 1;
                else if (mode == 1 && Tools.IsFormatError(err)) mode = 2;
                else if (!updated && !Tools.IsPermanentError(err))
                {
                    report("Checking for a downloader update…", -1);
                    if (!Updater.CheckAndUpdate()) throw new JobException(Tools.FriendlyError(err));
                    Log("Updated yt-dlp to " + Engine.Version + ", retrying");
                    updated = true;
                    mode = 1;
                }
                else throw new JobException(Tools.FriendlyError(err));
            }
        }

        int RunYtDlp(int mode, string infoJson, string tmp, string resultFile)
        {
            lastError = null;
            bool collection = Links.IsCollection(o.Url);
            // TikTok captions run long and Instagram titles repeat ("Video by ..."), so those get a shortened title plus the post id.
            string name = Links.IsYouTube(o.Url) ? "%(title)s.%(ext)s" : "%(title).80B [%(id)s].%(ext)s";
            var a = Tools.YtDlpArgs(mode == 1);
            a.AddRange(new[] {
                "--newline", "--no-mtime", "-N", "4", "-P", tmp,
                "-o", collection ? "%(playlist_title,playlist_id|Playlist)s/" + name : name,
                "--progress-template", ProgressTemplate,
                "--print-to-file", "after_move:%(filepath)s", resultFile,
            });
            a.AddRange(FormatArgs());
            if (mode == 0) a.AddRange(new[] { "--load-info-json", infoJson });
            else
            {
                if (!collection) a.Add("--no-playlist");
                a.Add("--");
                a.Add(o.Url);
            }
            Log("> yt-dlp " + Tools.Join(a));
            report(mode == 0 ? "Starting download…" : "Getting video info…", -1);
            return Tools.Run(Engine.Exe, a, OnLine, OnLine, job);
        }

        IEnumerable<string> FormatArgs()
        {
            switch (o.Format)
            {
                case Mp3: return new[] { "-f", "ba[acodec~='^(mp4a|opus)']/ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0", "--embed-metadata" };
                case M4a: return new[] { "-f", "ba[acodec^=mp4a]/ba/b", "-x", "--audio-format", "m4a", "--embed-metadata" };
                default:
                    var a = new List<string> { "-f", VideoFormat(), "--merge-output-format", o.Format == Mkv ? "mkv" : "mp4" };
                    // Without prefetched info, still prefer native H.264 whenever it matches the best resolution.
                    if (o.Quality == QMax && maxHeight == 0) a.AddRange(new[] { "-S", "res,fps,vcodec:h264" });
                    return a;
            }
        }

        // Prefers the site's native H.264 streams, which need no re-encode. "Max" takes the best stream up to 4K
        // (AV1, the smallest download) and re-encodes it, unless native H.264 already reaches that resolution.
        string VideoFormat()
        {
            bool max = o.Quality == QMax && (maxHeight == 0 || maxH264Height < maxHeight);
            int h = o.Quality == QMax ? 2160 : o.Quality == Q720 ? 720 : o.Quality == Q480 ? 480 : o.Quality == Q360 ? 360 : 1080;
            string res = "[height<=" + h + "]";
            string audio = o.Format == Mkv ? "ba[acodec~='^(mp4a|opus)']" : "ba[acodec^=mp4a]";
            if (max)
                return "bv" + res + "[dynamic_range=SDR]+" + audio + "/bv" + res + "+" + audio + "/bv" + res + "+ba/b" + res + "/b";
            const string h264 = "[vcodec~='^(avc1|h264)']"; // YouTube says avc1, TikTok says h264
            return "bv" + h264 + res + "+" + audio + "/bv" + h264 + res + "+ba/b" + h264 + res
                + "/bv" + res + "+" + audio + "/bv" + res + "+ba/b" + res + "/b";
        }

        void OnLine(string line)
        {
            if (line.StartsWith("PROG|", StringComparison.Ordinal)) { OnProgress(line); return; }
            Log(line);
            if (line.StartsWith("ERROR:", StringComparison.Ordinal)) lastError = line;
            else if (line.StartsWith("[download] Downloading item ", StringComparison.Ordinal))
            {
                var m = Regex.Match(line, @"item (\d+) of (\d+)");
                if (m.Success) prefix = "(" + m.Groups[1].Value + "/" + m.Groups[2].Value + ") ";
            }
            else if (line.StartsWith("[Merger]", StringComparison.Ordinal)) report(prefix + "Merging video and audio…", -1);
            else if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal)) report(prefix + (o.Format == Mp3 ? "Converting to MP3…" : "Saving M4A…"), -1);
            else if (line.StartsWith("[Metadata]", StringComparison.Ordinal) || line.StartsWith("[Fixup", StringComparison.Ordinal)) report(prefix + "Finishing…", -1);
        }

        void OnProgress(string line)
        {
            var p = line.Split('|');
            if (p.Length < 9) return;
            double done = Tools.Num(p[2]), total = Tools.Num(p[3]), speed = Tools.Num(p[5]), eta = Tools.Num(p[6]), frag = Tools.Num(p[7]), frags = Tools.Num(p[8]);
            if (total <= 0) total = Tools.Num(p[4]);
            int permille = total > 0 ? (int)(done * 1000 / total) : frags > 0 ? (int)(frag * 1000 / frags) : -1;
            var sb = new StringBuilder(prefix).Append("Downloading ").Append(!IsVideo || p[1] == "none" ? "audio" : "video").Append("…");
            if (permille >= 0) sb.Append(' ').Append(Math.Min(permille / 10, 100)).Append('%');
            if (speed > 0) sb.Append("  ·  ").Append(Tools.FormatSpeed(speed));
            if (eta > 0) sb.Append("  ·  ").Append(Tools.FormatTime(eta)).Append(" left");
            report(sb.ToString(), Math.Min(permille, 1000));
        }

        MediaInfo Probe(string path)
        {
            var mi = new MediaInfo();
            string codec = null;
            var args = new[] { "-v", "error", "-show_entries", "stream=codec_name,codec_type:format=duration", "-of", "default=nw=1", path };
            Tools.Run(Paths.FFprobe, args, line =>
            {
                if (line.StartsWith("codec_name=")) codec = line.Substring(11);
                else if (line == "codec_type=video") { if (mi.Video == null && codec != "mjpeg" && codec != "png") mi.Video = codec; }
                else if (line == "codec_type=audio") mi.Audio.Add(codec);
                else if (line.StartsWith("duration=")) mi.Duration = Tools.Num(line.Substring(9));
            }, null, job);
            return mi;
        }

        // Makes sure the file is H.264 in the chosen container, converting it (GPU first) only when needed.
        string EnsureH264(string path)
        {
            var mi = Probe(path);
            CheckCanceled();
            if (mi.Video == null) return path;
            string ext = o.Format == Mkv ? ".mkv" : ".mp4";
            bool h264 = mi.Video == "h264";
            bool audioOk = o.Format == Mkv || mi.Audio.All(a => Mp4Audio.Contains(a));
            if (h264 && audioOk && string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase)) return path;

            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path);
            string temp = Path.Combine(dir, name + ".converting" + ext), final = Path.Combine(dir, name + ext);
            var audioArgs = audioOk ? new[] { "-c:a", "copy" } : new[] { "-c:a", "aac", "-b:a", "192k" };

            // Each attempt: input options, video codec options, encoder name.
            var attempts = new List<Tuple<string[], string[], string>>();
            if (h264) attempts.Add(Tuple.Create(new string[0], new[] { "-c:v", "copy" }, "copy"));
            else
            {
                report(prefix + "Checking for a GPU encoder…", -1);
                string enc = Encoders.Detect();
                if (enc == "h264_nvenc")
                    attempts.Add(Tuple.Create(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" }, Encoders.Args(enc, true), enc));
                if (enc != "libx264") attempts.Add(Tuple.Create(new string[0], Encoders.Args(enc, false), enc));
                attempts.Add(Tuple.Create(new string[0], Encoders.Args("libx264", false), "libx264"));
            }

            bool hardwareFailed = false;
            foreach (var attempt in attempts)
            {
                CheckCanceled();
                string label = prefix + (h264 ? "Remuxing…" : "Converting to H.264 (" + Encoders.Describe(attempt.Item3) + ")…");
                var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
                a.AddRange(attempt.Item1);
                a.AddRange(new[] { "-i", path, "-map", "0:v:0", "-map", "0:a?" });
                a.AddRange(attempt.Item2);
                a.AddRange(audioArgs);
                a.Add(temp);
                if (RunFfmpeg(a, mi.Duration, label))
                {
                    if (hardwareFailed && attempt.Item3 == "libx264") Encoders.Forget();
                    File.Delete(path);
                    if (File.Exists(final)) File.Delete(final);
                    File.Move(temp, final);
                    return final;
                }
                CheckCanceled();
                Tools.TryDelete(temp);
                if (attempt.Item3 != "libx264" && attempt.Item3 != "copy") hardwareFailed = true;
            }
            throw new JobException("Couldn't convert the video to H.264. Click \"Show log\" for details.");
        }

        bool RunFfmpeg(List<string> args, double duration, string label)
        {
            report(label, duration > 0 ? 0 : -1);
            Log("> ffmpeg " + Tools.Join(args));
            string speed = "";
            int code = Tools.Run(Paths.FFmpeg, args, line =>
            {
                if (line.StartsWith("speed=") && line.EndsWith("x")) speed = "  ·  " + line.Substring(6).Trim();
                else if (line.StartsWith("out_time_us=") && duration > 0)
                {
                    double us = Tools.Num(line.Substring(12));
                    if (us < 0) return;
                    int permille = (int)Math.Min(1000, us / 1000 / duration);
                    report(label + " " + permille / 10 + "%" + speed, permille);
                }
            }, line => Log(line), job);
            return code == 0;
        }

        string MoveToFolder(string path, string tmp)
        {
            string root = tmp.TrimEnd('\\') + "\\";
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return path;
            string dest = Path.Combine(o.Folder, path.Substring(root.Length));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(path, dest);
            return dest;
        }
    }

    // Removes leftovers from earlier runs (temp files, old yt-dlp builds).
    static class Cleanup
    {
        public static void Run(string folder)
        {
            if (Process.GetProcessesByName("YTDownloader").Length > 1) return; // another instance may be using them
            try
            {
                if (Directory.Exists(Paths.Temp))
                {
                    foreach (var d in Directory.GetDirectories(Paths.Temp, "job-*")) if (Old(Directory.GetCreationTimeUtc(d))) Tools.TryDeleteDir(d);
                    foreach (var f in Directory.GetFiles(Paths.Temp)) if (Old(File.GetLastWriteTimeUtc(f)) && !f.EndsWith("last-log.txt")) Tools.TryDelete(f);
                }
            }
            catch { }
            try
            {
                if (Directory.Exists(folder))
                    foreach (var d in Directory.GetDirectories(folder, ".ytdownloader-*")) if (Old(Directory.GetCreationTimeUtc(d))) Tools.TryDeleteDir(d);
            }
            catch { }
            try
            {
                if (Directory.Exists(Paths.EngineRoot))
                    foreach (var d in Directory.GetDirectories(Paths.EngineRoot))
                        if (!Engine.Exe.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)) Tools.TryDeleteDir(d);
            }
            catch { }
        }

        static bool Old(DateTime utc) { return utc < Program.StartedUtc; }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags, Count, Timeout; }

        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        public static void Flash(IntPtr hwnd)
        {
            var fi = new FlashInfo { Size = (uint)Marshal.SizeOf(typeof(FlashInfo)), Hwnd = hwnd, Flags = 3 | 12 }; // FLASHW_ALL | FLASHW_TIMERNOFG
            FlashWindowEx(ref fi);
        }

        public static void SetPlaceholder(TextBox box, string text) { SendMessage(box.Handle, 0x1501, (IntPtr)1, text); } // EM_SETCUEBANNER

        public static string DownloadsFolder()
        {
            IntPtr p;
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
            {
                try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    // The modern Windows folder picker (falls back to the classic one).
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialog { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        public static string Pick(IWin32Window owner, string initial)
        {
            try
            {
                var dlg = (IFileDialog)new FileOpenDialog();
                uint options;
                dlg.GetOptions(out options);
                dlg.SetOptions(options | 0x20 | 0x40); // FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
                dlg.SetTitle("Choose where to save downloads");
                try
                {
                    IShellItem start;
                    if (Directory.Exists(initial)) { SHCreateItemFromParsingName(initial, IntPtr.Zero, typeof(IShellItem).GUID, out start); dlg.SetFolder(start); }
                }
                catch { }
                if (dlg.Show(owner.Handle) != 0) return null;
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(0x80058000, out path); // SIGDN_FILESYSPATH
                return path;
            }
            catch
            {
                using (var fb = new FolderBrowserDialog { SelectedPath = initial, Description = "Choose where to save downloads" })
                    return fb.ShowDialog(owner) == DialogResult.OK ? fb.SelectedPath : null;
            }
        }
    }

    sealed class MainForm : Form
    {
        static readonly string[] FormatNames = { "MP4 video (H.264)", "MKV video (H.264)", "MP3 audio", "M4A audio (original, fastest)" };
        static readonly string[] QualityNames = { "Best H.264 (up to 1080p)", "Max – up to 4K (slower)", "720p", "480p", "360p" };
        static readonly Color ErrorColor = Color.FromArgb(196, 30, 30), SuccessColor = Color.FromArgb(16, 124, 16);

        readonly TextBox urlBox = new TextBox();
        readonly Button pasteBtn = new Button();
        readonly Panel infoPanel = new Panel();
        readonly PictureBox thumb = new PictureBox();
        readonly Label titleLbl = new Label(), metaLbl = new Label();
        readonly ComboBox formatBox = new ComboBox(), qualityBox = new ComboBox();
        readonly TextBox folderBox = new TextBox();
        readonly Button browseBtn = new Button();
        readonly ProgressBar bar = new ProgressBar();
        readonly Label statusLbl = new Label();
        readonly LinkLabel logLink = new LinkLabel();
        readonly Button openBtn = new Button(), downloadBtn = new Button();
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer { Interval = 400 };

        Prefetch prefetch;
        DownloadJob job;
        Task jobTask;
        string lastClipboard, lastFile, logPath;
        int lastUrlLength;
        long lastReport;

        public MainForm()
        {
            SuspendLayout();
            Text = "YT Downloader";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 10, 12, 10) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            // Link
            urlBox.Dock = DockStyle.Fill;
            urlBox.Margin = new Padding(3, 5, 3, 3);
            pasteBtn.Text = "Paste";
            pasteBtn.MinimumSize = new Size(84, 27);
            pasteBtn.AutoSize = true;
            grid.Controls.Add(Caption("Link"), 0, 0);
            grid.Controls.Add(urlBox, 1, 0);
            grid.Controls.Add(pasteBtn, 2, 0);

            // Video info: thumbnail, title, channel and length
            infoPanel.Size = new Size(100, 72);
            infoPanel.Margin = new Padding(3, 6, 3, 6);
            infoPanel.Dock = DockStyle.Fill;
            thumb.SetBounds(0, 0, 128, 72);
            thumb.SizeMode = PictureBoxSizeMode.Zoom;
            thumb.BackColor = Color.FromArgb(232, 232, 232);
            thumb.InitialImage = null;
            thumb.ErrorImage = null;
            titleLbl.SetBounds(140, 12, 10, 24);
            titleLbl.AutoEllipsis = true;
            titleLbl.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
            metaLbl.SetBounds(140, 40, 10, 22);
            metaLbl.AutoEllipsis = true;
            metaLbl.ForeColor = SystemColors.GrayText;
            infoPanel.Controls.AddRange(new Control[] { thumb, titleLbl, metaLbl });
            infoPanel.Resize += (s, e) =>
            {
                titleLbl.Width = Math.Max(10, infoPanel.ClientSize.Width - titleLbl.Left);
                metaLbl.Width = titleLbl.Width;
            };
            grid.Controls.Add(infoPanel, 1, 1);
            grid.SetColumnSpan(infoPanel, 2);

            // Format and quality
            formatBox.DropDownStyle = ComboBoxStyle.DropDownList;
            formatBox.Items.AddRange(FormatNames);
            formatBox.Width = 210;
            qualityBox.DropDownStyle = ComboBoxStyle.DropDownList;
            qualityBox.Items.AddRange(QualityNames);
            qualityBox.Width = 190;
            var qualityLbl = new Label { Text = "Quality", AutoSize = true, Margin = new Padding(14, 7, 3, 0) };
            var formats = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
            formats.Controls.AddRange(new Control[] { formatBox, qualityLbl, qualityBox });
            grid.Controls.Add(Caption("Format"), 0, 2);
            grid.Controls.Add(formats, 1, 2);
            grid.SetColumnSpan(formats, 2);

            // Destination folder
            folderBox.Dock = DockStyle.Fill;
            folderBox.Margin = new Padding(3, 5, 3, 3);
            browseBtn.Text = "Browse…";
            browseBtn.MinimumSize = new Size(84, 27);
            browseBtn.AutoSize = true;
            grid.Controls.Add(Caption("Save to"), 0, 3);
            grid.Controls.Add(folderBox, 1, 3);
            grid.Controls.Add(browseBtn, 2, 3);

            // Progress and status
            bar.Dock = DockStyle.Fill;
            bar.Height = 16;
            bar.Margin = new Padding(3, 12, 3, 3);
            bar.Maximum = 1001;
            grid.Controls.Add(bar, 0, 4);
            grid.SetColumnSpan(bar, 3);
            statusLbl.AutoEllipsis = true;
            statusLbl.Dock = DockStyle.Fill;
            statusLbl.Height = 22;
            statusLbl.TextAlign = ContentAlignment.MiddleLeft;
            statusLbl.Text = "Ready";
            grid.Controls.Add(statusLbl, 0, 5);
            grid.SetColumnSpan(statusLbl, 2);
            logLink.Text = "Show log";
            logLink.AutoSize = true;
            logLink.Anchor = AnchorStyles.Right;
            logLink.Visible = false;
            grid.Controls.Add(logLink, 2, 5);

            // Buttons
            downloadBtn.Text = "Download";
            downloadBtn.Size = new Size(120, 32);
            downloadBtn.Font = new Font(Font, FontStyle.Bold);
            openBtn.Text = "Open folder";
            openBtn.Size = new Size(110, 32);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            buttons.Controls.AddRange(new Control[] { downloadBtn, openBtn });
            grid.Controls.Add(buttons, 0, 6);
            grid.SetColumnSpan(buttons, 3);

            Controls.Add(grid);
            AcceptButton = downloadBtn;

            string savedFolder = Settings.Get("folder", null);
            folderBox.Text = string.IsNullOrWhiteSpace(savedFolder) ? Native.DownloadsFolder() : savedFolder;
            formatBox.SelectedIndex = Settings.GetInt("format", 0, FormatNames.Length - 1);
            qualityBox.SelectedIndex = Settings.GetInt("quality", 0, QualityNames.Length - 1);
            qualityBox.Enabled = IsVideoFormat;
            ShowInfo("Paste a YouTube, TikTok or Instagram link", "Video details show up here automatically.");

            urlBox.TextChanged += OnUrlTextChanged;
            debounce.Tick += (s, e) => { debounce.Stop(); OnUrlChanged(); };
            pasteBtn.Click += (s, e) => PasteFromClipboard();
            formatBox.SelectedIndexChanged += (s, e) => { qualityBox.Enabled = IsVideoFormat && job == null; SaveSettings(); };
            qualityBox.SelectedIndexChanged += (s, e) => SaveSettings();
            browseBtn.Click += (s, e) => { string p = FolderPicker.Pick(this, folderBox.Text); if (p != null) { folderBox.Text = p; SaveSettings(); } };
            downloadBtn.Click += (s, e) => { if (job != null) CancelDownload(); else StartDownload(); };
            openBtn.Click += (s, e) => OpenFolder();
            logLink.LinkClicked += (s, e) => { if (logPath != null) try { Process.Start("notepad.exe", Tools.Quote(logPath)); } catch { } };
            EnableDrop(this);

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();
        }

        bool IsVideoFormat { get { return formatBox.SelectedIndex == DownloadJob.Mp4 || formatBox.SelectedIndex == DownloadJob.Mkv; } }

        static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0) };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.SetPlaceholder(urlBox, "Paste a YouTube, TikTok or Instagram link here");
            CheckClipboard();
            string folder = folderBox.Text;
            Task.Run(() =>
            {
                Cleanup.Run(folder); // before updating, so it never touches a build that's in use
                if (Updater.IsDue()) Updater.CheckAndUpdate();
            });
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            CheckClipboard();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (job != null)
            {
                if (MessageBox.Show(this, "A download is still running. Stop it and quit?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                job.Cancel();
                try { jobTask.Wait(3000); } catch { }
            }
            if (prefetch != null) prefetch.Cancel();
            SaveSettings();
            base.OnFormClosing(e);
        }

        void SaveSettings()
        {
            Settings.Set("folder", folderBox.Text.Trim());
            Settings.Set("format", formatBox.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            Settings.Set("quality", qualityBox.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            Settings.Save();
        }

        void EnableDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text)) e.Effect = DragDropEffects.Copy; };
            c.DragDrop += (s, e) =>
            {
                var text = (e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text)) as string;
                if (text != null && job == null) urlBox.Text = text.Trim();
            };
            foreach (Control child in c.Controls) EnableDrop(child);
        }

        // Picks up a YouTube, TikTok or Instagram link from the clipboard when the window opens or gets focus.
        void CheckClipboard()
        {
            if (job != null) return;
            string text;
            try
            {
                if (!Clipboard.ContainsText()) return;
                text = Clipboard.GetText().Trim();
            }
            catch { return; }
            if (text == lastClipboard || text.Length > 500 || !Links.IsSupported(Links.Normalize(text))) return;
            lastClipboard = text;
            urlBox.Text = text;
            downloadBtn.Focus();
        }

        void PasteFromClipboard()
        {
            try { if (Clipboard.ContainsText()) urlBox.Text = Clipboard.GetText().Trim(); } catch { }
            downloadBtn.Focus();
        }

        void OnUrlTextChanged(object sender, EventArgs e)
        {
            int len = urlBox.TextLength;
            bool pasted = Math.Abs(len - lastUrlLength) > 5;
            lastUrlLength = len;
            debounce.Stop();
            if (pasted) OnUrlChanged(); else debounce.Start();
        }

        void OnUrlChanged()
        {
            string url = Links.Normalize(urlBox.Text);
            if (prefetch != null && prefetch.Url == url) return;
            if (prefetch != null) { prefetch.Cancel(); prefetch = null; }
            thumb.CancelAsync();
            thumb.Image = null;
            if (url == null) { ShowInfo("Paste a YouTube, TikTok or Instagram link", "Video details show up here automatically."); return; }
            string site = Links.Platform(url);
            if (Links.IsCollection(url))
            {
                ShowInfo(site == Links.TikTok ? "TikTok profile link" : "Playlist or channel link", "Every video in it will be downloaded into a subfolder.");
                return;
            }
            string id = Links.VideoId(url);
            if (id != null) LoadThumb("https://i.ytimg.com/vi/" + id + "/mqdefault.jpg");
            ShowInfo("Loading " + (site ?? "video") + " info…", site == null ? "This site isn't officially supported, but it may still work." : "");
            var pf = new Prefetch(url);
            prefetch = pf;
            pf.Task.ContinueWith(t => SafeInvoke(() => OnPrefetchDone(pf)));
        }

        void OnPrefetchDone(Prefetch pf)
        {
            if (pf != prefetch || pf.Canceled) return;
            if (pf.Title != null)
            {
                var meta = new List<string>();
                string site = Links.Platform(pf.Url);
                if (site != null) meta.Add(site);
                if (pf.Channel != null) meta.Add(pf.Channel);
                if (pf.IsLive) meta.Add("LIVE");
                else if (pf.Duration > 0) meta.Add(Tools.FormatTime(pf.Duration));
                ShowInfo(pf.Title, string.Join("  ·  ", meta));
                if (thumb.Image == null && pf.Thumbnail != null && Links.VideoId(pf.Url) == null) LoadThumb(pf.Thumbnail);
            }
            else
            {
                ShowInfo("Couldn't load video info", Tools.FriendlyError(pf.Error));
                metaLbl.ForeColor = ErrorColor;
            }
        }

        void ShowInfo(string title, string meta)
        {
            titleLbl.Text = title;
            metaLbl.Text = meta;
            metaLbl.ForeColor = SystemColors.GrayText;
        }

        void LoadThumb(string url)
        {
            try { Net.Init(); thumb.LoadAsync(url); } catch { }
        }

        void SafeInvoke(Action action)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); } catch { }
        }

        void Report(string text, int permille)
        {
            // Called from worker threads for every progress line; refresh the UI at most ~15 times a second.
            long now = Stopwatch.GetTimestamp();
            if (permille >= 0 && permille < 1000 && now - Interlocked.Read(ref lastReport) < Stopwatch.Frequency / 15) return;
            Interlocked.Exchange(ref lastReport, now);
            SafeInvoke(() =>
            {
                if (job == null) return;
                SetStatus(text, SystemColors.ControlText);
                SetProgress(permille);
            });
        }

        void SetStatus(string text, Color color)
        {
            statusLbl.ForeColor = color;
            statusLbl.Text = text;
        }

        void SetProgress(int permille)
        {
            if (permille < 0)
            {
                bar.Style = ProgressBarStyle.Marquee;
                return;
            }
            bar.Style = ProgressBarStyle.Continuous;
            int v = Math.Max(0, Math.Min(1000, permille));
            bar.Value = v + 1; // stepping back skips the progress bar's slow fill animation
            bar.Value = v;
        }

        void SetBusy(bool busy)
        {
            urlBox.ReadOnly = busy;
            pasteBtn.Enabled = !busy;
            formatBox.Enabled = !busy;
            qualityBox.Enabled = !busy && IsVideoFormat;
            folderBox.ReadOnly = busy;
            browseBtn.Enabled = !busy;
            downloadBtn.Text = busy ? "Cancel" : "Download";
            downloadBtn.Enabled = true;
        }

        async void StartDownload()
        {
            debounce.Stop();
            OnUrlChanged();
            string url = Links.Normalize(urlBox.Text);
            if (url == null)
            {
                SetStatus("Paste a YouTube, TikTok or Instagram link first.", ErrorColor);
                urlBox.Focus();
                return;
            }
            string folder = folderBox.Text.Trim();
            try
            {
                if (folder.Length == 0) throw new IOException();
                Directory.CreateDirectory(folder);
            }
            catch
            {
                SetStatus("Choose a folder to save to.", ErrorColor);
                return;
            }
            if (Links.IsCollection(url) && MessageBox.Show(this, "This is a playlist, channel or profile link. Download every video in it?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            SaveSettings();

            var options = new JobOptions { Url = url, Folder = folder, Format = formatBox.SelectedIndex, Quality = qualityBox.SelectedIndex };
            var pf = prefetch != null && prefetch.Url == url ? prefetch : null;
            var current = new DownloadJob(options, Report);
            job = current;
            lastFile = null;
            logLink.Visible = false;
            SetBusy(true);
            SetStatus("Starting…", SystemColors.ControlText);
            SetProgress(-1);
            var watch = Stopwatch.StartNew();
            try
            {
                var task = Task.Run(() => current.Execute(pf));
                jobTask = task;
                List<string> files = await task;
                if (files.Count == 0) throw new JobException("Nothing was downloaded.");
                lastFile = files[files.Count - 1];
                string what = files.Count == 1 ? Path.GetFileName(lastFile) : files.Count + " files";
                string note = current.PartialFailure ? " (some videos failed)" : "";
                SetStatus("✓ Done in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s  —  " + what + note, SuccessColor);
                SetProgress(1000);
                if (ActiveForm != this) Native.Flash(Handle);
            }
            catch (OperationCanceledException)
            {
                SetProgress(0);
                SetStatus("Canceled.", SystemColors.ControlText);
            }
            catch (Exception ex)
            {
                SetProgress(0);
                SetStatus(ex is JobException ? ex.Message : "Error: " + ex.Message, ErrorColor);
                logPath = current.SaveLog();
                logLink.Visible = logPath != null;
            }
            finally
            {
                job = null;
                SetBusy(false);
            }
        }

        void CancelDownload()
        {
            if (job == null) return;
            job.Cancel();
            downloadBtn.Enabled = false;
            SetStatus("Canceling…", SystemColors.ControlText);
        }

        void OpenFolder()
        {
            try
            {
                if (lastFile != null && File.Exists(lastFile)) Process.Start("explorer.exe", "/select," + Tools.Quote(lastFile));
                else
                {
                    Directory.CreateDirectory(folderBox.Text.Trim());
                    Process.Start("explorer.exe", Tools.Quote(folderBox.Text.Trim()));
                }
            }
            catch (Exception ex) { SetStatus("Can't open the folder: " + ex.Message, ErrorColor); }
        }
    }
}
