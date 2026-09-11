use std::process::Command;

#[test]
fn game_server_rejects_direct_mcp_transport() {
    let output = Command::new(env!("CARGO_BIN_EXE_minimal-game-server"))
        .arg("--mcp-stdio")
        .output()
        .expect("server launches");
    assert!(!output.status.success());
    assert!(String::from_utf8_lossy(&output.stderr).contains("--mcp-stdio"));
    assert!(output.stdout.is_empty());
}
