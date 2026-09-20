//! Host-owned admission policy. Revocation affects new calls, not already queued work.

use std::{
    collections::BTreeSet,
    fs::File,
    io::{self, Read},
    path::PathBuf,
};

use serde::{Deserialize, Serialize};

use crate::mcp::ToolAccess;

const MAX_POLICY_BYTES: u64 = 16 * 1024;
const MAX_GRANTS: usize = 32;

/// The bridge assigns editor session IDs; consumers cannot choose their origin.
/// Credentials are deliberately redacted from Debug output.
#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub(super) enum CallOrigin {
    #[default]
    Local,
    Editor {
        session: String,
        credential: String,
    },
}

impl std::fmt::Debug for CallOrigin {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Local => f.write_str("Local"),
            Self::Editor { .. } => f.write_str("Editor { credential: [redacted] }"),
        }
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Grant {
    credential: String,
    #[serde(default = "inspection")]
    permissions: BTreeSet<ToolAccess>,
}

fn inspection() -> BTreeSet<ToolAccess> {
    BTreeSet::from([ToolAccess::Inspect])
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Policy {
    grants: Vec<Grant>,
}

/// Native-host debug access. Local MCP remains fully enabled for development.
/// Released hosts use inspection-only local access unless explicitly configured.
/// Editor credentials come from a bounded host-local JSON file, never registration.
/// The file is reread at admission so removal/rotation revokes the next call without
/// cooperation from the editor or bridge. Missing/invalid files deny editor access.
#[derive(Clone, Debug)]
pub struct DebugAccess {
    local: BTreeSet<ToolAccess>,
    policy_file: Option<PathBuf>,
}

impl Default for DebugAccess {
    fn default() -> Self {
        Self::for_build(cfg!(debug_assertions))
    }
}

impl DebugAccess {
    pub fn for_build(development: bool) -> Self {
        Self {
            local: if development {
                BTreeSet::from([
                    ToolAccess::Inspect,
                    ToolAccess::Capture,
                    ToolAccess::Mutate,
                    ToolAccess::Stop,
                ])
            } else {
                inspection()
            },
            policy_file: None,
        }
    }

    /// Select a private deployment configuration file. It must not be committed.
    pub fn with_policy_file(mut self, path: PathBuf) -> Self {
        self.policy_file = Some(path);
        self
    }

    fn policy(&self) -> io::Result<Policy> {
        let path = self
            .policy_file
            .as_ref()
            .ok_or_else(|| io::Error::other("editor access disabled"))?;
        let mut bytes = Vec::new();
        File::open(path)?
            .take(MAX_POLICY_BYTES + 1)
            .read_to_end(&mut bytes)?;
        if bytes.len() as u64 > MAX_POLICY_BYTES {
            return Err(io::Error::other("debug policy too large"));
        }
        // Do not include serde's error: it can contain credential source text.
        let policy: Policy =
            serde_json::from_slice(&bytes).map_err(|_| io::Error::other("invalid debug policy"))?;
        if policy.grants.len() > MAX_GRANTS
            || policy
                .grants
                .iter()
                .any(|grant| !credential_valid(&grant.credential))
        {
            return Err(io::Error::other("invalid debug grants"));
        }
        let unique: BTreeSet<_> = policy
            .grants
            .iter()
            .map(|grant| &grant.credential)
            .collect();
        if unique.len() != policy.grants.len() {
            return Err(io::Error::other("duplicate debug grant"));
        }
        Ok(policy)
    }

    #[cfg(test)]
    pub(super) fn permits(&self, origin: &CallOrigin, access: ToolAccess) -> bool {
        self.permissions(origin).contains(&access)
    }

    pub(super) fn permissions(&self, origin: &CallOrigin) -> BTreeSet<ToolAccess> {
        match origin {
            CallOrigin::Local => self.local.clone(),
            CallOrigin::Editor {
                session,
                credential,
            } => {
                if session.is_empty() || session.len() > 128 || !credential_valid(credential) {
                    return BTreeSet::new();
                }
                self.policy()
                    .ok()
                    .and_then(|policy| {
                        policy
                            .grants
                            .into_iter()
                            .find(|grant| grant.credential == *credential)
                            .map(|grant| grant.permissions)
                    })
                    .unwrap_or_default()
            }
        }
    }
}

pub(super) fn credential_valid(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn release_local_permissions_do_not_grant_stop_mutation_or_capture() {
        let release = DebugAccess::for_build(false);
        assert!(release.permits(&CallOrigin::Local, ToolAccess::Inspect));
        for access in [ToolAccess::Capture, ToolAccess::Mutate, ToolAccess::Stop] {
            assert!(!release.permits(&CallOrigin::Local, access));
            assert!(DebugAccess::for_build(true).permits(&CallOrigin::Local, access));
        }
    }

    #[test]
    fn editor_policy_defaults_to_inspection_and_revokes_on_rotation_or_removal() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("access.json");
        let token = "a".repeat(64);
        let origin = CallOrigin::Editor {
            session: "connection-1".into(),
            credential: token.clone(),
        };
        let access = DebugAccess::for_build(true).with_policy_file(path.clone());
        assert!(!access.permits(&origin, ToolAccess::Inspect));
        std::fs::write(
            &path,
            serde_json::json!({"grants":[{"credential":token}]}).to_string(),
        )
        .unwrap();
        assert!(access.permits(&origin, ToolAccess::Inspect));
        assert!(!access.permits(&origin, ToolAccess::Stop));
        std::fs::write(
            &path,
            serde_json::json!({"grants":[{"credential":token,"permissions":["stop"]}]}).to_string(),
        )
        .unwrap();
        assert!(access.permits(&origin, ToolAccess::Stop));
        assert!(!access.permits(&origin, ToolAccess::Inspect));
        std::fs::write(
            &path,
            serde_json::json!({"grants":[{"credential":"b".repeat(64)}]}).to_string(),
        )
        .unwrap();
        assert!(!access.permits(&origin, ToolAccess::Stop));
        std::fs::write(&path, "malformed").unwrap();
        assert!(!access.permits(&origin, ToolAccess::Inspect));
        std::fs::remove_file(&path).unwrap();
        assert!(!access.permits(&origin, ToolAccess::Inspect));
        assert!(!format!("{origin:?}").contains(&token));
    }
}
