//! One download: runs yt-dlp, makes sure video ends up as H.264, and moves the files into place.
use crate::prefetch::Prefetch;
use crate::process::{self, Job};
use crate::{encoders, engine, errors, links, paths, updater};
use regex::Regex;
use serde::Deserialize;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::{Arc, LazyLock, Mutex};
use std::time::Duration;

pub const MP4: usize = 0;
pub const MKV: usize = 1;
pub const MP3: usize = 2;
pub const M4A: usize = 3;
pub const Q_MAX: usize = 1;
pub const Q_720: usize = 2;
pub const Q_480: usize = 3;
pub const Q_360: usize = 4;

const PROGRESS_TEMPLATE: &str = "download:PROG|%(info.vcodec)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|\
%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(progress.fragment_index)s|%(progress.fragment_count)s";
const MP4_AUDIO: &[&str] = &["aac", "mp3", "ac3", "eac3", "alac"];
static ITEM: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"item (\d+) of (\d+)").unwrap());

#[derive(Deserialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Options {
    pub url: String,
    pub folder: String,
    pub format: usize,
    pub quality: usize,
}

pub enum Failure {
    Canceled,
    Message(String),
}

impl From<std::io::Error> for Failure {
    fn from(e: std::io::Error) -> Self {
        Failure::Message(format!("Error: {e}"))
    }
}

fn fail<T>(message: impl Into<String>) -> Result<T, Failure> {
    Err(Failure::Message(message.into()))
}

pub struct Outcome {
    pub files: Vec<PathBuf>,
    pub partial: bool,
}

pub type Report = Box<dyn Fn(&str, i32) + Send + Sync>;

pub struct DownloadJob {
    o: Options,
    report: Report,
    job: Job,
    log: Mutex<String>,
    canceled: AtomicBool,
    last_error: Mutex<Option<String>>,
    prefix: Mutex<String>,
    max_height: AtomicI64,
    max_h264_height: AtomicI64,
}

impl DownloadJob {
    pub fn new(options: Options, report: Report) -> DownloadJob {
        DownloadJob {
            o: options,
            report,
            job: Job::new(),
            log: Mutex::new(String::new()),
            canceled: AtomicBool::new(false),
            last_error: Mutex::new(None),
            prefix: Mutex::new(String::new()),
            max_height: AtomicI64::new(0),
            max_h264_height: AtomicI64::new(0),
        }
    }

    fn is_video(&self) -> bool {
        self.o.format == MP4 || self.o.format == MKV
    }

    pub fn cancel(&self) {
        self.canceled.store(true, Ordering::SeqCst);
        self.job.kill();
    }

    fn is_canceled(&self) -> bool {
        self.canceled.load(Ordering::SeqCst)
    }

    fn check(&self) -> Result<(), Failure> {
        if self.is_canceled() {
            Err(Failure::Canceled)
        } else {
            Ok(())
        }
    }

    fn log(&self, line: &str) {
        let mut log = self.log.lock().unwrap();
        log.push_str(line);
        log.push('\n');
    }

    fn report(&self, text: &str, permille: i32) {
        (self.report)(text, permille);
    }

    fn prefix(&self) -> String {
        self.prefix.lock().unwrap().clone()
    }

    pub fn save_log(&self) -> Option<PathBuf> {
        let dir = &paths::get().temp;
        fs::create_dir_all(dir).ok()?;
        let path = dir.join("last-log.txt");
        fs::write(&path, self.log.lock().unwrap().as_bytes()).ok()?;
        Some(path)
    }

    pub fn execute(&self, pf: Option<Arc<Prefetch>>) -> Result<Outcome, Failure> {
        self.log(&format!(
            "Clip Downloader {} | yt-dlp {} | {} {}",
            env!("CARGO_PKG_VERSION"),
            engine::version(),
            std::env::consts::OS,
            std::env::consts::ARCH
        ));
        if !engine::exe_path().exists() {
            return fail("yt-dlp is missing. Please reinstall Clip Downloader.");
        }
        let tmp = self.create_temp_dir()?;
        let result = (|| {
            let (downloaded, partial) = self.download(pf, &tmp)?;
            let mut files = Vec::new();
            for (i, file) in downloaded.iter().enumerate() {
                self.check()?;
                *self.prefix.lock().unwrap() =
                    if downloaded.len() > 1 { format!("({}/{}) ", i + 1, downloaded.len()) } else { String::new() };
                let path = if self.is_video() { self.ensure_h264(file)? } else { file.clone() };
                files.push(self.move_to_folder(&path, &tmp)?);
            }
            Ok(Outcome { files, partial })
        })();
        let _ = fs::remove_dir_all(&tmp);
        result
    }

    /// Work happens in a temp folder on the same drive as the destination, so the final move is instant.
    fn create_temp_dir(&self) -> Result<PathBuf, Failure> {
        let temp = &paths::get().temp;
        fs::create_dir_all(temp)?;
        let folder = Path::new(&self.o.folder);
        let dir = if same_volume(temp, folder) {
            temp.join(format!("job-{}", crate::stamp()))
        } else {
            folder.join(format!(".clipdownloader-{}", crate::stamp()))
        };
        fs::create_dir_all(&dir)?;
        Ok(dir)
    }

    fn download(&self, pf: Option<Arc<Prefetch>>, tmp: &Path) -> Result<(Vec<PathBuf>, bool), Failure> {
        let mut info_json = None;
        if let Some(pf) = pf {
            self.report("Getting video info…", -1);
            let info = pf.wait(&|| self.is_canceled()).ok_or(Failure::Canceled)?;
            if let Some(path) = info.json_path.filter(|p| p.exists()) {
                if pf.started.elapsed() < Duration::from_secs(4 * 3600) {
                    info_json = Some(path);
                }
            }
            self.max_height.store(info.max_height, Ordering::SeqCst);
            self.max_h264_height.store(info.max_h264_height, Ordering::SeqCst);
        }
        // Mode 0 reuses the prefetched info, 1 extracts fresh (fast, no HLS), 2 extracts fresh with everything.
        let mut mode = if info_json.is_some() { 0 } else { 1 };
        let mut updated = false;
        loop {
            self.check()?;
            let result_file = paths::get().temp.join(format!("result-{}.txt", crate::stamp()));
            let code = self.run_ytdlp(mode, info_json.as_deref(), tmp, &result_file);
            let mut files: Vec<PathBuf> = fs::read_to_string(&result_file)
                .map(|t| t.lines().filter(|l| !l.is_empty()).map(PathBuf::from).filter(|p| p.exists()).collect())
                .unwrap_or_default();
            files.dedup();
            let _ = fs::remove_file(&result_file);
            let code = code?;
            self.check()?;
            if code == 0 || !files.is_empty() {
                return Ok((files, code != 0));
            }

            let err = self.last_error.lock().unwrap().clone().unwrap_or_default();
            if mode == 0 {
                mode = 1;
            } else if mode == 1 && errors::is_format_error(&err) {
                mode = 2;
            } else if !updated && !errors::is_permanent_error(&err) {
                self.report("Checking for a downloader update…", -1);
                if !updater::check_and_update() {
                    return fail(errors::friendly_error(&err));
                }
                self.log(&format!("Updated yt-dlp to {}, retrying", engine::version()));
                updated = true;
                mode = 1;
            } else {
                return fail(errors::friendly_error(&err));
            }
        }
    }

    fn run_ytdlp(&self, mode: u8, info_json: Option<&Path>, tmp: &Path, result_file: &Path) -> std::io::Result<i32> {
        *self.last_error.lock().unwrap() = None;
        let collection = links::is_collection(&self.o.url);
        // TikTok captions run long and Instagram titles repeat ("Video by ..."), so those get a shortened title plus the post id.
        let name = if links::is_youtube(&self.o.url) { "%(title)s.%(ext)s" } else { "%(title).80B [%(id)s].%(ext)s" };
        let mut a = process::ytdlp_args(mode == 1);
        let output = if collection { format!("%(playlist_title,playlist_id|Playlist)s/{name}") } else { name.to_string() };
        a.extend(
            [
                "--newline",
                "--no-mtime",
                "-N",
                "4",
                "-P",
                &tmp.display().to_string(),
                "-o",
                &output,
                "--progress-template",
                PROGRESS_TEMPLATE,
                "--print-to-file",
                "after_move:%(filepath)s",
                &result_file.display().to_string(),
            ]
            .map(String::from),
        );
        a.extend(self.format_args());
        match info_json {
            Some(json) if mode == 0 => a.extend(["--load-info-json".into(), json.display().to_string()]),
            _ => {
                if !collection {
                    a.push("--no-playlist".into());
                }
                a.extend(["--".into(), self.o.url.clone()]);
            }
        }
        self.log(&format!("> yt-dlp {}", a.iter().map(|s| process::quote(s)).collect::<Vec<_>>().join(" ")));
        self.report(if mode == 0 { "Starting download…" } else { "Getting video info…" }, -1);
        let on_line = |line: &str| self.on_line(line);
        process::run(&engine::exe_path(), &a, Some(&self.job), &on_line, &on_line)
    }

    fn format_args(&self) -> Vec<String> {
        let a: Vec<&str> = match self.o.format {
            MP3 => vec!["-f", "ba[acodec~='^(mp4a|opus)']/ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0", "--embed-metadata"],
            M4A => vec!["-f", "ba[acodec^=mp4a]/ba/b", "-x", "--audio-format", "m4a", "--embed-metadata"],
            _ => {
                let mut a: Vec<String> = vec![
                    "-f".into(),
                    self.video_format(),
                    "--merge-output-format".into(),
                    if self.o.format == MKV { "mkv" } else { "mp4" }.into(),
                ];
                // Without prefetched info, still prefer native H.264 whenever it matches the best resolution.
                if self.o.quality == Q_MAX && self.max_height.load(Ordering::SeqCst) == 0 {
                    a.extend(["-S".into(), "res,fps,vcodec:h264".into()]);
                }
                return a;
            }
        };
        a.into_iter().map(String::from).collect()
    }

    /// Prefers the site's native H.264 streams, which need no re-encode. "Max" takes the best stream up to 4K
    /// (AV1, the smallest download) and re-encodes it, unless native H.264 already reaches that resolution.
    fn video_format(&self) -> String {
        let (max_h, max_h264) = (self.max_height.load(Ordering::SeqCst), self.max_h264_height.load(Ordering::SeqCst));
        let max = self.o.quality == Q_MAX && (max_h == 0 || max_h264 < max_h);
        let h = match self.o.quality {
            Q_MAX => 2160,
            Q_720 => 720,
            Q_480 => 480,
            Q_360 => 360,
            _ => 1080,
        };
        let res = format!("[height<={h}]");
        let audio = if self.o.format == MKV { "ba[acodec~='^(mp4a|opus)']" } else { "ba[acodec^=mp4a]" };
        if max {
            return format!("bv{res}[dynamic_range=SDR]+{audio}/bv{res}+{audio}/bv{res}+ba/b{res}/b");
        }
        let h264 = "[vcodec~='^(avc1|h264)']"; // YouTube says avc1, TikTok says h264
        format!("bv{h264}{res}+{audio}/bv{h264}{res}+ba/b{h264}{res}/bv{res}+{audio}/bv{res}+ba/b{res}/b")
    }

    fn on_line(&self, line: &str) {
        if line.starts_with("PROG|") {
            self.on_progress(line);
            return;
        }
        self.log(line);
        let prefix = self.prefix();
        if line.starts_with("ERROR:") {
            *self.last_error.lock().unwrap() = Some(line.to_string());
        } else if line.starts_with("[download] Downloading item ") {
            if let Some(c) = ITEM.captures(line) {
                *self.prefix.lock().unwrap() = format!("({}/{}) ", &c[1], &c[2]);
            }
        } else if line.starts_with("[Merger]") {
            self.report(&format!("{prefix}Merging video and audio…"), -1);
        } else if line.starts_with("[ExtractAudio]") {
            self.report(&format!("{prefix}{}", if self.o.format == MP3 { "Converting to MP3…" } else { "Saving M4A…" }), -1);
        } else if line.starts_with("[Metadata]") || line.starts_with("[Fixup") {
            self.report(&format!("{prefix}Finishing…"), -1);
        }
    }

    fn on_progress(&self, line: &str) {
        let p: Vec<&str> = line.split('|').collect();
        if p.len() < 9 {
            return;
        }
        let (done, mut total, speed, eta, frag, frags) = (num(p[2]), num(p[3]), num(p[5]), num(p[6]), num(p[7]), num(p[8]));
        if total <= 0.0 {
            total = num(p[4]);
        }
        let permille = if total > 0.0 {
            (done * 1000.0 / total) as i32
        } else if frags > 0.0 {
            (frag * 1000.0 / frags) as i32
        } else {
            -1
        };
        let what = if !self.is_video() || p[1] == "none" { "audio" } else { "video" };
        let mut s = format!("{}Downloading {what}…", self.prefix());
        if permille >= 0 {
            s.push_str(&format!(" {}%", (permille / 10).min(100)));
        }
        if speed > 0.0 {
            s.push_str(&format!("  ·  {}", format_speed(speed)));
        }
        if eta > 0.0 {
            s.push_str(&format!("  ·  {} left", crate::format_time(eta)));
        }
        self.report(&s, permille.min(1000));
    }

    fn probe(&self, path: &Path) -> MediaInfo {
        let mi = Mutex::new(MediaInfo::default());
        let codec = Mutex::new(String::new());
        let args = ["-v", "error", "-show_entries", "stream=codec_name,codec_type:format=duration", "-of", "default=nw=1"]
            .iter()
            .map(|s| s.to_string())
            .chain([path.display().to_string()])
            .collect::<Vec<_>>();
        let _ = process::run(
            &paths::get().ffprobe(),
            &args,
            Some(&self.job),
            &|line: &str| {
                let mut mi = mi.lock().unwrap();
                let mut codec = codec.lock().unwrap();
                if let Some(c) = line.strip_prefix("codec_name=") {
                    *codec = c.to_string();
                } else if line == "codec_type=video" {
                    if mi.video.is_none() && *codec != "mjpeg" && *codec != "png" {
                        mi.video = Some(codec.clone());
                    }
                } else if line == "codec_type=audio" {
                    mi.audio.push(codec.clone());
                } else if let Some(d) = line.strip_prefix("duration=") {
                    mi.duration = num(d);
                }
            },
            &|_: &str| {},
        );
        mi.into_inner().unwrap()
    }

    /// Makes sure the file is H.264 in the chosen container, converting it (GPU first) only when needed.
    fn ensure_h264(&self, path: &Path) -> Result<PathBuf, Failure> {
        let mi = self.probe(path);
        self.check()?;
        let Some(video) = mi.video.as_deref() else { return Ok(path.to_path_buf()) };
        let ext = if self.o.format == MKV { "mkv" } else { "mp4" };
        let h264 = video == "h264";
        let audio_ok = self.o.format == MKV || mi.audio.iter().all(|a| MP4_AUDIO.contains(&a.as_str()));
        let ext_ok = path.extension().is_some_and(|e| e.eq_ignore_ascii_case(ext));
        if h264 && audio_ok && ext_ok {
            return Ok(path.to_path_buf());
        }

        let dir = path.parent().unwrap_or(Path::new("."));
        let name = path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
        let temp = dir.join(format!("{name}.converting.{ext}"));
        let final_path = dir.join(format!("{name}.{ext}"));
        let audio_args: Vec<String> =
            if audio_ok { vec!["-c:a".into(), "copy".into()] } else { ["-c:a", "aac", "-b:a", "192k"].map(String::from).to_vec() };

        // Each attempt: input options, video codec options, encoder name.
        let mut attempts: Vec<(Vec<String>, Vec<String>, String)> = Vec::new();
        if h264 {
            attempts.push((vec![], vec!["-c:v".into(), "copy".into()], "copy".into()));
        } else {
            self.report(&format!("{}Checking for a GPU encoder…", self.prefix()), -1);
            let enc = encoders::detect();
            if enc == "h264_nvenc" {
                let input = ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"].map(String::from).to_vec();
                attempts.push((input, encoders::args(&enc, true), enc.clone()));
            }
            if enc != encoders::SOFTWARE {
                attempts.push((vec![], encoders::args(&enc, false), enc.clone()));
            }
            attempts.push((vec![], encoders::args(encoders::SOFTWARE, false), encoders::SOFTWARE.into()));
        }

        let mut hardware_failed = false;
        for (input, codec, enc) in attempts {
            self.check()?;
            let label = if h264 {
                format!("{}Remuxing…", self.prefix())
            } else {
                format!("{}Converting to H.264 ({})…", self.prefix(), encoders::describe(&enc))
            };
            let mut a: Vec<String> =
                ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats"].map(String::from).to_vec();
            a.extend(input);
            a.extend(["-i".into(), path.display().to_string(), "-map".into(), "0:v:0".into(), "-map".into(), "0:a?".into()]);
            a.extend(codec);
            a.extend(audio_args.iter().cloned());
            a.push(temp.display().to_string());
            if self.run_ffmpeg(&a, mi.duration, &label) {
                if hardware_failed && enc == encoders::SOFTWARE {
                    encoders::forget();
                }
                fs::remove_file(path)?;
                if final_path.exists() {
                    fs::remove_file(&final_path)?;
                }
                fs::rename(&temp, &final_path)?;
                return Ok(final_path);
            }
            self.check()?;
            let _ = fs::remove_file(&temp);
            if enc != encoders::SOFTWARE && enc != "copy" {
                hardware_failed = true;
            }
        }
        fail("Couldn't convert the video to H.264. Click \"Show log\" for details.")
    }

    fn run_ffmpeg(&self, args: &[String], duration: f64, label: &str) -> bool {
        self.report(label, if duration > 0.0 { 0 } else { -1 });
        self.log(&format!("> ffmpeg {}", args.iter().map(|s| process::quote(s)).collect::<Vec<_>>().join(" ")));
        let speed = Mutex::new(String::new());
        let code = process::run(
            &paths::get().ffmpeg(),
            args,
            Some(&self.job),
            &|line: &str| {
                if let Some(s) = line.strip_prefix("speed=").filter(|s| s.ends_with('x')) {
                    *speed.lock().unwrap() = format!("  ·  {}", s.trim());
                } else if let Some(us) = line.strip_prefix("out_time_us=") {
                    let us = num(us);
                    if duration > 0.0 && us >= 0.0 {
                        let permille = (us / 1000.0 / duration).min(1000.0) as i32;
                        self.report(&format!("{label} {}%{}", permille / 10, speed.lock().unwrap()), permille);
                    }
                }
            },
            &|line: &str| self.log(line),
        );
        matches!(code, Ok(0))
    }

    fn move_to_folder(&self, path: &Path, tmp: &Path) -> Result<PathBuf, Failure> {
        let Ok(rel) = path.strip_prefix(tmp) else { return Ok(path.to_path_buf()) };
        let dest = Path::new(&self.o.folder).join(rel);
        if let Some(parent) = dest.parent() {
            fs::create_dir_all(parent)?;
        }
        if dest.exists() {
            fs::remove_file(&dest)?;
        }
        if fs::rename(path, &dest).is_err() {
            fs::copy(path, &dest)?;
            let _ = fs::remove_file(path);
        }
        Ok(dest)
    }
}

#[derive(Default)]
struct MediaInfo {
    video: Option<String>,
    audio: Vec<String>,
    duration: f64,
}

fn num(s: &str) -> f64 {
    s.trim().parse().unwrap_or(-1.0)
}

fn format_speed(bytes_per_sec: f64) -> String {
    if bytes_per_sec >= 1_048_576.0 {
        format!("{:.1} MB/s", bytes_per_sec / 1_048_576.0)
    } else {
        format!("{:.0} KB/s", bytes_per_sec / 1024.0)
    }
}

#[cfg(windows)]
fn same_volume(a: &Path, b: &Path) -> bool {
    use std::path::Component;
    let root = |p: &Path| {
        std::path::absolute(p).ok().and_then(|p| match p.components().next() {
            Some(Component::Prefix(prefix)) => Some(prefix.as_os_str().to_string_lossy().to_lowercase()),
            _ => None,
        })
    };
    matches!((root(a), root(b)), (Some(x), Some(y)) if x == y)
}

#[cfg(unix)]
fn same_volume(a: &Path, b: &Path) -> bool {
    use std::os::unix::fs::MetadataExt;
    matches!((fs::metadata(a), fs::metadata(b)), (Ok(x), Ok(y)) if x.dev() == y.dev())
}
