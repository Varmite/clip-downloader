//! Removes leftovers from earlier runs (temp files, old yt-dlp builds).
use crate::{engine, paths};
use std::fs;
use std::path::Path;
use std::time::SystemTime;

pub fn run(folder: &str, started: SystemTime) {
    let p = paths::get();
    let old = |path: &Path| fs::metadata(path).and_then(|m| m.modified()).is_ok_and(|t| t < started);
    let entries = |dir: &Path| fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path()).collect::<Vec<_>>();

    for path in entries(&p.temp) {
        let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        if !old(&path) || name == "last-log.txt" {
            continue;
        }
        if path.is_dir() {
            let _ = fs::remove_dir_all(&path);
        } else {
            let _ = fs::remove_file(&path);
        }
    }
    for path in entries(Path::new(folder)) {
        let hidden_job = path.file_name().is_some_and(|n| n.to_string_lossy().starts_with(".clipdownloader-"));
        if hidden_job && path.is_dir() && old(&path) {
            let _ = fs::remove_dir_all(&path);
        }
    }
    let current = engine::exe_path();
    for dir in entries(&p.engine_root()) {
        if !current.starts_with(&dir) {
            let _ = fs::remove_dir_all(&dir);
        }
    }
}
