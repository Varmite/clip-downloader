// Clip Downloader UI. Talks to the Rust side through Tauri's global API (app.withGlobalTauri).
const { invoke } = window.__TAURI__.core;
const { listen } = window.__TAURI__.event;
const dialog = window.__TAURI__.dialog;
const clipboard = window.__TAURI__.clipboardManager;
const appWindow = window.__TAURI__.window.getCurrentWindow();

const $ = (id) => document.getElementById(id);
const ui = {
  url: $('url'), paste: $('paste'), badge: $('siteBadge'), sites: document.querySelectorAll('.site'),
  preview: $('preview'), thumb: $('thumb'), duration: $('duration'), title: $('title'), meta: $('meta'),
  format: $('format'), formatHint: $('formatHint'), quality: $('quality'), folder: $('folder'), browse: $('browse'),
  bar: $('bar'), fill: $('fill'), status: $('status'), logLink: $('logLink'),
  engine: $('engine'), open: $('open'), download: $('download'),
};

const FORMAT_HINTS = [
  'H.264 video that plays everywhere.',
  'H.264 video, keeps original audio.',
  'Audio only, best quality MP3.',
  'Audio only, original quality, fastest.',
];
const EMPTY_TITLE = 'Paste a link to get started';
const EMPTY_META = 'Video details show up here automatically.';

const state = { busy: false, format: 0, folder: '', currentUrl: null, collection: false, lastClipboard: null, lastFile: null };

// ---- Theme: CSS follows the OS via prefers-color-scheme; Tauri's theme events keep it right where the webview lags.
function applyTheme(theme) {
  if (theme === 'dark' || theme === 'light') document.documentElement.dataset.theme = theme;
}
appWindow.theme().then(applyTheme).catch(() => {});
appWindow.onThemeChanged(({ payload }) => applyTheme(payload)).catch(() => {});

// ---- Helpers
function formatTime(seconds) {
  const t = Math.round(seconds);
  const h = Math.floor(t / 3600), m = Math.floor(t / 60) % 60, s = t % 60;
  const pad = (n) => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}

function basename(p) {
  return p.split(/[\\/]/).pop();
}

function showInfo(title, meta, { error = false, loading = false } = {}) {
  ui.title.textContent = title;
  ui.meta.textContent = meta;
  ui.meta.classList.toggle('error', error);
  ui.preview.classList.toggle('loading', loading);
}

function setThumb(src) {
  ui.thumb.hidden = !src;
  if (src) ui.thumb.src = src;
  else ui.thumb.removeAttribute('src');
}
ui.thumb.addEventListener('error', () => setThumb(null));

function setDuration(text, live = false) {
  ui.duration.hidden = !text;
  ui.duration.textContent = text || '';
  ui.duration.classList.toggle('live', live);
}

function setSite(site) {
  ui.badge.hidden = !site;
  ui.badge.textContent = site || '';
  if (site) ui.badge.dataset.site = site;
  ui.sites.forEach((el) => el.classList.toggle('on', el.dataset.site === site));
}

function setStatus(text, kind = '') {
  ui.status.textContent = text;
  ui.status.className = `status ${kind}`;
}

function setProgress(permille, kind = '') {
  ui.bar.className = `bar ${kind}`;
  if (permille < 0) ui.bar.classList.add('indeterminate');
  else ui.fill.style.width = `${Math.min(1000, Math.max(0, permille)) / 10}%`;
}

function setFormat(v) {
  state.format = v;
  ui.format.querySelectorAll('button').forEach((b) => b.setAttribute('aria-checked', String(Number(b.dataset.v) === v)));
  ui.formatHint.textContent = FORMAT_HINTS[v];
  ui.quality.disabled = state.busy || v >= 2;
}

function setFolder(path) {
  state.folder = path;
  // The label is right-to-left so long paths keep their end visible; the marks stop it reordering the slashes.
  ui.folder.textContent = `\u200E${path}\u200E`;
  ui.folder.title = path;
}

function saveSettings() {
  invoke('save_settings', { folder: state.folder, format: state.format, quality: Number(ui.quality.value) }).catch(() => {});
}

function setBusy(busy) {
  state.busy = busy;
  ui.url.readOnly = busy;
  ui.paste.disabled = busy;
  ui.browse.disabled = busy;
  ui.format.querySelectorAll('button').forEach((b) => (b.disabled = busy));
  ui.quality.disabled = busy || state.format >= 2;
  ui.download.textContent = busy ? 'Cancel' : 'Download';
  ui.download.classList.toggle('cancel', busy);
  ui.download.disabled = false;
}

// ---- Link handling
let debounce = null;
let lastLength = 0;

async function onUrlChanged() {
  const info = await invoke('inspect', { text: ui.url.value });
  if (info.same) return;
  state.currentUrl = info.url;
  state.collection = info.collection;
  setSite(info.site);
  setThumb(info.videoId ? `https://i.ytimg.com/vi/${info.videoId}/mqdefault.jpg` : null);
  setDuration(null);
  if (!info.url) return showInfo(EMPTY_TITLE, EMPTY_META);
  if (info.collection) {
    const what = info.site === 'TikTok' ? 'TikTok profile' : 'Playlist or channel';
    return showInfo(`${what} link`, 'Every video in it will be downloaded into a subfolder.');
  }
  const unsupported = info.site ? '' : "This site isn't officially supported, but it may still work.";
  showInfo(`Loading ${info.site || 'video'} info…`, unsupported, { loading: true });
}

ui.url.addEventListener('input', () => {
  const len = ui.url.value.length;
  const pasted = Math.abs(len - lastLength) > 5;
  lastLength = len;
  clearTimeout(debounce);
  if (pasted) onUrlChanged();
  else debounce = setTimeout(onUrlChanged, 400);
});

ui.url.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') ui.download.click();
});

listen('prefetch', ({ payload: p }) => {
  if (p.url !== state.currentUrl) return;
  if (p.title) {
    const meta = [];
    if (p.channel) meta.push(p.channel);
    showInfo(p.title, meta.join('  ·  ') || ' ');
    if (p.isLive) setDuration('LIVE', true);
    else if (p.duration > 0) setDuration(formatTime(p.duration));
    if (ui.thumb.hidden && p.thumbnail) setThumb(p.thumbnail);
  } else {
    showInfo("Couldn't load video info", p.friendlyError || 'Something went wrong.', { error: true });
  }
});

async function setUrl(text) {
  ui.url.value = text.trim();
  lastLength = ui.url.value.length;
  clearTimeout(debounce);
  await onUrlChanged();
  ui.download.focus();
}

ui.paste.addEventListener('click', async () => {
  try {
    const text = await clipboard.readText();
    if (text) await setUrl(text);
  } catch {
    ui.url.focus();
  }
});

// Picks up a supported link from the clipboard when the window opens or gets focus.
async function checkClipboard() {
  if (state.busy) return;
  let text;
  try {
    text = ((await clipboard.readText()) || '').trim();
  } catch {
    return;
  }
  if (!text || text === state.lastClipboard || text.length > 500 || text === ui.url.value.trim()) return;
  state.lastClipboard = text;
  if (await invoke('detect', { text })) await setUrl(text);
}
window.addEventListener('focus', checkClipboard);

// Drag a link from the browser onto the window.
window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', (e) => {
  e.preventDefault();
  const text = e.dataTransfer.getData('text/uri-list') || e.dataTransfer.getData('text/plain');
  if (text && !state.busy) setUrl(text.split('\n')[0]);
});

// ---- Options
ui.format.addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b || state.busy) return;
  setFormat(Number(b.dataset.v));
  saveSettings();
});
ui.format.addEventListener('keydown', (e) => {
  if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
  const next = (state.format + (e.key === 'ArrowRight' ? 1 : 3)) % 4;
  setFormat(next);
  ui.format.querySelector(`[data-v="${next}"]`).focus();
  saveSettings();
});
ui.quality.addEventListener('change', saveSettings);

ui.browse.addEventListener('click', async () => {
  const picked = await dialog.open({ directory: true, defaultPath: state.folder, title: 'Choose where to save downloads' });
  if (typeof picked === 'string') {
    setFolder(picked);
    saveSettings();
  }
});

ui.open.addEventListener('click', async () => {
  try {
    if (state.lastFile) await invoke('reveal', { path: state.lastFile });
    else await invoke('open_folder', { path: state.folder });
  } catch (err) {
    setStatus(`Can't open the folder: ${err}`, 'error');
  }
});

ui.logLink.addEventListener('click', () => invoke('open_log').catch(() => {}));

// ---- Download
ui.download.addEventListener('click', () => (state.busy ? cancelDownload() : startDownload()));

async function startDownload() {
  clearTimeout(debounce);
  await onUrlChanged();
  const url = state.currentUrl;
  if (!url) {
    setStatus('Paste a YouTube, TikTok or Instagram link first.', 'error');
    ui.url.focus();
    return;
  }
  if (state.collection) {
    const ok = await dialog.ask('This is a playlist, channel or profile link. Download every video in it?', {
      title: 'Clip Downloader', kind: 'info',
    });
    if (!ok) return;
  }
  state.lastFile = null;
  ui.logLink.hidden = true;
  try {
    await invoke('start_download', {
      options: { url, folder: state.folder, format: state.format, quality: Number(ui.quality.value) },
    });
  } catch (err) {
    setStatus(String(err), 'error');
    return;
  }
  setBusy(true);
  setStatus('Starting…');
  setProgress(-1);
}

function cancelDownload() {
  invoke('cancel_download');
  ui.download.disabled = true;
  setStatus('Canceling…');
}

listen('progress', ({ payload }) => {
  if (!state.busy) return;
  setStatus(payload.text);
  setProgress(payload.permille);
});

listen('done', async ({ payload: d }) => {
  setBusy(false);
  if (d.ok) {
    state.lastFile = d.files[d.files.length - 1];
    const what = d.files.length === 1 ? basename(state.lastFile) : `${d.files.length} files`;
    const note = d.partial ? ' (some videos failed)' : '';
    setStatus(`✓ Done in ${d.seconds.toFixed(1)} s  —  ${what}${note}`, 'success');
    setProgress(1000, 'success');
    if (!document.hasFocus()) appWindow.requestUserAttention(2).catch(() => {}); // 2 = informational
  } else if (d.canceled) {
    setProgress(0);
    setStatus('Canceled.');
  } else {
    setProgress(0);
    setStatus(d.message, 'error');
    ui.logLink.hidden = !d.hasLog;
  }
});

listen('engine', ({ payload }) => (ui.engine.textContent = `yt-dlp ${payload}`));

listen('confirm-close', async () => {
  const quit = await dialog.ask('A download is still running. Stop it and quit?', { title: 'Clip Downloader', kind: 'warning' });
  if (quit) invoke('quit_app');
});

// ---- Start
(async () => {
  const s = await invoke('init_state');
  setFolder(s.folder);
  ui.quality.value = String(s.quality);
  setFormat(s.format);
  ui.engine.textContent = s.engineVersion ? `yt-dlp ${s.engineVersion}` : '';
  showInfo(EMPTY_TITLE, EMPTY_META);
  // The window starts hidden so the webview's blank white frame is never seen; show it now that the page is drawn.
  appWindow.show();
  ui.url.focus();
  checkClipboard();
})();
