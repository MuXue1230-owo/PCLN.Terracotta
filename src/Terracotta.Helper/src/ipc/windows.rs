use std::{mem::size_of, os::windows::io::AsRawHandle, ptr};

use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
use windows_sys::Win32::{
    Foundation::{CloseHandle, HANDLE, LocalFree},
    Security::{
        Authorization::{
            ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW,
            SDDL_REVISION_1,
        },
        GetTokenInformation, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER,
        TokenUser,
    },
    System::{
        Pipes::GetNamedPipeClientProcessId,
        Threading::{GetCurrentProcess, OpenProcessToken},
    },
};

use crate::cli::ValidatedArgs;

pub type LocalIpcStream = NamedPipeServer;

pub struct LocalIpcListener {
    server: Option<NamedPipeServer>,
    expected_client_pid: u32,
}

impl LocalIpcListener {
    pub fn bind(args: &ValidatedArgs) -> Result<Self, std::io::Error> {
        let mut options = ServerOptions::new();
        options
            .first_pipe_instance(true)
            .reject_remote_clients(true)
            .max_instances(1);
        let mut security = PipeSecurityAttributes::new()?;
        let server = unsafe {
            options.create_with_security_attributes_raw(&args.ipc, security.as_mut_ptr())?
        };
        Ok(Self {
            server: Some(server),
            expected_client_pid: args.parent_pid,
        })
    }

    pub async fn accept(&mut self) -> Result<LocalIpcStream, std::io::Error> {
        let server = self.server.take().ok_or_else(|| {
            std::io::Error::new(
                std::io::ErrorKind::NotConnected,
                "named-pipe listener only accepts one client",
            )
        })?;
        server.connect().await?;
        let mut client_pid = 0_u32;
        let result =
            unsafe { GetNamedPipeClientProcessId(server.as_raw_handle().cast(), &mut client_pid) };
        if result == 0 {
            return Err(std::io::Error::last_os_error());
        }
        if client_pid != self.expected_client_pid {
            return Err(std::io::Error::new(
                std::io::ErrorKind::PermissionDenied,
                "named-pipe client process does not match PCL N parent",
            ));
        }
        Ok(server)
    }
}

struct PipeSecurityAttributes {
    descriptor: PSECURITY_DESCRIPTOR,
    attributes: SECURITY_ATTRIBUTES,
}

impl PipeSecurityAttributes {
    fn new() -> Result<Self, std::io::Error> {
        let user_sid = current_user_sid_string()?;
        let sddl = format!("D:P(A;;GA;;;SY)(A;;GA;;;{user_sid})");
        let wide: Vec<u16> = sddl.encode_utf16().chain(std::iter::once(0)).collect();
        let mut descriptor = ptr::null_mut();
        let result = unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                wide.as_ptr(),
                SDDL_REVISION_1,
                &mut descriptor,
                ptr::null_mut(),
            )
        };
        if result == 0 {
            return Err(std::io::Error::last_os_error());
        }
        Ok(Self {
            descriptor,
            attributes: SECURITY_ATTRIBUTES {
                nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
                lpSecurityDescriptor: descriptor,
                bInheritHandle: 0,
            },
        })
    }

    fn as_mut_ptr(&mut self) -> *mut std::ffi::c_void {
        (&mut self.attributes as *mut SECURITY_ATTRIBUTES).cast()
    }
}

impl Drop for PipeSecurityAttributes {
    fn drop(&mut self) {
        if !self.descriptor.is_null() {
            unsafe {
                LocalFree(self.descriptor);
            }
        }
    }
}

fn current_user_sid_string() -> Result<String, std::io::Error> {
    let mut token: HANDLE = ptr::null_mut();
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
        return Err(std::io::Error::last_os_error());
    }
    let token = TokenHandle(token);

    let mut length = 0_u32;
    unsafe {
        GetTokenInformation(token.0, TokenUser, ptr::null_mut(), 0, &mut length);
    }
    if length < size_of::<TOKEN_USER>() as u32 {
        return Err(std::io::Error::last_os_error());
    }
    let mut buffer = vec![0_u8; length as usize];
    if unsafe {
        GetTokenInformation(
            token.0,
            TokenUser,
            buffer.as_mut_ptr().cast(),
            length,
            &mut length,
        )
    } == 0
    {
        return Err(std::io::Error::last_os_error());
    }
    let token_user = unsafe { &*(buffer.as_ptr().cast::<TOKEN_USER>()) };
    let mut sid_text = ptr::null_mut();
    if unsafe { ConvertSidToStringSidW(token_user.User.Sid, &mut sid_text) } == 0 {
        return Err(std::io::Error::last_os_error());
    }
    let sid = unsafe {
        let mut length = 0_usize;
        while *sid_text.add(length) != 0 {
            length += 1;
        }
        String::from_utf16_lossy(std::slice::from_raw_parts(sid_text, length))
    };
    unsafe {
        LocalFree(sid_text.cast());
    }
    Ok(sid)
}

struct TokenHandle(HANDLE);

impl Drop for TokenHandle {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
}
