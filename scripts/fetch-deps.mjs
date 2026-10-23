// Downloads the tools Clip Downloader bundles (yt-dlp, ffmpeg, ffprobe, deno) for the platform being built,
// into src-tauri/binaries. Runs automatically before `tauri dev` and `tauri build`; skips work if they're already there.
// Pass --force to re-download.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.join(root, 'src-tauri', 'binaries');
const work = path.join(root, 'src-tauri', 'target', 'deps-download');

// Tauri sets TAURI_ENV_TARGET_TRIPLE when it runs this script; otherwise use the machine we're on.
const host = { win32: 'pc-windows-msvc', darwin: 'apple-darwin', linux: 'unknown-linux-gnu' }[process.platform];
const arch = { x64: 'x86_64', arm64: 'aarch64' }[process.arch];
const triple = process.env.TAURI_ENV_TARGET_TRIPLE || `${arch}-${host}`;

const FFMPEG_BUILDS = 'https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest';
const MAC_FFMPEG = 'https://ffmpeg.martin-riedl.de/redirect/latest/macos';
const targets = {
  // The shared Windows build is ~150 MB smaller than the static one (its DLLs sit next to the exes).
  'x86_64-pc-windows-msvc': { ytdlp: 'yt-dlp.exe', ffmpeg: { archive: `${FFMPEG_BUILDS}/ffmpeg-master-latest-win64-gpl-shared.zip` } },
  'aarch64-apple-darwin': { ytdlp: 'yt-dlp_macos', ffmpeg: { mac: `${MAC_FFMPEG}/arm64/release` } },
  'x86_64-apple-darwin': { ytdlp: 'yt-dlp_macos', ffmpeg: { mac: `${MAC_FFMPEG}/amd64/release` } },
  'x86_64-unknown-linux-gnu': { ytdlp: 'yt-dlp_linux', ffmpeg: { archive: `${FFMPEG_BUILDS}/ffmpeg-master-latest-linux64-gpl.tar.xz` } },
  'aarch64-unknown-linux-gnu': { ytdlp: 'yt-dlp_linux_aarch64', ffmpeg: { archive: `${FFMPEG_BUILDS}/ffmpeg-master-latest-linuxarm64-gpl.tar.xz` } },
};
const target = targets[triple];
if (!target) {
  console.error(`fetch-deps: no prebuilt tools for ${triple}. Supported: ${Object.keys(targets).join(', ')}`);
  process.exit(1);
}

const windows = triple.includes('windows');
const exe = (name) => (windows ? `${name}.exe` : name);
const stampFile = path.join(root, 'src-tauri', 'target', 'deps-target.txt');
const wanted = ['yt-dlp', 'ffmpeg', 'ffprobe', 'deno'].map((n) => path.join(out, exe(n)));
const ready = fs.existsSync(stampFile) && fs.readFileSync(stampFile, 'utf8') === triple && wanted.every((f) => fs.existsSync(f));
if (ready && !process.argv.includes('--force')) {
  console.log(`fetch-deps: tools for ${triple} are already in place.`);
  process.exit(0);
}

fs.rmSync(out, { recursive: true, force: true });
fs.rmSync(work, { recursive: true, force: true });
fs.mkdirSync(out, { recursive: true });
fs.mkdirSync(work, { recursive: true });

async function download(url, file) {
  console.log(`fetch-deps: downloading ${url}`);
  const res = await fetch(url, { headers: { 'User-Agent': 'ClipDownloader-build' } });
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
  fs.writeFileSync(file, Buffer.from(await res.arrayBuffer()));
  return file;
}

async function text(url) {
  const res = await fetch(url, { headers: { 'User-Agent': 'ClipDownloader-build' } });
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
  return res.text();
}

function verify(file, expected) {
  const actual = createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  if (actual !== expected.toLowerCase()) throw new Error(`Checksum mismatch for ${path.basename(file)}`);
}

function extract(archive, dest) {
  fs.mkdirSync(dest, { recursive: true });
  if (archive.endsWith('.zip') && process.platform === 'linux') execFileSync('unzip', ['-q', '-o', archive, '-d', dest]);
  // On Windows, use the built-in bsdtar (handles zip and drive letters), not a GNU tar that may come first on PATH.
  else if (process.platform === 'win32') execFileSync(path.join(process.env.SystemRoot, 'System32', 'tar.exe'), ['-xf', archive, '-C', dest]);
  else execFileSync('tar', ['-xf', archive, '-C', dest]);
}

function find(dir, name) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      const hit = find(full, name);
      if (hit) return hit;
    } else if (entry.name === name) return full;
  }
  return null;
}

function place(from, name) {
  const dest = path.join(out, exe(name));
  fs.copyFileSync(from, dest);
  fs.chmodSync(dest, 0o755);
}

// yt-dlp: latest release, checked against its published SHA-256 sums.
const latest = await fetch('https://github.com/yt-dlp/yt-dlp/releases/latest', { redirect: 'manual' });
const tag = (latest.headers.get('location') || '').split('/tag/')[1];
if (!tag) throw new Error('Could not find the latest yt-dlp release');
const ytBase = `https://github.com/yt-dlp/yt-dlp/releases/download/${tag}`;
const ytSum = (await text(`${ytBase}/SHA2-256SUMS`)).split('\n').map((l) => l.trim().split(/\s+/)).find((p) => p[1] === target.ytdlp);
if (!ytSum) throw new Error(`No checksum for ${target.ytdlp}`);
const ytFile = await download(`${ytBase}/${target.ytdlp}`, path.join(work, target.ytdlp));
verify(ytFile, ytSum[0]);
place(ytFile, 'yt-dlp');
fs.writeFileSync(path.join(out, 'yt-dlp-version.txt'), tag);

// ffmpeg + ffprobe: static GPL builds.
if (target.ffmpeg.archive) {
  const archive = await download(target.ffmpeg.archive, path.join(work, path.basename(target.ffmpeg.archive)));
  extract(archive, path.join(work, 'ffmpeg'));
  const ffmpegBin = path.dirname(find(path.join(work, 'ffmpeg'), exe('ffmpeg')));
  for (const tool of ['ffmpeg', 'ffprobe']) place(path.join(ffmpegBin, exe(tool)), tool);
  for (const dll of fs.readdirSync(ffmpegBin).filter((f) => f.endsWith('.dll'))) fs.copyFileSync(path.join(ffmpegBin, dll), path.join(out, dll));
  const license = find(path.join(work, 'ffmpeg'), 'LICENSE.txt');
  if (license) fs.copyFileSync(license, path.join(out, 'FFMPEG-LICENSE.txt'));
} else {
  for (const tool of ['ffmpeg', 'ffprobe']) {
    const archive = await download(`${target.ffmpeg.mac}/${tool}.zip`, path.join(work, `${tool}.zip`));
    extract(archive, path.join(work, tool));
    place(find(path.join(work, tool), tool), tool);
  }
}

// deno: JavaScript runtime yt-dlp needs to solve YouTube's player challenges.
const denoAsset = `deno-${triple}.zip`;
const denoBase = 'https://github.com/denoland/deno/releases/latest/download';
const denoSum = (await text(`${denoBase}/${denoAsset}.sha256sum`)).match(/[0-9a-fA-F]{64}/);
const denoZip = await download(`${denoBase}/${denoAsset}`, path.join(work, denoAsset));
if (denoSum) verify(denoZip, denoSum[0]);
extract(denoZip, path.join(work, 'deno'));
place(find(path.join(work, 'deno'), exe('deno')), 'deno');

// Unsigned helper binaries won't run on Apple Silicon; give them an ad-hoc signature.
if (triple.includes('apple') && process.platform === 'darwin') {
  for (const f of wanted) execFileSync('codesign', ['--force', '--sign', '-', f]);
}

fs.writeFileSync(stampFile, triple);
fs.rmSync(work, { recursive: true, force: true });
console.log(`fetch-deps: tools for ${triple} are ready in ${path.relative(root, out)}`);
