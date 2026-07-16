use std::io;

use thiserror::Error;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

pub const MAX_TYPE_LENGTH: usize = 128;
pub const MAX_BODY_LENGTH: usize = 65_536;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RequestFrame {
    pub request_type: String,
    pub body: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResponseFrame {
    pub status: u8,
    pub body: Vec<u8>,
}

#[derive(Debug, Error)]
pub enum ScaffoldingError {
    #[error("Scaffolding I/O failed: {0}")]
    Io(#[from] io::Error),
    #[error("Invalid Scaffolding frame: {0}")]
    InvalidFrame(String),
    #[error("Invalid Scaffolding JSON: {0}")]
    Json(#[from] serde_json::Error),
    #[error("Scaffolding request failed with status {status}: {message}")]
    RemoteStatus { status: u8, message: String },
}

pub async fn read_request<R>(reader: &mut R) -> Result<RequestFrame, ScaffoldingError>
where
    R: AsyncRead + Unpin,
{
    let type_length = reader.read_u8().await? as usize;
    if type_length == 0 || type_length > MAX_TYPE_LENGTH {
        return Err(ScaffoldingError::InvalidFrame(format!(
            "request type length {type_length} is outside 1..={MAX_TYPE_LENGTH}"
        )));
    }

    let mut request_type = vec![0; type_length];
    reader.read_exact(&mut request_type).await?;
    if !request_type.iter().all(|byte| (0x21..=0x7e).contains(byte)) {
        return Err(ScaffoldingError::InvalidFrame(
            "request type must be printable ASCII without spaces".into(),
        ));
    }

    let body_length = reader.read_u32().await? as usize;
    if body_length > MAX_BODY_LENGTH {
        return Err(ScaffoldingError::InvalidFrame(format!(
            "request body length {body_length} exceeds {MAX_BODY_LENGTH}"
        )));
    }
    let mut body = vec![0; body_length];
    reader.read_exact(&mut body).await?;

    Ok(RequestFrame {
        request_type: String::from_utf8(request_type).map_err(|_| {
            ScaffoldingError::InvalidFrame("request type is not valid UTF-8".into())
        })?,
        body,
    })
}

pub async fn write_request<W>(
    writer: &mut W,
    request_type: &str,
    body: &[u8],
) -> Result<(), ScaffoldingError>
where
    W: AsyncWrite + Unpin,
{
    validate_request_type(request_type)?;
    validate_body_length(body.len())?;
    writer.write_u8(request_type.len() as u8).await?;
    writer.write_all(request_type.as_bytes()).await?;
    writer.write_u32(body.len() as u32).await?;
    writer.write_all(body).await?;
    writer.flush().await?;
    Ok(())
}

pub async fn read_response<R>(reader: &mut R) -> Result<ResponseFrame, ScaffoldingError>
where
    R: AsyncRead + Unpin,
{
    let status = reader.read_u8().await?;
    let body_length = reader.read_u32().await? as usize;
    if body_length > MAX_BODY_LENGTH {
        return Err(ScaffoldingError::InvalidFrame(format!(
            "response body length {body_length} exceeds {MAX_BODY_LENGTH}"
        )));
    }
    let mut body = vec![0; body_length];
    reader.read_exact(&mut body).await?;
    Ok(ResponseFrame { status, body })
}

pub async fn write_response<W>(
    writer: &mut W,
    status: u8,
    body: &[u8],
) -> Result<(), ScaffoldingError>
where
    W: AsyncWrite + Unpin,
{
    validate_body_length(body.len())?;
    writer.write_u8(status).await?;
    writer.write_u32(body.len() as u32).await?;
    writer.write_all(body).await?;
    writer.flush().await?;
    Ok(())
}

fn validate_request_type(request_type: &str) -> Result<(), ScaffoldingError> {
    if request_type.is_empty()
        || request_type.len() > MAX_TYPE_LENGTH
        || !request_type
            .bytes()
            .all(|byte| (0x21..=0x7e).contains(&byte))
    {
        return Err(ScaffoldingError::InvalidFrame(
            "request type must contain 1 to 128 printable ASCII bytes without spaces".into(),
        ));
    }
    Ok(())
}

fn validate_body_length(length: usize) -> Result<(), ScaffoldingError> {
    if length > MAX_BODY_LENGTH {
        return Err(ScaffoldingError::InvalidFrame(format!(
            "body length {length} exceeds {MAX_BODY_LENGTH}"
        )));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{
        MAX_BODY_LENGTH, ScaffoldingError, read_request, read_response, write_request,
        write_response,
    };

    #[tokio::test]
    async fn request_and_response_round_trip_use_network_byte_order() {
        let (mut client, mut server) = tokio::io::duplex(1024);
        let server_task = tokio::spawn(async move {
            let request = read_request(&mut server).await.unwrap();
            assert_eq!(request.request_type, "c:ping");
            assert_eq!(request.body, b"hello");
            write_response(&mut server, 0, &request.body).await.unwrap();
        });

        write_request(&mut client, "c:ping", b"hello")
            .await
            .unwrap();
        let response = read_response(&mut client).await.unwrap();
        assert_eq!(response.status, 0);
        assert_eq!(response.body, b"hello");
        server_task.await.unwrap();
    }

    #[tokio::test]
    async fn writer_rejects_oversized_body_before_io() {
        let mut sink = tokio::io::sink();
        let error = write_request(&mut sink, "c:ping", &vec![0; MAX_BODY_LENGTH + 1])
            .await
            .unwrap_err();
        assert!(matches!(error, ScaffoldingError::InvalidFrame(_)));
    }
}
