mod client;
mod model;
mod protocol;
mod server;

pub use client::{HeartbeatResult, ScaffoldingClient};
pub use model::{PlayerKind, PlayerProfile};
pub use protocol::{
    MAX_BODY_LENGTH, MAX_TYPE_LENGTH, RequestFrame, ResponseFrame, ScaffoldingError, read_request,
    read_response, write_request, write_response,
};
pub use server::{SUPPORTED_PROTOCOLS, ScaffoldingServer, ServerContext};
