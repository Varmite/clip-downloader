use std::path::PathBuf;
use std::sync::OnceLock;

pub struct Paths {
    /// Bundled yt-dlp, ffmpeg, ffprobe and deno.
    pub bin: PathBuf,
    /// Auto-updated yt-dlp builds live here.
    pub data: PathBuf,
    pub config: PathBuf,
    pub temp: PathBuf,
    pub downloads: PathBuf,
}

static PATHS: OnceLock<Paths> = OnceLock::new();

pub fn init(paths: Paths) {
    let _ = PATHS.set(paths);
}

pub fn get() -> &'static Paths {
    PATHS.get().expect("paths are set during setup")
}

/// Adds ".exe" on Windows.
pub fn exe(name: &str) -> String {
    if cfg!(windows) {
        format!("{name}.exe")
    } else {
        name.to_string()
    }
}

impl Paths {
    pub fn ffmpeg(&self) -> PathBuf {
        self.bin.join(exe("ffmpeg"))
    }

    pub fn ffprobe(&self) -> PathBuf {
        self.bin.join(exe("ffprobe"))
    }

    pub fn deno(&self) -> PathBuf {
        self.bin.join(exe("deno"))
    }

    pub fn engine_root(&self) -> PathBuf {
        self.data.join("engine")
    }
}
