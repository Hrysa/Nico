"""Exercise real, independently launched games through bridge MCP on a desktop.

Build binaries first, then run with --bin-dir <Cargo output directory>.
This opens a native client window and stops only processes started by this test.
"""

import argparse
import json
import pathlib
import queue
import socket
import subprocess
import tempfile
import threading
import time


class Mcp:
    def __init__(self, process):
        self.process = process
        self.messages = queue.Queue()
        self.next_id = 0
        self.notifications = 0

        def read():
            for line in process.stdout:
                self.messages.put(json.loads(line))

        threading.Thread(target=read, daemon=True).start()
        self.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                   "clientInfo": {"name": "nico-native-smoke", "version": "1"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def request(self, method, params):
        self.next_id += 1
        self.send({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params})
        deadline = time.monotonic() + 15
        while True:
            message = self.messages.get(timeout=max(0.01, deadline - time.monotonic()))
            if message.get("method") == "notifications/tools/list_changed":
                self.notifications += 1
                continue
            assert message.get("id") == self.next_id, message
            assert "error" not in message, message
            return message["result"]

    def call(self, name, arguments):
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        assert not result.get("isError"), result
        return result["structuredContent"]

    def ready(self, role, excluding=()):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            for instance in self.call("list_instances", {})["instances"]:
                if (instance["role"] == role and instance["connected"] and instance["ready"]
                        and instance["instance_id"] not in excluding):
                    assert instance["host"]["active"], instance
                    return instance["instance_id"]
            time.sleep(0.05)
        raise TimeoutError(f"{role} never became ready")

    def game(self, instance, tool, arguments=None):
        return self.call("call_game_tool", {"instance_id": instance, "tool_name": tool,
                                           "arguments": arguments or {}})

    def close(self):
        self.process.stdin.close()
        assert self.process.wait(timeout=10) == 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bin-dir", type=pathlib.Path, required=True)
    parser.add_argument("--sample", choices=("2d", "3d"), default="2d")
    args = parser.parse_args()
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        address = f"127.0.0.1:{reservation.getsockname()[1]}"
    processes = []
    with tempfile.TemporaryDirectory(prefix="nico-bridge-smoke-") as temp:
        logs = []

        def start(name, *arguments, mcp=False):
            path = args.bin_dir.resolve() / (name + (".exe" if __import__("os").name == "nt" else ""))
            log_path = pathlib.Path(temp) / f"{len(logs)}-{name}.log"
            log = log_path.open("w", encoding="utf-8")
            logs.append((log, log_path))
            process = subprocess.Popen([str(path), *arguments],
                                       stdin=subprocess.PIPE if mcp else subprocess.DEVNULL,
                                       stdout=subprocess.PIPE if mcp else subprocess.DEVNULL,
                                       stderr=log, text=True, encoding="utf-8",
                                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            processes.append(process)
            return process

        try:
            # Independently launched server may precede the bridge.
            server = start("minimal-game-server", "--bridge", address)
            bridge = Mcp(start("nico-bridge", "--listen", address, mcp=True))
            server_id = bridge.ready("server")
            client = start("minimal-game-client", "--bridge", address, "--sample", args.sample, "--background")
            client_id = bridge.ready("client")
            # Discover the new sample tools before invoking them. These edits are
            # queued; acceptance is distinct from runtime application.
            catalogs = bridge.call("list_game_tools", {})
            assert "sample_control" in json.dumps(catalogs), catalogs

            def sample_until(predicate):
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    state = bridge.game(client_id, "sample_state")
                    if predicate(state):
                        return state
                    time.sleep(0.02)
                raise TimeoutError(f"sample condition not met: {state}")

            def edit(action, **values):
                accepted = bridge.game(client_id, "sample_control", {"action": action, **values})
                assert accepted["accepted"], accepted
                state = sample_until(lambda state: state["last_applied_command"] >= accepted["command_id"])
                outcome = next(item for item in state["command_results"] if item["command_id"] == accepted["command_id"])
                assert outcome["error"] is None, state
                return state

            initial = sample_until(lambda state: state["texture_state"] == "ready")
            assert initial["snapshot_sequence"] >= 1 and initial["snapshot_age_ms"] >= 0, initial
            assert initial["closed"] is False, initial
            if args.sample == "3d":
                loaded = sample_until(lambda state: state["mesh_state"] == "ready" and state["checker_state"] == "ready")
                assert loaded["mesh_vertices"] == 24 and loaded["mesh_indices"] == 36, loaded
                assert loaded["mesh_draws"] == 1 and loaded["world_quads"] == 0, loaded
                rejected = bridge.request("tools/call", {"name": "call_game_tool", "arguments": {
                    "instance_id": client_id, "tool_name": "sample_control",
                    "arguments": {"action": "set_camera3d", "x": 0.0001, "y": 0, "z": 0}}})
                assert rejected.get("isError"), rejected
                assert bridge.game(client_id, "status")["failure"] is None
                rotated = edit("set_mesh_yaw", radians=0.5)
                assert rotated["mesh_yaw"] == 0.5, rotated
                camera3d = edit("set_camera3d", x=3, y=2, z=4)
                assert camera3d["camera3d_position"] == [3, 2, 4], camera3d
                # Quaternion poses exercise roll and vertical views without world-up reconstruction.
                for quaternion in ((0, 0, 2 ** -0.5, 2 ** -0.5), (2 ** -0.5, 0, 0, 2 ** -0.5)):
                    oriented = edit("set_camera_orientation", **dict(zip(("x", "y", "z", "w"), quaternion)))
                    assert all(abs(a - b) < 1e-5 for a, b in zip(oriented["camera3d_orientation"], quaternion)), oriented
                    sample_until(lambda state: state["frame"] >= oriented["frame"] + 2)
                    assert bridge.game(client_id, "status")["failure"] is None
                edit("set_camera3d", x=3, y=2, z=4)
                edit("set_mesh_enabled", value=False)
                unloaded = sample_until(lambda state: not state["mesh_resident"])
                assert unloaded["texture_state"] == "ready" and unloaded["hud_quads"] == 1, unloaded
                edit("set_mesh_enabled", value=True)
                sample_until(lambda state: state["mesh_state"] == "ready")
            assert "window_snapshot" in json.dumps(catalogs), catalogs
            capture = bridge.game(client_id, "window_snapshot")
            request_id = capture["request_id"]
            deadline = time.monotonic() + 10
            while capture["state"] == "pending" and time.monotonic() < deadline:
                time.sleep(0.02)
                capture = bridge.game(client_id, "window_snapshot", {"request_id": request_id})
                assert bridge.game(client_id, "status")["failure"] is None
            assert capture["state"] == "ready", capture
            image = pathlib.Path(capture["path"]).read_bytes()
            assert image[:8] == b"\x89PNG\r\n\x1a\n", capture
            assert int.from_bytes(image[16:20], "big") == capture["width"], capture
            assert int.from_bytes(image[20:24], "big") == capture["height"], capture
            assert bridge.game(client_id, "window_snapshot", {"request_id": request_id}) == capture
            moved = edit("set_position", x=2, y=1)
            assert moved["positions"] == [[2, 1]], moved
            camera = edit("set_camera", x=1, y=0)
            assert camera["camera_center"] == [1, 0] and camera["hud_center"] == [48, 48], camera
            hidden = edit("set_sprite_visible", value=False)
            assert hidden["world_quads"] == 0 and hidden["hud_quads"] == 1, hidden
            assert hidden["mesh_draws"] == 0, hidden
            edit("set_texture_enabled", value=False)
            sample_until(lambda state: not state["texture_resident"] and (args.sample != "3d" or not state["checker_resident"]))
            edit("set_texture_enabled", value=True)
            sample_until(lambda state: state["texture_state"] == "ready")
            if args.sample == "3d":
                sample_until(lambda state: state["checker_state"] == "ready")
            edit("set_sprite_visible", value=True)
            edit("set_camera", x=0, y=0)
            edit("set_position", x=0, y=0)
            initial_diagnostics = {}
            for role, instance in [("server", server_id), ("client", client_id)]:
                state = bridge.call(f"minimal_game.{role}.game_state", {"instance_id": instance, "arguments": {}})
                assert state["frame_updates"] > 0 and state["entity_count"] == 1, state
                host_status = bridge.game(instance, "status")
                if role == "client":
                    assert host_status["graphics"]["presented_frames"] > 0, host_status
                else:
                    assert host_status["graphics"] is None, host_status
                page = bridge.game(instance, "diagnostics")
                assert page["records"] and page["capacity"] == 256, page
                assert any(record["level"] == "INFO" for record in page["records"]), page
                initial_diagnostics[role] = page["records"][0]
            cached = bridge.call("instance_status", {"instance_id": client_id})
            assert cached["host"]["graphics"]["presented_frames"] > 0, cached
            bridge.close()
            time.sleep(0.2)
            assert server.poll() is None and client.poll() is None, "bridge shutdown stopped gameplay"
            bridge = Mcp(start("nico-bridge", "--listen", address, mcp=True))
            new_server_id = bridge.ready("server")
            new_client_id = bridge.ready("client")
            assert new_server_id != server_id and new_client_id != client_id
            for role, instance in [("server", new_server_id), ("client", new_client_id)]:
                page = bridge.game(instance, "diagnostics", {"after": 0, "limit": 1})
                assert page["records"][0] == initial_diagnostics[role], page
            bridge.game(new_server_id, "stop")
            assert server.wait(timeout=10) == 0
            assert client.poll() is None, "stopping server affected client"
            # Independently restart the game while Codex's bridge connection remains stable.
            server = start("minimal-game-server", "--bridge", address)
            restarted_id = bridge.ready("server", excluding=(new_server_id,))
            assert restarted_id != new_server_id
            assert bridge.game(restarted_id, "game_state")["frame_updates"] > 0
            bridge.game(new_client_id, "stop")
            bridge.game(restarted_id, "stop")
            assert client.wait(timeout=10) == 0 and server.wait(timeout=10) == 0
            deadline = time.monotonic() + 5
            while bridge.call("bridge_status", {})["connected_instances"]:
                assert time.monotonic() < deadline
                time.sleep(0.02)
            for instance in (new_client_id, restarted_id):
                status = bridge.call("instance_status", {"instance_id": instance})
                assert status["host"]["state"] == "stopped", status
                if instance == new_client_id:
                    assert status["host"]["graphics"]["presented_frames"] > 0, status
            assert bridge.call("list_game_tools", {})["catalogs"], "offline schemas were lost"
            bridge.close()
            print(json.dumps({"result": "passed", "sample": args.sample, "sample_controls": True, "window_snapshot": True, "client_and_server_ready": True,
                              "custom_tools_routed": True, "diagnostics_survive_bridge_restart": True,
                              "graphics_status_retained": True, "games_survive_bridge_restart": True,
                              "server_restarted_without_bridge_restart": True,
                              "independent_stops": True, "final_status_retained": True,
                              "list_change_notifications": bridge.notifications}))
        finally:
            for process in processes:
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=3)
            for log, path in logs:
                log.close()
                text = path.read_text(encoding="utf-8", errors="replace")
                for line in text.splitlines():
                    if "graphics device created" in line or "WARN" in line:
                        print(line)


if __name__ == "__main__":
    main()
