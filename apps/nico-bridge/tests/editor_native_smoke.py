"""Exercise the real editor against an opted-in release client via bridge MCP.

Uses isolated ports and private fixtures; opens and cleans up only test-owned windows.
With --rebuild, rebuilds the compatible client while the same editor stays open.
Captures and separately sampled metadata are retained under --evidence.
"""
import argparse
import hashlib
import json
import pathlib
import secrets
import shutil
import subprocess
import tempfile
import time

from native_smoke import Mcp
from editor_rpc_smoke import address


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bridge", type=pathlib.Path, required=True)
    parser.add_argument("--editor", type=pathlib.Path, required=True)
    parser.add_argument("--release-client", type=pathlib.Path, required=True)
    parser.add_argument("--evidence", type=pathlib.Path, required=True)
    parser.add_argument("--rebuild", action="store_true")
    parser.add_argument("--ssh-tunnel", action="store_true")
    parser.add_argument("--release-server", type=pathlib.Path)
    args = parser.parse_args()
    if args.ssh_tunnel and not args.release_server:
        parser.error("--ssh-tunnel requires --release-server")
    args.evidence.mkdir(parents=True, exist_ok=True)
    processes, logs = [], []
    tunnel = None
    with tempfile.TemporaryDirectory(prefix="nico-native-attach-") as directory:
        root = pathlib.Path(directory)
        project = root / "project"
        project.mkdir()
        endpoint_token, host_token = secrets.token_hex(32), secrets.token_hex(32)
        token_file, credential_file, policy_file = (root / name for name in ("endpoint.token", "host.token", "host.json"))
        token_file.write_text(endpoint_token)
        credential_file.write_text(host_token)
        policy_file.write_text(json.dumps({"grants": [{"credential": host_token,
                                                     "permissions": ["inspect", "capture", "stop"]}]}))
        for path in (token_file, credential_file, policy_file):
            path.chmod(0o600)
        game_address, editor_address = address(), address()
        while editor_address == game_address:
            editor_address = address()

        def start(binary, *arguments, mcp=False):
            log = (args.evidence / f"process-{len(processes)}.log").open("w")
            logs.append(log)
            process = subprocess.Popen([str(binary.resolve()), *arguments],
                                       stdin=subprocess.PIPE if mcp else subprocess.DEVNULL,
                                       stdout=subprocess.PIPE if mcp else subprocess.DEVNULL,
                                       stderr=log, text=True)
            processes.append(process)
            return process

        def ready(process):
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                assert process.poll() is None, f"process {process.pid} exited {process.returncode}"
                for instance in bridge.call("list_instances", {})["instances"]:
                    if instance["pid"] == process.pid and instance["connected"] and instance["ready"]:
                        return instance
                time.sleep(0.05)
            raise TimeoutError(f"PID {process.pid} never became ready")

        def debug(**action):
            expect_failure = action.pop("_expect_failure", False)
            command = bridge.game(editor_id, "editor_debug_command", action)["command_id"]
            deadline = time.monotonic() + 12
            while time.monotonic() < deadline:
                state = bridge.game(editor_id, "editor_debug_state")
                outcome = next((entry for entry in state["outcomes"] if entry["command_id"] == command), None)
                if outcome is not None:
                    fragments, offset = [], 0
                    while True:
                        page = bridge.game(editor_id, "editor_debug_result", {"command_id": command, "offset": offset, "limit": 16384})
                        fragments.append(page["json_fragment"])
                        if page["complete"]:
                            break
                        offset = page["next_offset"]
                    outcome = json.loads("".join(fragments))
                    if expect_failure:
                        assert outcome["outcome"] != "reply" or outcome["result"].get("isError"), outcome
                        return outcome
                    assert outcome["outcome"] == "reply", outcome
                    assert not outcome["result"].get("isError"), outcome
                    return outcome["result"]["structuredContent"]
                time.sleep(0.02)
            raise TimeoutError(f"editor command {command} did not finish")

        def capture(invoke, label):
            capture_id = invoke("window_snapshot", {})["request_id"]
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                result = invoke("window_snapshot", {"request_id": capture_id})
                if result["state"] == "ready":
                    destination = args.evidence / f"{label}.png"
                    shutil.copyfile(result["path"], destination)
                    return {**result, "retained_path": str(destination)}
                time.sleep(0.04)
            raise TimeoutError(f"{label} capture did not finish")

        def download(label):
            destination = root / f"{label}.png"
            result = debug(action="capture", destination=str(destination))
            assert result["sha256"] == hashlib.sha256(destination.read_bytes()).hexdigest()
            retained = args.evidence / f"{label}.png"
            shutil.copyfile(destination, retained)
            return {**result, "retained_path":str(retained), "transfer":"editor RPC chunks, independently hash-verified"}

        try:
            bridge = Mcp(start(args.bridge, "--listen", game_address, "--editor-listen", editor_address,
                               "--editor-token-file", str(token_file), mcp=True))
            connect_address = editor_address
            if args.ssh_tunnel:
                from ssh_fixture import SshTunnel
                tunnel = SshTunnel(root, editor_address, args.evidence)
                tunnel.start()
                connect_address = tunnel.forward
            editor = start(args.editor, "--project", str(project), "--bridge", game_address, "--background")
            editor_instance = ready(editor)
            editor_id = editor_instance["instance_id"]
            deadline = time.monotonic() + 30
            while bridge.game(editor_id, "editor_state").get("loading"):
                assert time.monotonic() < deadline
                time.sleep(0.05)
            # A copied executable avoids locking Cargo output during the compatible rebuild.
            initial_binary = root / args.release_client.name
            shutil.copy2(args.release_client, initial_binary)
            client = start(initial_binary, "--enable-debug", "--debug-access-file", str(policy_file),
                           "--bridge", game_address, "--background")
            initial = ready(client)
            client_id = initial["instance_id"]
            print(f"Automated attach control starts: editor PID {editor.pid} ({editor_id}), client PID {client.pid} ({client_id})", flush=True)
            debug(action="connect", address=connect_address, token_file=str(token_file))
            debug(action="discover")
            attached = debug(action="attach", instance_id=client_id, api_version=initial["api_version"],
                             credential_file=str(credential_file))
            assert attached["identity"] == initial["identity"]
            entity_page = debug(action="call", tool_name="debug_entities", arguments={"limit": 1})
            entity = debug(action="call", tool_name="debug_entity", arguments={
                "snapshot_id": entity_page["snapshot_id"], "reference": entity_page["entities"][0]["reference"]})
            assert entity["tick"] == entity_page["tick"]
            time.sleep(0.5)  # Allow several rendered panel frames before the capture request.
            editor_capture = capture(lambda tool, values: bridge.game(editor_id, tool, values), "editor-attached")
            client_capture = download("release-client")
            assert client_capture["process_session"] == initial["identity"]["process_session"]
            assert isinstance(client_capture["frame_id"], int) and client_capture["frame_id"] >= 0
            before = debug(action="call", tool_name="status", arguments={})
            debug(action="detach")
            time.sleep(0.3)
            after = bridge.game(client_id, "status")
            assert client.poll() is None and after["graphics"]["presented_frames"] > before["graphics"]["presented_frames"]
            evidence = {"editor": editor_instance, "initial_client": initial, "entity_page": entity_page,
                        "entity": entity, "editor_capture": editor_capture, "client_capture": client_capture,
                        "detach_before": before, "detach_after": after,
                        "sampling": "Captures and status are sampled separately; no desktop visibility or user acceptance claim."}
            if args.ssh_tunnel:
                server = start(args.release_server, "--enable-debug", "--debug-access-file", str(policy_file), "--bridge", game_address)
                server_instance = ready(server)
                server_id = server_instance["instance_id"]
                debug(action="attach", instance_id=server_id, api_version=server_instance["api_version"], credential_file=str(credential_file))
                server_state = debug(action="call", tool_name="game_state", arguments={})
                diagnostics = debug(action="call", tool_name="diagnostics", arguments={"limit":8})
                policy_file.write_text(json.dumps({"grants": []}))
                denied = debug(action="call", tool_name="game_state", arguments={}, _expect_failure=True)
                assert denied["result"]["structuredContent"]["error"]["code"] == "access_denied"
                policy_file.write_text(json.dumps({"grants":[{"credential":host_token,"permissions":["inspect","capture","stop"]}]}))
                tunnel.disconnect()
                debug(action="discover", _expect_failure=True)
                assert editor.poll() is None and client.poll() is None and server.poll() is None
                assert bridge.game(server_id, "status")["ready"] and bridge.game(client_id, "status")["ready"]
                tunnel.start_forward()
                debug(action="connect", address=connect_address, token_file=str(token_file))
                debug(action="attach", instance_id=server_id, api_version=server_instance["api_version"], credential_file=str(credential_file))
                assert debug(action="call", tool_name="stop", arguments={})["accepted"]
                assert server.wait(timeout=15) == 0
                evidence["ssh"] = {"transport":"authenticated OpenSSH loopback forwarding with pinned host key", "wrong_key_rejected":True,"host_revocation":True,"tunnel_disconnect_survival":True,"server":server_instance,"server_state":server_state,"server_diagnostics":diagnostics}
            if args.rebuild:
                print("Rebuilding the compatible release client; the editor remains open", flush=True)
                build_log = (args.evidence / "compatible-rebuild.log").open("w")
                try:
                    subprocess.run(["cargo", "rustc", "--release", "-p", "minimal-game-client", "--", "-C", "debuginfo=1"],
                                   stdout=build_log, stderr=subprocess.STDOUT, check=True, timeout=300)
                finally:
                    build_log.close()
                assert editor.poll() is None and client.poll() is None
                rebuilt_hash = hashlib.sha256(args.release_client.read_bytes()).hexdigest()
                assert rebuilt_hash != initial["identity"]["build_id"]
            debug(action="attach", instance_id=client_id, api_version=initial["api_version"], credential_file=str(credential_file))
            assert debug(action="call", tool_name="stop", arguments={})["accepted"]
            assert client.wait(timeout=15) == 0
            if args.rebuild:
                client = start(args.release_client, "--enable-debug", "--debug-access-file", str(policy_file),
                               "--bridge", game_address, "--background")
                replacement = ready(client)
                assert replacement["identity"]["build_id"] == rebuilt_hash
                assert replacement["identity"]["process_session"] != initial["identity"]["process_session"]
                assert editor.poll() is None
                debug(action="attach", instance_id=replacement["instance_id"], api_version=replacement["api_version"], credential_file=str(credential_file))
                replacement_page = debug(action="call", tool_name="debug_entities", arguments={"limit": 1})
                evidence["replacement_client"] = replacement
                evidence["replacement_entities"] = replacement_page
                evidence["replacement_capture"] = download("rebuilt-client")
                assert debug(action="call", tool_name="stop", arguments={})["accepted"]
                assert client.wait(timeout=15) == 0
            debug(action="disconnect")
            assert bridge.game(editor_id, "stop")["accepted"]
            assert editor.wait(timeout=15) == 0
            bridge.close()
            (args.evidence / "evidence.json").write_text(json.dumps(evidence, indent=2))
            for log in logs:
                log.flush()
                contents = pathlib.Path(log.name).read_text()
                assert endpoint_token not in contents and host_token not in contents
            print("PASS: editor attach, entity inspection, captures, detach survival" + (", compatible rebuild/reattach" if args.rebuild else "") + ("; authenticated SSH tunnel, revocation, and independent client/server survival" if args.ssh_tunnel else "") + "; automated control stopped and all test windows closed", flush=True)
        finally:
            if tunnel is not None:
                tunnel.close()
            for process in reversed(processes):
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=10)
            for log in logs:
                log.close()


if __name__ == "__main__":
    main()
