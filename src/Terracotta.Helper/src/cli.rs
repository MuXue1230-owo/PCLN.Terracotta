use std::path::{Path, PathBuf};

use clap::Parser;

use crate::{error::HelperError, protocol::PROTOCOL_VERSION};

#[derive(Debug, Parser)]
#[command(name = "terracotta-helper", version, disable_help_subcommand = true)]
pub struct Args {
    #[arg(long)]
    pub ipc: String,
    #[arg(long)]
    pub parent_pid: u32,
    #[arg(long)]
    pub data_dir: PathBuf,
    #[arg(long)]
    pub log_dir: PathBuf,
    #[arg(long)]
    pub protocol_version: u32,
}

#[derive(Debug)]
pub struct ValidatedArgs {
    pub ipc: String,
    pub parent_pid: u32,
    pub data_dir: PathBuf,
    pub log_dir: PathBuf,
}

impl Args {
    pub fn validate(self) -> Result<ValidatedArgs, HelperError> {
        if self.parent_pid == 0 {
            return Err(HelperError::InvalidArguments(
                "--parent-pid must be greater than zero".into(),
            ));
        }
        if self.protocol_version != PROTOCOL_VERSION {
            return Err(HelperError::InvalidArguments(format!(
                "unsupported --protocol-version {}",
                self.protocol_version
            )));
        }
        ensure_absolute(&self.data_dir, "--data-dir")?;
        ensure_absolute(&self.log_dir, "--log-dir")?;
        validate_ipc_endpoint(&self.ipc)?;
        Ok(ValidatedArgs {
            ipc: self.ipc,
            parent_pid: self.parent_pid,
            data_dir: self.data_dir,
            log_dir: self.log_dir,
        })
    }
}

fn ensure_absolute(path: &Path, name: &str) -> Result<(), HelperError> {
    if !path.is_absolute() {
        return Err(HelperError::InvalidArguments(format!(
            "{name} must be an absolute path"
        )));
    }
    Ok(())
}

#[cfg(windows)]
fn validate_ipc_endpoint(endpoint: &str) -> Result<(), HelperError> {
    let Some(nonce) = endpoint.strip_prefix(r"\\.\pipe\pcln-terracotta-") else {
        return Err(HelperError::InvalidArguments(
            "--ipc must use the Terracotta local named-pipe prefix".into(),
        ));
    };
    if is_hex_nonce(nonce, 32) {
        Ok(())
    } else {
        Err(HelperError::InvalidArguments(
            "--ipc named-pipe nonce is invalid".into(),
        ))
    }
}

#[cfg(unix)]
fn validate_ipc_endpoint(endpoint: &str) -> Result<(), HelperError> {
    let path = Path::new(endpoint);
    if !path.is_absolute() {
        return Err(HelperError::InvalidArguments(
            "--ipc must be an absolute Unix socket path".into(),
        ));
    }
    let Some(file_name) = path.file_name().and_then(|value| value.to_str()) else {
        return Err(HelperError::InvalidArguments(
            "--ipc has no valid socket file name".into(),
        ));
    };
    let Some(nonce) = file_name
        .strip_prefix("terracotta-")
        .and_then(|value| value.strip_suffix(".sock"))
    else {
        return Err(HelperError::InvalidArguments(
            "--ipc socket file name is invalid".into(),
        ));
    };
    if is_hex_nonce(nonce, 32) {
        Ok(())
    } else {
        Err(HelperError::InvalidArguments(
            "--ipc socket nonce is invalid".into(),
        ))
    }
}

fn is_hex_nonce(value: &str, length: usize) -> bool {
    value.len() == length
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

#[cfg(test)]
mod tests {
    use super::is_hex_nonce;

    #[test]
    fn nonce_is_strict_lowercase_hex() {
        assert!(is_hex_nonce("0123456789abcdef0123456789abcdef", 32));
        assert!(!is_hex_nonce("0123456789ABCDEF0123456789ABCDEF", 32));
        assert!(!is_hex_nonce("0123456789abcdef", 32));
    }
}
