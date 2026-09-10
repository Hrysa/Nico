//! Runs the real server and controls it from another thread without a transport.

use std::{
    error::Error,
    io, thread,
    time::{Duration, Instant},
};

use minimal_game_shared::MinimalGamePlugin;
use nico_launch::server::FixedRateServerRunner;
use nico_ops::{HostState, control_channel};
use nico_runtime::AppBuilder;

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    let step = Duration::from_nanos(16_666_667);
    let mut app = AppBuilder::new()
        .with_fixed_step(step)
        .add_plugin(MinimalGamePlugin)
        .build()?;
    let (control, operations) = control_channel();
    let observer = control.clone();
    let controller = thread::spawn(move || -> Result<(), io::Error> {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            let status = observer.status();
            if status.is_ready() {
                println!("ready: completed_steps={}", status.completed_steps);
                observer.request_stop().map_err(io::Error::other)?;
                return Ok(());
            }
            if status.is_finished() {
                return Err(io::Error::other(format!(
                    "host finished before readiness: {status:?}"
                )));
            }
            if Instant::now() >= deadline {
                let _ = observer.request_stop();
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "server readiness timeout",
                ));
            }
            thread::sleep(Duration::from_millis(1));
        }
    });
    let result = app.run_with(FixedRateServerRunner::new(step).with_operations(operations));
    controller
        .join()
        .map_err(|_| io::Error::other("controller panicked"))??;
    result?;
    let final_status = control.status();
    println!("finished: {final_status:?}");
    if final_status.state != HostState::Stopped {
        return Err(io::Error::other("server did not stop successfully").into());
    }
    Ok(())
}
