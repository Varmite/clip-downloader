# Third-party software

Clip Downloader's own code is MIT-licensed (see `LICENSE`). The installers also include these separate programs,
which keep their own licenses. They run as separate executables; Clip Downloader does not link against them.

| Program | What it does here | License | Source |
| --- | --- | --- | --- |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | Reads video pages and downloads the streams | The Unlicense (public domain) | https://github.com/yt-dlp/yt-dlp |
| [FFmpeg](https://ffmpeg.org) (`ffmpeg`, `ffprobe`) | Merges, converts and inspects media | GPL v3 or later (these builds include GPL components such as x264) | Windows/Linux builds: https://github.com/yt-dlp/FFmpeg-Builds · macOS builds: https://ffmpeg.martin-riedl.de · FFmpeg source: https://ffmpeg.org/download.html |
| [Deno](https://deno.com) | JavaScript runtime yt-dlp uses for YouTube | MIT | https://github.com/denoland/deno |
| [Tauri](https://tauri.app) and its Rust/JS dependencies | App framework | MIT / Apache-2.0 | https://github.com/tauri-apps/tauri |

The full GPL v3 text is available at https://www.gnu.org/licenses/gpl-3.0.html. The exact FFmpeg source used for each
build is linked from the build provider's release page above.
