//! Runs console tools without a window and lets a whole download's process tree be killed at once:
//! a job object on Windows, a process group on macOS and Linux.
use std::io::{self, BufRead, BufReader, Read};
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;

pub type LineFn<'a> = &'a (dyn Fn(&str) + Sync);

pub struct Job {
    inner: Mutex<Inner>,
}

struct Inner {
    killed: bool,
    #[cfg(windows)]
    handle: isize,
    #[cfg(unix)]
    groups: Vec<i32>,
}

impl Job {
    pub fn new() -> Job {
        Job {
            inner: Mutex::new(Inner {
                killed: false,
                #[cfg(windows)]
                handle: win::create(),
                #[cfg(unix)]
                groups: Vec::new(),
            }),
        }
    }

    #[cfg_attr(windows, allow(unused_mut))]
    fn add(&self, child: &Child) {
        let mut g = self.inner.lock().unwrap();
        #[cfg(windows)]
        {
            use std::os::windows::io::AsRawHandle;
            win::assign(g.handle, child.as_raw_handle());
            if g.killed {
                win::terminate(g.handle);
            }
        }
        #[cfg(unix)]
        {
            let pgid = child.id() as i32;
            g.groups.push(pgid);
            if g.killed {
                unsafe { libc::kill(-pgid, libc::SIGKILL) };
            }
        }
    }

    #[allow(unused_variables)]
    fn remove(&self, child: &Child) {
        #[cfg(unix)]
        {
            let pgid = child.id() as i32;
            self.inner.lock().unwrap().groups.retain(|&p| p != pgid);
        }
    }

    pub fn kill(&self) {
        let mut g = self.inner.lock().unwrap();
        g.killed = true;
        #[cfg(windows)]
        win::terminate(g.handle);
        #[cfg(unix)]
        for &pgid in &g.groups {
            unsafe { libc::kill(-pgid, libc::SIGKILL) };
        }
    }
}

#[cfg(windows)]
impl Drop for Job {
    fn drop(&mut self) {
        win::close(self.inner.lock().unwrap().handle);
    }
}

#[cfg(windows)]
mod win {
    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE};
    use windows_sys::Win32::System::JobObjects::*;

    pub fn create() -> isize {
        unsafe {
            let h = CreateJobObjectW(std::ptr::null(), std::ptr::null());
            if h.is_null() {
                return 0;
            }
            let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
            // Every process in the job dies when the app exits, even if it crashes.
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            SetInformationJobObject(
                h,
                JobObjectExtendedLimitInformation,
                &info as *const _ as *const core::ffi::c_void,
                std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            );
            h as isize
        }
    }

    pub fn assign(job: isize, process: HANDLE) {
        if job != 0 {
            unsafe { AssignProcessToJobObject(job as HANDLE, process) };
        }
    }

    pub fn terminate(job: isize) {
        if job != 0 {
            unsafe { TerminateJobObject(job as HANDLE, 1) };
        }
    }

    pub fn close(job: isize) {
        if job != 0 {
            unsafe { CloseHandle(job as HANDLE) };
        }
    }
}

/// Runs a tool, streaming its output lines to the callbacks. Returns the exit code.
pub fn run(exe: &Path, args: &[String], job: Option<&Job>, on_out: LineFn, on_err: LineFn) -> io::Result<i32> {
    let mut cmd = Command::new(exe);
    cmd.args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .env("PYTHONIOENCODING", "utf-8")
        .env("PYTHONUTF8", "1");
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        cmd.process_group(0);
    }
    let mut child = cmd.spawn()?;
    if let Some(j) = job {
        j.add(&child);
    }
    let out = child.stdout.take().expect("stdout is piped");
    let err = child.stderr.take().expect("stderr is piped");
    std::thread::scope(|s| {
        s.spawn(move || read_lines(err, on_err));
        read_lines(out, on_out);
    });
    let status = child.wait();
    if let Some(j) = job {
        j.remove(&child);
    }
    Ok(status?.code().unwrap_or(-1))
}

fn read_lines(r: impl Read, f: LineFn) {
    let mut reader = BufReader::new(r);
    let mut buf = Vec::new();
    loop {
        buf.clear();
        match reader.read_until(b'\n', &mut buf) {
            Ok(0) | Err(_) => break,
            Ok(_) => f(String::from_utf8_lossy(&buf).trim_end_matches(['\r', '\n'])),
        }
    }
}

/// yt-dlp arguments every run shares.
pub fn ytdlp_args(skip_hls: bool) -> Vec<String> {
    let p = crate::paths::get();
    let mut a: Vec<String> = vec!["--ignore-config".into(), "--ffmpeg-location".into(), p.bin.display().to_string()];
    if p.deno().exists() {
        a.push("--js-runtimes".into());
        a.push(format!("deno:{}", p.deno().display()));
    }
    a.extend(["--js-runtimes".into(), "node".into()]);
    // HLS manifests are only needed for live streams; skipping them saves about a second.
    if skip_hls {
        a.extend(["--extractor-args".into(), "youtube:skip=hls".into()]);
    }
    a
}

/// Quotes an argument for the log, so commands can be copied into a terminal.
pub fn quote(arg: &str) -> String {
    if !arg.is_empty() && !arg.contains([' ', '\t', '"', '\'']) {
        arg.to_string()
    } else {
        format!("\"{}\"", arg.replace('"', "\\\""))
    }
}
