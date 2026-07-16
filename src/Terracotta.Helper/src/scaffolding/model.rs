use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum PlayerKind {
    Host,
    Guest,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PlayerProfile {
    pub name: String,
    pub machine_id: String,
    pub vendor: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub kind: Option<PlayerKind>,
}

impl PlayerProfile {
    pub fn validate(&self) -> bool {
        valid_text(&self.name, 64)
            && valid_text(&self.machine_id, 128)
            && valid_text(&self.vendor, 256)
    }
}

fn valid_text(value: &str, maximum_length: usize) -> bool {
    !value.trim().is_empty()
        && value.len() <= maximum_length
        && !value.chars().any(char::is_control)
}
