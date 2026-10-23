//! Finds the newest yt-dlp: the copy bundled with the app or an auto-updated one.
use crate::paths::{self, exe};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

static ENGINE: Mutex<Option<(PathBuf, String)>> = Mutex::new(None);

pub fn resolve() {
    let p = paths::get();
    let mut best_exe = p.bin.join(exe("yt-dlp"));
    let mut best_ver = read_version(&p.bin.join("yt-dlp-version.txt")).unwrap_or_default();
    if let Ok(entries) = fs::read_dir(p.engine_root()) {
        for dir in entries.flatten().map(|e| e.path()).filter(|d| d.is_dir()) {
            if dir.extension().is_some_and(|e| e == "tmp") {
                continue;
            }
            let candidate = dir.join(exe("yt-dlp"));
            if let Some(ver) = read_version(&dir.join("version.txt")) {
                if ver > best_ver && candidate.exists() {
                    best_ver = ver;
                    best_exe = candidate;
                }
            }
        }
    }
    *ENGINE.lock().unwrap() = Some((best_exe, best_ver));
}

fn read_version(file: &Path) -> Option<String> {
    fs::read_to_string(file).ok().map(|s| s.trim().to_string()).filter(|s| !s.is_empty())
}

pub fn exe_path() -> PathBuf {
    ENGINE.lock().unwrap().as_ref().map(|e| e.0.clone()).unwrap_or_default()
}

pub fn version() -> String {
    ENGINE.lock().unwrap().as_ref().map(|e| e.1.clone()).unwrap_or_default()
}
