pub mod session;
#[cfg(unix)]
mod unix;
#[cfg(windows)]
mod windows;

#[cfg(unix)]
pub use unix::{LocalIpcListener, LocalIpcStream};
#[cfg(windows)]
pub use windows::{LocalIpcListener, LocalIpcStream};
