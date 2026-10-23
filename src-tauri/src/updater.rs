//! Keeps yt-dlp current: sites change often, and a newer yt-dlp usually fixes broken downloads.
use crate::{engine, paths, settings};
use sha2::{Digest, Sha256};
use std::fs::{self, File};
use std::io;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

static RUNNING: AtomicBool = AtomicBool::new(false);
const USER_AGENT: &str = "ClipDownloader";

fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

pub fn is_due() -> bool {
    now().saturating_sub(settings::get().last_update_check) > 12 * 3600
}

/// The yt-dlp release file for this platform.
fn asset() -> Option<&'static str> {
    if cfg!(windows) {
        Some("yt-dlp.exe")
    } else if cfg!(target_os = "macos") {
        Some("yt-dlp_macos")
    } else if cfg!(all(target_os = "linux", target_arch = "x86_64")) {
        Some("yt-dlp_linux")
    } else if cfg!(all(target_os = "linux", target_arch = "aarch64")) {
        Some("yt-dlp_linux_aarch64")
    } else {
        None
    }
}

/// Installs the latest yt-dlp release if it is newer than ours. Returns true if it did.
pub fn check_and_update() -> bool {
    if RUNNING.swap(true, Ordering::SeqCst) {
        return false;
    }
    settings::update(|s| s.last_update_check = now());
    let updated = install_latest().unwrap_or(false);
    RUNNING.store(false, Ordering::SeqCst);
    updated
}

fn install_latest() -> Result<bool, Box<dyn std::error::Error>> {
    let Some(asset) = asset() else { return Ok(false) };
    let Some(tag) = latest_tag() else { return Ok(false) };
    if tag <= engine::version() {
        return Ok(false);
    }

    let base = format!("https://github.com/yt-dlp/yt-dlp/releases/download/{tag}/");
    let agent = ureq::AgentBuilder::new().user_agent(USER_AGENT).timeout_connect(Duration::from_secs(15)).build();
    let sums = agent.get(&format!("{base}SHA2-256SUMS")).call()?.into_string()?;
    let Some(expected) = sums.lines().find_map(|line| {
        let mut parts = line.split_whitespace();
        let (hash, name) = (parts.next()?, parts.next()?);
        (name == asset).then(|| hash.to_ascii_lowercase())
    }) else {
        return Ok(false);
    };

    let root = paths::get().engine_root();
    let tmp = root.join(format!("{tag}.tmp"));
    let dest = root.join(&tag);
    let _ = fs::remove_dir_all(&tmp);
    fs::create_dir_all(&tmp)?;
    let result = (|| -> Result<bool, Box<dyn std::error::Error>> {
        let file = tmp.join(paths::exe("yt-dlp"));
        let mut reader = agent.get(&format!("{base}{asset}")).call()?.into_reader();
        io::copy(&mut reader, &mut File::create(&file)?)?;
        if sha256(&file)? != expected {
            return Ok(false);
        }
        make_executable(&file)?;
        fs::write(tmp.join("version.txt"), &tag)?;
        let _ = fs::remove_dir_all(&dest);
        fs::rename(&tmp, &dest)?;
        engine::resolve();
        Ok(true)
    })();
    let _ = fs::remove_dir_all(&tmp);
    result
}

fn latest_tag() -> Option<String> {
    let agent = ureq::AgentBuilder::new().redirects(0).user_agent(USER_AGENT).timeout(Duration::from_secs(15)).build();
    let resp = agent.head("https://github.com/yt-dlp/yt-dlp/releases/latest").call().ok()?;
    let location = resp.header("location")?;
    let tag = location.rsplit("/tag/").next()?;
    let valid = !tag.is_empty() && tag.len() <= 16 && tag.chars().all(|c| c.is_ascii_digit() || c == '.');
    valid.then(|| tag.to_string())
}

fn sha256(file: &Path) -> io::Result<String> {
    let mut hasher = Sha256::new();
    io::copy(&mut File::open(file)?, &mut hasher)?;
    Ok(hasher.finalize().iter().map(|b| format!("{b:02x}")).collect())
}

#[allow(unused_variables)]
pub fn make_executable(file: &Path) -> io::Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(file, fs::Permissions::from_mode(0o755))?;
    }
    Ok(())
}
