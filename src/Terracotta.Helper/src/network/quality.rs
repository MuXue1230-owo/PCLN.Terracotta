use std::{
    net::SocketAddr,
    time::{Duration, Instant},
};

use tokio::{net::TcpStream, time::timeout};

use crate::{
    room::{ConnectionMode, NetworkStatus, RoomMember},
    scaffolding::{PlayerKind, PlayerProfile},
};

/// Probes a loopback (or mesh-local) TCP endpoint and returns round-trip milliseconds.
pub async fn probe_tcp_rtt(address: SocketAddr) -> Option<u32> {
    let started = Instant::now();
    match timeout(Duration::from_secs(2), TcpStream::connect(address)).await {
        Ok(Ok(_stream)) => {
            let millis = started.elapsed().as_millis().min(u128::from(u32::MAX)) as u32;
            Some(millis)
        }
        Ok(Err(_)) | Err(_) => None,
    }
}

pub fn network_from_probe(
    rtt_ms: Option<u32>,
    prefer_direct: bool,
    allow_relay: bool,
) -> NetworkStatus {
    let healthy = rtt_ms.is_some();
    let connection_mode = if !healthy {
        ConnectionMode::Unknown
    } else if prefer_direct {
        ConnectionMode::Direct
    } else if allow_relay {
        ConnectionMode::Relay
    } else {
        ConnectionMode::Unknown
    };
    NetworkStatus {
        nat_type: Some(classify_nat(rtt_ms, connection_mode)),
        connection_mode,
        round_trip_time_milliseconds: rtt_ms,
        packet_loss_percent: if healthy { Some(0.0) } else { None },
        relay_node: if connection_mode == ConnectionMode::Relay {
            Some("shared-public".into())
        } else {
            None
        },
        is_healthy: healthy,
    }
}

pub fn members_from_profiles(
    profiles: Vec<PlayerProfile>,
    rtt_ms: Option<u32>,
    mode: ConnectionMode,
) -> Vec<RoomMember> {
    profiles
        .into_iter()
        .map(|profile| RoomMember {
            id: profile.machine_id,
            display_name: profile.name,
            connection_mode: match profile.kind {
                Some(PlayerKind::Host) => ConnectionMode::Direct,
                _ => mode,
            },
            round_trip_time_milliseconds: rtt_ms,
            packet_loss_percent: if rtt_ms.is_some() { Some(0.0) } else { None },
        })
        .collect()
}

fn classify_nat(rtt_ms: Option<u32>, mode: ConnectionMode) -> String {
    match (rtt_ms, mode) {
        (None, _) => "Unreachable".into(),
        (Some(ms), ConnectionMode::Direct) if ms < 40 => "LikelyOpenOrFullCone".into(),
        (Some(ms), ConnectionMode::Direct) if ms < 120 => "LikelyAddressRestricted".into(),
        (Some(_), ConnectionMode::Direct) => "LikelySymmetricOrCongested".into(),
        (Some(_), ConnectionMode::Relay) => "RelayAssisted".into(),
        _ => "Unknown".into(),
    }
}

#[cfg(test)]
mod tests {
    use super::{classify_nat, network_from_probe};
    use crate::room::ConnectionMode;

    #[test]
    fn healthy_probe_marks_direct_network() {
        let status = network_from_probe(Some(12), true, true);
        assert!(status.is_healthy);
        assert_eq!(status.connection_mode, ConnectionMode::Direct);
        assert_eq!(status.nat_type.as_deref(), Some("LikelyOpenOrFullCone"));
    }

    #[test]
    fn missing_probe_is_unhealthy() {
        let status = network_from_probe(None, true, true);
        assert!(!status.is_healthy);
        assert_eq!(status.nat_type.as_deref(), Some("Unreachable"));
        assert_eq!(classify_nat(None, ConnectionMode::Unknown), "Unreachable");
    }
}
