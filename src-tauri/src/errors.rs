//! Turns yt-dlp's error lines into short messages people can act on.
use regex::Regex;
use std::sync::LazyLock;

static PREFIX: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^ERROR:\s*(\[[^\]]+\]\s*)?([\w-]+:\s*)?").unwrap());

fn has(e: &str, part: &str) -> bool {
    e.to_lowercase().contains(&part.to_lowercase())
}

fn any(e: &str, parts: &[&str]) -> bool {
    parts.iter().any(|p| has(e, p))
}

pub fn is_format_error(e: &str) -> bool {
    any(e, &["Requested format is not available", "No video formats found", "Only images are available"])
}

pub fn is_permanent_error(e: &str) -> bool {
    any(
        e,
        &[
            "Private video", "members-only", "confirm your age", "Video unavailable", "video is unavailable",
            "video is not available", "has been removed", "Unsupported URL", "live event will begin", "Premieres in",
        ],
    )
}

pub fn friendly_error(e: &str) -> String {
    let msg = |s: &str| s.to_string();
    if e.is_empty() {
        return msg("Download failed. Click \"Show log\" for details.");
    }
    if has(e, "not a bot") {
        return msg("YouTube wants to confirm you're not a bot. Wait a few minutes and try again.");
    }
    if any(e, &["login required", "rate-limit reached", "empty media response", "log in"]) {
        return msg("The site wants you to sign in to see this. It may be private, or try again in a few minutes.");
    }
    if any(e, &["confirm your age", "age-restricted"]) {
        return msg("This video is age-restricted and needs a signed-in account.");
    }
    if any(e, &["Private video", "account is private"]) {
        return msg("This video is private.");
    }
    if any(e, &["IP address is blocked", "not available in your"]) {
        return msg("This video isn't available in your country.");
    }
    if any(e, &["members-only", "Join this channel"]) {
        return msg("This video is only for channel members.");
    }
    if is_format_error(e) {
        return msg("No downloadable version of this video was found.");
    }
    if any(e, &["live event will begin", "Premieres in"]) {
        return msg("This live stream or premiere hasn't started yet.");
    }
    if has(e, "HTTP Error 403") {
        return msg("The site refused the download (HTTP 403). Try again in a moment.");
    }
    if any(e, &["Video unavailable", "has been removed", "not available"]) {
        return msg("This video is unavailable.");
    }
    if any(e, &["Unsupported URL", "is not a valid URL"]) {
        return msg("That link isn't a supported video link.");
    }
    if any(e, &["No space left", "Errno 28"]) {
        return msg("The disk is full.");
    }
    if any(e, &["Permission denied", "Errno 13"]) {
        return msg("Can't write to the save folder. Pick another folder.");
    }
    if any(e, &["getaddrinfo", "Failed to resolve", "timed out", "Unable to download", "Connection"]) {
        return msg("Network problem. Check your internet connection and try again.");
    }
    let e = PREFIX.replace(e, "");
    if e.chars().count() > 160 {
        format!("{}…", e.chars().take(160).collect::<String>())
    } else {
        e.into_owned()
    }
}
