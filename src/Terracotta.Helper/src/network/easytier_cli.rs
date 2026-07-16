use std::{
    path::{Path, PathBuf},
    process::Stdio,
    time::Duration,
};

use tokio::process::Command;

use crate::room::{ConnectionMode, NetworkStatus};

/// Best-effort peer quality sample extracted from `easytier-cli peer`.
#[derive(Debug, Clone, PartialEq)]
pub struct PeerQualitySample {
    pub nat_type: Option<String>,
    pub latency_ms: Option<u32>,
    pub loss_rate: Option<f64>,
    pub connection_mode: ConnectionMode,
    pub relay_node: Option<String>,
}

pub fn resolve_easytier_cli(core_binary: &Path) -> Option<PathBuf> {
    let directory = core_binary.parent()?;
    let candidate = directory.join(if cfg!(windows) {
        "easytier-cli.exe"
    } else {
        "easytier-cli"
    });
    candidate.is_file().then_some(candidate)
}

pub async fn query_peer_quality(cli: &Path, rpc_portal: &str) -> Option<PeerQualitySample> {
    let output = tokio::time::timeout(
        Duration::from_secs(3),
        Command::new(cli)
            .arg("-p")
            .arg(rpc_portal)
            .arg("peer")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output(),
    )
    .await
    .ok()?
    .ok()?;

    if !output.status.success() {
        return None;
    }
    let text = String::from_utf8_lossy(&output.stdout);
    parse_peer_table(&text)
}

pub fn parse_peer_table(text: &str) -> Option<PeerQualitySample> {
    let mut best: Option<PeerQualitySample> = None;
    for line in text.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() || trimmed.starts_with('|') && trimmed.contains("ipv4") {
            continue;
        }
        // Prefer pipe-delimited CLI tables; also accept whitespace-separated dumps.
        let cells: Vec<&str> = if trimmed.contains('|') {
            trimmed
                .split('|')
                .map(str::trim)
                .filter(|cell| !cell.is_empty())
                .collect()
        } else {
            trimmed.split_whitespace().collect()
        };
        if cells.len() < 5 {
            continue;
        }

        let cost = cells.iter().find(|cell| {
            matches!(
                cell.to_ascii_lowercase().as_str(),
                "local" | "p2p" | "relay" | "direct"
            )
        });
        let latency = cells.iter().find_map(|cell| parse_latency(cell));
        let loss = cells.iter().find_map(|cell| parse_loss(cell));
        let nat = cells.iter().find_map(|cell| parse_nat(cell));
        if cost.is_none() && latency.is_none() && nat.is_none() {
            continue;
        }

        let cost_text = cost
            .map(|value| value.to_ascii_lowercase())
            .unwrap_or_default();
        let connection_mode = if cost_text.contains("relay") {
            ConnectionMode::Relay
        } else if cost_text.contains("p2p")
            || cost_text.contains("direct")
            || cost_text == "local"
            || latency.is_some()
        {
            ConnectionMode::Direct
        } else {
            ConnectionMode::Unknown
        };

        let sample = PeerQualitySample {
            nat_type: nat.map(str::to_owned),
            latency_ms: latency,
            loss_rate: loss,
            connection_mode,
            relay_node: (connection_mode == ConnectionMode::Relay)
                .then(|| "easytier-relay".to_owned()),
        };

        best = Some(match best {
            None => sample,
            Some(previous) => prefer_sample(previous, sample),
        });
    }
    best
}

pub fn network_from_peer_sample(sample: &PeerQualitySample) -> NetworkStatus {
    NetworkStatus {
        nat_type: sample.nat_type.clone().or_else(|| Some("Unknown".into())),
        connection_mode: sample.connection_mode,
        round_trip_time_milliseconds: sample.latency_ms,
        packet_loss_percent: sample.loss_rate.map(|value| value * 100.0),
        relay_node: sample.relay_node.clone(),
        is_healthy: sample.latency_ms.is_some()
            || sample.connection_mode != ConnectionMode::Unknown,
    }
}

fn prefer_sample(left: PeerQualitySample, right: PeerQualitySample) -> PeerQualitySample {
    match (left.latency_ms, right.latency_ms) {
        (Some(a), Some(b)) if b < a => right,
        (None, Some(_)) => right,
        _ => left,
    }
}

fn parse_latency(cell: &str) -> Option<u32> {
    let cleaned = cell.trim().trim_end_matches("ms").trim();
    if cleaned == "*" {
        return None;
    }
    cleaned
        .parse::<f64>()
        .ok()
        .map(|value| value.round() as u32)
}

fn parse_loss(cell: &str) -> Option<f64> {
    let cleaned = cell.trim().trim_end_matches('%').trim();
    if cleaned == "*" {
        return None;
    }
    cleaned
        .parse::<f64>()
        .ok()
        .map(|value| if value > 1.0 { value / 100.0 } else { value })
}

fn parse_nat(cell: &str) -> Option<&str> {
    let lower = cell.to_ascii_lowercase();
    if lower.contains("cone")
        || lower.contains("symmetric")
        || lower.contains("unknown")
        || lower.contains("open")
    {
        Some(cell)
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::{network_from_peer_sample, parse_peer_table};
    use crate::room::ConnectionMode;

    #[test]
    fn parses_pipe_table_peer_rows() {
        let text = r#"
| ipv4         | hostname | cost | lat_ms | loss_rate | nat_type  |
| 10.144.144.1 | host     | p2p  | 18     | 0.01      | FullCone  |
| 10.144.144.2 | guest    | relay| 90     | 0.05      | Symmetric |
"#;
        let sample = parse_peer_table(text).unwrap();
        assert_eq!(sample.latency_ms, Some(18));
        assert_eq!(sample.connection_mode, ConnectionMode::Direct);
        assert_eq!(sample.nat_type.as_deref(), Some("FullCone"));
        let network = network_from_peer_sample(&sample);
        assert!(network.is_healthy);
        assert_eq!(network.round_trip_time_milliseconds, Some(18));
    }
}
