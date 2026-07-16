use crate::error::HelperError;

pub struct ParentGuard {
    #[cfg(unix)]
    pid: libc::pid_t,
    #[cfg(windows)]
    handle: windows_sys::Win32::Foundation::HANDLE,
}

impl ParentGuard {
    pub fn attach(pid: u32) -> Result<Self, HelperError> {
        #[cfg(unix)]
        {
            let pid = libc::pid_t::try_from(pid)
                .map_err(|_| HelperError::Parent("parent PID is out of range".into()))?;
            if !is_unix_process_alive(pid)? {
                return Err(HelperError::Parent("parent process is not running".into()));
            }
            Ok(Self { pid })
        }
        #[cfg(windows)]
        {
            use windows_sys::Win32::System::Threading::{
                OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
            };
            let handle = unsafe {
                OpenProcess(
                    PROCESS_SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION,
                    0,
                    pid,
                )
            };
            if handle.is_null() {
                return Err(HelperError::Parent(
                    std::io::Error::last_os_error().to_string(),
                ));
            }
            Ok(Self { handle })
        }
    }

    pub async fn wait_for_exit(self) -> Result<(), HelperError> {
        #[cfg(unix)]
        {
            let pid = self.pid;
            loop {
                tokio::time::sleep(std::time::Duration::from_millis(500)).await;
                if !is_unix_process_alive(pid)? {
                    return Ok(());
                }
            }
        }
        #[cfg(windows)]
        {
            use windows_sys::Win32::{
                Foundation::{WAIT_FAILED, WAIT_OBJECT_0, WAIT_TIMEOUT},
                System::Threading::WaitForSingleObject,
            };
            loop {
                match unsafe { WaitForSingleObject(self.handle, 0) } {
                    WAIT_OBJECT_0 => return Ok(()),
                    WAIT_TIMEOUT => {
                        tokio::time::sleep(std::time::Duration::from_millis(500)).await;
                    }
                    WAIT_FAILED => {
                        return Err(HelperError::Parent(
                            std::io::Error::last_os_error().to_string(),
                        ));
                    }
                    result => {
                        return Err(HelperError::Parent(format!(
                            "WaitForSingleObject returned {result}"
                        )));
                    }
                }
            }
        }
    }
}

#[cfg(unix)]
fn is_unix_process_alive(pid: libc::pid_t) -> Result<bool, HelperError> {
    let result = unsafe { libc::kill(pid, 0) };
    if result == 0 {
        return Ok(true);
    }
    let error = std::io::Error::last_os_error();
    match error.raw_os_error() {
        Some(libc::EPERM) => Ok(true),
        Some(libc::ESRCH) => Ok(false),
        _ => Err(HelperError::Parent(error.to_string())),
    }
}

#[cfg(windows)]
impl Drop for ParentGuard {
    fn drop(&mut self) {
        if !self.handle.is_null() {
            unsafe {
                windows_sys::Win32::Foundation::CloseHandle(self.handle);
            }
        }
    }
}
