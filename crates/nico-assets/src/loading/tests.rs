use super::*;
use nico_runtime::App;

fn handle(id: u128) -> Handle<Texture> {
    Handle::new(AssetId::from_u128(id))
}

fn manual(capacity: usize) -> (App, Backend) {
    let (service, backend) = service_channel(1).unwrap();
    let store = TextureStore {
        catalog: (1..=3)
            .map(|id| (AssetId::from_u128(id), PathBuf::from("unused.png")))
            .collect(),
        entries: BTreeMap::new(),
        limits: TextureLimits {
            max_assets: capacity,
            ..TextureLimits::default()
        },
        next_generation: 0,
        active: None,
        service,
        worker: None,
        closed: false,
    };
    let mut builder = AppBuilder::new().with_event_capacity(1);
    TextureStore::attach(&mut builder, store).unwrap();
    let mut app = builder.build().unwrap();
    app.start().unwrap();
    (app, backend)
}

fn request(app: &mut App, id: u128) -> AssetLease<Texture> {
    app.world_mut()
        .resource_mut::<TextureStore>()
        .unwrap()
        .request(handle(id))
        .unwrap()
}
fn tick(app: &mut App) {
    app.tick(Duration::ZERO).unwrap();
}
fn complete(backend: &Backend, result: LoadResult) {
    let (context, request) = backend.try_next().unwrap().into_parts();
    *request.result.lock().unwrap() = Some(result);
    backend
        .complete(context, Ok(LoadCompletion::new()))
        .unwrap();
}
fn pixel() -> Arc<Texture> {
    Arc::new(Texture {
        width: 1,
        height: 1,
        pixels: vec![10, 20, 30, 255],
    })
}

#[test]
fn shared_requests_publish_at_update_and_last_lease_releases_pixels() {
    let (mut app, backend) = manual(2);
    let first = request(&mut app, 1);
    let second = request(&mut app, 1);
    let third = second.clone();
    tick(&mut app);
    let texture = pixel();
    let weak = Arc::downgrade(&texture);
    complete(&backend, Ok(texture));
    assert!(matches!(
        app.world()
            .resource::<TextureStore>()
            .unwrap()
            .state(handle(1)),
        Some(TextureState::Loading)
    ));
    tick(&mut app);
    assert!(matches!(backend.try_next(), Err(RequestPollError::Empty)));
    drop(first);
    drop(second);
    tick(&mut app);
    assert!(weak.upgrade().is_some());
    drop(third);
    tick(&mut app);
    assert!(
        app.world()
            .resource::<TextureStore>()
            .unwrap()
            .state(handle(1))
            .is_none()
    );
    assert!(
        weak.upgrade().is_none(),
        "retained service events must not pin pixels"
    );
}

#[test]
fn release_and_reacquisition_discard_the_old_completion() {
    let (mut app, backend) = manual(1);
    let lease = request(&mut app, 1);
    tick(&mut app);
    let (context, old) = backend.try_next().unwrap().into_parts();
    drop(lease);
    tick(&mut app);
    assert!(context.is_cancelled());
    let _new = request(&mut app, 1);
    *old.result.lock().unwrap() = Some(Ok(pixel()));
    backend
        .complete(context, Ok(LoadCompletion::new()))
        .unwrap();
    tick(&mut app);
    assert!(matches!(
        app.world()
            .resource::<TextureStore>()
            .unwrap()
            .state(handle(1)),
        Some(TextureState::Loading)
    ));
    complete(&backend, Ok(pixel()));
    tick(&mut app);
    assert!(matches!(
        app.world()
            .resource::<TextureStore>()
            .unwrap()
            .state(handle(1)),
        Some(TextureState::Ready(_))
    ));
}

#[test]
fn reacquisition_before_reconciliation_keeps_the_same_request() {
    let (mut app, backend) = manual(1);
    let first = request(&mut app, 1);
    tick(&mut app);
    drop(first);
    let _second = request(&mut app, 1);
    complete(&backend, Ok(pixel()));
    tick(&mut app);
    assert!(matches!(
        app.world()
            .resource::<TextureStore>()
            .unwrap()
            .state(handle(1)),
        Some(TextureState::Ready(_))
    ));
    assert!(matches!(backend.try_next(), Err(RequestPollError::Empty)));
}

#[test]
fn failure_is_retained_and_retry_is_explicit() {
    let (mut app, backend) = manual(1);
    let _lease = request(&mut app, 1);
    tick(&mut app);
    complete(
        &backend,
        Err(TextureError::InvalidPng("bad signature".into())),
    );
    tick(&mut app);
    tick(&mut app);
    assert!(matches!(backend.try_next(), Err(RequestPollError::Empty)));
    let store = app.world_mut().resource_mut::<TextureStore>().unwrap();
    assert!(matches!(
        store.state(handle(1)),
        Some(TextureState::Failed(TextureError::InvalidPng(_)))
    ));
    store.retry(handle(1)).unwrap();
    assert_eq!(store.retry(handle(1)), Err(TextureError::NotFailed));
    tick(&mut app);
    complete(&backend, Ok(pixel()));
    tick(&mut app);
    assert!(matches!(
        app.world()
            .resource::<TextureStore>()
            .unwrap()
            .state(handle(1)),
        Some(TextureState::Ready(_))
    ));
}

#[test]
fn bounded_requests_reject_overload_without_a_loading_entry() {
    let (mut app, backend) = manual(1);
    let first = request(&mut app, 1);
    let store = app.world_mut().resource_mut::<TextureStore>().unwrap();
    assert!(matches!(
        store.request(handle(2)),
        Err(TextureError::Capacity)
    ));
    assert!(store.state(handle(2)).is_none());
    assert!(matches!(
        store.request(handle(99)),
        Err(TextureError::UnknownAsset)
    ));
    drop(first);
    tick(&mut app);
    let _second = request(&mut app, 2);
    tick(&mut app);
    complete(&backend, Ok(pixel()));
    tick(&mut app);
    assert!(matches!(
        app.world()
            .resource::<TextureStore>()
            .unwrap()
            .state(handle(2)),
        Some(TextureState::Ready(_))
    ));
}

#[test]
fn serial_dispatch_preserves_all_results_with_single_event_capacity() {
    let (mut app, backend) = manual(3);
    let _leases: Vec<_> = (1..=3).map(|id| request(&mut app, id)).collect();
    tick(&mut app);
    for _ in 0..3 {
        complete(&backend, Ok(pixel()));
        tick(&mut app);
    }
    for id in 1..=3 {
        assert!(matches!(
            app.world()
                .resource::<TextureStore>()
                .unwrap()
                .state(handle(id)),
            Some(TextureState::Ready(_))
        ));
    }
}

#[test]
fn shutdown_cancels_pending_work_rejects_requests_and_allows_late_lease_drop() {
    let (mut app, backend) = manual(1);
    let lease = request(&mut app, 1);
    tick(&mut app);
    let (context, request) = backend.try_next().unwrap().into_parts();
    app.shutdown().unwrap();
    assert!(context.is_cancelled());
    assert!(
        backend
            .complete(context, Ok(LoadCompletion::new()))
            .is_err()
    );
    drop(request);
    let store = app.world_mut().resource_mut::<TextureStore>().unwrap();
    assert!(matches!(
        store.request(handle(1)),
        Err(TextureError::Closed)
    ));
    assert!(store.state(handle(1)).is_none());
    drop(app);
    drop(lease);
}

#[test]
fn snapshot_can_pin_pixels_after_store_release() {
    let (mut app, backend) = manual(1);
    let lease = request(&mut app, 1);
    tick(&mut app);
    complete(&backend, Ok(pixel()));
    tick(&mut app);
    let Some(TextureState::Ready(texture)) = app
        .world()
        .resource::<TextureStore>()
        .unwrap()
        .state(handle(1))
    else {
        panic!("not ready")
    };
    let snapshot = texture.clone();
    drop(lease);
    tick(&mut app);
    assert_eq!(snapshot.pixels(), &[10, 20, 30, 255]);
    assert!(
        app.world()
            .resource::<TextureStore>()
            .unwrap()
            .state(handle(1))
            .is_none()
    );
}

fn fixture() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../games/minimal-game/assets/presentation/textures/sample.png")
}

#[test]
fn real_png_is_decoded_and_limits_are_enforced() {
    let texture = decode_png(&fixture(), TextureLimits::default()).unwrap();
    assert_eq!((texture.width(), texture.height()), (2, 2));
    assert_eq!(
        texture.pixels(),
        &[
            255, 255, 255, 255, 255, 80, 40, 255, 40, 160, 255, 255, 0, 0, 0, 0
        ]
    );
    for limits in [
        TextureLimits {
            max_file_bytes: 1,
            ..TextureLimits::default()
        },
        TextureLimits {
            max_dimension: 1,
            ..TextureLimits::default()
        },
    ] {
        assert!(matches!(
            decode_png(&fixture(), limits),
            Err(TextureError::LimitExceeded)
        ));
    }
    assert!(
        decode_png(
            &fixture(),
            TextureLimits {
                max_decoded_bytes: 1,
                ..TextureLimits::default()
            }
        )
        .is_err()
    );
    assert!(matches!(
        decode_png(
            &fixture().with_extension("missing"),
            TextureLimits::default()
        ),
        Err(TextureError::Io(_))
    ));
    let invalid = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("Cargo.toml");
    assert!(matches!(
        decode_png(&invalid, TextureLimits::default()),
        Err(TextureError::InvalidPng(_))
    ));
}

#[test]
fn native_worker_loads_real_content_and_joins_on_shutdown() {
    let mut builder = AppBuilder::new();
    TextureStore::install(
        &mut builder,
        fixture().parent().unwrap(),
        [(handle(1).id(), "sample.png".into())],
        TextureLimits::default(),
    )
    .unwrap();
    let mut app = builder.build().unwrap();
    app.start().unwrap();
    let lease = request(&mut app, 1);
    let deadline = std::time::Instant::now() + Duration::from_secs(5);
    loop {
        tick(&mut app);
        match app
            .world()
            .resource::<TextureStore>()
            .unwrap()
            .state(handle(1))
        {
            Some(TextureState::Ready(_)) => break,
            Some(TextureState::Failed(error)) => panic!("{error}"),
            _ => assert!(std::time::Instant::now() < deadline, "worker timeout"),
        }
        thread::sleep(Duration::from_millis(1));
    }
    app.shutdown().unwrap();
    assert!(
        app.world()
            .resource::<TextureStore>()
            .unwrap()
            .worker
            .is_none()
    );
    drop(lease);
}

#[test]
fn invalid_installation_can_be_corrected_and_duplicate_installation_is_rejected() {
    let mut builder = AppBuilder::new();
    assert!(
        TextureStore::install(
            &mut builder,
            ".",
            [(handle(1).id(), "../bad.png".into())],
            TextureLimits::default()
        )
        .is_err()
    );
    TextureStore::install(
        &mut builder,
        ".",
        [(handle(1).id(), "good.png".into())],
        TextureLimits::default(),
    )
    .unwrap();
    assert!(TextureStore::install(&mut builder, ".", [], TextureLimits::default()).is_err());
    let mut app = builder.build().unwrap();
    app.start().unwrap();
    app.shutdown().unwrap();
}

#[test]
fn backend_disconnect_fails_loading_entries_instead_of_hanging() {
    let (mut app, backend) = manual(2);
    let _first = request(&mut app, 1);
    let _second = request(&mut app, 2);
    tick(&mut app);
    drop(backend);
    tick(&mut app);
    for id in 1..=2 {
        assert!(matches!(
            app.world()
                .resource::<TextureStore>()
                .unwrap()
                .state(handle(id)),
            Some(TextureState::Failed(TextureError::WorkerUnavailable))
        ));
    }
}

#[test]
fn shutdown_releases_completed_but_unpublished_pixels() {
    let (mut app, backend) = manual(1);
    let _lease = request(&mut app, 1);
    tick(&mut app);
    let texture = pixel();
    let weak = Arc::downgrade(&texture);
    complete(&backend, Ok(texture));
    app.shutdown().unwrap();
    assert!(weak.upgrade().is_none());
}

fn encoded(color: png::ColorType, depth: png::BitDepth, pixels: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::new();
    {
        let mut encoder = png::Encoder::new(&mut bytes, 1, 1);
        encoder.set_color(color);
        encoder.set_depth(depth);
        if color == png::ColorType::Indexed {
            encoder.set_palette(vec![10, 20, 30]);
            encoder.set_trns(vec![40]);
        }
        encoder
            .write_header()
            .unwrap()
            .write_image_data(pixels)
            .unwrap();
    }
    bytes
}

#[test]
fn png_variants_normalize_to_rgba8_and_truncation_fails() {
    for (color, depth, input, expected) in [
        (
            png::ColorType::Grayscale,
            png::BitDepth::Eight,
            vec![10],
            [10, 10, 10, 255],
        ),
        (
            png::ColorType::GrayscaleAlpha,
            png::BitDepth::Eight,
            vec![10, 20],
            [10, 10, 10, 20],
        ),
        (
            png::ColorType::Rgb,
            png::BitDepth::Eight,
            vec![10, 20, 30],
            [10, 20, 30, 255],
        ),
        (
            png::ColorType::Indexed,
            png::BitDepth::Eight,
            vec![0],
            [10, 20, 30, 40],
        ),
        (
            png::ColorType::Grayscale,
            png::BitDepth::Sixteen,
            vec![10, 255],
            [10, 10, 10, 255],
        ),
    ] {
        let mut bytes = encoded(color, depth, &input);
        let texture = decode_bytes(bytes.clone(), TextureLimits::default()).unwrap();
        assert_eq!(texture.pixels(), expected);
        bytes.truncate(bytes.len() / 2);
        assert!(matches!(
            decode_bytes(bytes, TextureLimits::default()),
            Err(TextureError::InvalidPng(_))
        ));
    }
}
