//! These implementations intentionally live outside the library's private modules.
use nico_assets::{
    AssetError, AssetId, Texture,
    import::{
        AssetImporter, ImportBudget, ImportContext, ImportError, ImportErrorKind, ImportRegistry,
        ImporterDescriptor,
    },
};

fn id(value: u128) -> AssetId {
    AssetId::from_u128(value)
}

struct PixelImporter(&'static str);
impl AssetImporter for PixelImporter {
    type Output = Texture;
    type Settings = u8;
    fn descriptor(&self) -> ImporterDescriptor {
        // Deliberately overlapping extensions: only explicit selection is used.
        ImporterDescriptor {
            id: self.0,
            version: "1",
            extensions: &["dat"],
        }
    }
    fn validate_settings(&self, alpha: &u8) -> Result<(), ImportError> {
        if *alpha == 0 {
            return Err(ImportError::new(
                ImportErrorKind::InvalidSettings,
                "alpha",
                "test alpha must be nonzero",
            ));
        }
        Ok(())
    }
    fn import(&self, context: &mut ImportContext<'_>, alpha: &u8) -> Result<Texture, ImportError> {
        let color = *context.bytes().first().ok_or_else(|| {
            ImportError::new(ImportErrorKind::Malformed, "empty_pixel", "missing pixel")
                .at("byte 0")
        })?;
        context.claim_decoded(4)?;
        Ok(Texture::rgba8(1, 1, vec![color, 0, 0, *alpha]).unwrap())
    }
}

#[test]
fn external_importers_share_an_engine_output_type_and_keep_per_asset_settings() {
    let mut registry = ImportRegistry::new();
    let first = registry.register(PixelImporter("game.pixel")).unwrap();
    let second = registry
        .register(PixelImporter("game.other_pixel"))
        .unwrap();
    registry
        .asset(id(1), "same.dat", &first, 100, ImportBudget::default())
        .unwrap();
    registry
        .asset(id(2), "same.dat", &second, 200, ImportBudget::default())
        .unwrap();
    assert_eq!(
        registry
            .import_bytes(id(1), &[42], &|| false)
            .unwrap()
            .pixels(),
        &[42, 0, 0, 100]
    );
    assert_eq!(
        registry
            .import_bytes(id(2), &[42], &|| false)
            .unwrap()
            .pixels(),
        &[42, 0, 0, 200]
    );
    assert_eq!(registry.source(id(1)).unwrap().importer.id, "game.pixel");
    assert_eq!(registry.importers().count(), 2);
    #[cfg(feature = "png-import")]
    {
        let png = registry
            .register(nico_assets::importers::PngImporter)
            .unwrap();
        registry
            .asset(
                id(3),
                "sample.png",
                &png,
                Default::default(),
                Default::default(),
            )
            .unwrap();
        let bytes =
            include_bytes!("../../../games/minimal-game/assets/presentation/textures/sample.png");
        assert_eq!(
            registry
                .import_bytes(id(3), bytes, &|| false)
                .unwrap()
                .width(),
            2
        );
    }
}

#[test]
fn invalid_registration_and_configuration_do_not_replace_valid_entries() {
    let mut registry = ImportRegistry::new();
    let token = registry.register(PixelImporter("game.pixel")).unwrap();
    assert_eq!(
        registry
            .register(PixelImporter("game.pixel"))
            .err()
            .unwrap()
            .code(),
        "duplicate_importer"
    );
    assert!(registry.register(PixelImporter("")).is_err());
    let mut other = ImportRegistry::new();
    assert_eq!(
        other
            .asset(id(1), "a.dat", &token, 1, Default::default())
            .unwrap_err()
            .code(),
        "foreign_importer"
    );
    for path in ["", "../escape", "/absolute", "a/../b"] {
        assert_eq!(
            registry
                .asset(id(1), path, &token, 1, Default::default())
                .unwrap_err()
                .code(),
            "invalid_source"
        );
    }
    assert!(
        registry
            .asset(id(1), "a.dat", &token, 0, Default::default())
            .is_err()
    );
    assert!(
        registry
            .asset(
                id(1),
                "a.dat",
                &token,
                1,
                ImportBudget {
                    max_input_bytes: 0,
                    ..Default::default()
                }
            )
            .is_err()
    );
    registry
        .asset(id(1), "a.dat", &token, 1, Default::default())
        .unwrap();
    assert_eq!(
        registry
            .asset(id(1), "b.dat", &token, 2, Default::default())
            .unwrap_err()
            .code(),
        "duplicate_asset"
    );
    assert_eq!(
        registry
            .import_bytes(id(1), &[3], &|| false)
            .unwrap()
            .pixels(),
        &[3, 0, 0, 1]
    );
}

fn import_failure(error: AssetError, kind: ImportErrorKind, code: &str) {
    match error {
        AssetError::Import { importer, error } => {
            assert_eq!(importer.id, "game.pixel");
            assert_eq!(error.kind(), kind);
            assert_eq!(error.code(), code);
        }
        other => panic!("unexpected error: {other}"),
    }
}

#[test]
fn source_and_output_budgets_cancellation_and_errors_are_structured() {
    let mut registry = ImportRegistry::new();
    let token = registry.register(PixelImporter("game.pixel")).unwrap();
    registry
        .asset(
            id(1),
            "a.dat",
            &token,
            1,
            ImportBudget {
                max_input_bytes: 1,
                max_decoded_bytes: 3,
            },
        )
        .unwrap();
    import_failure(
        registry
            .import_bytes(id(1), &[1, 2], &|| false)
            .unwrap_err(),
        ImportErrorKind::LimitExceeded,
        "import_budget",
    );
    import_failure(
        registry.import_bytes(id(1), &[1], &|| false).unwrap_err(),
        ImportErrorKind::LimitExceeded,
        "import_budget",
    );
    import_failure(
        registry.import_bytes(id(1), &[1], &|| true).unwrap_err(),
        ImportErrorKind::Cancelled,
        "cancelled",
    );
    import_failure(
        registry.import_bytes(id(1), &[], &|| false).unwrap_err(),
        ImportErrorKind::Malformed,
        "empty_pixel",
    );
    let message = "骨".repeat(2000);
    let error = ImportError::new(ImportErrorKind::Malformed, &message, &message).at(&message);
    assert!(error.code().len() <= 128);
    assert!(error.message().len() <= 1024);
    assert!(error.location().unwrap().len() <= 1024);
    assert!(error.is_truncated());
}

#[cfg(feature = "gltf-import")]
#[test]
fn static_glb_importer_works_without_runtime() {
    let mut registry = ImportRegistry::new();
    let token = registry
        .register(nico_assets::importers::StaticGlbImporter)
        .unwrap();
    registry
        .asset(
            id(1),
            "cube.glb",
            &token,
            Default::default(),
            Default::default(),
        )
        .unwrap();
    let bytes = include_bytes!("../../../games/minimal-game/assets/presentation/meshes/cube.glb");
    let mesh = registry.import_bytes(id(1), bytes, &|| false).unwrap();
    assert_eq!(mesh.vertices().len(), 24);
}

#[cfg(feature = "runtime-loading")]
mod runtime {
    use super::*;
    use nico_assets::{
        Handle,
        loading::{AssetState, AssetStore, StoreLimits},
    };
    use nico_runtime::{App, AppBuilder};
    use std::{
        sync::{
            Arc,
            atomic::{AtomicBool, AtomicUsize, Ordering},
        },
        time::{Duration, Instant},
    };

    #[derive(Debug)]
    struct TextAsset {
        text: String,
        attempt: usize,
    }
    struct TextImporter {
        calls: Arc<AtomicUsize>,
        fail_first: bool,
        gate_first: bool,
        finished: Arc<AtomicBool>,
        panic: bool,
    }
    impl AssetImporter for TextImporter {
        type Output = TextAsset;
        type Settings = String;
        fn descriptor(&self) -> ImporterDescriptor {
            ImporterDescriptor {
                id: "game.text",
                version: "2",
                extensions: &["toml"],
            }
        }
        fn import(
            &self,
            context: &mut ImportContext<'_>,
            prefix: &String,
        ) -> Result<TextAsset, ImportError> {
            let attempt = self.calls.fetch_add(1, Ordering::SeqCst) + 1;
            assert!(!self.panic, "intentional external importer panic");
            if self.gate_first && attempt == 1 {
                let deadline = Instant::now() + Duration::from_secs(5);
                while Instant::now() < deadline {
                    if let Err(error) = context.check_cancelled() {
                        self.finished.store(true, Ordering::SeqCst);
                        return Err(error);
                    }
                    std::thread::sleep(Duration::from_millis(1));
                }
                panic!("test did not cancel gated importer");
            }
            if self.fail_first && attempt == 1 {
                return Err(ImportError::new(
                    ImportErrorKind::Malformed,
                    "first_attempt",
                    "retry explicitly",
                ));
            }
            context.claim_decoded(prefix.len() + context.bytes().len())?;
            Ok(TextAsset {
                text: format!("{prefix}{}", String::from_utf8_lossy(context.bytes())),
                attempt,
            })
        }
    }
    fn importer() -> TextImporter {
        TextImporter {
            calls: Arc::new(AtomicUsize::new(0)),
            fail_first: false,
            gate_first: false,
            finished: Arc::new(AtomicBool::new(false)),
            panic: false,
        }
    }
    fn registry(importer: TextImporter) -> ImportRegistry<TextAsset> {
        let mut registry = ImportRegistry::new();
        let token = registry.register(importer).unwrap();
        for value in [1, 2] {
            registry
                .asset(
                    id(value),
                    "Cargo.toml",
                    &token,
                    format!("asset{value}:"),
                    Default::default(),
                )
                .unwrap();
        }
        registry
    }
    fn handle(value: u128) -> Handle<TextAsset> {
        Handle::new(id(value))
    }
    fn app(registry: ImportRegistry<TextAsset>) -> App {
        let mut builder = AppBuilder::new().with_event_capacity(1);
        AssetStore::install_with_importers(
            &mut builder,
            env!("CARGO_MANIFEST_DIR"),
            registry,
            StoreLimits::default(),
        )
        .unwrap();
        let mut app = builder.build().unwrap();
        app.start().unwrap();
        app
    }
    fn until(app: &mut App, predicate: impl Fn(&App) -> bool) {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            app.tick(Duration::ZERO).unwrap();
            if predicate(app) {
                return;
            }
            assert!(Instant::now() < deadline, "import timed out");
            std::thread::sleep(Duration::from_millis(1));
        }
    }
    fn state(app: &App, value: u128) -> &AssetState<TextAsset> {
        app.world()
            .resource::<AssetStore<TextAsset>>()
            .unwrap()
            .state(handle(value))
            .unwrap()
    }

    #[test]
    fn custom_asset_failure_retry_shared_ownership_and_rejected_installation() {
        let mut importer = importer();
        importer.fail_first = true;
        let calls = importer.calls.clone();
        let mut builder = AppBuilder::new();
        assert!(
            AssetStore::<TextAsset>::install_with_importers(
                &mut builder,
                ".",
                ImportRegistry::new(),
                StoreLimits { max_assets: 0 }
            )
            .is_err()
        );
        AssetStore::install_with_importers(
            &mut builder,
            env!("CARGO_MANIFEST_DIR"),
            registry(importer),
            StoreLimits::default(),
        )
        .unwrap();
        assert!(
            AssetStore::<TextAsset>::install_with_importers(
                &mut builder,
                ".",
                ImportRegistry::new(),
                StoreLimits::default()
            )
            .is_err()
        );
        let mut app = builder.build().unwrap();
        app.start().unwrap();
        let store = app
            .world_mut()
            .resource_mut::<AssetStore<TextAsset>>()
            .unwrap();
        let lease = store.request(handle(1)).unwrap();
        let other = store.request(handle(1)).unwrap();
        until(&mut app, |a| matches!(state(a, 1), AssetState::Failed(_)));
        assert!(
            matches!(state(&app, 1), AssetState::Failed(AssetError::Import { error, .. }) if error.code() == "first_attempt")
        );
        app.world_mut()
            .resource_mut::<AssetStore<TextAsset>>()
            .unwrap()
            .retry(handle(1))
            .unwrap();
        until(&mut app, |a| matches!(state(a, 1), AssetState::Ready(_)));
        let AssetState::Ready(asset) = state(&app, 1) else {
            unreachable!()
        };
        let snapshot = asset.clone();
        assert!(snapshot.text.starts_with("asset1:[package]"));
        assert_eq!(calls.load(Ordering::SeqCst), 2);
        drop((lease, other));
        app.tick(Duration::ZERO).unwrap();
        let store = app.world().resource::<AssetStore<TextAsset>>().unwrap();
        assert!(store.state(handle(1)).is_none());
        assert_eq!(store.source(handle(1)).unwrap().importer.id, "game.text");
        app.shutdown().unwrap();
        assert_eq!(snapshot.attempt, 2);
    }

    #[test]
    fn cancelled_user_import_cannot_publish_into_reacquired_entry() {
        let mut importer = importer();
        importer.gate_first = true;
        let calls = importer.calls.clone();
        let finished = importer.finished.clone();
        let mut app = app(registry(importer));
        let lease = app
            .world_mut()
            .resource_mut::<AssetStore<TextAsset>>()
            .unwrap()
            .request(handle(1))
            .unwrap();
        until(&mut app, |_| calls.load(Ordering::SeqCst) == 1);
        drop(lease);
        app.tick(Duration::ZERO).unwrap();
        let _next = app
            .world_mut()
            .resource_mut::<AssetStore<TextAsset>>()
            .unwrap()
            .request(handle(1))
            .unwrap();
        until(&mut app, |a| matches!(state(a, 1), AssetState::Ready(_)));
        let AssetState::Ready(asset) = state(&app, 1) else {
            unreachable!()
        };
        assert_eq!(asset.attempt, 2);
        assert!(finished.load(Ordering::SeqCst));
        app.shutdown().unwrap();
    }

    #[test]
    fn shutdown_cancels_and_joins_user_importer() {
        let mut importer = importer();
        importer.gate_first = true;
        let calls = importer.calls.clone();
        let finished = importer.finished.clone();
        let mut app = app(registry(importer));
        let lease = app
            .world_mut()
            .resource_mut::<AssetStore<TextAsset>>()
            .unwrap()
            .request(handle(1))
            .unwrap();
        until(&mut app, |_| calls.load(Ordering::SeqCst) == 1);
        app.shutdown().unwrap();
        assert!(finished.load(Ordering::SeqCst));
        assert!(matches!(
            app.world_mut()
                .resource_mut::<AssetStore<TextAsset>>()
                .unwrap()
                .request(handle(1)),
            Err(AssetError::Closed)
        ));
        drop(lease);
    }

    #[test]
    fn importer_panic_fails_active_and_queued_assets_without_retry() {
        let mut importer = importer();
        importer.panic = true;
        let calls = importer.calls.clone();
        let mut app = app(registry(importer));
        let store = app
            .world_mut()
            .resource_mut::<AssetStore<TextAsset>>()
            .unwrap();
        let _one = store.request(handle(1)).unwrap();
        let _two = store.request(handle(2)).unwrap();
        until(&mut app, |a| {
            [1, 2].into_iter().all(|id| {
                matches!(
                    state(a, id),
                    AssetState::Failed(AssetError::WorkerUnavailable)
                )
            })
        });
        assert_eq!(calls.load(Ordering::SeqCst), 1);
        app.shutdown().unwrap();
    }

    #[test]
    fn source_failures_preserve_provenance_and_never_invoke_importer() {
        let importer = importer();
        let calls = importer.calls.clone();
        let mut registry = ImportRegistry::new();
        let token = registry.register(importer).unwrap();
        registry
            .asset(
                id(1),
                "Cargo.toml",
                &token,
                String::new(),
                ImportBudget {
                    max_input_bytes: 1,
                    ..Default::default()
                },
            )
            .unwrap();
        registry
            .asset(
                id(2),
                "missing-source-for-import-test",
                &token,
                String::new(),
                Default::default(),
            )
            .unwrap();
        let mut app = app(registry);
        let store = app
            .world_mut()
            .resource_mut::<AssetStore<TextAsset>>()
            .unwrap();
        let _one = store.request(handle(1)).unwrap();
        let _two = store.request(handle(2)).unwrap();
        until(&mut app, |a| {
            [1, 2]
                .into_iter()
                .all(|id| matches!(state(a, id), AssetState::Failed(_)))
        });
        for (id, kind) in [
            (1, ImportErrorKind::LimitExceeded),
            (2, ImportErrorKind::Io),
        ] {
            assert!(
                matches!(state(&app, id), AssetState::Failed(AssetError::Import { importer, error })
                if importer.id == "game.text" && error.kind() == kind)
            );
        }
        assert_eq!(calls.load(Ordering::SeqCst), 0);
        app.shutdown().unwrap();
    }
}
