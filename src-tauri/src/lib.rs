// Clip Downloader - a small, fast desktop front-end for yt-dlp + ffmpeg.
mod cleanup;
mod download;
mod encoders;
mod engine;
mod errors;
mod links;
mod paths;
mod prefetch;
mod process;
mod settings;
mod updater;

use download::{DownloadJob, Failure, Options};
use prefetch::Prefetch;
use serde::Serialize;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Emitter, Manager, RunEvent, WindowEvent};

static PREFETCH: Mutex<Option<Arc<Prefetch>>> = Mutex::new(None);
static JOB: Mutex<Option<Arc<DownloadJob>>> = Mutex::new(None);
static LOG_PATH: Mutex<Option<PathBuf>> = Mutex::new(None);

/// A unique, increasing name part for temp files.
pub(crate) fn stamp() -> String {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0);
    format!("{nanos}-{}", COUNTER.fetch_add(1, Ordering::SeqCst))
}

pub(crate) fn format_time(seconds: f64) -> String {
    let t = seconds.round().max(0.0) as u64;
    let (h, m, s) = (t / 3600, t / 60 % 60, t % 60);
    if h > 0 {
        format!("{h}:{m:02}:{s:02}")
    } else {
        format!("{m}:{s:02}")
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct InitInfo {
    folder: String,
    format: usize,
    quality: usize,
    engine_version: String,
    app_version: String,
}

#[tauri::command]
fn init_state(app: AppHandle) -> InitInfo {
    let s = settings::get();
    let folder = if s.folder.trim().is_empty() { paths::get().downloads.display().to_string() } else { s.folder };
    InitInfo {
        folder,
        format: s.format,
        quality: s.quality,
        engine_version: engine::version(),
        app_version: app.package_info().version.to_string(),
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct LinkInfo {
    url: Option<String>,
    site: Option<&'static str>,
    collection: bool,
    video_id: Option<String>,
    /// The link is the one already being looked up, so the shown details still apply.
    same: bool,
}

/// Recognizes a pasted link and starts fetching its details in the background.
#[tauri::command]
fn inspect(app: AppHandle, text: String) -> LinkInfo {
    let url = links::normalize(&text);
    let info = |same: bool| LinkInfo {
        site: url.as_deref().and_then(links::platform),
        collection: url.as_deref().is_some_and(links::is_collection),
        video_id: url.as_deref().and_then(links::video_id),
        url: url.clone(),
        same,
    };
    let mut current = PREFETCH.lock().unwrap();
    if url.is_some() && current.as_ref().is_some_and(|p| Some(&p.url) == url.as_ref()) {
        return info(true);
    }
    if let Some(p) = current.take() {
        p.cancel();
    }
    if let Some(u) = url.as_ref().filter(|u| !links::is_collection(u)) {
        *current = Some(Prefetch::start(u.clone(), move |info| {
            let _ = app.emit("prefetch", info);
        }));
    }
    info(false)
}

/// Which supported site a piece of text links to, without looking anything up.
#[tauri::command]
fn detect(text: String) -> Option<&'static str> {
    links::normalize(&text).as_deref().and_then(links::platform)
}

#[tauri::command]
fn save_settings(folder: String, format: usize, quality: usize) {
    settings::update(|s| {
        s.folder = folder.trim().to_string();
        s.format = format.min(3);
        s.quality = quality.min(4);
    });
}

#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
struct Progress {
    text: String,
    permille: i32,
}

#[derive(Serialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
struct Done {
    ok: bool,
    canceled: bool,
    files: Vec<String>,
    partial: bool,
    seconds: f64,
    message: Option<String>,
    has_log: bool,
}

#[tauri::command]
fn start_download(app: AppHandle, options: Options) -> Result<(), String> {
    let mut slot = JOB.lock().unwrap();
    if slot.is_some() {
        return Err("A download is already running.".into());
    }
    let folder = options.folder.trim().to_string();
    if folder.is_empty() || std::fs::create_dir_all(&folder).is_err() {
        return Err("Choose a folder to save to.".into());
    }
    save_settings(folder.clone(), options.format, options.quality);
    let options = Options { folder, ..options };

    let pf = PREFETCH.lock().unwrap().as_ref().filter(|p| p.url == options.url).cloned();
    // Called for every progress line; refresh the UI at most ~15 times a second.
    let last = Mutex::new(Instant::now() - Duration::from_secs(1));
    let emitter = app.clone();
    let report = move |text: &str, permille: i32| {
        let mut last = last.lock().unwrap();
        if (0..1000).contains(&permille) && last.elapsed() < Duration::from_millis(66) {
            return;
        }
        *last = Instant::now();
        let _ = emitter.emit("progress", Progress { text: text.to_string(), permille });
    };
    let job = Arc::new(DownloadJob::new(options, Box::new(report)));
    *slot = Some(job.clone());
    drop(slot);

    std::thread::spawn(move || {
        let started = Instant::now();
        let mut done = Done { seconds: 0.0, ..Default::default() };
        match job.execute(pf) {
            Ok(outcome) if !outcome.files.is_empty() => {
                done.ok = true;
                done.partial = outcome.partial;
                done.files = outcome.files.iter().map(|f| f.display().to_string()).collect();
            }
            Ok(_) => done.message = Some("Nothing was downloaded.".into()),
            Err(Failure::Canceled) => done.canceled = true,
            Err(Failure::Message(m)) => done.message = Some(m),
        }
        if done.message.is_some() {
            let log = job.save_log();
            done.has_log = log.is_some();
            *LOG_PATH.lock().unwrap() = log;
        }
        done.seconds = started.elapsed().as_secs_f64();
        *JOB.lock().unwrap() = None;
        let _ = app.emit("done", done);
    });
    Ok(())
}

#[tauri::command]
fn cancel_download() {
    if let Some(job) = JOB.lock().unwrap().clone() {
        job.cancel();
    }
}

#[tauri::command]
fn reveal(path: String) -> Result<(), String> {
    tauri_plugin_opener::reveal_item_in_dir(&path).map_err(|e| e.to_string())
}

#[tauri::command]
fn open_folder(path: String) -> Result<(), String> {
    std::fs::create_dir_all(&path).map_err(|e| e.to_string())?;
    tauri_plugin_opener::open_path(&path, None::<&str>).map_err(|e| e.to_string())
}

#[tauri::command]
fn open_log() -> Result<(), String> {
    let path = LOG_PATH.lock().unwrap().clone().ok_or("No log yet.")?;
    tauri_plugin_opener::open_path(path.display().to_string(), None::<&str>).map_err(|e| e.to_string())
}

/// Stops everything that is running and quits, after the user confirmed it.
#[tauri::command]
fn quit_app(app: AppHandle) {
    shutdown();
    let deadline = Instant::now() + Duration::from_secs(3);
    while JOB.lock().unwrap().is_some() && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(50));
    }
    app.exit(0);
}

fn shutdown() {
    cancel_download();
    if let Some(p) = PREFETCH.lock().unwrap().take() {
        p.cancel();
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let started = SystemTime::now();
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(w) = app.get_webview_window("main") {
                let _ = w.unminimize();
                let _ = w.set_focus();
            }
        }))
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_clipboard_manager::init())
        .setup(move |app| {
            let path = app.path();
            let home = path.home_dir().unwrap_or_default();
            paths::init(paths::Paths {
                bin: dunce::simplified(&path.resource_dir()?).join("bin"),
                data: path.app_local_data_dir()?,
                config: path.app_config_dir()?,
                temp: std::env::temp_dir().join("ClipDownloader"),
                downloads: path.download_dir().unwrap_or_else(|_| home.join("Downloads")),
            });
            settings::load();
            engine::resolve();
            let handle = app.handle().clone();
            std::thread::spawn(move || {
                // Cleanup runs before updating, so it never touches a build that's in use.
                cleanup::run(&settings::get().folder, started);
                if updater::is_due() && updater::check_and_update() {
                    let _ = handle.emit("engine", engine::version());
                }
            });
            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                if JOB.lock().unwrap().is_some() {
                    api.prevent_close();
                    let _ = window.emit("confirm-close", ());
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            init_state,
            inspect,
            detect,
            save_settings,
            start_download,
            cancel_download,
            reveal,
            open_folder,
            open_log,
            quit_app
        ])
        .build(tauri::generate_context!())
        .expect("error while building Clip Downloader");
    app.run(|_, event| {
        if let RunEvent::Exit = event {
            shutdown();
        }
    });
}
