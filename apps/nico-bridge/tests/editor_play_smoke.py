"""Validate editor-owned paired play through isolated bridge MCP and native windows.

Build nico-editor, nico-bridge, minimal-game-server first. The editor itself builds
profile hosts in target/editor-play. Only fixture projects/processes are cleaned up.
"""
import argparse
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
    parser.add_argument("--arena", action="store_true", help="Validate the Arena adapter profile and real network connection")
    parser.add_argument("--bin-dir", type=pathlib.Path, default=pathlib.Path("target/debug"))
    parser.add_argument("--evidence", type=pathlib.Path, default=pathlib.Path("target/editor-play-evidence"))
    args = parser.parse_args()
    args.evidence.mkdir(parents=True, exist_ok=True)
    processes, logs = [], []
    bridge = editor_id = None
    with tempfile.TemporaryDirectory(prefix="play-fixture-", dir="target") as folder:
        root = pathlib.Path(folder).resolve()
        project = root / "project"
        source = pathlib.Path("games/arena-arpg" if args.arena else "games/minimal-game")
        shutil.copytree(source / "assets", project / "assets", ignore=shutil.ignore_patterns(".nico"))
        shutil.copyfile(source / "nico.project.toml", project / "nico.project.toml")
        token = root / "endpoint.token"
        token.write_text(secrets.token_hex(32))
        token.chmod(0o600)
        game_address, editor_address = address(), address()
        while game_address == editor_address:
            editor_address = address()

        def start(name, *arguments, mcp=False):
            log = (args.evidence / f"{name}-{len(processes)}.log").open("w")
            logs.append(log)
            process = subprocess.Popen([str((args.bin_dir / name).resolve()), *arguments],
                                       stdin=subprocess.PIPE if mcp else subprocess.DEVNULL,
                                       stdout=subprocess.PIPE if mcp else subprocess.DEVNULL,
                                       stderr=log, text=True)
            processes.append(process)
            return process

        def ready(process):
            deadline = time.monotonic() + 40
            while time.monotonic() < deadline:
                assert process.poll() is None, process.returncode
                for entry in bridge.call("list_instances", {})["instances"]:
                    if entry["pid"] == process.pid and entry["connected"] and entry["ready"]:
                        bridge.call("list_game_tools", {"instance_id": entry["instance_id"]})
                        return entry
                time.sleep(0.05)
            raise TimeoutError("fixture host not ready")

        def state():
            return bridge.game(editor_id, "editor_state")

        def command(action, expect_error=False, **arguments):
            command_id = bridge.game(editor_id, "editor_command", {"action": action, **arguments})["command_id"]
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                for result in state().get("command_results", []):
                    if result["command_id"] == command_id:
                        assert bool(result["error"]) == expect_error, result
                        return result
                time.sleep(0.03)
            raise TimeoutError(f"command {action} did not finish")

        def phase(wanted, previous=None, timeout=660):
            deadline = time.monotonic() + timeout
            last_phase = None
            while time.monotonic() < deadline:
                current = state().get("play_session", {})
                if current.get("phase") != last_phase:
                    print("Play phase:", current.get("phase"), current.get("error"), flush=True)
                    last_phase = current.get("phase")
                if current.get("session_id") != previous and current.get("phase") == wanted:
                    return current
                if wanted == "running" and current.get("phase") == "failed" and current.get("session_id") != previous:
                    raise AssertionError(current)
                time.sleep(0.1)
            raise TimeoutError(current)

        def capture(instance, label):
            bridge.call("list_game_tools", {"instance_id": instance})
            request = bridge.game(instance, "window_snapshot")["request_id"]
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                result = bridge.game(instance, "window_snapshot", {"request_id": request})
                if result["state"] == "ready":
                    destination = args.evidence / f"{label}.png"
                    shutil.copyfile(result["path"], destination)
                    return {**result, "retained_path": str(destination)}
                time.sleep(0.05)
            raise TimeoutError("capture not ready")

        evidence = {}
        try:
            bridge = Mcp(start("nico-bridge", "--listen", game_address, "--editor-listen", editor_address,
                               "--editor-token-file", str(token), mcp=True))
            unrelated = start("minimal-game-server", "--bridge", game_address)
            unrelated_instance = ready(unrelated)
            editor = start("nico-editor", "--project", str(project), "--bridge", game_address, "--background")
            editor_instance = ready(editor)
            editor_id = editor_instance["instance_id"]
            while state().get("loading"):
                time.sleep(0.05)
            cache_marker = project / ".nico" / "play-cache-preservation-test"
            cache_marker.parent.mkdir(exist_ok=True)
            cache_marker.write_text("preserve project cache")
            print(f"Automated play control starts: editor PID {editor.pid}, {editor_id}", flush=True)
            command("configure_play", bridge=game_address, editor_endpoint=editor_address,
                    endpoint_token_file=str(token), release=False)
            command("play")
            first = phase("running")
            assert pathlib.Path(first["content_path"]).samefile(project)
            assert not (pathlib.Path(first["session_path"]) / "project").exists()
            assert cache_marker.read_text() == "preserve project cache"
            print("Owned hosts:", first["server"]["pid"], first["client"]["pid"], flush=True)
            for role in ("server", "client"):
                assert first[role]["ready"]
                assert first[role]["identity"]["content_revision"] == first["content_revision"]
            assert pathlib.Path(first["server_data"]).is_dir()
            assert unrelated.poll() is None
            evidence.update(editor=editor_instance, unrelated=unrelated_instance, first=first)
            evidence["client_capture"] = capture(first["client"]["instance_id"], "owned-client")
            evidence["editor_capture"] = capture(editor_id, "editor-play")
            assert evidence["client_capture"]["process_session"] == first["client"]["identity"]["process_session"]
            if args.arena:
                server_address = first["server"]["scene"]["address"]
                assert server_address != "127.0.0.1:47640"
                assert first["client"]["scene"]["server"] == server_address
                assert first["client"]["scene"]["connection"] == "connected"
                assert first["client"]["scene"]["input_ready"]
                assert first["client"]["scene"]["rendered_meshes"] > 0
                command("stop")
                stopped = phase("exited")
                assert stopped["resources_removed"]
                assert all(stopped[role]["exit_code"] == 0 and stopped[role]["exited"] for role in ("client", "server"))
                assert unrelated.poll() is None
                assert project.is_dir() and cache_marker.read_text() == "preserve project cache"
                assert not (pathlib.Path(stopped["session_path"]) / "host-access.json").exists()
                # Repeated Play must reuse the same source project and cache while
                # reusing its project-local Play directory.
                command("play")
                repeated = phase("running", previous=first["session_id"])
                assert repeated["content_path"] == first["content_path"]
                assert repeated["content_revision"] == first["content_revision"]
                assert repeated["session_path"] == first["session_path"]
                assert cache_marker.read_text() == "preserve project cache"
                command("stop")
                repeated_stop = phase("exited")
                assert repeated_stop["resources_removed"]
                assert all(repeated_stop[role]["exit_code"] == 0 for role in ("client", "server"))
                assert project.is_dir() and cache_marker.read_text() == "preserve project cache"
                evidence.update(repeated=repeated, repeated_stop=repeated_stop)
                evidence.update(stopped=stopped, sampling="State and GPU captures sampled separately; no user-observed acceptance claim.")
                (args.evidence / "evidence.json").write_text(json.dumps(evidence, indent=2))
                bridge.game(editor_id, "stop")
                assert editor.wait(timeout=20) == 0
                bridge.game(unrelated_instance["instance_id"], "stop")
                assert unrelated.wait(timeout=15) == 0
                print("Automated Arena play control stopped; all test-owned windows closed. PASS", flush=True)
                return
            obj = state()["objects"][0]
            position = list(obj["position"])
            position[0] += 0.5
            command("transform", id=obj["id"], position=position, rotation=obj["rotation"], scale=obj["scale"])
            command("restart", expect_error=True)
            assert state()["play_session"]["session_id"] == first["session_id"]
            command("save_and_restart")
            second = phase("running", previous=first["session_id"])
            assert first["content_revision"] != second["content_revision"]
            assert first["server_data"] == second["server_data"]
            assert pathlib.Path(first["server_data"]).is_dir()
            assert pathlib.Path(first["content_path"]).samefile(project)
            assert first["session_path"] == second["session_path"]
            assert cache_marker.read_text() == "preserve project cache"
            for role in ("server", "client"):
                assert second[role]["pid"] != first[role]["pid"]
                assert second[role]["identity"]["content_revision"] == second["content_revision"]
                assert second[role]["scene"]["objects"][0]["position"] == position
            command("stop")
            stopped = phase("exited")
            assert stopped["resources_removed"] and stopped["server_progress_saved"]
            for role in ("server", "client"):
                assert stopped[role]["exited"] and stopped[role]["exit_code"] == 0
                assert not stopped[role]["forced"]
            assert unrelated.poll() is None
            evidence.update(second=second, stopped=stopped)

            # A valid server followed by a client argument failure must reap both.
            manifest = project / "nico.project.toml"
            original = manifest.read_text()
            manifest.write_text(original.replace('client = "minimal-game-client"', 'client = "minimal-game-server"'))
            command("play")
            failed = phase("failed", previous=stopped["session_id"])
            assert failed["server"]["exited"] and failed["resources_removed"]
            assert unrelated.poll() is None
            evidence["partial_failure"] = failed
            manifest.write_text(original.replace('server = "minimal-game-server"', 'server = "missing-play-target"'))
            command("play")
            build_failed = phase("failed", previous=failed["session_id"])
            assert "build failed" in build_failed["error"]
            assert build_failed["server"] is None and build_failed["client"] is None
            assert pathlib.Path(build_failed["content_path"]).samefile(project)
            assert not (pathlib.Path(build_failed["session_path"]) / "host-access.json").exists()
            evidence["build_failure"] = build_failed
            manifest.write_text(original)
            command("configure_play", bridge=address(), editor_endpoint=editor_address,
                    endpoint_token_file=str(token), release=False)
            command("play")
            pending = phase("starting_server", previous=build_failed["session_id"])
            command("stop")
            cancelled = phase("exited")
            assert cancelled["server"]["exited"] and cancelled["client"] is None
            assert cancelled["resources_removed"] and unrelated.poll() is None
            evidence["startup_cancellation"] = cancelled
            command("configure_play", bridge=game_address, editor_endpoint=editor_address,
                    endpoint_token_file=str(token), release=False)
            command("play")
            third = phase("running", previous=cancelled["session_id"])
            # Bridge restart changes registration IDs without granting process ownership.
            bridge.process.terminate()
            bridge.process.wait(timeout=10)
            time.sleep(0.5)
            bridge = Mcp(start("nico-bridge", "--listen", game_address, "--editor-listen", editor_address,
                               "--editor-token-file", str(token), mcp=True))
            editor_id = ready(editor)["instance_id"]
            unrelated_instance = ready(unrelated)
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                reconnected = state()["play_session"]
                if all(reconnected[role].get("connected") and reconnected[role].get("instance_id") != third[role]["instance_id"] for role in ("client", "server")):
                    break
                time.sleep(0.1)
            else:
                raise TimeoutError("owned hosts did not reconnect after bridge restart")
            for role in ("client", "server"):
                assert reconnected[role]["pid"] == third[role]["pid"]
                assert reconnected[role]["identity"] == third[role]["identity"]
            evidence["bridge_reconnect"] = reconnected
            # Revoked endpoint access requires direct-child fallback, never a broad kill.
            endpoint_secret = token.read_text()
            token.unlink()
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                inaccessible = state()["play_session"]
                if all(not inaccessible[role].get("connected") for role in ("client", "server")):
                    break
                time.sleep(0.1)
            else:
                raise TimeoutError("endpoint revocation was not observed")
            command("stop")
            forced = phase("exited")
            assert forced["resources_removed"]
            assert all(forced[role]["forced"] and forced[role]["exited"] for role in ("client", "server"))
            assert unrelated.poll() is None
            evidence["fallback_stop"] = forced
            token.write_text(endpoint_secret)
            token.chmod(0o600)
            command("play")
            third = phase("running", previous=forced["session_id"])
            # Closing the editor owns cleanup; disconnecting MCP alone does not.
            bridge.game(editor_id, "stop")
            assert editor.wait(timeout=30) == 0
            assert pathlib.Path(third["content_path"]).samefile(project)
            assert not (pathlib.Path(third["session_path"]) / "host-access.json").exists()
            assert cache_marker.read_text() == "preserve project cache"
            assert pathlib.Path(third["server_data"]).is_dir()
            assert unrelated.poll() is None
            evidence["editor_exit_session"] = third
            evidence["sampling"] = "GPU captures and state sampled separately; no user-observed acceptance claim."
            (args.evidence / "evidence.json").write_text(json.dumps(evidence, indent=2))
            bridge.game(unrelated_instance["instance_id"], "stop")
            assert unrelated.wait(timeout=15) == 0
            print("Automated play control stopped; all test-owned windows closed. PASS", flush=True)
        finally:
            if bridge and editor_id:
                try:
                    bridge.game(editor_id, "editor_command", {"action": "stop"})
                    deadline = time.monotonic() + 20
                    while state().get("play_session", {}).get("active") and time.monotonic() < deadline:
                        time.sleep(0.1)
                    bridge.game(editor_id, "stop")
                except (AssertionError, OSError, TimeoutError):
                    pass
            for process in reversed(processes):
                if process.poll() is None:
                    process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
            for log in logs:
                log.close()


if __name__ == "__main__":
    main()
