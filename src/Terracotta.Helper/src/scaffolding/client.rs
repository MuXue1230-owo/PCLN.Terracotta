use std::{collections::HashSet, net::SocketAddr, time::Duration};

use tokio::{net::TcpStream, time::Instant};

use super::{PlayerProfile, SUPPORTED_PROTOCOLS, ScaffoldingError, read_response, write_request};

#[derive(Debug, Clone)]
pub struct HeartbeatResult {
    pub latency: Duration,
    pub players: Vec<PlayerProfile>,
}

pub struct ScaffoldingClient {
    stream: TcpStream,
    profile: PlayerProfile,
}

impl ScaffoldingClient {
    pub async fn connect(
        address: SocketAddr,
        profile: PlayerProfile,
    ) -> Result<Self, ScaffoldingError> {
        if !address.ip().is_loopback() {
            return Err(ScaffoldingError::InvalidFrame(
                "Scaffolding clients must connect through a loopback forward".into(),
            ));
        }
        if !profile.validate() {
            return Err(ScaffoldingError::InvalidFrame(
                "player profile contains invalid text".into(),
            ));
        }
        let stream = tokio::time::timeout(Duration::from_secs(10), TcpStream::connect(address))
            .await
            .map_err(|_| {
                ScaffoldingError::Io(std::io::Error::new(
                    std::io::ErrorKind::TimedOut,
                    "Scaffolding connection timed out",
                ))
            })??;
        let mut client = Self { stream, profile };
        client.send_player_ping().await?;
        client.negotiate_protocols().await?;
        Ok(client)
    }

    pub async fn ping(&mut self, payload: &[u8]) -> Result<Vec<u8>, ScaffoldingError> {
        if payload.len() >= 32 {
            return Err(ScaffoldingError::InvalidFrame(
                "ping payload must be shorter than 32 bytes".into(),
            ));
        }
        self.request("c:ping", payload).await
    }

    pub async fn minecraft_port(&mut self) -> Result<u16, ScaffoldingError> {
        let body = self.request("c:server_port", &[]).await?;
        if body.len() != 2 {
            return Err(ScaffoldingError::InvalidFrame(
                "server port response must contain exactly two bytes".into(),
            ));
        }
        Ok(u16::from_be_bytes([body[0], body[1]]))
    }

    pub async fn player_profiles(&mut self) -> Result<Vec<PlayerProfile>, ScaffoldingError> {
        let body = self.request("c:player_profiles_list", &[]).await?;
        let profiles: Vec<PlayerProfile> = serde_json::from_slice(&body)?;
        if profiles.iter().any(|profile| !profile.validate()) {
            return Err(ScaffoldingError::InvalidFrame(
                "server returned an invalid player profile".into(),
            ));
        }
        Ok(profiles)
    }

    pub async fn heartbeat(&mut self) -> Result<HeartbeatResult, ScaffoldingError> {
        let started = Instant::now();
        self.send_player_ping().await?;
        let latency = started.elapsed();
        let players = self.player_profiles().await?;
        Ok(HeartbeatResult { latency, players })
    }

    async fn send_player_ping(&mut self) -> Result<(), ScaffoldingError> {
        let body = serde_json::to_vec(&self.profile)?;
        self.request("c:player_ping", &body).await?;
        Ok(())
    }

    async fn negotiate_protocols(&mut self) -> Result<(), ScaffoldingError> {
        let offered = SUPPORTED_PROTOCOLS.join("\0");
        let body = self.request("c:protocols", offered.as_bytes()).await?;
        let accepted: HashSet<&str> = std::str::from_utf8(&body)
            .map_err(|_| {
                ScaffoldingError::InvalidFrame(
                    "protocol negotiation response is not valid ASCII".into(),
                )
            })?
            .split('\0')
            .filter(|value| !value.is_empty())
            .collect();
        for required in ["c:server_port", "c:player_ping", "c:player_profiles_list"] {
            if !accepted.contains(required) {
                return Err(ScaffoldingError::InvalidFrame(format!(
                    "server does not support required protocol {required}"
                )));
            }
        }
        Ok(())
    }

    async fn request(
        &mut self,
        request_type: &str,
        body: &[u8],
    ) -> Result<Vec<u8>, ScaffoldingError> {
        write_request(&mut self.stream, request_type, body).await?;
        let response =
            tokio::time::timeout(Duration::from_secs(10), read_response(&mut self.stream))
                .await
                .map_err(|_| {
                    ScaffoldingError::Io(std::io::Error::new(
                        std::io::ErrorKind::TimedOut,
                        "Scaffolding response timed out",
                    ))
                })??;
        if response.status != 0 {
            let message = if response.status == 255 {
                String::from_utf8_lossy(&response.body).into_owned()
            } else {
                "remote request rejected".into()
            };
            return Err(ScaffoldingError::RemoteStatus {
                status: response.status,
                message,
            });
        }
        Ok(response.body)
    }
}
