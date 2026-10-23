use regex::Regex;
use std::sync::LazyLock;
use url::Url;

pub const YOUTUBE: &str = "YouTube";
pub const TIKTOK: &str = "TikTok";
pub const INSTAGRAM: &str = "Instagram";

static SHORT_ID: LazyLock<Regex> = LazyLock::new(|| Regex::new("^/([A-Za-z0-9_-]{11})$").unwrap());
static PATH_ID: LazyLock<Regex> =
    LazyLock::new(|| Regex::new("^/(?:shorts|live|embed|v|e)/([A-Za-z0-9_-]{11})(?:/|$)").unwrap());
static ID: LazyLock<Regex> = LazyLock::new(|| Regex::new("^[A-Za-z0-9_-]{11}$").unwrap());
static TIKTOK_PROFILE: LazyLock<Regex> = LazyLock::new(|| Regex::new("^/@[^/]+/?$").unwrap());

/// Turns pasted text into a full http(s) link, or None if it can't be one.
pub fn normalize(text: &str) -> Option<String> {
    let t = text.trim();
    if t.is_empty() || t.len() > 2000 || t.chars().any(char::is_whitespace) {
        return None;
    }
    let has_scheme = |p: &str| t.get(..p.len()).is_some_and(|s| s.eq_ignore_ascii_case(p));
    let s = if has_scheme("http://") || has_scheme("https://") { t.to_string() } else { format!("https://{t}") };
    let u = Url::parse(&s).ok()?;
    if !u.host_str()?.contains('.') {
        return None;
    }
    Some(s)
}

fn is_host(h: &str, domain: &str) -> bool {
    h == domain || h.ends_with(&format!(".{domain}"))
}

/// Works out which site a link is from: YouTube, TikTok, Instagram, or None for anything else.
pub fn platform(url: &str) -> Option<&'static str> {
    let u = Url::parse(url).ok()?;
    let h = u.host_str()?.to_ascii_lowercase();
    if h == "youtu.be" || is_host(&h, "youtube.com") || is_host(&h, "youtube-nocookie.com") {
        Some(YOUTUBE)
    } else if is_host(&h, "tiktok.com") {
        Some(TIKTOK)
    } else if is_host(&h, "instagram.com") || h == "instagr.am" {
        Some(INSTAGRAM)
    } else {
        None
    }
}

pub fn is_youtube(url: &str) -> bool {
    platform(url) == Some(YOUTUBE)
}

pub fn video_id(url: &str) -> Option<String> {
    if !is_youtube(url) {
        return None;
    }
    let u = Url::parse(url).ok()?;
    let capture = |re: &Regex| re.captures(u.path()).map(|c| c[1].to_string());
    if u.host_str()? == "youtu.be" {
        return capture(&SHORT_ID);
    }
    u.query_pairs()
        .find(|(k, v)| k == "v" && ID.is_match(v))
        .map(|(_, v)| v.into_owned())
        .or_else(|| capture(&PATH_ID))
}

/// A link that isn't a single video: a YouTube playlist or channel, or a TikTok profile.
pub fn is_collection(url: &str) -> bool {
    match platform(url) {
        Some(YOUTUBE) => video_id(url).is_none(),
        Some(TIKTOK) => Url::parse(url).is_ok_and(|u| TIKTOK_PROFILE.is_match(u.path())),
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_sites() {
        let n = |s: &str| normalize(s).unwrap();
        assert_eq!(platform(&n("youtu.be/dQw4w9WgXcQ")), Some(YOUTUBE));
        assert_eq!(platform(&n("https://m.youtube.com/watch?v=dQw4w9WgXcQ")), Some(YOUTUBE));
        assert_eq!(platform(&n("https://vm.tiktok.com/ZMabc123/")), Some(TIKTOK));
        assert_eq!(platform(&n("https://www.instagram.com/reel/CqJ8fFPOZ9J/")), Some(INSTAGRAM));
        assert_eq!(platform(&n("https://instagr.am/p/abc/")), Some(INSTAGRAM));
        assert_eq!(platform(&n("https://notyoutube.com/watch")), None);
        assert_eq!(normalize("not a link"), None);
    }

    #[test]
    fn finds_ids_and_collections() {
        assert_eq!(video_id("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1").as_deref(), Some("dQw4w9WgXcQ"));
        assert_eq!(video_id("https://youtube.com/shorts/dQw4w9WgXcQ").as_deref(), Some("dQw4w9WgXcQ"));
        assert!(is_collection("https://www.youtube.com/playlist?list=PL123"));
        assert!(is_collection("https://www.tiktok.com/@someone"));
        assert!(!is_collection("https://www.tiktok.com/@someone/video/7106594312292453675"));
        assert!(!is_collection("https://www.instagram.com/reel/CqJ8fFPOZ9J/"));
    }
}
