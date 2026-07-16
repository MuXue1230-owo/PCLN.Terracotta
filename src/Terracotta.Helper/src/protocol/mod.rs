use serde::{Deserialize, Serialize};
use serde_json::Value;

pub mod framing;

pub const PROTOCOL_VERSION: u32 = 1;
pub const MAX_FRAME_SIZE: usize = 1024 * 1024;

#[derive(Debug, Deserialize, Serialize)]
pub struct Envelope {
    pub protocol: u32,
    pub id: String,
    #[serde(rename = "type")]
    pub message_type: String,
    pub payload: Value,
}

impl Envelope {
    pub fn response<T: Serialize>(
        id: String,
        message_type: impl Into<String>,
        payload: T,
    ) -> Result<Self, serde_json::Error> {
        Ok(Self {
            protocol: PROTOCOL_VERSION,
            id,
            message_type: message_type.into(),
            payload: serde_json::to_value(payload)?,
        })
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HelloRequest {
    pub auth_token: String,
    pub client: String,
    pub client_version: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HelloAccepted<'a> {
    pub helper_version: &'a str,
    pub capabilities: &'a [&'a str],
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ErrorResponse<'a> {
    pub code: &'a str,
    pub message: &'a str,
    pub retryable: bool,
}
