//! Versioned editor RPC over loopback. Remote deployments must tunnel this endpoint.

use std::{
    io::{self, BufRead, Read, Write},
    net::SocketAddr,
    path::PathBuf,
    time::Duration,
};

use serde::{Deserialize, Serialize};
use serde_json::json;
use tokio::{io::BufReader, net::TcpStream};

use super::{
    access::{CallOrigin, credential_valid},
    server::{Bridge, failure},
    wire,
};
use crate::mcp::{CallToolResult, Map, Value};

pub const EDITOR_PROTOCOL: u32 = 1;

/// Optional editor listener configuration. Both endpoints remain loopback-only.
#[derive(Clone, Debug)]
pub struct EditorEndpoint {
    pub address: SocketAddr,
    /// A private file containing exactly one 64-hex-character token (plus whitespace).
    /// Reread on every request: removing or rotating it revokes existing connections.
    pub token_file: PathBuf,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Hello {
    protocol: u32,
    token: String,
}

/// Editor requests cannot supply a call origin or impersonate local MCP.
/// Credentials deliberately have no Debug implementation.
#[derive(Serialize, Deserialize)]
#[serde(tag = "method", rename_all = "snake_case", deny_unknown_fields)]
pub enum EditorRequest {
    List,
    Attach {
        instance_id: String,
        api_version: String,
        credential: String,
    },
    Catalog,
    Call {
        tool_name: String,
        arguments: Map<String, Value>,
    },
    Detach,
}

fn token_matches(endpoint: &EditorEndpoint, token: &str) -> bool {
    let read = || -> io::Result<String> {
        let mut value = String::new();
        std::fs::File::open(&endpoint.token_file)?
            .take(130)
            .read_to_string(&mut value)?;
        Ok(value)
    };
    credential_valid(token) && read().is_ok_and(|value| value.trim() == token)
}

fn request(name: &str, arguments: Value) -> rmcp::model::CallToolRequestParams {
    let mut request = rmcp::model::CallToolRequestParams::new(name.to_owned());
    request.arguments = arguments.as_object().cloned();
    request
}

pub(super) async fn serve_editor(
    stream: TcpStream,
    bridge: Bridge,
    endpoint: EditorEndpoint,
    session: String,
) -> io::Result<()> {
    let (reader, mut writer) = stream.into_split();
    let mut reader = BufReader::new(reader);
    let hello: Hello = tokio::time::timeout(wire::IO_TIMEOUT, wire::read_json(&mut reader))
        .await
        .map_err(io::Error::other)??;
    if hello.protocol != EDITOR_PROTOCOL || !token_matches(&endpoint, &hello.token) {
        wire::write_json(
            &mut writer,
            &failure(
                "authentication_failed",
                "editor protocol or credential rejected",
            ),
        )
        .await?;
        return Ok(());
    }
    wire::write_json(
        &mut writer,
        &CallToolResult::structured(json!({"protocol":EDITOR_PROTOCOL,"session_id":session})),
    )
    .await?;
    let mut attached: Option<(String, CallOrigin)> = None;
    loop {
        let call: EditorRequest =
            tokio::time::timeout(Duration::from_secs(120), wire::read_json(&mut reader))
                .await
                .map_err(io::Error::other)??;
        if !token_matches(&endpoint, &hello.token) {
            wire::write_json(
                &mut writer,
                &failure("access_revoked", "editor endpoint access revoked"),
            )
            .await?;
            return Ok(());
        }
        let result = match call {
            EditorRequest::List => bridge.invoke(request("list_instances", json!({}))).await,
            EditorRequest::Attach {
                instance_id,
                api_version,
                credential,
            } => {
                // A failed reattach clears the previous target, avoiding accidental calls to it.
                attached = None;
                let compatible = bridge.compatible(&instance_id, &api_version);
                if !compatible {
                    failure(
                        "incompatible_instance",
                        "instance is unavailable or API version differs",
                    )
                } else {
                    let origin = CallOrigin::Editor {
                        session: session.clone(),
                        credential,
                    };
                    let status = bridge.invoke_as(request("call_game_tool", json!({"instance_id":instance_id,"tool_name":"status","arguments":{}})), origin.clone()).await;
                    if status.is_error != Some(true) {
                        attached = Some((instance_id.clone(), origin));
                        CallToolResult::structured(
                            json!({"attached":true,"instance_id":instance_id,"api_version":api_version,"host":status.structured_content,"debug_access":status.meta,"identity":bridge.identity(&instance_id)}),
                        )
                    } else {
                        status
                    }
                }
            }
            EditorRequest::Detach => {
                attached = None;
                CallToolResult::structured(json!({"attached":false,"stopped":false}))
            }
            EditorRequest::Catalog => {
                if let Some((id, origin)) = &attached {
                    let admission = bridge
                        .invoke_as(
                            request(
                                "call_game_tool",
                                json!({"instance_id":id,"tool_name":"status","arguments":{}}),
                            ),
                            origin.clone(),
                        )
                        .await;
                    if admission.is_error == Some(true) {
                        admission
                    } else {
                        bridge
                            .invoke(request("list_game_tools", json!({"instance_id":id})))
                            .await
                    }
                } else {
                    failure("not_attached", "attach to an instance first")
                }
            }
            EditorRequest::Call {
                tool_name,
                arguments,
            } => {
                if let Some((id, origin)) = &attached {
                    bridge.invoke_as(request("call_game_tool", json!({"instance_id":id,"tool_name":tool_name,"arguments":arguments})), origin.clone()).await
                } else {
                    failure("not_attached", "attach to an instance first")
                }
            }
        };
        if serde_json::to_vec(&result).map_or(true, |bytes| bytes.len() + 1 > wire::MAX_FRAME) {
            wire::write_json(
                &mut writer,
                &failure(
                    "result_too_large",
                    "result exceeds editor frame limit; use bounded game queries",
                ),
            )
            .await?;
        } else {
            wire::write_json(&mut writer, &result).await?;
        }
    }
}

/// Blocking connection for an editor-owned worker. Never call from the UI thread.
/// A transport failure closes the stream; callers must explicitly reconnect/attach.
/// Requests are never replayed after timeout. Drop/detach never sends host stop.
pub struct EditorConnection {
    stream: std::io::BufReader<std::net::TcpStream>,
    failed: bool,
}

impl EditorConnection {
    pub fn connect(address: SocketAddr, token: String) -> io::Result<Self> {
        super::check_address(address)?;
        if !credential_valid(&token) {
            return Err(io::Error::other("invalid editor credential"));
        }
        let stream = std::net::TcpStream::connect_timeout(&address, wire::IO_TIMEOUT)?;
        stream.set_read_timeout(Some(wire::CALL_TIMEOUT + wire::IO_TIMEOUT))?;
        stream.set_write_timeout(Some(wire::IO_TIMEOUT))?;
        let mut connection = Self {
            stream: std::io::BufReader::new(stream),
            failed: false,
        };
        let result = connection.exchange(&Hello {
            protocol: EDITOR_PROTOCOL,
            token,
        })?;
        if result.is_error == Some(true) {
            return Err(io::Error::other("editor authentication failed"));
        }
        Ok(connection)
    }

    pub fn request(&mut self, request: &EditorRequest) -> io::Result<CallToolResult> {
        self.exchange(request)
    }

    fn exchange(&mut self, request: &impl Serialize) -> io::Result<CallToolResult> {
        if self.failed {
            return Err(io::Error::other(
                "editor connection is closed; execution outcome may be unknown",
            ));
        }
        let result = self.exchange_inner(request);
        if result.is_err() {
            self.failed = true;
            let _ = self.stream.get_ref().shutdown(std::net::Shutdown::Both);
        }
        result
    }

    fn exchange_inner(&mut self, request: &impl Serialize) -> io::Result<CallToolResult> {
        let mut bytes =
            serde_json::to_vec(request).map_err(|_| io::Error::other("invalid editor request"))?;
        bytes.push(b'\n');
        if bytes.len() > wire::MAX_FRAME {
            return Err(io::Error::other("editor request too large"));
        }
        self.stream.get_mut().write_all(&bytes)?;
        let mut response = Vec::new();
        (&mut self.stream)
            .take(wire::MAX_FRAME as u64 + 1)
            .read_until(b'\n', &mut response)?;
        if response.len() > wire::MAX_FRAME || response.last() != Some(&b'\n') {
            return Err(io::Error::other("invalid or oversized editor response"));
        }
        serde_json::from_slice(&response).map_err(|_| io::Error::other("invalid editor response"))
    }
}
