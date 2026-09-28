//! Host-owned admission policy for local MCP operations.

use std::collections::BTreeSet;

use crate::mcp::ToolAccess;

/// Native-host debug access. Development hosts allow every tool access class.
/// Released hosts allow inspection only.
#[derive(Clone, Debug)]
pub struct DebugAccess {
    local: BTreeSet<ToolAccess>,
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
                BTreeSet::from([ToolAccess::Inspect])
            },
        }
    }

    pub(super) fn permissions(&self) -> BTreeSet<ToolAccess> {
        self.local.clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn release_local_permissions_do_not_grant_stop_mutation_or_capture() {
        let release = DebugAccess::for_build(false).permissions();
        let development = DebugAccess::for_build(true).permissions();
        assert!(release.contains(&ToolAccess::Inspect));
        for access in [ToolAccess::Capture, ToolAccess::Mutate, ToolAccess::Stop] {
            assert!(!release.contains(&access));
            assert!(development.contains(&access));
        }
    }
}
