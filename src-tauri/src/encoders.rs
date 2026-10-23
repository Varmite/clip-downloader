//! Picks the fastest working H.264 encoder (GPU first) and remembers it.
use crate::{paths, process, settings};
use std::sync::Mutex;

pub const SOFTWARE: &str = "libx264";
static CACHED: Mutex<Option<String>> = Mutex::new(None);

fn hardware() -> &'static [&'static str] {
    if cfg!(target_os = "macos") {
        &["h264_videotoolbox"]
    } else if cfg!(windows) {
        &["h264_nvenc", "h264_qsv", "h264_amf"]
    } else {
        &["h264_nvenc", "h264_qsv"]
    }
}

pub fn detect() -> String {
    let mut cached = CACHED.lock().unwrap();
    if let Some(enc) = cached.as_ref() {
        return enc.clone();
    }
    let found = settings::get().encoder.unwrap_or_else(|| {
        let works = |enc: &str| {
            let mut a: Vec<String> = ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=320x240:d=0.1", "-frames:v", "2"]
                .map(String::from)
                .to_vec();
            a.extend(args(enc, false));
            a.extend(["-f", "null", "-"].map(String::from));
            matches!(process::run(&paths::get().ffmpeg(), &a, None, &|_: &str| {}, &|_: &str| {}), Ok(0))
        };
        let enc = hardware().iter().find(|e| works(e)).copied().unwrap_or(SOFTWARE).to_string();
        settings::update(|s| s.encoder = Some(enc.clone()));
        enc
    });
    *cached = Some(found.clone());
    found
}

pub fn forget() {
    *CACHED.lock().unwrap() = None;
    settings::update(|s| s.encoder = None);
}

pub fn args(enc: &str, gpu_frames: bool) -> Vec<String> {
    let a: &[&str] = match enc {
        "h264_nvenc" if gpu_frames => &["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "20", "-b:v", "0", "-profile:v", "high"],
        "h264_nvenc" => &[
            "-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "20", "-b:v", "0", "-profile:v", "high", "-pix_fmt", "yuv420p",
        ],
        "h264_qsv" => &["-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "20", "-profile:v", "high", "-pix_fmt", "nv12"],
        "h264_amf" => &[
            "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "22", "-qp_b", "24", "-profile:v", "high", "-pix_fmt", "yuv420p",
        ],
        "h264_videotoolbox" => &["-c:v", "h264_videotoolbox", "-q:v", "65", "-profile:v", "high", "-pix_fmt", "yuv420p"],
        _ => &["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p"],
    };
    a.iter().map(|s| s.to_string()).collect()
}

pub fn describe(enc: &str) -> &'static str {
    match enc {
        "h264_nvenc" => "NVIDIA GPU",
        "h264_qsv" => "Intel GPU",
        "h264_amf" => "AMD GPU",
        "h264_videotoolbox" => "Apple GPU",
        _ => "CPU",
    }
}
