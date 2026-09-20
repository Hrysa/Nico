//! Process and loaded-build identity, independent of bridge connection lifetime.

use std::sync::OnceLock;

/// Random process-lifetime identity. It survives transport reconnects, not restart.
pub fn process_session_id() -> &'static str {
    static SESSION: OnceLock<String> = OnceLock::new();
    SESSION.get_or_init(|| uuid::Uuid::new_v4().to_string())
}

#[cfg(feature = "bridge")]
#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DebugIdentity {
    pub process_session: String,
    /// SHA-256 of the executable file opened at first debug startup, cached thereafter.
    pub build_id: String,
    /// Game-supplied revision of the content actually loaded. None means unknown;
    /// paths or build equality must not be used to infer content equivalence.
    pub content_revision: Option<String>,
}

#[cfg(feature = "bridge")]
impl DebugIdentity {
    pub fn capture(content_revision: Option<String>) -> std::io::Result<Self> {
        use sha2::{Digest, Sha256};
        use std::io::Read;
        static BUILD: OnceLock<Result<String, String>> = OnceLock::new();
        let build_id = BUILD
            .get_or_init(|| {
                let hash = || -> std::io::Result<String> {
                    let mut file = std::fs::File::open(std::env::current_exe()?)?;
                    let mut digest = Sha256::new();
                    let mut bytes = [0_u8; 65536];
                    loop {
                        let count = file.read(&mut bytes)?;
                        if count == 0 {
                            break;
                        }
                        digest.update(&bytes[..count]);
                    }
                    Ok(format!("{:x}", digest.finalize()))
                };
                hash().map_err(|_| "cannot fingerprint debug executable".to_owned())
            })
            .clone()
            .map_err(std::io::Error::other)?;
        let identity = Self {
            process_session: process_session_id().into(),
            build_id,
            content_revision,
        };
        if !identity.valid() {
            return Err(std::io::Error::other("invalid debug identity"));
        }
        Ok(identity)
    }

    pub(crate) fn valid(&self) -> bool {
        uuid::Uuid::parse_str(&self.process_session).is_ok()
            && self.build_id.len() == 64
            && self.build_id.bytes().all(|byte| byte.is_ascii_hexdigit())
            && self.content_revision.as_ref().is_none_or(|value| {
                !value.is_empty()
                    && value.len() <= 128
                    && value.bytes().all(|byte| byte.is_ascii_graphic())
            })
    }
}

#[cfg(all(test, feature = "bridge"))]
mod tests {
    use super::*;
    #[test]
    fn repeated_capture_preserves_process_and_build_but_never_infers_content() {
        let first = DebugIdentity::capture(None).unwrap();
        let second = DebugIdentity::capture(Some("loaded-content-digest".into())).unwrap();
        assert_eq!(first.process_session, second.process_session);
        assert_eq!(first.build_id, second.build_id);
        assert!(first.content_revision.is_none());
        assert_eq!(
            second.content_revision.as_deref(),
            Some("loaded-content-digest")
        );
        assert!(first.valid());
        assert!(DebugIdentity::capture(Some("".into())).is_err());
    }
}
