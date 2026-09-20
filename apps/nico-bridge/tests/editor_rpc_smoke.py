"""Validate release-server debug opt-in and editor RPC through an isolated bridge.

Build nico-bridge (debug) and minimal-game-server (--release) first. No windows open;
only this test's subprocesses are cleaned up. Native editor UI acceptance is separate.
"""
import argparse
import json
import pathlib
import secrets
import socket
import subprocess
import tempfile
import time

from native_smoke import Mcp


def address():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return f"127.0.0.1:{sock.getsockname()[1]}"


class Editor:
    def __init__(self, endpoint, token):
        host, port = endpoint.split(":")
        self.socket = socket.create_connection((host, int(port)), timeout=7)
        self.file = self.socket.makefile("rwb")
        assert not self.request(protocol=1, token=token).get("isError")

    def request(self, **request):
        self.file.write(json.dumps(request).encode() + b"\n")
        self.file.flush()
        data = self.file.readline(256 * 1024 + 1)
        assert data.endswith(b"\n") and len(data) <= 256 * 1024
        return json.loads(data)

    def call(self, tool, **arguments):
        return self.request(method="call", tool_name=tool, arguments=arguments)

    def close(self):
        self.file.close()
        self.socket.close()


def error(result, code):
    assert result.get("isError") and result["structuredContent"]["error"]["code"] == code, result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bridge", type=pathlib.Path, required=True)
    parser.add_argument("--release-server", type=pathlib.Path, required=True)
    args = parser.parse_args()
    processes = []
    logs = []
    editors = []
    with tempfile.TemporaryDirectory(prefix="nico-editor-rpc-") as directory:
        root = pathlib.Path(directory)
        endpoint_token, host_token = secrets.token_hex(32), secrets.token_hex(32)
        token_file, policy = root / "endpoint.token", root / "host.json"
        token_file.write_text(endpoint_token)
        policy.write_text(json.dumps({"grants": [{"credential": host_token}]}))
        for path in (token_file, policy):
            path.chmod(0o600)
        game_address, editor_address = address(), address()
        while editor_address == game_address:
            editor_address = address()

        def start(binary, *arguments, mcp=False):
            log = (root / f"process-{len(processes)}.log").open("w")
            logs.append(log)
            process = subprocess.Popen([str(binary.resolve()), *arguments],
                                       stdin=subprocess.PIPE if mcp else subprocess.DEVNULL,
                                       stdout=subprocess.PIPE if mcp else subprocess.DEVNULL,
                                       stderr=log, text=True)
            processes.append(process)
            return process

        def bridge():
            return Mcp(start(args.bridge, "--listen", game_address,
                             "--editor-listen", editor_address,
                             "--editor-token-file", str(token_file), mcp=True))

        def editor():
            value = Editor(editor_address, endpoint_token)
            editors.append(value)
            return value

        try:
            mcp = bridge()
            disabled = start(args.release_server, "--bridge", game_address)
            # Explicit custom address must not bypass the release default. Observe
            # a live process for two heartbeat windows, never just a failed launch.
            for _ in range(20):
                assert disabled.poll() is None
                assert not mcp.call("list_instances", {})["instances"]
                time.sleep(0.1)
            disabled.terminate()  # Debugging is deliberately disabled in this fixture.
            disabled.wait(timeout=10)

            server = start(args.release_server, "--enable-debug", "--bridge", game_address,
                           "--debug-access-file", str(policy))
            first = mcp.ready("server")
            status = mcp.call("instance_status", {"instance_id": first})
            assert status["pid"] == server.pid
            import hashlib
            assert status["identity"]["build_id"] == hashlib.sha256(args.release_server.read_bytes()).hexdigest()
            identity = status["identity"]
            mcp.game(first, "game_state")
            error(mcp.request("tools/call", {"name": "call_game_tool", "arguments": {
                "instance_id": first, "tool_name": "stop", "arguments": {}}}), "access_denied")
            connection = editor()
            error(connection.request(method="attach", instance_id=first,
                                     api_version="wrong", credential=host_token), "incompatible_instance")
            attach = connection.request(method="attach", instance_id=first,
                                        api_version="1", credential=host_token)
            assert not attach.get("isError"), attach
            assert attach["structuredContent"]["debug_access"]["nico.debug"]["permissions"] == ["inspect"]
            assert not connection.call("game_state").get("isError")
            page = connection.call("debug_entities", limit=1)["structuredContent"]
            reference = page["entities"][0]["reference"]
            assert reference["process_session"] == identity["process_session"]
            detail = connection.call("debug_entity", snapshot_id=page["snapshot_id"], reference=reference)["structuredContent"]
            assert detail["tick"] == page["tick"] and "position" in detail["properties"]
            error(connection.call("stop"), "access_denied")
            assert not connection.request(method="detach").get("isError")
            assert server.poll() is None
            assert mcp.game(first, "status")["ready"]
            assert not connection.request(method="attach", instance_id=first,
                                          api_version="1", credential=host_token).get("isError")
            policy.write_text(json.dumps({"grants": []}))
            error(connection.call("game_state"), "access_denied")
            assert server.poll() is None
            policy.write_text(json.dumps({"grants": [{"credential": host_token}]}))
            assert not connection.call("game_state").get("isError")

            # Bridge exit leaves the release game alive. It reconnects with a new
            # instance ID; neither endpoint silently redirects the stale ID.
            connection.close()
            editors.remove(connection)
            mcp.close()
            assert server.poll() is None
            mcp = bridge()
            second = mcp.ready("server")
            assert second != first
            assert mcp.call("instance_status", {"instance_id":second})["identity"] == identity
            connection = editor()
            error(connection.request(method="attach", instance_id=first,
                                     api_version="1", credential=host_token), "incompatible_instance")
            assert not connection.request(method="attach", instance_id=second,
                                          api_version="1", credential=host_token).get("isError")
            error(connection.call("debug_entity", snapshot_id=page["snapshot_id"], reference=reference), "stale_entity")
            assert not connection.call("debug_entities", limit=1).get("isError")
            assert not connection.call("diagnostics", limit=8).get("isError")
            policy.write_text(json.dumps({"grants": [{"credential": host_token,
                                                      "permissions": ["inspect", "stop"]}]}))
            assert connection.call("stop")["structuredContent"]["accepted"]
            assert server.wait(timeout=10) == 0
            connection.close()
            editors.remove(connection)
            mcp.close()
            for log in logs:
                log.flush()
                contents = pathlib.Path(log.name).read_text()
                assert endpoint_token not in contents and host_token not in contents
            print("PASS: release default, explicit opt-in, inspection, denied stop, host revocation, detach survival, bridge restart, stale IDs, diagnostics, authorized stop, credential redaction, executable identity, paged inspection, and reconnect handle invalidation")
        finally:
            for connection in editors:
                connection.close()
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
