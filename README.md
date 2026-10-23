# Clip Downloader

A small, fast desktop app for saving videos from **YouTube**, **TikTok** and **Instagram**.
Paste a link, press **Download**. It works out which site the link is from by itself.

- **Video:** H.264 MP4 or MKV that plays everywhere, up to 4K
- **Audio:** MP3, or the original M4A (fastest)
- Playlists, channels and TikTok profiles download into their own folder
- Light and dark mode follow your system theme
- Uses your GPU (NVIDIA, Intel, AMD, Apple) when a video needs converting
- Keeps its downloader up to date by itself, so site changes don't break it for long
- Free and open source, no ads, no account

## Download

Get the latest version from the **[Releases page](../../releases/latest)**:

| System | File |
| --- | --- |
| Windows 10/11 | `…_x64-setup.exe` or `…_x64_en-US.msi` |
| macOS (Apple Silicon) | `…_aarch64.dmg` |
| macOS (Intel) | `…_x64.dmg` |
| Ubuntu / Debian | `…_amd64.deb` |
| Fedora / openSUSE | `…x86_64.rpm` |
| Any Linux | `…_amd64.AppImage` |

The installers aren't code-signed yet, so your system may warn you the first time:

- **Windows:** on the SmartScreen prompt, click **More info → Run anyway**.
- **macOS:** right-click the app and choose **Open**, or run `xattr -cr "/Applications/Clip Downloader.app"`.

## Using it

1. Copy a video link. Clip Downloader picks it up from the clipboard when you switch to it, or press **Paste**.
2. Choose a format and quality.
3. Press **Download**. Files go to your Downloads folder unless you pick another one.

Some Instagram posts can only be seen when signed in; those can't be downloaded yet.

Please only download videos you have the right to, and respect each site's terms.

## Building from source

You need [Node.js](https://nodejs.org) 20+, [Rust](https://rustup.rs), and the
[Tauri prerequisites](https://tauri.app/start/prerequisites/) for your system.

```sh
npm install
npm run dev      # run the app
npm run build    # build installers for this system into src-tauri/target/release/bundle
```

The first run downloads yt-dlp, ffmpeg and deno for your platform into `src-tauri/binaries`
(`scripts/fetch-deps.mjs`).

### Releasing

Bump `version` in `package.json`, `src-tauri/tauri.conf.json` and `src-tauri/Cargo.toml`, then push a tag:

```sh
git tag v2.0.1 && git push origin v2.0.1
```

GitHub Actions builds the Windows, macOS and Linux installers and publishes them as a release.

## Project layout

| Path | |
| --- | --- |
| `src/` | The interface (HTML, CSS, JavaScript) |
| `src-tauri/src/` | The Rust side: link detection, yt-dlp/ffmpeg runs, H.264 conversion, updates |
| `scripts/fetch-deps.mjs` | Downloads the bundled tools for each platform |
| `.github/workflows/release.yml` | Builds and publishes the installers |
| `legacy/windows-winforms/` | The original Windows-only version (YT Downloader 1.x) |

## License

MIT, see [LICENSE](LICENSE). Bundled tools keep their own licenses, see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
