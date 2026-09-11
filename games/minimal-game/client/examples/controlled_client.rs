//! Observe actual GPU readiness, then stop the native host from another thread.

use std::{
    error::Error,
    io, thread,
    time::{Duration, Instant},
};

use minimal_game_shared::{MinimalGamePlugin, PlayerCommand};
use nico_launch::init_logging;
use nico_ops::{HostState, control_channel};
use nico_runtime::AppBuilder;
use nico_winit::{NativeClientConfig, run_native_client_with_operations};

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    init_logging(None)?;
    let app = AppBuilder::new().add_plugin(MinimalGamePlugin).build()?;
    let config = NativeClientConfig::new(
        "Nico controlled client",
        "assets/presentation/shaders/generated/wgpu/bootstrap.wgsl",
    );
    let (control, operations) = control_channel();
    let observer = control.clone();
    let controller = thread::spawn(move || -> Result<(), io::Error> {
        let deadline = Instant::now() + Duration::from_secs(15);
        loop {
            let status = observer.status();
            if status.is_ready() {
                println!("GPU ready: completed_steps={}", status.completed_steps);
                observer.request_stop().map_err(io::Error::other)?;
                return Ok(());
            }
            if status.is_finished() {
                return Err(io::Error::other(format!(
                    "client finished before readiness: {status:?}"
                )));
            }
            if Instant::now() >= deadline {
                let _ = observer.request_stop();
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "client readiness timeout",
                ));
            }
            thread::sleep(Duration::from_millis(1));
        }
    });
    // Native hosting stays on the main thread; this demonstration needs no input.
    let result =
        run_native_client_with_operations::<PlayerCommand>(app, config, |_, _| {}, operations);
    let observed = controller
        .join()
        .map_err(|_| io::Error::other("controller panicked"));
    let status = control.status();
    println!("finished: {status:?}");
    result?;
    observed??;
    if status.state != HostState::Stopped {
        return Err(io::Error::other("client did not stop successfully").into());
    }
    Ok(())
}
