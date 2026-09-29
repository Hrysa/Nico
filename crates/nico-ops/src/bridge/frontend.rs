//! Per-client stdio sessions. Only initialization and future calls reconnect; calls are never replayed.
use std::{sync::Arc, time::Duration};

use rmcp::{
    ClientHandler, ErrorData, Peer, RoleClient, RoleServer, ServerHandler, ServiceExt,
    model::{
        CallToolRequestParams, CallToolResponse, ListToolsResult, PaginatedRequestParams,
        ServerInfo,
    },
    service::{NotificationContext, RequestContext, RunningService},
};
use tokio::sync::{Mutex, watch};

use super::{
    daemon::{self, DaemonConfig},
    server::Bridge,
};

#[derive(Clone)]
struct Notifications(watch::Sender<u64>);
impl ClientHandler for Notifications {
    async fn on_tool_list_changed(&self, _: NotificationContext<RoleClient>) {
        self.0
            .send_modify(|revision| *revision = revision.wrapping_add(1));
    }
}

struct Connection {
    config: DaemonConfig,
    changes: watch::Sender<u64>,
    service: Mutex<Option<RunningService<RoleClient, Notifications>>>,
}

impl Connection {
    async fn peer(&self) -> Result<Peer<RoleClient>, ErrorData> {
        let mut slot = self.service.lock().await;
        if let Some(service) = slot.as_ref()
            && !service.is_closed()
            && !service.peer().is_transport_closed()
        {
            return Ok(service.peer().clone());
        }
        slot.take();
        let stream = daemon::ensure(&self.config).await.map_err(unavailable)?;
        let (read, write) = stream.into_split();
        let service = tokio::time::timeout(
            Duration::from_secs(3),
            Notifications(self.changes.clone()).serve((read, write)),
        )
        .await
        .map_err(unavailable)?
        .map_err(unavailable)?;
        let peer = service.peer().clone();
        *slot = Some(service);
        self.changes
            .send_modify(|revision| *revision = revision.wrapping_add(1));
        Ok(peer)
    }
}

fn unavailable(error: impl std::fmt::Display) -> ErrorData {
    ErrorData::internal_error(format!("bridge unavailable before dispatch: {error}"), None)
}

fn uncertain(error: impl std::fmt::Display) -> ErrorData {
    ErrorData::internal_error(
        format!(
            "bridge call failed: {error}; execution outcome may be unknown; do not automatically retry mutations"
        ),
        Some(serde_json::json!({"code":"bridge_outcome_unknown","retry_safe":false})),
    )
}

#[derive(Clone)]
struct Frontend(Arc<Connection>);
impl ServerHandler for Frontend {
    fn get_info(&self) -> ServerInfo {
        Bridge::new().get_info()
    }

    async fn on_initialized(&self, context: NotificationContext<RoleServer>) {
        let mut changes = self.0.changes.subscribe();
        tokio::spawn(async move {
            let mut check = tokio::time::interval(Duration::from_secs(1));
            loop {
                tokio::select! {
                    changed = changes.changed() => {
                        if changed.is_err() || context.peer.notify_tool_list_changed().await.is_err() { break; }
                    }
                    _ = check.tick() => { if context.peer.is_transport_closed() { break; } }
                }
            }
        });
    }

    async fn list_tools(
        &self,
        params: Option<PaginatedRequestParams>,
        _: RequestContext<RoleServer>,
    ) -> Result<ListToolsResult, ErrorData> {
        self.0
            .peer()
            .await?
            .list_tools(params)
            .await
            .map_err(uncertain)
    }

    async fn call_tool(
        &self,
        params: CallToolRequestParams,
        context: RequestContext<RoleServer>,
    ) -> Result<CallToolResponse, ErrorData> {
        let peer = self.0.peer().await?;
        tokio::select! {
            result = peer.call_tool_once(params) => result.map_err(uncertain),
            _ = context.ct.cancelled() => Err(uncertain("caller cancelled the request")),
        }
    }
}

/// Serve one MCP client over stdio, sharing a daemon with other frontends.
/// Ending this session leaves the daemon and connected games running.
/// The current executable must support the daemon arguments used by `nico-mcp-bridge`.
pub fn serve_frontend(
    config: DaemonConfig,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    let result = runtime.block_on(async {
        let connection = Arc::new(Connection {
            config,
            changes: watch::channel(0).0,
            service: Mutex::new(None),
        });
        // Fail startup clearly when an old bridge or another service owns the game port.
        connection.peer().await?;
        let observed = connection.clone();
        let reconnect = tokio::spawn(async move {
            loop {
                tokio::time::sleep(Duration::from_secs(1)).await;
                if let Err(error) = observed.peer().await {
                    eprintln!("Nico daemon reconnect: {error}");
                }
            }
        });
        let result = async {
            let service = Frontend(connection).serve(rmcp::transport::stdio()).await?;
            service.waiting().await?;
            Ok::<_, Box<dyn std::error::Error + Send + Sync>>(())
        }
        .await;
        reconnect.abort();
        result
    });
    runtime.shutdown_background();
    result
}
