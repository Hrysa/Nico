"""Check Windows daemon survival when the first frontend's process job closes."""
import argparse
import ctypes
from ctypes import wintypes
import json
from pathlib import Path
import queue
import socket
import subprocess
import tempfile
import threading
import time


class BasicLimits(ctypes.Structure):
    _fields_ = [
        ("process_time", ctypes.c_int64), ("job_time", ctypes.c_int64),
        ("flags", wintypes.DWORD), ("min_working", ctypes.c_size_t),
        ("max_working", ctypes.c_size_t), ("process_limit", wintypes.DWORD),
        ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD),
        ("scheduling", wintypes.DWORD),
    ]


class ExtendedLimits(ctypes.Structure):
    _fields_ = [
        ("basic", BasicLimits), ("io", ctypes.c_uint64 * 6),
        ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
        ("peak_process", ctypes.c_size_t), ("peak_job", ctypes.c_size_t),
    ]


class Mcp:
    def __init__(self, command, suspended=False):
        self.process = subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True,
            creationflags=subprocess.CREATE_NO_WINDOW | (4 if suspended else 0),
        )
        self.output = queue.Queue()
        self.request_id = 0
        def read():
            for line in self.process.stdout:
                self.output.put(json.loads(line))
            self.output.put(None)
        threading.Thread(target=read, daemon=True).start()

    def send(self, value):
        self.process.stdin.write(json.dumps(value) + "\n")
        self.process.stdin.flush()

    def request(self, method, params):
        self.request_id += 1
        self.send(dict(jsonrpc="2.0", id=self.request_id, method=method, params=params))
        while True:
            reply = self.output.get(timeout=15)
            assert reply is not None, self.process.stderr.read()
            if "id" not in reply:
                continue
            assert reply["id"] == self.request_id and "error" not in reply, reply
            return reply["result"]

    def initialize(self):
        self.request("initialize", dict(protocolVersion="2025-11-25", capabilities={},
                                        clientInfo=dict(name="job-test", version="1")))
        self.send(dict(jsonrpc="2.0", method="notifications/initialized"))

    def pid(self):
        return self.request("tools/call", dict(name="bridge_status", arguments={}))[
            "structuredContent"]["bridge_pid"]

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bin-dir", default="target/bridge-validation/debug")
    args = parser.parse_args()
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    resume = ctypes.WinDLL("ntdll").NtResumeProcess
    resume.argtypes = [wintypes.HANDLE]
    resume.restype = ctypes.c_long
    job = kernel.CreateJobObjectW(None, None)
    assert job, ctypes.WinError(ctypes.get_last_error())
    limits = ExtendedLimits()
    limits.basic.flags = 0x2000  # Kill on close; breakaway is deliberately forbidden.
    assert kernel.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits))
    with socket.socket() as port:
        port.bind(("127.0.0.1", 0))
        address = port.getsockname()
    with tempfile.TemporaryDirectory(prefix="nico daemon job ") as state:
        command = [str(Path(args.bin_dir).resolve() / "nico-mcp-bridge.exe"),
                   "--listen", f"127.0.0.1:{address[1]}", "--state-dir", state,
                   "--idle-seconds", "1"]
        first = second = None
        try:
            first = Mcp(command, suspended=True)
            assert kernel.AssignProcessToJobObject(job, int(first.process._handle)), ctypes.WinError(ctypes.get_last_error())
            assert resume(int(first.process._handle)) == 0
            first.initialize()
            daemon_pid = first.pid()
            second = Mcp(command)
            second.initialize()
            assert second.pid() == daemon_pid
            assert kernel.CloseHandle(job)
            job = None
            first.process.wait(timeout=5)
            assert second.pid() == daemon_pid
            print(f"PASS: daemon {daemon_pid} survived first frontend job termination")
        finally:
            if job:
                kernel.CloseHandle(job)
            if first:
                first.close()
            if second:
                second.close()
        deadline = time.monotonic() + 10
        while True:
            # A connection itself resets idle time, so probes must be farther apart than the timeout.
            time.sleep(1.5)
            with socket.socket() as probe:
                probe.settimeout(0.2)
                if probe.connect_ex(address) != 0:
                    break
            assert time.monotonic() < deadline, "daemon did not exit when idle"
        # Let process-local file handles close before removing this test's state folder.
        time.sleep(0.3)
        print("PASS: daemon exited after the final frontend disconnected")


if __name__ == "__main__":
    main()
