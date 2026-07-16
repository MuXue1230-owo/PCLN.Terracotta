use serde::Serialize;
use tokio::sync::broadcast;

use super::{NetworkStatus, RoomMember, RoomSnapshot};

const EVENT_CAPACITY: usize = 64;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PeerEvent {
    pub member: RoomMember,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PeerLeftEvent {
    pub id: String,
}

#[derive(Debug, Clone)]
pub enum RoomEvent {
    PeerJoined(PeerEvent),
    PeerLeft(PeerLeftEvent),
    PeerUpdated(PeerEvent),
    NetworkUpdated(NetworkStatus),
    StateChanged(RoomSnapshot),
}

impl RoomEvent {
    pub fn message_type(&self) -> &'static str {
        match self {
            Self::PeerJoined(_) => "peer.joined",
            Self::PeerLeft(_) => "peer.left",
            Self::PeerUpdated(_) => "peer.updated",
            Self::NetworkUpdated(_) => "network.updated",
            Self::StateChanged(_) => "room.state-changed",
        }
    }

    pub fn payload(&self) -> Result<serde_json::Value, serde_json::Error> {
        match self {
            Self::PeerJoined(value) | Self::PeerUpdated(value) => serde_json::to_value(value),
            Self::PeerLeft(value) => serde_json::to_value(value),
            Self::NetworkUpdated(value) => serde_json::to_value(value),
            Self::StateChanged(value) => serde_json::to_value(value),
        }
    }
}

#[derive(Clone)]
pub struct RoomEventBus {
    sender: broadcast::Sender<RoomEvent>,
}

impl Default for RoomEventBus {
    fn default() -> Self {
        Self::new()
    }
}

impl RoomEventBus {
    pub fn new() -> Self {
        let (sender, _) = broadcast::channel(EVENT_CAPACITY);
        Self { sender }
    }

    pub fn subscribe(&self) -> broadcast::Receiver<RoomEvent> {
        self.sender.subscribe()
    }

    pub fn publish(&self, event: RoomEvent) {
        // Ignore lagging / no-subscriber errors; events are best-effort.
        let _ = self.sender.send(event);
    }
}

#[cfg(test)]
mod tests {
    use super::{PeerEvent, RoomEvent, RoomEventBus};
    use crate::room::{ConnectionMode, RoomMember};

    #[tokio::test]
    async fn subscribers_receive_published_events() {
        let bus = RoomEventBus::new();
        let mut receiver = bus.subscribe();
        bus.publish(RoomEvent::PeerJoined(PeerEvent {
            member: RoomMember {
                id: "a".into(),
                display_name: "A".into(),
                connection_mode: ConnectionMode::Direct,
                round_trip_time_milliseconds: Some(1),
                packet_loss_percent: Some(0.0),
            },
        }));
        let event = receiver.recv().await.unwrap();
        assert_eq!(event.message_type(), "peer.joined");
    }
}
