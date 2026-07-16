use thiserror::Error;

#[derive(Debug, Error)]
pub enum HelperError {
    #[error("invalid arguments: {0}")]
    InvalidArguments(String),
    #[error("invalid bootstrap secret")]
    InvalidBootstrapSecret,
    #[error("local IPC failed: {0}")]
    Ipc(#[from] std::io::Error),
    #[error("IPC protocol failed: {0}")]
    Protocol(#[from] crate::protocol::framing::FrameError),
    #[error("parent process monitoring failed: {0}")]
    Parent(String),
}

impl HelperError {
    pub fn exit_code(&self) -> u8 {
        match self {
            Self::InvalidArguments(_) => 2,
            Self::InvalidBootstrapSecret => 3,
            Self::Ipc(_) => 4,
            Self::Protocol(_) => 5,
            Self::Parent(_) => 6,
        }
    }
}
