//! Headless consumer for the shared sprite/HUD texture.
use nico_assets::{
    AssetId, Handle,
    loading::{Texture, TextureLimits, TextureState, TextureStore},
};
use nico_runtime::AppBuilder;
use std::{
    path::PathBuf,
    thread,
    time::{Duration, Instant},
};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../games/minimal-game/assets/presentation");
    let id = AssetId::from_u128(1);
    let handle = Handle::<Texture>::new(id);
    let mut builder = AppBuilder::new();
    TextureStore::install(
        &mut builder,
        root,
        [(id, "textures/sample.png".into())],
        TextureLimits::default(),
    )?;
    let mut app = builder.build()?;
    app.start()?;
    let lease = app
        .world_mut()
        .resource_mut::<TextureStore>()?
        .request(handle)?;
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        app.tick(Duration::from_millis(16))?;
        match app.world().resource::<TextureStore>()?.state(handle) {
            Some(TextureState::Ready(texture)) => {
                println!(
                    "Loaded {}x{} texture ({} RGBA8 bytes)",
                    texture.width(),
                    texture.height(),
                    texture.pixels().len()
                );
                break;
            }
            Some(TextureState::Failed(error)) => return Err(error.clone().into()),
            _ if Instant::now() >= deadline => return Err("texture load timed out".into()),
            _ => thread::sleep(Duration::from_millis(2)),
        }
    }
    drop(lease);
    app.tick(Duration::ZERO)?;
    assert!(
        app.world()
            .resource::<TextureStore>()?
            .state(handle)
            .is_none()
    );
    app.shutdown()?;
    println!("Released texture and joined loader worker");
    Ok(())
}
