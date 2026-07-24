use std::{io, net::SocketAddr, time::Duration};

use tokio::{
    net::UdpSocket,
    sync::watch,
    task::JoinHandle,
    time::{MissedTickBehavior, interval},
};

const MINECRAFT_LAN_DISCOVERY: &str = "127.0.0.1:4445";
const BROADCAST_INTERVAL: Duration = Duration::from_millis(1_500);

/// Advertises the member-side loopback forward to the local Minecraft client.
///
/// PCL CE's lobby implementation does the same after creating its local TCP
/// forward, which lets the joined room appear in Minecraft's multiplayer list
/// without asking the player to add a server manually.
pub struct MinecraftLanBroadcast {
    shutdown: watch::Sender<bool>,
    task: JoinHandle<()>,
}

impl MinecraftLanBroadcast {
    pub async fn start(local_minecraft: SocketAddr) -> io::Result<Self> {
        let socket = UdpSocket::bind("127.0.0.1:0").await?;
        let destination = MINECRAFT_LAN_DISCOVERY
            .parse::<SocketAddr>()
            .expect("static Minecraft LAN discovery endpoint is valid");
        let payload = payload(local_minecraft.port());
        let (shutdown, mut shutdown_rx) = watch::channel(false);
        let task = tokio::spawn(async move {
            let mut ticker = interval(BROADCAST_INTERVAL);
            ticker.set_missed_tick_behavior(MissedTickBehavior::Skip);
            loop {
                tokio::select! {
                    changed = shutdown_rx.changed() => {
                        if changed.is_err() || *shutdown_rx.borrow() {
                            break;
                        }
                    }
                    _ = ticker.tick() => {
                        if let Err(error) = socket.send_to(&payload, destination).await {
                            tracing::debug!(%error, "Failed to advertise Terracotta Minecraft forward");
                        }
                    }
                }
            }
        });
        Ok(Self { shutdown, task })
    }

    pub async fn stop(self) {
        let _ = self.shutdown.send(true);
        let _ = self.task.await;
    }
}

fn payload(port: u16) -> Vec<u8> {
    format!("[MOTD]PCL N Terracotta[/MOTD][AD]{port}[/AD]").into_bytes()
}

#[cfg(test)]
mod tests {
    use super::payload;

    #[test]
    fn payload_matches_minecraft_lan_announcement_format() {
        assert_eq!(
            payload(25_565),
            b"[MOTD]PCL N Terracotta[/MOTD][AD]25565[/AD]"
        );
    }
}
