//! Shared listener ownership and on-demand process startup.
use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read},
    net::SocketAddr,
    path::PathBuf,
    process::{Command, Stdio},
    time::Duration,
};

use rmcp::ServiceExt;
use serde::{Deserialize, Serialize};
use tokio::{
    io::BufReader,
    net::{TcpListener, TcpStream},
    sync::Semaphore,
    time::Instant,
};

use super::{
    check_address,
    server::{Bridge, serve_game},
    wire,
};

const VERSION: u32 = 1;
const START_TIMEOUT: Duration = Duration::from_secs(8);

/// One daemon serves each game address. Its state directory belongs to the local user.
#[derive(Clone, Debug)]
pub struct DaemonConfig {
    pub game_address: SocketAddr,
    pub state_dir: PathBuf,
    pub idle_timeout: Duration,
}

impl DaemonConfig {
    pub fn new(game_address: SocketAddr) -> Self {
        let key = game_address.to_string().replace([':', '[', ']'], "_");
        Self {
            game_address,
            state_dir: std::env::temp_dir().join("nico-mcp-bridge").join(key),
            idle_timeout: Duration::from_secs(30),
        }
    }

    fn prepare(&self) -> io::Result<()> {
        check_address(self.game_address)?;
        if self.game_address.port() == 0 || self.idle_timeout.is_zero() {
            return Err(io::Error::other(
                "daemon requires a fixed game port and positive idle timeout",
            ));
        }
        fs::create_dir_all(&self.state_dir)?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&self.state_dir, fs::Permissions::from_mode(0o700))?;
        }
        Ok(())
    }

    fn lock(&self, name: &str) -> io::Result<File> {
        OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(self.state_dir.join(name))
    }
}

#[derive(Serialize, Deserialize)]
struct Endpoint {
    version: u32,
    game_address: SocketAddr,
    address: SocketAddr,
    token: String,
    pid: u32,
}

async fn connect(config: &DaemonConfig) -> io::Result<TcpStream> {
    let mut bytes = Vec::new();
    File::open(config.state_dir.join("endpoint.json"))?
        .take(4097)
        .read_to_end(&mut bytes)?;
    if bytes.len() > 4096 {
        return Err(io::Error::other("daemon endpoint file is too large"));
    }
    let endpoint: Endpoint = serde_json::from_slice(&bytes).map_err(io::Error::other)?;
    if endpoint.version != VERSION || endpoint.game_address != config.game_address {
        return Err(io::Error::other("incompatible daemon endpoint"));
    }
    check_address(endpoint.address)?;
    let mut stream = TcpStream::connect(endpoint.address).await?;
    wire::write_json(&mut stream, &endpoint.token).await?;
    let mut reader = BufReader::new(stream);
    let version: u32 = wire::read_json(&mut reader).await?;
    if version != VERSION {
        return Err(io::Error::other("incompatible daemon handshake"));
    }
    Ok(reader.into_inner())
}

async fn probe(config: &DaemonConfig) -> io::Result<TcpStream> {
    tokio::time::timeout(Duration::from_millis(500), connect(config))
        .await
        .map_err(io::Error::other)?
}

/// Connect or launch one independent daemon. The lock is released by the OS on process exit.
pub(super) async fn ensure(config: &DaemonConfig) -> io::Result<TcpStream> {
    config.prepare()?;
    if let Ok(stream) = probe(config).await {
        return Ok(stream);
    }
    let deadline = Instant::now() + START_TIMEOUT;
    let lock = config.lock("startup.lock")?;
    loop {
        match lock.try_lock() {
            Ok(()) => break,
            Err(std::fs::TryLockError::WouldBlock) => {
                if Instant::now() >= deadline {
                    return Err(io::Error::other(
                        "timed out waiting for daemon startup lock",
                    ));
                }
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            Err(std::fs::TryLockError::Error(error)) => return Err(error),
        }
    }
    if let Ok(stream) = probe(config).await {
        return Ok(stream);
    }
    let log = File::create(config.state_dir.join("daemon.log"))?;
    let mut command = Command::new(std::env::current_exe()?);
    command
        .arg("--daemon")
        .arg("--listen")
        .arg(config.game_address.to_string())
        .arg("--state-dir")
        .arg(std::path::absolute(&config.state_dir)?)
        .arg("--idle-seconds")
        .arg(config.idle_timeout.as_secs().max(1).to_string())
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::from(log));
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // Break away from a parent's kill-on-close job; never create a console window.
        command.creation_flags(0x01000000 | 0x08000000);
    }
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        command.process_group(0);
    }
    let mut child = match command.spawn() {
        Ok(child) => Some(child),
        #[cfg(windows)]
        Err(error) if error.kind() == io::ErrorKind::PermissionDenied => {
            spawn_windows_broker(config)?;
            None
        }
        Err(error) => return Err(error),
    };
    loop {
        if let Some(status) = child
            .as_mut()
            .map(|child| child.try_wait())
            .transpose()?
            .flatten()
        {
            return Err(io::Error::other(format!(
                "bridge daemon exited ({status}); see {}",
                config.state_dir.join("daemon.log").display()
            )));
        }
        if let Ok(stream) = probe(config).await {
            // Reap our child without tying its lifetime to this frontend.
            if let Some(mut child) = child {
                std::thread::spawn(move || {
                    let _ = child.wait();
                });
            }
            return Ok(stream);
        }
        if Instant::now() >= deadline {
            // Only this startup attempt owns this child. No ready daemon was found.
            if let Some(mut child) = child {
                let _ = child.kill();
                let _ = child.wait();
            }
            return Err(io::Error::other(format!(
                "bridge daemon readiness timed out; see {}",
                config.state_dir.join("daemon.log").display()
            )));
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

#[cfg(windows)]
fn spawn_windows_broker(config: &DaemonConfig) -> io::Result<()> {
    use std::os::windows::process::CommandExt;
    // CIM creates the process outside the frontend's job. Data never becomes PowerShell code.
    let arguments = [
        std::env::current_exe()?.to_string_lossy().into_owned(),
        "--daemon".into(),
        "--listen".into(),
        config.game_address.to_string(),
        "--state-dir".into(),
        std::path::absolute(&config.state_dir)?
            .to_string_lossy()
            .into_owned(),
        "--idle-seconds".into(),
        config.idle_timeout.as_secs().max(1).to_string(),
    ];
    let script = r#"
$ErrorActionPreference = 'Stop'
$parts = ConvertFrom-Json $env:NICO_DAEMON_ARGUMENTS
$quoted = foreach ($part in $parts) {
    '"' + [regex]::Replace([regex]::Replace($part, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
$startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ShowWindow=[uint16]0}
$result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine=($quoted -join ' '); ProcessStartupInformation=$startup}
if ($result.ReturnValue -ne 0) { throw "Daemon process creation failed: $($result.ReturnValue)" }
"#;
    let result = Command::new("powershell.exe")
        .args(["-NoProfile", "-NonInteractive", "-Command", script])
        .env(
            "NICO_DAEMON_ARGUMENTS",
            serde_json::to_string(&arguments).map_err(io::Error::other)?,
        )
        .stdin(Stdio::null())
        .creation_flags(0x08000000)
        .output()?;
    if !result.status.success() {
        return Err(io::Error::other(format!(
            "Windows daemon broker failed: {}",
            String::from_utf8_lossy(&result.stderr)
        )));
    }
    Ok(())
}

/// Run a shared daemon until no frontend or game remains for the idle timeout.
/// The caller owns this process; disconnects never stop game processes.
pub fn serve_daemon(config: DaemonConfig) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let result = run_daemon(&config);
    if let Err(error) = &result {
        let _ = fs::write(config.state_dir.join("daemon.log"), format!("{error}\n"));
    }
    result
}

fn run_daemon(config: &DaemonConfig) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    config.prepare()?;
    let owner = config.lock("owner.lock")?;
    owner
        .try_lock()
        .map_err(|e| io::Error::other(format!("daemon already owns this state directory: {e}")))?;
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    runtime.block_on(async {
        let games = TcpListener::bind(config.game_address).await?;
        let clients = TcpListener::bind(SocketAddr::new(config.game_address.ip(), 0)).await?;
        let endpoint = Endpoint {
            version: VERSION, game_address: config.game_address, address: clients.local_addr()?,
            token: uuid::Uuid::new_v4().to_string(), pid: std::process::id(),
        };
        fs::write(config.state_dir.join("endpoint.json"), serde_json::to_vec(&endpoint)?)?;
        eprintln!("Nico daemon {}: games={}, frontends={}", endpoint.pid, endpoint.game_address, endpoint.address);
        let bridge = Bridge::new();
        let slots = std::sync::Arc::new(Semaphore::new(32));
        let game_slots = std::sync::Arc::new(Semaphore::new(32));
        let mut idle_since = Instant::now();
        let mut interval = tokio::time::interval(Duration::from_millis(100));
        loop {
            tokio::select! {
                accepted = games.accept() => {
                    let (stream, _) = accepted?;
                    if let Ok(slot) = game_slots.clone().try_acquire_owned() {
                        let bridge = bridge.clone();
                        tokio::spawn(async move { let _slot = slot; let _ = serve_game(stream, bridge).await; });
                    }
                    idle_since = Instant::now();
                }
                accepted = clients.accept() => {
                    let (stream, _) = accepted?;
                    if let Ok(slot) = slots.clone().try_acquire_owned() {
                        let bridge = bridge.clone();
                        let token = endpoint.token.clone();
                        tokio::spawn(async move {
                            let _slot = slot;
                            let mut reader = BufReader::new(stream);
                            let Ok(Ok(supplied)) = tokio::time::timeout(wire::IO_TIMEOUT, wire::read_json::<_, String>(&mut reader)).await else { return; };
                            if supplied != token { return; }
                            let mut stream = reader.into_inner();
                            if wire::write_json(&mut stream, &VERSION).await.is_err() { return; }
                            let (read, write) = stream.into_split();
                            let Ok(Ok(service)) = tokio::time::timeout(START_TIMEOUT, bridge.serve((read, write))).await else { return; };
                            let _ = service.waiting().await;
                        });
                    }
                    idle_since = Instant::now();
                }
                _ = interval.tick() => {
                    if slots.available_permits() != 32 || game_slots.available_permits() != 32 || bridge.has_games() {
                        idle_since = Instant::now();
                    } else if idle_since.elapsed() >= config.idle_timeout { break; }
                }
            }
        }
        // A stale endpoint is harmless: the next frontend verifies the handshake and replaces it.
        Ok::<_, Box<dyn std::error::Error + Send + Sync>>(())
    })
}
