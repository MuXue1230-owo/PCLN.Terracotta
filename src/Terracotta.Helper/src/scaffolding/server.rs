use std::{
    collections::{HashMap, HashSet},
    io,
    net::SocketAddr,
    sync::Arc,
    time::{Duration, Instant},
};

use tokio::{
    net::{TcpListener, TcpStream},
    sync::{RwLock, watch},
    task::JoinSet,
};

use super::{PlayerKind, PlayerProfile, ScaffoldingError, read_request, write_response};

pub const SUPPORTED_PROTOCOLS: &[&str] = &[
    "c:ping",
    "c:protocols",
    "c:server_port",
    "c:player_ping",
    "c:player_profiles_list",
];
const INVALID_REQUEST_STATUS: u8 = 32;
const UNKNOWN_REQUEST_STATUS: u8 = 255;
const PLAYER_TIMEOUT: Duration = Duration::from_secs(10);
const CLEANUP_INTERVAL: Duration = Duration::from_secs(5);

#[derive(Debug, Clone)]
struct TrackedPlayer {
    profile: PlayerProfile,
    last_seen: Instant,
}

pub struct ServerContext {
    minecraft_port: u16,
    host_machine_id: String,
    players: RwLock<HashMap<String, TrackedPlayer>>,
}

impl ServerContext {
    pub fn new(mut host: PlayerProfile, minecraft_port: u16) -> Result<Self, ScaffoldingError> {
        if minecraft_port == 0 {
            return Err(ScaffoldingError::InvalidFrame(
                "Minecraft server port must be non-zero".into(),
            ));
        }
        if !host.validate() {
            return Err(ScaffoldingError::InvalidFrame(
                "host profile contains invalid text".into(),
            ));
        }
        host.kind = Some(PlayerKind::Host);
        let host_machine_id = host.machine_id.clone();
        let players = HashMap::from([(
            host_machine_id.clone(),
            TrackedPlayer {
                profile: host,
                last_seen: Instant::now(),
            },
        )]);
        Ok(Self {
            minecraft_port,
            host_machine_id,
            players: RwLock::new(players),
        })
    }

    pub async fn player_profiles(&self) -> Vec<PlayerProfile> {
        let mut profiles: Vec<_> = self
            .players
            .read()
            .await
            .values()
            .map(|tracked| tracked.profile.clone())
            .collect();
        profiles.sort_by(|left, right| {
            player_order(left)
                .cmp(&player_order(right))
                .then_with(|| left.machine_id.cmp(&right.machine_id))
        });
        profiles
    }

    async fn record_guest(&self, mut profile: PlayerProfile) -> Result<(), ScaffoldingError> {
        if !profile.validate() || profile.machine_id == self.host_machine_id {
            return Err(ScaffoldingError::InvalidFrame(
                "guest profile is invalid or collides with the host identity".into(),
            ));
        }
        profile.kind = Some(PlayerKind::Guest);
        self.players.write().await.insert(
            profile.machine_id.clone(),
            TrackedPlayer {
                profile,
                last_seen: Instant::now(),
            },
        );
        Ok(())
    }

    async fn remove_stale_guests(&self) {
        let now = Instant::now();
        let host_machine_id = &self.host_machine_id;
        self.players.write().await.retain(|machine_id, tracked| {
            machine_id == host_machine_id || now.duration_since(tracked.last_seen) <= PLAYER_TIMEOUT
        });
    }
}

fn player_order(profile: &PlayerProfile) -> u8 {
    match profile.kind {
        Some(PlayerKind::Host) => 0,
        _ => 1,
    }
}

pub struct ScaffoldingServer {
    listener: TcpListener,
    context: Arc<ServerContext>,
}

impl ScaffoldingServer {
    pub async fn bind(
        address: SocketAddr,
        context: Arc<ServerContext>,
    ) -> Result<Self, ScaffoldingError> {
        if !address.ip().is_loopback() {
            return Err(ScaffoldingError::InvalidFrame(
                "Scaffolding server must bind to a loopback address".into(),
            ));
        }
        Ok(Self {
            listener: TcpListener::bind(address).await?,
            context,
        })
    }

    pub fn local_addr(&self) -> io::Result<SocketAddr> {
        self.listener.local_addr()
    }

    pub async fn run(self, mut shutdown: watch::Receiver<bool>) -> Result<(), ScaffoldingError> {
        let mut clients = JoinSet::new();
        let mut cleanup = tokio::time::interval(CLEANUP_INTERVAL);
        cleanup.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);

        loop {
            tokio::select! {
                accepted = self.listener.accept() => {
                    let (stream, _) = accepted?;
                    let context = Arc::clone(&self.context);
                    let client_shutdown = shutdown.clone();
                    clients.spawn(async move { serve_client(stream, context, client_shutdown).await });
                }
                _ = cleanup.tick() => self.context.remove_stale_guests().await,
                changed = shutdown.changed() => {
                    if changed.is_err() || *shutdown.borrow() {
                        break;
                    }
                }
                Some(result) = clients.join_next(), if !clients.is_empty() => {
                    match result {
                        Ok(Ok(())) => {}
                        Ok(Err(error)) => tracing::debug!(%error, "Scaffolding client disconnected"),
                        Err(error) => tracing::warn!(%error, "Scaffolding client task failed"),
                    }
                }
            }
        }

        clients.abort_all();
        while clients.join_next().await.is_some() {}
        Ok(())
    }
}

async fn serve_client(
    mut stream: TcpStream,
    context: Arc<ServerContext>,
    mut shutdown: watch::Receiver<bool>,
) -> Result<(), ScaffoldingError> {
    loop {
        let request = tokio::select! {
            request = read_request(&mut stream) => request?,
            changed = shutdown.changed() => {
                if changed.is_err() || *shutdown.borrow() {
                    return Ok(());
                }
                continue;
            }
        };

        match request.request_type.as_str() {
            "c:ping" if request.body.len() < 32 => {
                write_response(&mut stream, 0, &request.body).await?;
            }
            "c:ping" => {
                write_response(&mut stream, INVALID_REQUEST_STATUS, &[]).await?;
            }
            "c:protocols" => {
                let offered: HashSet<&str> = std::str::from_utf8(&request.body)
                    .unwrap_or_default()
                    .split('\0')
                    .filter(|value| !value.is_empty())
                    .collect();
                let protocols = SUPPORTED_PROTOCOLS
                    .iter()
                    .copied()
                    .filter(|protocol| offered.is_empty() || offered.contains(protocol))
                    .collect::<Vec<_>>()
                    .join("\0");
                write_response(&mut stream, 0, protocols.as_bytes()).await?;
            }
            "c:server_port" if request.body.is_empty() => {
                write_response(&mut stream, 0, &context.minecraft_port.to_be_bytes()).await?;
            }
            "c:player_ping" => match serde_json::from_slice::<PlayerProfile>(&request.body) {
                Ok(profile) => match context.record_guest(profile).await {
                    Ok(()) => write_response(&mut stream, 0, &[]).await?,
                    Err(_) => write_response(&mut stream, INVALID_REQUEST_STATUS, &[]).await?,
                },
                Err(_) => write_response(&mut stream, INVALID_REQUEST_STATUS, &[]).await?,
            },
            "c:player_profiles_list" if request.body.is_empty() => {
                let profiles = context.player_profiles().await;
                let body = serde_json::to_vec(&profiles)?;
                write_response(&mut stream, 0, &body).await?;
            }
            _ => {
                write_response(
                    &mut stream,
                    UNKNOWN_REQUEST_STATUS,
                    b"unsupported Scaffolding request",
                )
                .await?;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use std::{net::SocketAddr, sync::Arc};

    use tokio::sync::watch;

    use super::{ScaffoldingServer, ServerContext};
    use crate::scaffolding::{PlayerKind, PlayerProfile, ScaffoldingClient};

    #[tokio::test]
    async fn client_and_server_exchange_legacy_scaffolding_contracts() {
        let host = PlayerProfile {
            name: "Host".into(),
            machine_id: "host-machine".into(),
            vendor: "PCL N Terracotta".into(),
            kind: Some(PlayerKind::Guest),
        };
        let context = Arc::new(ServerContext::new(host, 25_565).unwrap());
        let server = ScaffoldingServer::bind(
            "127.0.0.1:0".parse::<SocketAddr>().unwrap(),
            Arc::clone(&context),
        )
        .await
        .unwrap();
        let address = server.local_addr().unwrap();
        let (shutdown_tx, shutdown_rx) = watch::channel(false);
        let task = tokio::spawn(server.run(shutdown_rx));

        let guest = PlayerProfile {
            name: "Guest".into(),
            machine_id: "guest-machine".into(),
            vendor: "PCL CE compatibility test".into(),
            kind: None,
        };
        let mut client = ScaffoldingClient::connect(address, guest).await.unwrap();
        assert_eq!(client.minecraft_port().await.unwrap(), 25_565);
        assert_eq!(client.ping(b"probe").await.unwrap(), b"probe");
        let heartbeat = client.heartbeat().await.unwrap();
        assert_eq!(heartbeat.players.len(), 2);
        assert_eq!(heartbeat.players[0].kind, Some(PlayerKind::Host));
        assert_eq!(heartbeat.players[1].kind, Some(PlayerKind::Guest));

        drop(client);
        shutdown_tx.send(true).unwrap();
        task.await.unwrap().unwrap();
    }
}
