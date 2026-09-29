"""Check default splash, loading progress, and Meadow through an isolated bridge."""
import argparse
import json
import pathlib
import shutil
import socket
import subprocess
import sys
import tempfile
import time

sys.dont_write_bytecode = True
from mcp_client import Mcp


def address(kind=socket.SOCK_STREAM):
    with socket.socket(type=kind) as sock:
        sock.bind(("127.0.0.1", 0))
        return f"127.0.0.1:{sock.getsockname()[1]}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bin-dir", type=pathlib.Path, required=True)
    parser.add_argument("--output-dir", type=pathlib.Path,
                        default=pathlib.Path("target/splash-native-evidence"))
    args = parser.parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    processes, logs = [], []
    report = {"result": "running", "samples": [], "captures": []}
    bridge_address, server_address = address(), address(socket.SOCK_DGRAM)

    def start(name, *arguments, mcp=False):
        suffix = ".exe" if sys.platform == "win32" else ""
        binary = args.bin_dir.resolve() / (name + suffix)
        log = (args.output_dir / f"{name}.log").open("w", encoding="utf-8")
        logs.append(log)
        process = subprocess.Popen(
            [str(binary), *arguments],
            stdin=subprocess.PIPE if mcp else subprocess.DEVNULL,
            stdout=subprocess.PIPE if mcp else subprocess.DEVNULL,
            stderr=log, text=True, encoding="utf-8")
        processes.append(process)
        return process

    try:
        with tempfile.TemporaryDirectory(prefix="nico-splash-world-") as data:
            bridge = Mcp(start("nico-mcp-bridge", "--listen", bridge_address, mcp=True))
            server = start("arena-arpg-server", "--bridge", bridge_address,
                           "--listen", server_address, "--data-dir", data)
            sid = bridge.ready("server")
            client = start("arena-arpg-client", "--bridge", bridge_address,
                           "--server", server_address, "--background")
            print(json.dumps({"event": "observation_started", "pid": client.pid}), flush=True)
            phases = set()
            captured = set()
            deadline = time.monotonic() + 45
            final_id = None
            while time.monotonic() < deadline:
                instances = bridge.call("list_instances", {})["instances"]
                current = next((i for i in instances if i["pid"] == client.pid and i["connected"]), None)
                if current is None:
                    time.sleep(.01)
                    continue
                cid = current["instance_id"]
                try:
                    state = bridge.game(cid, "scene_loading")
                except AssertionError:
                    # A scene transition replaces the registration. Rediscover its new ID.
                    continue
                phase = state["phase"]
                phases.add(phase)
                report["samples"].append({"instance_id": cid, "pid": client.pid, "loading": state})
                assert phase != "failed", state
                activated = phase != "loaded" or state["active_scene_generation"] == 2
                if phase in ("splash", "loading", "preparing", "loaded") and phase not in captured and current["ready"] and activated:
                    before = state
                    try:
                        capture = bridge.game(cid, "window_snapshot")
                        capture_deadline = time.monotonic() + 5
                        while capture["state"] == "pending" and time.monotonic() < capture_deadline:
                            time.sleep(.01)
                            capture = bridge.game(cid, "window_snapshot", {"request_id": capture["request_id"]})
                        if capture["state"] == "ready":
                            name = phase + ".png"
                            shutil.copyfile(capture["path"], args.output_dir / name)
                            after = bridge.game(cid, "scene_loading")
                            report["captures"].append({"file": name, "instance_id": cid,
                                                       "pid": client.pid, "before": before, "after": after})
                            captured.add(phase)
                    except AssertionError:
                        pass
                if phase == "loaded" and state["active_scene_generation"] == 2 and "loaded" in captured:
                    assert state["total"] > 0 and state["completed"] == state["total"], state
                    final_id = cid
                    break
                time.sleep(.01)
            assert final_id is not None, report
            assert "splash" in phases and phases.intersection({"loading", "preparing"}), phases
            assert "splash" in captured, captured
            frames = [sample["loading"]["application_frames"] for sample in report["samples"]]
            generations = {sample["loading"]["active_scene_generation"] for sample in report["samples"]}
            assert frames == sorted(frames), "application frame count reset during scene switch"
            assert {1, 2}.issubset(generations), generations
            report["root_lifecycle"] = {"frames_before": frames[0], "frames_after": frames[-1],
                                        "scene_generations": sorted(g for g in generations if g is not None)}
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                world = bridge.game(final_id, "world_client_state")
                if world["connection"] == "connected":
                    break
                time.sleep(.03)
            assert world["connection"] == "connected", world
            report["world_after_loading"] = world
            report["scene_info"] = {}
            for role, instance in (("client", final_id), ("server", sid)):
                scene = bridge.game(instance, "scene_info")
                assert scene["content_revision"] is None, scene
                assert scene["definition_state"] == "parsed_and_validated", scene
                assert scene["scene"].replace("\\", "/").endswith("meadow.scene.toml"), scene
                report["scene_info"][role] = scene
            capture = bridge.game(final_id, "window_snapshot")
            deadline = time.monotonic() + 10
            while capture["state"] == "pending" and time.monotonic() < deadline:
                time.sleep(.03)
                capture = bridge.game(final_id, "window_snapshot", {"request_id": capture["request_id"]})
            assert capture["state"] == "ready", capture
            shutil.copyfile(capture["path"], args.output_dir / "meadow-connected.png")
            report["status"] = bridge.game(final_id, "status")
            assert report["status"]["graphics"]["presented_frames"] > 0
            assert report["status"]["failure"] is None
            for instance, process in ((final_id, client), (sid, server)):
                assert bridge.game(instance, "stop")["accepted"]
                assert process.wait(timeout=10) == 0
            bridge.close()
            report["result"] = "passed"
            report["sampling"] = "Captures and loading snapshots are separate samples. Desktop visibility is not verified."
            print(json.dumps({"event": "observation_stopped", "window_open": False}), flush=True)
    finally:
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        for log in logs:
            log.close()
        (args.output_dir / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
