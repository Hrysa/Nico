"""Isolated arena desktop checks through real MCP; opens one client window."""
import argparse, json, math, pathlib, shutil, socket, subprocess, time, sys
sys.dont_write_bytecode = True
from native_smoke import Mcp

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--bin-dir',type=pathlib.Path,required=True)
    parser.add_argument('--output-dir',type=pathlib.Path,default=pathlib.Path('target/arena-native-evidence'))
    parser.add_argument('--combat-only',action='store_true',help='Skip window transition/focus checks; still verify gameplay, captures, diagnostics and orderly stop')
    args=parser.parse_args(); args.output_dir.mkdir(parents=True,exist_ok=True)
    (args.output_dir/'report.json').write_text(json.dumps({'result':'running'}),encoding='utf-8')
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0)); address=f'127.0.0.1:{sock.getsockname()[1]}'
    processes=[]; logs=[]
    def start(name,*arguments,mcp=False):
        path=args.bin_dir.resolve()/(name+('.exe' if __import__('os').name=='nt' else ''))
        log=(args.output_dir/f'{len(logs)}-{name}.log').open('w',encoding='utf-8'); logs.append(log)
        p=subprocess.Popen([str(path),*arguments],stdin=subprocess.PIPE if mcp else subprocess.DEVNULL,
            stdout=subprocess.PIPE if mcp else subprocess.DEVNULL,stderr=log,text=True,encoding='utf-8',
            creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0)); processes.append(p); return p
    try:
        server=start('arena-arpg-server','--bridge',address)
        bridge=Mcp(start('nico-bridge','--listen',address,mcp=True)); sid=bridge.ready('server')
        client=start('arena-arpg-client','--bridge',address,'--background'); cid=bridge.ready('client')
        catalog=json.dumps(bridge.call('list_game_tools',{}))
        assert all(name in catalog for name in ('client_control','window_state','window_control'))
        def command(instance,name,**values):
            state=bridge.game(instance,'game_state')
            accepted=bridge.game(instance,name,dict(run_id=state['run_id'],**values))
            deadline=time.monotonic()+8
            while time.monotonic()<deadline:
                result=bridge.game(instance,'game_command',{'command_id':accepted['command_id']})
                if result['state'] not in ('pending','running'):
                    assert result['state']=='completed',result
                    return result
                time.sleep(.01)
            raise TimeoutError(name)
        def capture(name):
            result=bridge.game(cid,'window_snapshot'); deadline=time.monotonic()+10
            while result['state']=='pending' and time.monotonic()<deadline:
                time.sleep(.03);result=bridge.game(cid,'window_snapshot',{'request_id':result['request_id']})
            assert result['state']=='ready',result
            shutil.copyfile(result['path'],args.output_dir/name)
        command(cid,'game_restart');capture('arena-start.png')
        edit=bridge.game(cid,'client_control',{'action':'camera','yaw':.45,'pitch':.65});deadline=time.monotonic()+5
        while time.monotonic()<deadline:
            view=bridge.game(cid,'client_state')
            if view['last_applied_command']>=edit['command_id']:break
            time.sleep(.02)
        assert abs(view['camera']['yaw']-.45)<.0001,view
        capture('arena-orbit.png')
        buffer_checks=[]
        for instance in (sid,cid):
            command(instance,'game_restart')
            attack=command(instance,'game_attack',yaw=0)
            command(instance,'game_move',x=0,z=0,ticks=10)
            dodge=command(instance,'game_dodge',x=1,z=0)
            assert dodge['start_tick']>=attack['start_tick']+18,(attack,dodge)
            assert dodge['applied_ticks']==1,dodge
            buffer_checks.append({'role':'server' if instance==sid else 'client','attack':attack,'dodge':dodge})
        runs=[];captured_waves=set()
        for instance in (sid,cid):
            started=time.monotonic()
            command(instance,'game_restart');command(instance,'game_dodge',x=1,z=0)
            deadline=time.monotonic()+180;waves=set();intermissions=set()
            while time.monotonic()<deadline:
                state=bridge.game(instance,'game_state')
                if state['state']!='playing':break
                waves.add(state['wave'])
                if state['intermission_ticks']>0:
                    if instance==cid and state['wave'] not in intermissions:
                        capture(f"arena-wave-{state['wave']}-cleared.png")
                    intermissions.add(state['wave']);time.sleep(.025);continue
                if instance==cid and state['wave']>1 and state['wave_tick']<50 and state['wave'] not in captured_waves:
                    capture(f"arena-wave-{state['wave']}.png");captured_waves.add(state['wave'])
                hero=state['actors'][0]
                if hero['action']['kind']=='idle':
                    target=min((m for m in state['actors'][1:] if m['health']>0),key=lambda m:sum((m['position'][k]-hero['position'][k])**2 for k in ('x','z')))
                    dx=target['position']['x']-hero['position']['x'];dz=target['position']['z']-hero['position']['z'];d=math.hypot(dx,dz)
                    threats=[m for m in state['actors'][1:] if m['health']>0 and m['action']['phase']=='windup' and math.hypot(m['position']['x']-hero['position']['x'],m['position']['z']-hero['position']['z'])<m['attack_range']+.8]
                    threat=next((m for m in threats if m['action']['phase_ticks_remaining']<=12),None)
                    if threat and hero['dodge_cooldown']==0:command(instance,'game_dodge',x=threat['facing']['z'],z=-threat['facing']['x'])
                    elif d<=2 and not threats:command(instance,'game_attack',yaw=math.atan2(dx,dz))
                    elif d>2:command(instance,'game_move',x=dx/d,z=dz/d,ticks=1)
                time.sleep(.012)
            assert state['state']=='won' and state['wave']==3 and waves=={1,2,3} and intermissions=={1,2},state
            runs.append({'role':'server' if instance==sid else 'client','win_tick':state['tick'],'waves':sorted(waves),'health':state['actors'][0]['health'],'automated_win_elapsed_seconds':round(time.monotonic()-started,3)})
            if instance==cid:capture('arena-victory.png')
            command(instance,'game_restart');deadline=time.monotonic()+25;telegraph_captured=False
            while time.monotonic()<deadline:
                state=bridge.game(instance,'game_state')
                if state['state']=='lost':break
                if instance==cid and not telegraph_captured and any(a['action'].get('phase')=='windup' and 9<=a['action'].get('phase_ticks_remaining',0)<=12 for a in state['actors'][1:]):
                    capture('arena-telegraph.png');telegraph_captured=True
                time.sleep(.025)
            assert state['state']=='lost',state
            if instance==cid:capture('arena-defeat.png')
            command(instance,'game_restart')
        transitions=[];outcome=None
        if not args.combat_only:
            transitions=[]
            def window(action,predicate,**values):
                accepted=bridge.game(cid,'window_control',dict(action=action,**values))
                deadline=time.monotonic()+8
                while time.monotonic()<deadline:
                    observed=bridge.game(cid,'window_state',{'request_id':accepted['request_id']})
                    assert observed['request_state']!='failed',observed
                    if observed['request_state']=='applied' and predicate(observed):
                        transitions.append(dict(action=action,observed=observed));return observed
                    time.sleep(.025)
                raise TimeoutError(f'window {action}: {observed}')
            def recovered():
                start=time.monotonic();initial=bridge.game(cid,'status')['graphics']['presented_frames']
                deadline=start+8
                while time.monotonic()<deadline:
                    current=bridge.game(cid,'status')
                    assert current['failure'] is None,current
                    if current['graphics']['presented_frames']>=initial+10:
                        return {'presentations':current['graphics']['presented_frames']-initial,'elapsed_seconds':round(time.monotonic()-start,3)}
                    time.sleep(.025)
                raise TimeoutError('presentations did not resume')
            for width,height in ((800,600),(1100,700),(640,480),(1280,720)):
                window('resize',lambda s: abs(s['logical_size'][0]-width)<2 and abs(s['logical_size'][1]-height)<2,width=width,height=height)
                transitions[-1]['recovery']=recovered()
            window('maximize',lambda s:s['maximized'])
            transitions[-1]['recovery']=recovered()
            window('restore',lambda s:not s['maximized'] and s['minimized'] is False)
            transitions[-1]['recovery']=recovered()
            # This foreground request affects only our test-owned window. Minimize must
            # release real pointer capture and cancel a runtime-owned movement lease.
            window('focus',lambda s:s['focused'])
            command(cid,'game_restart')
            window('pointer_capture',lambda s:s['pointer_captured'],value=True)
            state=bridge.game(cid,'game_state')
            movement=bridge.game(cid,'game_move',{'run_id':state['run_id'],'x':1,'z':0,'ticks':120})
            window('minimize',lambda s:s['minimized'] is True and not s['focused'] and not s['pointer_captured'])
            window('pointer_capture',lambda s:not s['pointer_captured'],value=False)
            time.sleep(.15)
            window('restore',lambda s:s['minimized'] is False and not s['maximized'])
            transitions[-1]['recovery']=recovered()
            outcome=bridge.game(cid,'game_command',{'command_id':movement['command_id']})
            assert outcome['state']=='cancelled' and outcome['reason']=='focus_lost',outcome
            capture('arena-restored.png')
            status=bridge.game(cid,'status');assert status['graphics']['presented_frames']>0 and status['failure'] is None,status
            window('pointer_capture',lambda s:not s['pointer_captured'],value=False)
        status=bridge.game(cid,'status')
        assert status['graphics']['presented_frames']>0 and status['failure'] is None,status
        view=bridge.game(cid,'client_state')
        report={'result':'passed','scope':'combat_only' if args.combat_only else 'combat_and_window_lifecycle','buffer_checks':buffer_checks,'runs':runs,'window_transitions':transitions,'focus_loss_command':outcome,'status':status,'client_view':view,'diagnostics':{role:bridge.game(i,'diagnostics') for role,i in [('client',cid),('server',sid)]}}
        for instance,p in ((cid,client),(sid,server)):
            assert bridge.game(instance,'stop')['accepted'];assert p.wait(timeout=10)==0
        bridge.close();(args.output_dir/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(json.dumps({'result':'passed','output':str(args.output_dir.resolve()),'presented_frames':status['graphics']['presented_frames']}))
    except Exception as error:
        (args.output_dir/'report.json').write_text(json.dumps({'result':'failed','error':str(error)}),encoding='utf-8')
        raise
    finally:
        for p in reversed(processes):
            if p.poll() is None:
                p.terminate()
                try:p.wait(timeout=5)
                except subprocess.TimeoutExpired:p.kill();p.wait(timeout=5)
        for log in logs:log.close()
if __name__=='__main__':main()
