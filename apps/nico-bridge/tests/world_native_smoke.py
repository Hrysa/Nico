"""Two real world clients, isolated bridge/server, persistent saves and GPU captures.

Run from the repository root after building all three binaries. Opens two windows;
never attaches to or stops an existing user's game/bridge. No desktop-success claim.
"""
import argparse
import json
import math
import os
import pathlib
import shutil
import socket
import subprocess
import sys
import tempfile
import time

sys.dont_write_bytecode = True
from native_smoke import Mcp


def endpoint():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return f'127.0.0.1:{sock.getsockname()[1]}'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bin-dir', type=pathlib.Path, required=True)
    parser.add_argument('--output-dir', type=pathlib.Path, default=pathlib.Path('target/world-native-evidence'))
    args = parser.parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    processes, logs, hosts = [], [], []
    report = {'result': 'running', 'captures': [], 'checks': {},
              'sampling': 'GPU PNGs and before/after snapshots are separate samples.',
              'user_observation': 'Not collected; no desktop visibility claim.'}
    (args.output_dir / "report.json").write_text(json.dumps(report), encoding="utf-8")
    bridge_address, game_address = endpoint(), endpoint()
    bridge = None

    def start(name, *arguments, mcp=False):
        exe = args.bin_dir.resolve() / (name + ('.exe' if os.name == 'nt' else ''))
        log = (args.output_dir / f'{len(logs)}-{name}.log').open('w', encoding='utf-8')
        logs.append(log)
        process = subprocess.Popen([str(exe), *arguments], stdin=subprocess.PIPE if mcp else subprocess.DEVNULL,
                                   stdout=subprocess.PIPE if mcp else subprocess.DEVNULL, stderr=log,
                                   text=True, encoding='utf-8', creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        processes.append(process)
        return process

    def until(read, predicate, timeout=10):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            value = read()
            if predicate(value):
                return value
            time.sleep(.015)
        raise TimeoutError(f'condition not met: {value}')

    def discover(process, role):
        identity = bridge.ready(role, excluding=[i for i, _ in hosts])
        instances = bridge.call('list_instances', {})['instances']
        entry = next(i for i in instances if i['instance_id'] == identity)
        assert entry['pid'] == process.pid, entry
        bridge.call('list_game_tools', {})
        hosts.append((identity, process))
        report.setdefault('instances', []).append(entry)
        return identity

    def state(identity):
        return bridge.game(identity, 'world_client_state')

    def actor(snapshot):
        return next(o for o in snapshot['authoritative']['objects'] if o['id'] == snapshot['authoritative']['player'])

    def command(identity, action, wait=True, **values):
        accepted = bridge.game(identity, 'world_action', {'action': action, **values})
        if not wait:
            return accepted
        def completed(s):
            result = next((c for c in s['commands'] if c['command_id'] == accepted['command_id']), None)
            if result is None:
                return False
            assert result['state'] in ('applied', 'submitted'), result
            return result['state'] == 'applied' or (
                s['epoch'] == result['epoch'] and s['authoritative']['acknowledged_input'] >= result['sequence'])
        return until(lambda: state(identity), completed)

    def capture(identity, filename):
        before = state(identity)
        requested = bridge.game(identity, 'window_snapshot')
        result = until(lambda: bridge.game(identity, 'window_snapshot', {'request_id': requested['request_id']}),
                       lambda r: r['state'] != 'pending')
        assert result['state'] == 'ready', result
        target = args.output_dir / filename
        shutil.copyfile(result['path'], target)
        assert target.read_bytes()[:8] == b'\x89PNG\r\n\x1a\n'
        report['captures'].append({'instance_id': identity, 'file': filename, 'before': before,
                                   'capture': result, 'after': state(identity)})

    def go(identity, x, z):
        for _ in range(100):
            p = actor(state(identity))['position']
            dx, dz = x-p['x'], z-p['z']
            distance = math.hypot(dx, dz)
            if distance < .15:
                return
            command(identity, 'move', x=dx/distance, z=dz/distance,
                    ticks=min(90, max(1, int(distance*15))))
        raise AssertionError('movement did not reach target')

    def record(snapshot):
        p = actor(snapshot)
        return {**{k: p[k] for k in ('position', 'health', 'equipped')},
                **{k: snapshot['authoritative'][k] for k in ('experience', 'inventory')}}

    save_directory = tempfile.TemporaryDirectory(prefix="nico-world-native-")
    saves = save_directory.name
    try:
        bridge = Mcp(start('nico-bridge', '--listen', bridge_address, mcp=True))
        def launch_server():
            process = start('arena-arpg-server', '--bridge', bridge_address, '--listen', game_address, '--data-dir', saves)
            return process, discover(process, 'server')
        server, sid = launch_server()
        clients = []
        for name in ('alice', 'bob'):
            process = start('arena-arpg-client', '--bridge', bridge_address, '--server', game_address,
                            '--character', name, '--background')
            identity = discover(process, 'client')
            until(lambda: state(identity), lambda s: s['connection'] == 'connected')
            clients.append((identity, process))
        a, b = [c[0] for c in clients]
        print(json.dumps({'event': 'control_started', 'instances': report['instances']}), flush=True)
        for identity in (a, b):
            command(identity, 'move', wait=False, x=0, z=-1, ticks=120)
            time.sleep(.3)
            capture(identity, f'{identity[-2:]}-movement.png')
            until(lambda: state(identity), lambda s: s['active_movement'] is None)
            assert len([o for o in state(identity)['authoritative']['objects'] if o['kind'] == 'player']) == 2
        # Observe an actual authoritative dodge and capture that client while active.
        command(a, 'dodge', wait=False, x=1, z=0)
        dodging = until(lambda: state(a), lambda s: actor(s)['action']['kind'] == 'dodge')
        report['checks']['dodge'] = dodging
        capture(a, 'dodge.png')
        until(lambda: state(a), lambda s: actor(s)['action']['kind'] == 'idle')
        # Approach through the clear middle path, away from the authored camp.
        go(a, .8, -21)
        go(b, -1.4, -21)
        spawn = bridge.game(sid, 'world_spawn', {'kind': 'brute', 'x': -.3, 'z': -18})
        spawned = until(lambda: bridge.game(sid, 'world_state'),
                        lambda s: any(c['command_id'] == spawn['command_id'] for c in s['commands']))
        result = next(c for c in spawned['commands'] if c['command_id'] == spawn['command_id'])
        assert result['state'] == 'completed', result
        monster = result['entity']
        combat, attacking = [], set()
        deadline = time.monotonic() + 18
        while time.monotonic() < deadline:
            world = bridge.game(sid, 'world_state')
            enemy = next(o for o in world['objects'] if o['id'] == monster)
            combat.append({'tick': world['tick'], 'objects': world['objects']})
            if enemy['health'] == 0:
                break
            for identity in (a, b):
                p = actor(state(identity))
                assert p['health'] > 0, ('combat death', identity, p)
                dx, dz = enemy['position']['x']-p['position']['x'], enemy['position']['z']-p['position']['z']
                distance = math.hypot(dx, dz)
                if distance > 1.7:
                    command(identity, 'move', x=dx/distance, z=dz/distance, ticks=5)
                elif p['action']['kind'] == 'idle':
                    command(identity, 'attack', yaw=math.atan2(dx, dz))
                    attacking.add(identity)
            if len(attacking) == 2 and not report['checks'].get('combat_capture'):
                capture(a, 'cooperative-combat.png')
                report['checks']['combat_capture'] = True
            time.sleep(.02)
        assert enemy['health'] == 0 and len(attacking) == 2, combat[-1]
        report['checks']['cooperative_combat'] = combat
        awarded = until(lambda: {i: state(i) for i in (a, b)},
                        lambda states: any(s['authoritative']['experience'] > 0 for s in states.values()))
        winner = next(i for i, s in awarded.items() if s['authoritative']['experience'] > 0)
        other = b if winner == a else a
        s = state(winner)
        loot = next(o for o in s['authoritative']['objects'] if o['kind'] == 'loot')
        p = actor(s)['position']
        if math.hypot(loot['position']['x']-p['x'], loot['position']['z']-p['z']) > 1.9:
            go(winner, loot['position']['x'], loot['position']['z'])
        command(winner, 'pickup', id=loot['id'])
        equipped = command(winner, 'equip')
        assert actor(equipped)['equipped'] == 'iron_sword' and equipped['authoritative']['inventory'] == ['iron_sword']
        report['checks']['loot_equipment'] = equipped
        capture(winner, 'equipped.png')
        go(winner, .8, -35)
        # The existing monster respawns after 15 seconds. The idle other player
        # remains near its home, proving death and respawn through normal AI.
        dead = until(lambda: state(other), lambda s: actor(s)['health'] == 0, timeout=45)
        report['checks']['death'] = dead
        capture(other, 'death.png')
        until(lambda: state(other), lambda s: s['authoritative']['tick'] >= actor(s)['action']['respawn_tick'])
        respawn = command(other, 'respawn')
        assert actor(respawn)['health'] == 100, respawn
        report['checks']['respawn'] = respawn
        go(other, -3.6, -35)
        before = state(winner)
        command(winner, 'reconnect')
        restored = until(lambda: state(winner), lambda s: s['connection'] == 'connected' and s['epoch'] > before['epoch'])
        assert record(restored) == record(before), (record(before), record(restored))
        report['checks']['reconnect'] = {'before': before, 'after': restored}
        before_restart = {i: state(i) for i in (a, b)}
        assert bridge.game(sid, 'stop')['accepted']
        assert server.wait(timeout=10) == 0
        server, sid = launch_server()
        for identity in (a, b):
            before = before_restart[identity]
            restored = until(lambda: state(identity), lambda s: s['connection'] == 'connected' and s['epoch'] > before['epoch'])
            assert record(restored) == record(before), (record(before), record(restored))
            report['checks'].setdefault('restart', []).append({'before': before, 'after': restored})
        capture(winner, 'restart-restored.png')
        for identity, process in clients + [(sid, server)]:
            assert bridge.game(identity, 'status')['failure'] is None
            assert bridge.game(identity, 'stop')['accepted']
            assert process.wait(timeout=10) == 0
        report['result'] = 'passed'
    except Exception as error:
        report.update(result='failed', error=str(error))
        raise
    finally:
        # Only test-owned processes. Prefer orderly MCP shutdown even on failure.
        for identity, process in reversed(hosts):
            if process.poll() is None and bridge is not None:
                try:
                    bridge.game(identity, 'stop')
                    process.wait(timeout=10)
                except Exception:
                    pass
        if bridge is not None and bridge.process.poll() is None:
            try:
                bridge.close()
            except Exception:
                pass
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        save_directory.cleanup()
        for log in logs:
            log.close()
        report['process_exits'] = [{'pid': p.pid, 'exit_code': p.returncode} for p in processes]
        (args.output_dir / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(json.dumps({'event': 'control_stopped', 'windows_open': False, 'result': report['result']}), flush=True)


if __name__ == '__main__':
    main()
