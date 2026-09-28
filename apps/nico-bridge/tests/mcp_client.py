"""Shared MCP client for isolated native tests."""
import json
import queue
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
