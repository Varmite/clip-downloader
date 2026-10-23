//! Fetches video info in the background as soon as a link is pasted, so Download can start right away.
use crate::process::{self, Job};
use crate::{engine, errors, paths};
use serde::Serialize;
use serde_json::Value;
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

#[derive(Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Info {
    pub url: String,
    pub title: Option<String>,
    pub channel: Option<String>,
    pub thumbnail: Option<String>,
    pub duration: f64,
    pub is_live: bool,
    pub error: Option<String>,
    pub friendly_error: Option<String>,
    #[serde(skip)]
    pub json_path: Option<PathBuf>,
    /// Tallest SDR video stream up to 4K, and tallest native H.264 one.
    #[serde(skip)]
    pub max_height: i64,
    #[serde(skip)]
    pub max_h264_height: i64,
}

pub struct Prefetch {
    pub url: String,
    pub started: Instant,
    job: Job,
    canceled: AtomicBool,
    result: Mutex<Option<Info>>,
    finished: Condvar,
}

impl Prefetch {
    pub fn start(url: String, on_done: impl FnOnce(&Info) + Send + 'static) -> Arc<Prefetch> {
        let p = Arc::new(Prefetch {
            url,
            started: Instant::now(),
            job: Job::new(),
            canceled: AtomicBool::new(false),
            result: Mutex::new(None),
            finished: Condvar::new(),
        });
        let worker = p.clone();
        std::thread::spawn(move || {
            let info = worker.fetch();
            *worker.result.lock().unwrap() = Some(info.clone());
            worker.finished.notify_all();
            if !worker.is_canceled() {
                on_done(&info);
            }
        });
        p
    }

    fn is_canceled(&self) -> bool {
        self.canceled.load(Ordering::SeqCst)
    }

    fn fetch(&self) -> Info {
        let mut info = Info { url: self.url.clone(), ..Default::default() };
        for attempt in 0..2 {
            if self.is_canceled() {
                break;
            }
            let json = Mutex::new(String::new());
            let last_error = Mutex::new(None::<String>);
            let mut args = process::ytdlp_args(attempt == 0);
            args.extend(["-J".into(), "--no-playlist".into(), "--".into(), self.url.clone()]);
            let code = process::run(
                &engine::exe_path(),
                &args,
                Some(&self.job),
                &|line: &str| json.lock().unwrap().push_str(line),
                &|line: &str| {
                    if line.starts_with("ERROR:") {
                        *last_error.lock().unwrap() = Some(line.to_string());
                    }
                },
            );
            if self.is_canceled() {
                break;
            }
            let json = json.into_inner().unwrap();
            match code {
                Ok(0) if !json.is_empty() => {
                    fill(&mut info, &json);
                    let _ = fs::create_dir_all(&paths::get().temp);
                    let path = paths::get().temp.join(format!("info-{}.json", crate::stamp()));
                    if fs::write(&path, &json).is_ok() {
                        info.json_path = Some(path);
                    }
                    info.error = None;
                    return info;
                }
                Ok(code) => {
                    info.error = Some(last_error.into_inner().unwrap().unwrap_or(format!("yt-dlp exited with code {code}")))
                }
                Err(e) => info.error = Some(format!("Couldn't start yt-dlp: {e}")),
            }
            if !info.error.as_deref().is_some_and(errors::is_format_error) {
                break;
            }
        }
        info.friendly_error = info.error.as_deref().map(errors::friendly_error);
        info
    }

    /// Waits for the info; None if `canceled` says to stop waiting.
    pub fn wait(&self, canceled: &dyn Fn() -> bool) -> Option<Info> {
        let mut guard = self.result.lock().unwrap();
        loop {
            if let Some(info) = guard.as_ref() {
                return Some(info.clone());
            }
            if canceled() {
                return None;
            }
            guard = self.finished.wait_timeout(guard, Duration::from_millis(100)).unwrap().0;
        }
    }

    pub fn cancel(&self) {
        self.canceled.store(true, Ordering::SeqCst);
        self.job.kill();
        if let Some(path) = self.result.lock().unwrap().as_ref().and_then(|i| i.json_path.as_ref()) {
            let _ = fs::remove_file(path);
        }
    }
}

fn fill(info: &mut Info, json: &str) {
    let Ok(d) = serde_json::from_str::<Value>(json) else { return };
    let text = |v: &Value, key: &str| v.get(key).and_then(Value::as_str).filter(|s| !s.is_empty()).map(String::from);
    info.title = Some(text(&d, "title").unwrap_or_else(|| "(untitled)".into()));
    info.channel = text(&d, "channel").or_else(|| text(&d, "uploader"));
    info.thumbnail = text(&d, "thumbnail");
    info.duration = d.get("duration").and_then(Value::as_f64).unwrap_or(0.0);
    info.is_live = d.get("is_live").and_then(Value::as_bool).unwrap_or(false);
    for f in d.get("formats").and_then(Value::as_array).into_iter().flatten() {
        let vcodec = text(f, "vcodec").unwrap_or_else(|| "none".into());
        let range = text(f, "dynamic_range").unwrap_or_else(|| "SDR".into());
        let Some(height) = f.get("height").and_then(Value::as_i64) else { continue };
        if vcodec == "none" || range != "SDR" || height > 2160 {
            continue;
        }
        info.max_height = info.max_height.max(height);
        if vcodec.starts_with("avc1") || vcodec.starts_with("h264") {
            info.max_h264_height = info.max_h264_height.max(height);
        }
    }
}
