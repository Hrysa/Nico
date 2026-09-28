"""Private loopback OpenSSH fixture; never changes system SSH configuration."""
import os
import pathlib
import pwd
import shutil
import socket
import subprocess
import time

from editor_rpc_smoke import address


class SshTunnel:
    def __init__(self, root, endpoint, evidence):
        self.root = pathlib.Path(root) / "ssh"
        self.root.mkdir(mode=0o700)
        self.endpoint = endpoint
        self.forward = address()
        self.listen = address()
        while self.forward in (self.listen, endpoint):
            self.forward = address()
        self.server = self.client = None
        self.logs = []
        self.ssh = shutil.which("ssh")
        self.sshd = shutil.which("sshd") or "/usr/sbin/sshd"
        keygen = shutil.which("ssh-keygen")
        if not self.ssh or not keygen or not pathlib.Path(self.sshd).exists():
            raise RuntimeError("OpenSSH client/server/keygen are required for this fixture")
        for name in ("host", "client", "wrong"):
            subprocess.run([keygen, "-q", "-t", "ed25519", "-N", "", "-f", str(self.root / name)], check=True)
        (self.root / "authorized_keys").write_text((self.root / "client.pub").read_text())
        (self.root / "authorized_keys").chmod(0o600)
        self.user = pwd.getpwuid(os.getuid()).pw_name
        port = self.listen.split(":")[1]
        host_key = " ".join((self.root / "host.pub").read_text().split()[:2])
        (self.root / "known_hosts").write_text(f"[127.0.0.1]:{port} {host_key}\n")
        (self.root / "sshd_config").write_text(f"""Port {port}
ListenAddress 127.0.0.1
HostKey {self.root / 'host'}
PidFile {self.root / 'sshd.pid'}
AuthorizedKeysFile {self.root / 'authorized_keys'}
AllowUsers {self.user}
AuthenticationMethods publickey
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
PermitRootLogin no
StrictModes yes
AllowTcpForwarding local
PermitOpen {endpoint}
AllowAgentForwarding no
X11Forwarding no
PermitTTY no
ForceCommand /usr/bin/false
LogLevel VERBOSE
""")
        self.evidence = pathlib.Path(evidence)

    def _log(self, name):
        log = (self.evidence / name).open("w")
        self.logs.append(log)
        return log

    def _ssh_args(self, key="client"):
        return [self.ssh, "-F", os.devnull, "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes",
                "-o", "IdentityAgent=none", "-o", "StrictHostKeyChecking=yes", "-o",
                f"UserKnownHostsFile={self.root / 'known_hosts'}", "-o", "ConnectTimeout=3",
                "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=1", "-o",
                "ServerAliveCountMax=2", "-i", str(self.root / key), "-p", self.listen.split(":")[1],
                "-N", "-L", f"{self.forward}:{self.endpoint}", f"{self.user}@127.0.0.1"]

    @staticmethod
    def _ready(process, address):
        host, port = address.split(":")
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(f"SSH fixture process exited {process.returncode}; inspect retained SSH logs")
            try:
                with socket.create_connection((host, int(port)), timeout=0.2):
                    return
            except OSError:
                time.sleep(0.05)
        raise TimeoutError("SSH fixture listener did not become ready")

    def start(self):
        self.server = subprocess.Popen([self.sshd, "-D", "-e", "-f", str(self.root / "sshd_config")],
                                       stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                       stderr=self._log("ssh-server.log"))
        self._ready(self.server, self.listen)
        rejected = subprocess.run(self._ssh_args("wrong"), stdin=subprocess.DEVNULL,
                                  capture_output=True, text=True, timeout=8)
        assert rejected.returncode != 0 and "Permission denied" in rejected.stderr, rejected.stderr
        self.start_forward()

    def start_forward(self):
        self.client = subprocess.Popen(self._ssh_args(), stdin=subprocess.DEVNULL,
                                       stdout=subprocess.DEVNULL, stderr=self._log("ssh-client.log"))
        self._ready(self.client, self.forward)

    def disconnect(self):
        if self.client is not None and self.client.poll() is None:
            self.client.terminate()
            self.client.wait(timeout=5)

    def close(self):
        self.disconnect()
        if self.server is not None and self.server.poll() is None:
            self.server.terminate()
            self.server.wait(timeout=5)
        for log in self.logs:
            log.close()
