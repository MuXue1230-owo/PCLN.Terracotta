use std::{
    fs,
    os::unix::fs::{FileTypeExt, MetadataExt, PermissionsExt},
    path::PathBuf,
};

use tokio::net::{UnixListener, UnixStream};

use crate::cli::ValidatedArgs;

pub type LocalIpcStream = UnixStream;

pub struct LocalIpcListener {
    listener: UnixListener,
    path: PathBuf,
    device: u64,
    inode: u64,
}

impl LocalIpcListener {
    pub fn bind(args: &ValidatedArgs) -> Result<Self, std::io::Error> {
        let path = PathBuf::from(&args.ipc);
        if path.exists() {
            return Err(std::io::Error::new(
                std::io::ErrorKind::AlreadyExists,
                "IPC socket path already exists",
            ));
        }
        let listener = UnixListener::bind(&path)?;
        fs::set_permissions(&path, fs::Permissions::from_mode(0o600))?;
        let metadata = fs::symlink_metadata(&path)?;
        Ok(Self {
            listener,
            path,
            device: metadata.dev(),
            inode: metadata.ino(),
        })
    }

    pub async fn accept(&mut self) -> Result<LocalIpcStream, std::io::Error> {
        let (stream, _) = self.listener.accept().await?;
        let credentials = stream.peer_cred()?;
        if credentials.uid() != unsafe { libc::geteuid() } {
            return Err(std::io::Error::new(
                std::io::ErrorKind::PermissionDenied,
                "IPC peer user does not match Helper user",
            ));
        }
        Ok(stream)
    }
}

impl Drop for LocalIpcListener {
    fn drop(&mut self) {
        let Ok(metadata) = fs::symlink_metadata(&self.path) else {
            return;
        };
        if metadata.file_type().is_socket()
            && metadata.dev() == self.device
            && metadata.ino() == self.inode
        {
            let _ = fs::remove_file(&self.path);
        }
    }
}
