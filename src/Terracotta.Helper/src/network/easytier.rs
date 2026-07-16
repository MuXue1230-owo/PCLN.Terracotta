use std::{env, net::SocketAddr, path::PathBuf, process::Stdio, time::Duration};

use tokio::{process::Command, time::sleep};

use crate::room::RoomError;

use super::credentials::RoomCredentials;

/// Default public shared nodes used when the environment does not override peers.
const DEFAULT_SHARED_PEERS: &[&str] = &[
    "tcp://public.easytier.top:11010",
    "tcp://easytier.public.kkrainbow.top:11010",
];

#[derive(Debug, Clone)]
pub struct EasyTierLaunchConfig {
    pub binary: PathBuf,
    pub prefer_direct: bool,
    pub allow_relay: bool,
    pub host_ipv4: Option<&'static str>,
    /// EasyTier `--port-forward` entries (`tcp://local/remote`).
    pub port_forwards: Vec<String>,
    /// Loopback RPC portal for `easytier-cli` diagnostics.
    pub rpc_portal: SocketAddr,
}

pub struct EasyTierNode {
    child: tokio::process::Child,
    rpc_portal: SocketAddr,
    binary: PathBuf,
}

impl EasyTierNode {
    pub fn rpc_portal(&self) -> SocketAddr {
        self.rpc_portal
    }

    pub fn binary(&self) -> &std::path::Path {
        &self.binary
    }

    pub async fn stop(mut self) -> Result<(), RoomError> {
        let _ = self.child.start_kill();
        match tokio::time::timeout(Duration::from_secs(3), self.child.wait()).await {
            Ok(Ok(_)) => Ok(()),
            Ok(Err(error)) => Err(RoomError::new(
                "network.easytier-stop-failed",
                format!("Failed to stop EasyTier: {error}"),
                false,
            )),
            Err(_) => {
                let _ = self.child.start_kill();
                let _ = self.child.wait().await;
                Ok(())
            }
        }
    }
}

pub fn resolve_easytier_binary() -> Option<PathBuf> {
    if let Ok(explicit) = env::var("TERRACOTTA_EASYTIER_PATH") {
        let path = PathBuf::from(explicit);
        if path.is_file() {
            return Some(path);
        }
    }

    let current = env::current_exe().ok()?;
    let directory = current.parent()?;
    let candidate = directory.join(easytier_file_name());
    if candidate.is_file() {
        return Some(candidate);
    }
    None
}

/// Deterministic loopback RPC portal from room material to keep cli queries stable.
pub fn rpc_portal_for_room(room_code: &str) -> SocketAddr {
    use sha2::{Digest, Sha256};
    let digest = Sha256::digest(room_code.as_bytes());
    let port = 19_000 + u16::from_be_bytes([digest[0], digest[1]]) % 1_000;
    SocketAddr::from(([127, 0, 0, 1], port))
}

pub async fn start_easytier(
    credentials: &RoomCredentials,
    config: EasyTierLaunchConfig,
) -> Result<EasyTierNode, RoomError> {
    if !config.binary.is_file() {
        return Err(easytier_missing());
    }

    let mut command = Command::new(&config.binary);
    command
        .kill_on_drop(true)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .env("ET_NETWORK_NAME", &credentials.network_name)
        .env("ET_NETWORK_SECRET", credentials.network_secret.as_str())
        .arg("--use-smoltcp")
        .arg("--rpc-portal")
        .arg(config.rpc_portal.to_string())
        .arg("--rpc-portal-whitelist")
        .arg("127.0.0.1/32,::1/128");

    if !allow_tun() {
        command.arg("--no-tun");
    }

    if let Some(ipv4) = config.host_ipv4 {
        command.arg("--ipv4").arg(ipv4);
    }

    if config.prefer_direct {
        command.arg("--latency-first");
    }
    let _ = config.allow_relay;

    for peer in shared_peers() {
        command.arg("-p").arg(peer);
    }

    for forward in &config.port_forwards {
        command.arg("--port-forward").arg(forward);
    }

    let mut child = command.spawn().map_err(|error| {
        RoomError::new(
            "network.easytier-start-failed",
            format!("Failed to start EasyTier: {error}"),
            true,
        )
    })?;

    sleep(Duration::from_millis(250)).await;
    if let Ok(Some(status)) = child.try_wait() {
        return Err(RoomError::new(
            "network.easytier-start-failed",
            format!("EasyTier exited immediately with status {status}."),
            true,
        ));
    }

    Ok(EasyTierNode {
        child,
        rpc_portal: config.rpc_portal,
        binary: config.binary,
    })
}

pub fn easytier_missing() -> RoomError {
    RoomError::new(
        "network.easytier-missing",
        "The EasyTier runtime was not found next to terracotta-helper. Place easytier-core in the same native directory or set TERRACOTTA_EASYTIER_PATH.",
        false,
    )
}

pub fn allow_tun() -> bool {
    matches!(
        env::var("TERRACOTTA_EASYTIER_ALLOW_TUN").as_deref(),
        Ok("1" | "true" | "TRUE" | "yes" | "YES")
    )
}

fn easytier_file_name() -> &'static str {
    if cfg!(windows) {
        "easytier-core.exe"
    } else {
        "easytier-core"
    }
}

fn shared_peers() -> Vec<String> {
    if let Ok(value) = env::var("TERRACOTTA_EASYTIER_PEERS") {
        let peers = value
            .split([',', ';'])
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_owned)
            .collect::<Vec<_>>();
        if !peers.is_empty() {
            return peers;
        }
    }
    DEFAULT_SHARED_PEERS
        .iter()
        .map(|value| (*value).to_owned())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::{
        allow_tun, easytier_file_name, easytier_missing, resolve_easytier_binary,
        rpc_portal_for_room,
    };

    #[test]
    fn missing_error_uses_stable_code() {
        assert_eq!(easytier_missing().code, "network.easytier-missing");
        assert!(!easytier_missing().retryable);
    }

    #[test]
    fn platform_binary_name_matches_os() {
        if cfg!(windows) {
            assert_eq!(easytier_file_name(), "easytier-core.exe");
        } else {
            assert_eq!(easytier_file_name(), "easytier-core");
        }
    }

    #[test]
    fn resolve_returns_none_without_sidecar() {
        if std::env::var_os("TERRACOTTA_EASYTIER_PATH").is_none() {
            let _ = resolve_easytier_binary();
        }
    }

    #[test]
    fn tun_defaults_off() {
        let _ = allow_tun();
    }

    #[test]
    fn rpc_portal_is_stable_and_loopback() {
        let left = rpc_portal_for_room("AB12-CD34-EF56");
        let right = rpc_portal_for_room("AB12-CD34-EF56");
        assert_eq!(left, right);
        assert!(left.ip().is_loopback());
        assert!(left.port() >= 19_000);
    }
}
