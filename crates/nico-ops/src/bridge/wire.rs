use std::{io, time::Duration};

use serde::{Deserialize, Serialize};
use tokio::io::{AsyncBufRead, AsyncBufReadExt, AsyncWrite, AsyncWriteExt};

use crate::{
    HostStatus,
    mcp::{CallToolResult, Map, Tool, Value},
};

pub(super) const VERSION: u32 = 1;
pub(super) const MAX_FRAME: usize = 256 * 1024;
pub(super) const MAX_TOOLS: usize = 64;
pub(super) const QUEUE: usize = 32;
pub(super) const IO_TIMEOUT: Duration = Duration::from_secs(2);
pub(super) const CALL_TIMEOUT: Duration = Duration::from_secs(5);
pub(super) const STALE_TIMEOUT: Duration = Duration::from_secs(4);

/// Role of an independently launched game host.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum GameRole {
    Client,
    Server,
}

impl GameRole {
    pub(super) fn name(self) -> &'static str {
        match self {
            Self::Client => "client",
            Self::Server => "server",
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub(super) enum Message {
    Register {
        protocol: u32,
        game: String,
        role: GameRole,
        api_version: String,
        pid: u32,
        tools: Vec<Tool>,
        status: HostStatus,
    },
    Registered {
        instance_id: String,
    },
    Rejected {
        reason: String,
    },
    Snapshot {
        status: HostStatus,
    },
    Ping,
    Call {
        id: u64,
        name: String,
        arguments: Map<String, Value>,
    },
    Reply {
        id: u64,
        result: CallToolResult,
    },
}

pub(super) fn identifier(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 48
        && value
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || c == b'_' || c == b'-')
}

pub(super) async fn read_message<R: AsyncBufRead + Unpin>(reader: &mut R) -> io::Result<Message> {
    let mut line = Vec::new();
    loop {
        let buffer = reader.fill_buf().await?;
        if buffer.is_empty() {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "bridge disconnected",
            ));
        }
        let end = buffer.iter().position(|byte| *byte == b'\n');
        let count = end.map_or(buffer.len(), |index| index + 1);
        if line.len() + count > MAX_FRAME {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "bridge message too large",
            ));
        }
        line.extend_from_slice(&buffer[..count]);
        reader.consume(count);
        if end.is_some() {
            return serde_json::from_slice(&line).map_err(io::Error::other);
        }
    }
}

pub(super) async fn write_message<W: AsyncWrite + Unpin>(
    writer: &mut W,
    message: &Message,
) -> io::Result<()> {
    let mut bytes = serde_json::to_vec(message).map_err(io::Error::other)?;
    bytes.push(b'\n');
    if bytes.len() > MAX_FRAME {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "bridge message too large",
        ));
    }
    tokio::time::timeout(IO_TIMEOUT, writer.write_all(&bytes))
        .await
        .map_err(io::Error::other)?
}

// One dedicated reader retains partial frames across actor select! wakeups.
pub(super) struct ReaderTask(tokio::task::JoinHandle<()>);
impl ReaderTask {
    pub(super) fn abort(&self) {
        self.0.abort();
    }
}
impl Drop for ReaderTask {
    fn drop(&mut self) {
        self.0.abort();
    }
}

pub(super) fn reader_task<R: AsyncBufRead + Unpin + Send + 'static>(
    mut reader: R,
) -> (tokio::sync::mpsc::Receiver<io::Result<Message>>, ReaderTask) {
    let (sender, receiver) = tokio::sync::mpsc::channel(QUEUE);
    let task = tokio::spawn(async move {
        loop {
            let message = read_message(&mut reader).await;
            let failed = message.is_err();
            if sender.send(message).await.is_err() || failed {
                break;
            }
        }
    });
    (receiver, ReaderTask(task))
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::BufReader;

    #[tokio::test]
    async fn partial_frames_round_trip_and_malformed_or_oversized_input_is_rejected() {
        let (mut writer, reader) = tokio::io::duplex(8);
        let task = tokio::spawn(async move {
            write_message(&mut writer, &Message::Ping).await.unwrap();
        });
        assert!(matches!(
            read_message(&mut BufReader::new(reader)).await.unwrap(),
            Message::Ping
        ));
        task.await.unwrap();
        for bytes in [
            b"bad json\n".to_vec(),
            vec![b'x'; MAX_FRAME + 1],
            b"{\"type\":\"ping\"".to_vec(),
        ] {
            assert!(read_message(&mut bytes.as_slice()).await.is_err());
        }
    }
}
