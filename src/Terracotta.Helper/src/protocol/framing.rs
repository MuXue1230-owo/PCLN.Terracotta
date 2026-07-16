use std::io::ErrorKind;

use serde::Serialize;
use thiserror::Error;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use zeroize::Zeroize;

use super::{Envelope, MAX_FRAME_SIZE};

#[derive(Debug, Error)]
pub enum FrameError {
    #[error("IPC stream ended")]
    EndOfStream,
    #[error("IPC frame header was truncated")]
    TruncatedHeader,
    #[error("IPC frame payload was truncated")]
    TruncatedPayload,
    #[error("IPC frame length {0} is invalid")]
    InvalidLength(u32),
    #[error("IPC frame contains invalid JSON: {0}")]
    InvalidJson(#[from] serde_json::Error),
    #[error("IPC I/O failed: {0}")]
    Io(#[from] std::io::Error),
}

pub async fn read_frame<R>(reader: &mut R) -> Result<Envelope, FrameError>
where
    R: AsyncRead + Unpin,
{
    let mut header = [0_u8; 4];
    match reader.read(&mut header[..1]).await {
        Ok(0) => return Err(FrameError::EndOfStream),
        Ok(_) => {}
        Err(error) => return Err(FrameError::Io(error)),
    }
    if let Err(error) = reader.read_exact(&mut header[1..]).await {
        return Err(if error.kind() == ErrorKind::UnexpectedEof {
            FrameError::TruncatedHeader
        } else {
            FrameError::Io(error)
        });
    }
    let length = u32::from_le_bytes(header);
    if length == 0 || length as usize > MAX_FRAME_SIZE {
        return Err(FrameError::InvalidLength(length));
    }
    let mut body = vec![0_u8; length as usize];
    if let Err(error) = reader.read_exact(&mut body).await {
        body.zeroize();
        return Err(if error.kind() == ErrorKind::UnexpectedEof {
            FrameError::TruncatedPayload
        } else {
            FrameError::Io(error)
        });
    }
    let result = serde_json::from_slice(&body);
    body.zeroize();
    result.map_err(FrameError::InvalidJson)
}

pub async fn write_frame<W, T>(writer: &mut W, value: &T) -> Result<(), FrameError>
where
    W: AsyncWrite + Unpin,
    T: Serialize,
{
    let mut body = serde_json::to_vec(value)?;
    if body.is_empty() || body.len() > MAX_FRAME_SIZE {
        body.zeroize();
        return Err(FrameError::InvalidLength(body.len() as u32));
    }
    writer.write_all(&(body.len() as u32).to_le_bytes()).await?;
    writer.write_all(&body).await?;
    writer.flush().await?;
    body.zeroize();
    Ok(())
}

#[cfg(test)]
mod tests {
    use serde_json::json;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};

    use super::{FrameError, read_frame, write_frame};
    use crate::protocol::{Envelope, MAX_FRAME_SIZE, PROTOCOL_VERSION};

    #[tokio::test]
    async fn frame_round_trip_uses_little_endian_length() {
        let (mut left, mut right) = tokio::io::duplex(4096);
        let envelope = Envelope {
            protocol: PROTOCOL_VERSION,
            id: "request-1".into(),
            message_type: "room.status".into(),
            payload: json!({}),
        };
        let write = tokio::spawn(async move { write_frame(&mut left, &envelope).await });
        let mut header = [0_u8; 4];
        right.read_exact(&mut header).await.unwrap();
        let length = u32::from_le_bytes(header) as usize;
        assert!(length > 0 && length <= MAX_FRAME_SIZE);
        let mut body = vec![0_u8; length];
        right.read_exact(&mut body).await.unwrap();
        write.await.unwrap().unwrap();
        let decoded: Envelope = serde_json::from_slice(&body).unwrap();
        assert_eq!(decoded.id, "request-1");
    }

    #[tokio::test]
    async fn rejects_zero_and_oversized_lengths_before_payload_read() {
        for length in [0_u32, (MAX_FRAME_SIZE + 1) as u32] {
            let (mut left, mut right) = tokio::io::duplex(16);
            left.write_all(&length.to_le_bytes()).await.unwrap();
            drop(left);
            assert!(matches!(
                read_frame(&mut right).await,
                Err(FrameError::InvalidLength(value)) if value == length
            ));
        }
    }
}
