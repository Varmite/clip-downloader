use crate::paths;
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;
use std::sync::Mutex;

#[derive(Serialize, Deserialize, Clone, Default, Debug)]
#[serde(default, rename_all = "camelCase")]
pub struct Settings {
    pub folder: String,
    pub format: usize,
    pub quality: usize,
    pub encoder: Option<String>,
    /// Unix seconds of the last yt-dlp update check.
    pub last_update_check: u64,
}

static SETTINGS: Mutex<Option<Settings>> = Mutex::new(None);

fn file() -> PathBuf {
    paths::get().config.join("settings.json")
}

pub fn load() {
    let mut s: Settings = fs::read_to_string(file())
        .ok()
        .and_then(|t| serde_json::from_str(&t).ok())
        .unwrap_or_default();
    if s.format > 3 {
        s.format = 0;
    }
    if s.quality > 4 {
        s.quality = 0;
    }
    *SETTINGS.lock().unwrap() = Some(s);
}

pub fn get() -> Settings {
    SETTINGS.lock().unwrap().clone().unwrap_or_default()
}

pub fn update(f: impl FnOnce(&mut Settings)) {
    let snapshot = {
        let mut guard = SETTINGS.lock().unwrap();
        let s = guard.get_or_insert_with(Default::default);
        f(s);
        s.clone()
    };
    let _ = fs::create_dir_all(&paths::get().config);
    if let Ok(json) = serde_json::to_string_pretty(&snapshot) {
        let _ = fs::write(file(), json);
    }
}
