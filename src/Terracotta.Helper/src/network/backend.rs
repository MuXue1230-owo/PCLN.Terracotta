use std::{net::SocketAddr, sync::Arc, time::Duration};

use async_trait::async_trait;
use tokio::{
    net::TcpStream,
    sync::{Mutex, watch},
    time::timeout,
};
use zeroize::Zeroizing;

use crate::{
    room::{
        BackendRefresh, BackendRoom, ConnectionMode, CreateRoomRequest, JoinRoomRequest,
        NetworkStatus, RoomBackend, RoomError, RoomMember,
    },
    scaffolding::{PlayerKind, PlayerProfile, ScaffoldingClient, ScaffoldingServer, ServerContext},
};

use super::{
    credentials::{RoomCredentials, machine_id_from_identity},
    discovery::{
        RoomEndpointAdvertisement, clear_local_endpoint, load_local_endpoint, now_unix_seconds,
        publish_local_endpoint,
    },
    easytier::{
        EasyTierLaunchConfig, EasyTierNode, easytier_missing, resolve_easytier_binary,
        start_easytier,
    },
    mesh::{HOST_VIRTUAL_IPV4, MeshEndpoints},
    port_forward::PortForward,
    quality::{members_from_profiles, network_from_probe, probe_tcp_rtt},
};

const LOCAL_DISCOVERY_ATTEMPTS: u32 = 8;
const LOCAL_DISCOVERY_INTERVAL: Duration = Duration::from_millis(250);
const MESH_PROBE_ATTEMPTS: u32 = 40;
const MESH_PROBE_INTERVAL: Duration = Duration::from_millis(500);

struct HostSession {
    credentials: RoomCredentials,
    endpoints: MeshEndpoints,
    easytier: EasyTierNode,
    scaffolding_shutdown: watch::Sender<bool>,
    scaffolding_task: tokio::task::JoinHandle<Result<(), crate::scaffolding::ScaffoldingError>>,
    scaffolding_context: Arc<ServerContext>,
    scaffolding_addr: SocketAddr,
    minecraft: SocketAddr,
    mesh_scaffolding_ingress: PortForward,
    mesh_minecraft_ingress: PortForward,
    prefer_direct: bool,
    allow_relay: bool,
}

struct MemberSession {
    easytier: EasyTierNode,
    /// Present when join used same-machine discovery instead of EasyTier port-forward.
    scaffolding_forward: Option<PortForward>,
    minecraft_forward: Option<PortForward>,
    local_scaffolding: SocketAddr,
    local_minecraft: SocketAddr,
    prefer_direct: bool,
    allow_relay: bool,
}

enum ActiveSession {
    Host(HostSession),
    Member(MemberSession),
}

/// Production room backend: EasyTier process + Scaffolding + local/mesh port forwards.
pub struct EasyTierRoomBackend {
    identity: Mutex<Option<Zeroizing<[u8; 32]>>>,
    session: Mutex<Option<ActiveSession>>,
}

impl Default for EasyTierRoomBackend {
    fn default() -> Self {
        Self::new()
    }
}

impl EasyTierRoomBackend {
    pub fn new() -> Self {
        Self {
            identity: Mutex::new(None),
            session: Mutex::new(None),
        }
    }

    async fn require_identity(&self) -> Result<Zeroizing<[u8; 32]>, RoomError> {
        self.identity.lock().await.clone().ok_or_else(|| {
            RoomError::new(
                "identity.not-initialized",
                "Initialize the secure identity before entering a room.",
                false,
            )
        })
    }

    async fn ensure_idle(&self) -> Result<(), RoomError> {
        if self.session.lock().await.is_some() {
            return Err(RoomError::new(
                "room.operation-in-progress",
                "A room session is already active in the network backend.",
                false,
            ));
        }
        Ok(())
    }

    async fn stop_session_locked(session: &mut Option<ActiveSession>) {
        if let Some(active) = session.take() {
            match active {
                ActiveSession::Host(host) => {
                    let room_code = host.credentials.room_code.clone();
                    let _ = host.scaffolding_shutdown.send(true);
                    host.scaffolding_task.abort();
                    host.mesh_scaffolding_ingress.stop().await;
                    host.mesh_minecraft_ingress.stop().await;
                    let _ = host.easytier.stop().await;
                    let _ = clear_local_endpoint(&room_code);
                }
                ActiveSession::Member(member) => {
                    if let Some(forward) = member.scaffolding_forward {
                        forward.stop().await;
                    }
                    if let Some(forward) = member.minecraft_forward {
                        forward.stop().await;
                    }
                    let _ = member.easytier.stop().await;
                }
            }
        }
    }
}

#[async_trait]
impl RoomBackend for EasyTierRoomBackend {
    async fn set_identity(&self, identity: Zeroizing<[u8; 32]>) {
        *self.identity.lock().await = Some(identity);
    }

    async fn create(&self, request: &CreateRoomRequest) -> Result<BackendRoom, RoomError> {
        self.ensure_idle().await?;
        let identity = self.require_identity().await?;
        let binary = resolve_easytier_binary().ok_or_else(easytier_missing)?;
        let minecraft = request
            .lan_address
            .parse::<SocketAddr>()
            .map_err(|_| RoomError::invalid("The LAN address must be a valid IP endpoint."))?;
        if !minecraft.ip().is_loopback() || minecraft.port() == 0 {
            return Err(RoomError::invalid(
                "The LAN address must use a loopback IP and a non-zero port.",
            ));
        }

        let credentials = RoomCredentials::generate()?;
        let endpoints = MeshEndpoints::from_room_code(&credentials.room_code);
        let easytier = start_easytier(
            &credentials,
            EasyTierLaunchConfig {
                binary,
                prefer_direct: request.prefer_direct,
                allow_relay: request.allow_relay,
                host_ipv4: Some("10.144.144.1"),
                port_forwards: Vec::new(),
            },
        )
        .await?;

        let host_profile = PlayerProfile {
            name: "Host".into(),
            machine_id: machine_id_from_identity(&identity),
            vendor: "PCL N Terracotta".into(),
            kind: Some(PlayerKind::Host),
        };
        let context = Arc::new(ServerContext::new(host_profile, minecraft.port()).map_err(
            |error| {
                RoomError::new(
                    "network.scaffolding-failed",
                    format!("Failed to create Scaffolding context: {error}"),
                    false,
                )
            },
        )?);
        let server =
            ScaffoldingServer::bind(SocketAddr::from(([127, 0, 0, 1], 0)), Arc::clone(&context))
                .await
                .map_err(|error| {
                    RoomError::new(
                        "network.scaffolding-failed",
                        format!("Failed to bind Scaffolding server: {error}"),
                        true,
                    )
                })?;
        let scaffolding_addr = server.local_addr().map_err(|error| {
            RoomError::new(
                "network.scaffolding-failed",
                format!("Failed to read Scaffolding address: {error}"),
                true,
            )
        })?;
        let (shutdown_tx, shutdown_rx) = watch::channel(false);
        let scaffolding_task = tokio::spawn(server.run(shutdown_rx));

        let mesh_scaffolding_ingress = match PortForward::start_on(
            endpoints.mesh_ingress_bind_scaffolding(),
            scaffolding_addr,
        )
        .await
        {
            Ok(value) => value,
            Err(error) => {
                let _ = shutdown_tx.send(true);
                scaffolding_task.abort();
                let _ = easytier.stop().await;
                return Err(RoomError::new(
                    "network.mesh-ingress-failed",
                    format!("Failed to bind mesh Scaffolding ingress: {error}"),
                    true,
                ));
            }
        };
        let mesh_minecraft_ingress =
            match PortForward::start_on(endpoints.mesh_ingress_bind_minecraft(), minecraft).await {
                Ok(value) => value,
                Err(error) => {
                    mesh_scaffolding_ingress.stop().await;
                    let _ = shutdown_tx.send(true);
                    scaffolding_task.abort();
                    let _ = easytier.stop().await;
                    return Err(RoomError::new(
                        "network.mesh-ingress-failed",
                        format!("Failed to bind mesh Minecraft ingress: {error}"),
                        true,
                    ));
                }
            };

        if let Err(error) = publish_local_endpoint(&RoomEndpointAdvertisement {
            room_code: credentials.room_code.clone(),
            scaffolding: scaffolding_addr.to_string(),
            minecraft: minecraft.to_string(),
            published_unix_seconds: now_unix_seconds(),
        }) {
            mesh_scaffolding_ingress.stop().await;
            mesh_minecraft_ingress.stop().await;
            let _ = shutdown_tx.send(true);
            scaffolding_task.abort();
            let _ = easytier.stop().await;
            return Err(error);
        }

        let room = BackendRoom {
            room_code: credentials.room_code.clone(),
            local_address: None,
            network: host_network_status(request.prefer_direct, request.allow_relay),
            members: vec![RoomMember {
                id: machine_id_from_identity(&identity),
                display_name: "Host".into(),
                connection_mode: ConnectionMode::Direct,
                round_trip_time_milliseconds: Some(0),
                packet_loss_percent: Some(0.0),
            }],
        };

        *self.session.lock().await = Some(ActiveSession::Host(HostSession {
            credentials,
            endpoints,
            easytier,
            scaffolding_shutdown: shutdown_tx,
            scaffolding_task,
            scaffolding_context: context,
            scaffolding_addr,
            minecraft,
            mesh_scaffolding_ingress,
            mesh_minecraft_ingress,
            prefer_direct: request.prefer_direct,
            allow_relay: request.allow_relay,
        }));

        tracing::info!(
            host = %HOST_VIRTUAL_IPV4,
            scaffolding_port = endpoints.scaffolding_port,
            minecraft_port = endpoints.minecraft_port,
            "Terracotta host mesh endpoints published"
        );
        Ok(room)
    }

    async fn join(&self, request: &JoinRoomRequest) -> Result<BackendRoom, RoomError> {
        self.ensure_idle().await?;
        let identity = self.require_identity().await?;
        let binary = resolve_easytier_binary().ok_or_else(easytier_missing)?;
        let credentials = RoomCredentials::from_room_code(&request.room_code)?;
        let endpoints = MeshEndpoints::from_room_code(&credentials.room_code);

        // Prefer same-machine discovery (no mesh port-forward needed).
        if let Some(advertisement) = try_local_endpoint(&credentials.room_code).await? {
            return self
                .join_via_local_discovery(identity, credentials, binary, advertisement)
                .await;
        }

        // Cross-machine path: EasyTier port-forward to host virtual endpoints.
        let port_forwards = endpoints.member_port_forwards().to_vec();
        let easytier = start_easytier(
            &credentials,
            EasyTierLaunchConfig {
                binary,
                prefer_direct: true,
                allow_relay: true,
                host_ipv4: None,
                port_forwards,
            },
        )
        .await?;

        let local_scaffolding = endpoints.member_local_scaffolding_addr();
        let local_minecraft = endpoints.member_local_minecraft_addr();
        if let Err(error) = wait_for_tcp(local_scaffolding).await {
            let _ = easytier.stop().await;
            return Err(error);
        }

        let guest_profile = PlayerProfile {
            name: "Player".into(),
            machine_id: machine_id_from_identity(&identity),
            vendor: "PCL N Terracotta".into(),
            kind: Some(PlayerKind::Guest),
        };
        let (members, rtt_ms, connection_mode) =
            collect_member_snapshot(local_scaffolding, guest_profile).await;

        let room = BackendRoom {
            room_code: credentials.room_code.clone(),
            local_address: Some(local_minecraft.to_string()),
            network: NetworkStatus {
                nat_type: Some("Unknown".into()),
                connection_mode,
                round_trip_time_milliseconds: rtt_ms,
                packet_loss_percent: None,
                relay_node: None,
                is_healthy: true,
            },
            members,
        };
        drop(credentials);

        *self.session.lock().await = Some(ActiveSession::Member(MemberSession {
            easytier,
            scaffolding_forward: None,
            minecraft_forward: None,
            local_scaffolding,
            local_minecraft,
            prefer_direct: true,
            allow_relay: true,
        }));
        Ok(room)
    }

    async fn set_lan_address(&self, address: SocketAddr) -> Result<(), RoomError> {
        let mut guard = self.session.lock().await;
        let Some(ActiveSession::Host(host)) = guard.as_mut() else {
            return Err(RoomError::new(
                "room.not-host",
                "Only a connected room host can change the LAN address.",
                false,
            ));
        };
        if !address.ip().is_loopback() || address.port() == 0 {
            return Err(RoomError::invalid(
                "The LAN address must use a loopback IP and a non-zero port.",
            ));
        }

        let _ = host.scaffolding_shutdown.send(true);
        host.scaffolding_task.abort();

        let identity = self.require_identity().await?;
        let host_profile = PlayerProfile {
            name: "Host".into(),
            machine_id: machine_id_from_identity(&identity),
            vendor: "PCL N Terracotta".into(),
            kind: Some(PlayerKind::Host),
        };
        let context = Arc::new(ServerContext::new(host_profile, address.port()).map_err(
            |error| {
                RoomError::new(
                    "network.scaffolding-failed",
                    format!("Failed to create Scaffolding context: {error}"),
                    false,
                )
            },
        )?);
        let server =
            ScaffoldingServer::bind(SocketAddr::from(([127, 0, 0, 1], 0)), Arc::clone(&context))
                .await
                .map_err(|error| {
                    RoomError::new(
                        "network.scaffolding-failed",
                        format!("Failed to rebind Scaffolding server: {error}"),
                        true,
                    )
                })?;
        let scaffolding_addr = server.local_addr().map_err(|error| {
            RoomError::new(
                "network.scaffolding-failed",
                format!("Failed to read Scaffolding address: {error}"),
                true,
            )
        })?;
        let (shutdown_tx, shutdown_rx) = watch::channel(false);
        let scaffolding_task = tokio::spawn(server.run(shutdown_rx));

        let mesh_scaffolding_ingress = PortForward::start_on(
            host.endpoints.mesh_ingress_bind_scaffolding(),
            scaffolding_addr,
        )
        .await
        .map_err(|error| {
            RoomError::new(
                "network.mesh-ingress-failed",
                format!("Failed to rebind mesh Scaffolding ingress: {error}"),
                true,
            )
        })?;
        let mesh_minecraft_ingress =
            PortForward::start_on(host.endpoints.mesh_ingress_bind_minecraft(), address)
                .await
                .map_err(|error| {
                    RoomError::new(
                        "network.mesh-ingress-failed",
                        format!("Failed to rebind mesh Minecraft ingress: {error}"),
                        true,
                    )
                })?;

        // Swap replacements in, then stop the previous ingresses (which own the old sockets).
        host.scaffolding_shutdown = shutdown_tx;
        host.scaffolding_task = scaffolding_task;
        host.scaffolding_context = context;
        host.scaffolding_addr = scaffolding_addr;
        host.minecraft = address;
        let previous_scaffolding =
            std::mem::replace(&mut host.mesh_scaffolding_ingress, mesh_scaffolding_ingress);
        let previous_minecraft =
            std::mem::replace(&mut host.mesh_minecraft_ingress, mesh_minecraft_ingress);
        previous_scaffolding.stop().await;
        previous_minecraft.stop().await;

        publish_local_endpoint(&RoomEndpointAdvertisement {
            room_code: host.credentials.room_code.clone(),
            scaffolding: scaffolding_addr.to_string(),
            minecraft: address.to_string(),
            published_unix_seconds: now_unix_seconds(),
        })?;
        Ok(())
    }

    async fn diagnose(&self) -> Result<NetworkStatus, RoomError> {
        let refresh = self.refresh().await?;
        refresh.network.ok_or_else(|| {
            RoomError::new(
                "room.not-connected",
                "Network diagnostics require an active room.",
                false,
            )
        })
    }

    async fn refresh(&self) -> Result<BackendRefresh, RoomError> {
        let guard = self.session.lock().await;
        match guard.as_ref() {
            Some(ActiveSession::Host(host)) => {
                let rtt_ms = probe_tcp_rtt(host.minecraft).await;
                let network = network_from_probe(rtt_ms, host.prefer_direct, host.allow_relay);
                let profiles = host.scaffolding_context.player_profiles().await;
                let members = members_from_profiles(profiles, rtt_ms, network.connection_mode);
                Ok(BackendRefresh {
                    network: Some(network),
                    members: Some(members),
                    local_address: None,
                })
            }
            Some(ActiveSession::Member(member)) => {
                let rtt_ms = probe_tcp_rtt(member.local_minecraft).await;
                let network = network_from_probe(rtt_ms, member.prefer_direct, member.allow_relay);
                // Best-effort Scaffolding member list over the local forward.
                let members = match ScaffoldingClient::connect(
                    member.local_scaffolding,
                    PlayerProfile {
                        name: "Probe".into(),
                        machine_id: format!("probe-{}", now_unix_seconds()),
                        vendor: "PCL N Terracotta".into(),
                        kind: Some(PlayerKind::Guest),
                    },
                )
                .await
                {
                    Ok(mut client) => match client.heartbeat().await {
                        Ok(heartbeat) => Some(members_from_profiles(
                            heartbeat.players,
                            Some(heartbeat.latency.as_millis().min(u128::from(u32::MAX)) as u32),
                            network.connection_mode,
                        )),
                        Err(_) => None,
                    },
                    Err(_) => None,
                };
                Ok(BackendRefresh {
                    network: Some(network),
                    members,
                    local_address: Some(member.local_minecraft.to_string()),
                })
            }
            None => Ok(BackendRefresh::default()),
        }
    }

    async fn leave(&self) -> Result<(), RoomError> {
        let mut guard = self.session.lock().await;
        Self::stop_session_locked(&mut guard).await;
        Ok(())
    }
}

impl EasyTierRoomBackend {
    async fn join_via_local_discovery(
        &self,
        identity: Zeroizing<[u8; 32]>,
        credentials: RoomCredentials,
        binary: std::path::PathBuf,
        advertisement: RoomEndpointAdvertisement,
    ) -> Result<BackendRoom, RoomError> {
        let easytier = start_easytier(
            &credentials,
            EasyTierLaunchConfig {
                binary,
                prefer_direct: true,
                allow_relay: true,
                host_ipv4: None,
                port_forwards: Vec::new(),
            },
        )
        .await?;

        let scaffolding_target = match advertisement.scaffolding_addr() {
            Ok(value) => value,
            Err(error) => {
                let _ = easytier.stop().await;
                return Err(error);
            }
        };
        let minecraft_target = match advertisement.minecraft_addr() {
            Ok(value) => value,
            Err(error) => {
                let _ = easytier.stop().await;
                return Err(error);
            }
        };

        let scaffolding_forward = match PortForward::start(scaffolding_target).await {
            Ok(value) => value,
            Err(error) => {
                let _ = easytier.stop().await;
                return Err(RoomError::new(
                    "network.forward-failed",
                    format!("Failed to create Scaffolding forward: {error}"),
                    true,
                ));
            }
        };
        let minecraft_forward = match PortForward::start(minecraft_target).await {
            Ok(value) => value,
            Err(error) => {
                scaffolding_forward.stop().await;
                let _ = easytier.stop().await;
                return Err(RoomError::new(
                    "network.forward-failed",
                    format!("Failed to create Minecraft forward: {error}"),
                    true,
                ));
            }
        };

        let guest_profile = PlayerProfile {
            name: "Player".into(),
            machine_id: machine_id_from_identity(&identity),
            vendor: "PCL N Terracotta".into(),
            kind: Some(PlayerKind::Guest),
        };
        let (members, rtt_ms, _) =
            collect_member_snapshot(scaffolding_forward.local_addr(), guest_profile).await;

        let room = BackendRoom {
            room_code: credentials.room_code.clone(),
            local_address: Some(minecraft_forward.local_addr().to_string()),
            network: NetworkStatus {
                nat_type: Some("Local".into()),
                connection_mode: ConnectionMode::Direct,
                round_trip_time_milliseconds: rtt_ms,
                packet_loss_percent: Some(0.0),
                relay_node: None,
                is_healthy: true,
            },
            members,
        };
        let local_scaffolding = scaffolding_forward.local_addr();
        let local_minecraft = minecraft_forward.local_addr();
        drop(credentials);

        *self.session.lock().await = Some(ActiveSession::Member(MemberSession {
            easytier,
            scaffolding_forward: Some(scaffolding_forward),
            minecraft_forward: Some(minecraft_forward),
            local_scaffolding,
            local_minecraft,
            prefer_direct: true,
            allow_relay: true,
        }));
        Ok(room)
    }
}

async fn try_local_endpoint(
    room_code: &str,
) -> Result<Option<RoomEndpointAdvertisement>, RoomError> {
    for _ in 0..LOCAL_DISCOVERY_ATTEMPTS {
        if let Some(advertisement) = load_local_endpoint(room_code)? {
            return Ok(Some(advertisement));
        }
        tokio::time::sleep(LOCAL_DISCOVERY_INTERVAL).await;
    }
    Ok(None)
}

async fn wait_for_tcp(address: SocketAddr) -> Result<(), RoomError> {
    for attempt in 1..=MESH_PROBE_ATTEMPTS {
        match timeout(Duration::from_millis(400), TcpStream::connect(address)).await {
            Ok(Ok(_stream)) => return Ok(()),
            Ok(Err(_)) | Err(_) => {
                tracing::debug!(%address, attempt, "Waiting for mesh local forward");
                tokio::time::sleep(MESH_PROBE_INTERVAL).await;
            }
        }
    }
    Err(RoomError::new(
        "network.peer-unreachable",
        format!(
            "The room host was not reachable via EasyTier mesh at {HOST_VIRTUAL_IPV4} (probed local forward {address}). Ensure the host is online, both sides share the same room code, and consider TERRACOTTA_EASYTIER_ALLOW_TUN=1 when userspace routing is insufficient."
        ),
        true,
    ))
}

async fn collect_member_snapshot(
    scaffolding: SocketAddr,
    guest_profile: PlayerProfile,
) -> (Vec<RoomMember>, Option<u32>, ConnectionMode) {
    let mut members = vec![RoomMember {
        id: guest_profile.machine_id.clone(),
        display_name: guest_profile.name.clone(),
        connection_mode: ConnectionMode::Unknown,
        round_trip_time_milliseconds: None,
        packet_loss_percent: None,
    }];
    let mut rtt_ms = None;
    let mut mode = ConnectionMode::Unknown;
    match ScaffoldingClient::connect(scaffolding, guest_profile).await {
        Ok(mut client) => match client.heartbeat().await {
            Ok(heartbeat) => {
                rtt_ms = Some(heartbeat.latency.as_millis().min(u128::from(u32::MAX)) as u32);
                mode = ConnectionMode::Direct;
                members = heartbeat
                    .players
                    .into_iter()
                    .map(|profile| RoomMember {
                        id: profile.machine_id,
                        display_name: profile.name,
                        connection_mode: ConnectionMode::Direct,
                        round_trip_time_milliseconds: rtt_ms,
                        packet_loss_percent: None,
                    })
                    .collect();
            }
            Err(error) => tracing::debug!(%error, "Scaffolding heartbeat failed after join"),
        },
        Err(error) => tracing::debug!(%error, "Scaffolding client connect failed after join"),
    }
    (members, rtt_ms, mode)
}

fn host_network_status(prefer_direct: bool, allow_relay: bool) -> NetworkStatus {
    NetworkStatus {
        nat_type: Some("Unknown".into()),
        connection_mode: if prefer_direct {
            ConnectionMode::Direct
        } else if allow_relay {
            ConnectionMode::Relay
        } else {
            ConnectionMode::Unknown
        },
        round_trip_time_milliseconds: Some(0),
        packet_loss_percent: Some(0.0),
        relay_node: None,
        is_healthy: true,
    }
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use super::EasyTierRoomBackend;
    use crate::room::{CreateRoomRequest, RoomBackend, RoomService};

    #[tokio::test]
    async fn missing_easytier_binary_returns_stable_fault() {
        unsafe {
            std::env::set_var(
                "TERRACOTTA_EASYTIER_PATH",
                std::env::temp_dir().join("terracotta-missing-easytier-core"),
            );
        }

        let backend = Arc::new(EasyTierRoomBackend::new());
        RoomBackend::set_identity(backend.as_ref(), zeroize::Zeroizing::new([9_u8; 32])).await;
        let service = RoomService::new(backend);
        let error = service
            .create(CreateRoomRequest {
                game_session_id: "session-1".into(),
                lan_address: "127.0.0.1:25565".into(),
                prefer_direct: true,
                allow_relay: true,
            })
            .await
            .unwrap_err();
        assert_eq!(error.code, "network.easytier-missing");

        unsafe {
            std::env::remove_var("TERRACOTTA_EASYTIER_PATH");
        }
    }
}
