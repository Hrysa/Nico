pub mod environment;
pub mod network;
mod prediction;
mod visuals;
use crate::{
    camera::Camera,
    character::{CharacterAssets, definition::VisualDefinition},
};
use arena_arpg_shared::{
    Vec2,
    open_world::{ITEM_SWORD, ObjectKind, PlayerInput},
};
use network::WorldClient;
use nico_input::{
    InputDeviceKind as Kind, InputState,
    fixed::{FixedInput, InputFrame},
};
use nico_ops::{
    commands::FifoCommands,
    mcp::{CallToolResult, Tool, ToolExtensions},
    publication::Publication,
};
use nico_presentation::{Scene3d, UiScene};
use nico_runtime::{AppBuilder, RuntimeError, Stage, events::EventReader};
use nico_winit::{NativeWindowState, WindowFocusLost, keyboard, pointer};
use serde::Deserialize;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};
#[derive(Clone, Copy, Default)]
pub struct FrameInput {
    movement: [f32; 2],
    look: [f32; 2],
    pressed: [bool; 6],
}
pub fn map_input(input: &InputState, output: &mut Vec<FrameInput>) {
    let key = |key| input.button(Kind::Keyboard, key);
    let edge = |key| input.just_pressed(Kind::Keyboard, key);
    output.push(FrameInput {
        movement: [
            key(keyboard::D) as u8 as f32 - key(keyboard::A) as u8 as f32,
            key(keyboard::W) as u8 as f32 - key(keyboard::S) as u8 as f32,
        ],
        look: input.motion(Kind::Pointer, pointer::MOTION),
        pressed: [
            input.just_pressed(Kind::Pointer, pointer::LEFT),
            edge(keyboard::SPACE),
            edge(keyboard::E),
            edge(keyboard::F),
            edge(keyboard::R),
            edge(keyboard::Q),
        ],
    });
}
#[derive(Deserialize)]
#[serde(tag = "action", rename_all = "snake_case", deny_unknown_fields)]
enum Edit {
    Move { x: f64, z: f64, ticks: u16 },
    Attack { yaw: f64 },
    Dodge { x: f64, z: f64 },
    Pickup { id: u64 },
    Talk,
    Equip,
    Respawn,
    Reconnect,
    Camera { yaw: f32, pitch: f32, distance: f32 },
}
impl Edit {
    fn valid(&self) -> bool {
        match self {
            Self::Move { x, z, ticks } => {
                x.is_finite()
                    && z.is_finite()
                    && x * x + z * z <= 1.00001
                    && (1..=120).contains(ticks)
            }
            Self::Dodge { x, z } => {
                x.is_finite() && z.is_finite() && x * x + z * z <= 1.00001 && x * x + z * z > 0.
            }
            Self::Attack { yaw } => yaw.is_finite() && yaw.abs() <= std::f64::consts::PI,
            Self::Pickup { id } => *id > 0,
            Self::Camera {
                yaw,
                pitch,
                distance,
            } => {
                yaw.is_finite()
                    && yaw.abs() <= std::f32::consts::PI
                    && pitch.is_finite()
                    && (0.15..=1.1).contains(pitch)
                    && distance.is_finite()
                    && (0.5..=20.).contains(distance)
            }
            _ => true,
        }
    }
}
struct Operations {
    commands: FifoCommands<(u64, Edit), Value, 32>,
    snapshot: Publication<Value>,
    movement: Option<Movement>,
}
struct Movement {
    id: u64,
    direction: Vec2,
    remaining: u16,
}
fn error(code: &str) -> CallToolResult {
    CallToolResult::structured_error(json!({"error":{"code":code}}))
}
fn schema() -> serde_json::Map<String, Value> {
    let number = json!({"type":"number","minimum":-1,"maximum":1});
    json!({"type":"object","oneOf":[
        {"properties":{"action":{"const":"move"},"x":number,"z":number,"ticks":{"type":"integer","minimum":1,"maximum":120}},"required":["action","x","z","ticks"],"additionalProperties":false},
        {"properties":{"action":{"const":"attack"},"yaw":{"type":"number","minimum":-std::f64::consts::PI,"maximum":std::f64::consts::PI}},"required":["action","yaw"],"additionalProperties":false},
        {"properties":{"action":{"const":"dodge"},"x":number,"z":number},"required":["action","x","z"],"additionalProperties":false},
        {"properties":{"action":{"const":"pickup"},"id":{"type":"integer","minimum":1}},"required":["action","id"],"additionalProperties":false},
        {"properties":{"action":{"enum":["talk","equip","respawn","reconnect"]}},"required":["action"],"additionalProperties":false},
        {"properties":{"action":{"const":"camera"},"yaw":{"type":"number","minimum":-std::f32::consts::PI,"maximum":std::f32::consts::PI},"pitch":{"type":"number","minimum":0.15,"maximum":1.1},"distance":{"type":"number","minimum":0.5,"maximum":20}},"required":["action","yaw","pitch","distance"],"additionalProperties":false}
    ]}).as_object().unwrap().clone()
}
pub fn register(
    mut builder: AppBuilder,
    client: WorldClient,
    assets: [Option<Arc<CharacterAssets>>; 3],
    definitions: [VisualDefinition; 3],
    environment: environment::Environment,
) -> std::io::Result<(AppBuilder, ToolExtensions)> {
    let ops = Arc::new(Mutex::new(Operations {
        commands: FifoCommands::new(128),
        snapshot: Publication::default(),
        movement: None,
    }));
    let mut tools = ToolExtensions::default();
    let read = ops.clone();
    tools.register(Tool::new("world_client_state","Inspect connection, authoritative snapshot, local prediction, acknowledgement, rendering and client command outcomes. Submitted means queued to network; compare sequence with authoritative acknowledged_input and resulting state.",json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone()),move|args|{
        if !args.is_empty(){return error("invalid_arguments");}read.lock().unwrap().snapshot.json().map(CallToolResult::structured).unwrap_or_else(||error("not_ready"))
    })?;
    let write = ops.clone();
    tools.register(Tool::new("world_action","Queue local player input or camera/reconnect control at a fixed boundary. Move lasts bounded ticks. Poll world_client_state.commands; submitted is not proof of server action success. Never blindly retry timeouts.",schema()),move|args|{
        let Ok(edit)=serde_json::from_value::<Edit>(Value::Object(args))else{return error("invalid_arguments");};if !edit.valid(){return error("invalid_arguments");}
        let mut ops=write.lock().unwrap();if ops.commands.is_closed(){return error("shutting_down");}
        match ops.commands.submit(|id|(id,edit)){Ok(id)=>CallToolResult::structured(json!({"accepted":true,"command_id":id})),Err(_)=>error("busy")}
    })?;
    let inspection = json!({"definitions":definitions,"hero_imported":assets[0].is_some(),"hero_resolved":assets[0].as_ref().map(|a|a.inspection()),"resolved":assets.iter().map(|a|a.as_ref().map(|a|a.inspection())).collect::<Vec<_>>()});
    tools.register(
        Tool::new(
            "client_characters",
            "Inspect loaded character visual content.",
            json!({"type":"object","properties":{},"additionalProperties":false})
                .as_object()
                .unwrap()
                .clone(),
        ),
        move |args| {
            if args.is_empty() {
                CallToolResult::structured(inspection.clone())
            } else {
                error("invalid_arguments")
            }
        },
    )?;
    builder.insert_resource(client);
    builder.insert_resource(Camera::default());
    builder.insert_resource(Scene3d::default());
    builder.insert_resource(UiScene::default());
    let mut frames = EventReader::<FrameInput>::new();
    let mut focus = EventReader::<WindowFocusLost>::new();
    let mut held = FixedInput::<2, 2, 6>::default();
    let control = ops.clone();
    builder.add_system(Stage::FixedUpdate,"world_client::network_input",move|ctx|{
        let window=ctx.world.resource::<NativeWindowState>().cloned().unwrap_or_default();
        for frame in ctx.events.read(&mut frames){held.push(InputFrame{held:frame.movement,deltas:frame.look,pressed:frame.pressed});}
        let lost=ctx.events.read(&mut focus).count()!=0;
        if lost||!window.focused||!window.pointer_captured{held.clear();}
        let sample=held.take();let human=sample.held!=[0.;2]||sample.pressed.iter().any(|x|*x);
        let mut ops=control.lock().unwrap();
        let mut movement=ops.movement.take();
        if (human||lost)&&let Some(active)=movement.take(){ops.commands.record(json!({"command_id":active.id,"state":"cancelled","reason":"human_input_or_focus_loss"}));}
        let client=ctx.world.resource_mut::<WorldClient>()?;
        client.poll();
        if client.status=="connected"&&!client.input_ready(){
            // Do not consume transient human input or queued tool actions while
            // the server is catching up. Movement leases count sent inputs.
            held.push(sample);
            ops.movement=movement;
            return Ok(());
        }
        let camera=ctx.world.resource_mut::<Camera>()?;camera.orbit(sample.deltas);let yaw=f64::from(camera.rig.yaw()).clamp(-std::f64::consts::PI,std::f64::consts::PI);
        let mut direction=nico_presentation_control::coordinates::rotate_on_floor(yaw,[-f64::from(sample.held[0]),f64::from(sample.held[1])]);let length=direction[0].hypot(direction[1]).max(1.);direction[0]/=length;direction[1]/=length;
        let mut input=PlayerInput{movement:Vec2::new(direction[0],direction[1]),..Default::default()};
        let request=ops.commands.pop();let mut command=None;let mut reconnect=sample.pressed[5];
        if let Some((id,edit))=request {
            if let Some(active)=movement.take(){ops.commands.record(json!({"command_id":active.id,"state":"cancelled","reason":"replaced"}));}
            match edit{
                Edit::Camera{yaw,pitch,distance}=>{camera.rig.set_angles(yaw,pitch);camera.rig.set_distance(distance);ops.commands.record(json!({"command_id":id,"state":"applied"}));},
                Edit::Reconnect=>{reconnect=true;ops.commands.record(json!({"command_id":id,"state":"applied"}));},
                Edit::Move{x,z,ticks}=>movement=Some(Movement{id,direction:Vec2::new(x,z),remaining:ticks}),
                edit=>{command=Some(id);match edit{Edit::Attack{yaw}=>input.attack_yaw=Some(yaw),Edit::Dodge{x,z}=>input.dodge=Some(Vec2::new(x,z)),Edit::Pickup{id}=>input.pickup=Some(id),Edit::Talk=>input.talk=true,Edit::Equip=>input.equip=Some(ITEM_SWORD.into()),Edit::Respawn=>input.respawn=true,_=>unreachable!()};}
            }
        }
        let client=ctx.world.resource_mut::<WorldClient>()?;
        if reconnect{client.reconnect();}
        if sample.pressed[0]{input.attack_yaw=Some(yaw);}
        if sample.pressed[1]{input.dodge=Some(if input.movement.x!=0.||input.movement.z!=0.{input.movement}else{client.prediction.as_ref().map_or(Vec2::new(yaw.sin(),yaw.cos()),|p|p.actor.facing)});}
        if sample.pressed[2]&&let Some(snapshot)=&client.latest&&let Some(local)=snapshot.objects.iter().find(|o|o.id==snapshot.player){input.pickup=snapshot.objects.iter().filter(|o|o.kind==ObjectKind::Loot&&(o.position.x-local.position.x).hypot(o.position.z-local.position.z)<=2.).min_by_key(|o|o.id).map(|o|o.id);}
        if sample.pressed[2] && input.pickup.is_none(){input.talk=true;}
        if sample.pressed[3]{input.equip=Some(ITEM_SWORD.into());}input.respawn|=sample.pressed[4];
        if let Some(active)=&mut movement{input.movement=active.direction;active.remaining-=1;if active.remaining==0{command=Some(active.id);movement=None;}}
        if client.status=="connected"{
            match client.send(input){Ok(sequence)=>{if let Some(id)=command{ops.commands.record(json!({"command_id":id,"state":"submitted","sequence":sequence,"epoch":client.epoch}));}},Err(message)=>{if let Some(id)=command{ops.commands.record(json!({"command_id":id,"state":"rejected","error":message}));}client.fail(message);}}
        }else{
            if let Some(active)=movement.take(){command=Some(active.id);}
            if let Some(id)=command{ops.commands.record(json!({"command_id":id,"state":"rejected","error":"not_connected"}));}
        }
        ops.movement=movement;
        Ok(())
    });
    let read = ops.clone();
    let mut visuals = visuals::Visuals::new(assets, definitions, environment);
    builder.add_system(Stage::Update,"world_client::extract",move|ctx|{
        let window=ctx.world.resource::<NativeWindowState>().cloned().unwrap_or_default();
        let client=ctx.world.resource::<WorldClient>()?;let position=client.prediction.as_ref().map(|p|p.actor.position).unwrap_or(client.zone.settlement);let obstacles=client.zone.obstacles.clone();let dt=ctx.time.delta();
        let camera=ctx.world.resource_mut::<Camera>()?;
        let view=camera.rig.view([position.x as f32,1.2,position.z as f32],dt.as_secs_f32(),|sweep|obstacles.iter().filter_map(|o|sweep.cast_aabb(std::array::from_fn(|i|(o.center[i]-o.size[i]/2.)as f32),std::array::from_fn(|i|(o.center[i]+o.size[i]/2.)as f32))).reduce(f32::min));
        let camera_info=json!({"yaw":camera.rig.yaw(),"pitch":camera.rig.pitch(),"distance":camera.rig.distance(),"position":view.position});
        let client=ctx.world.resource::<WorldClient>()?;
        let (scene,hud)=visuals.render(client,view,window.logical_size,window.pointer_captured,dt).map_err(|message|RuntimeError::System{stage:"Update",name:"world_client::extract".into(),message})?;
        if scene.meshes.len()>256{return Err(RuntimeError::System{stage:"Update",name:"world_client::extract".into(),message:"world draw budget exceeded".into()});}
        let mut ops=read.lock().unwrap();let state=json!({"connection":client.status,"error":client.error,"last_disconnect":client.last_disconnect,"input_ready":client.input_ready(),"character":client.name,"server":client.address.to_string(),"epoch":client.epoch,"sent_input":client.sequence,"authoritative":client.latest,"server_snapshot_age_ms":client.received.map(|t|t.elapsed().as_millis()as u64),"prediction":client.prediction.as_ref().map(|p|json!({"actor":p.actor,"pending_inputs":p.pending.len(),"acknowledged_input":p.acknowledged,"correction_m":p.correction_m,"tick":p.tick})),"camera":camera_info,"animation":visuals.animation,"actor_animations":visuals.actor_animations,"environment":visuals.environment.inspection,"rendered_meshes":scene.meshes.len(),"active_movement":ops.movement.as_ref().map(|m|json!({"command_id":m.id,"remaining_inputs":m.remaining})),"commands":ops.commands.history()});ops.snapshot.publish(state);
        *ctx.world.resource_mut::<Scene3d>()?=scene;*ctx.world.resource_mut::<UiScene>()?=hud;Ok(())
    });
    builder.add_system(Stage::Shutdown, "world_client::close", move |ctx| {
        ctx.world.resource_mut::<WorldClient>()?.close();
        let mut ops = ops.lock().unwrap();
        ops.close();
        Ok(())
    });
    Ok((builder, tools))
}

impl Operations {
    fn close(&mut self) {
        if let Some(active) = self.movement.take() {
            self.commands
                .record(json!({"command_id":active.id,"state":"cancelled","reason":"shutdown"}));
        }
        self.commands
            .close_with(|(id, _)| json!({"command_id":id,"state":"cancelled","reason":"shutdown"}));
        let mut state = self.snapshot.get().cloned().unwrap_or_else(|| json!({}));
        state["connection"] = json!("closed");
        state["input_ready"] = json!(false);
        state["active_movement"] = Value::Null;
        state["commands"] = json!(self.commands.history());
        self.snapshot.publish(state);
        self.snapshot.close();
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn shutdown_finishes_active_and_queued_commands_in_final_publication() {
        let mut ops = Operations {
            commands: FifoCommands::new(128),
            snapshot: Publication::default(),
            movement: None,
        };
        let first = ops
            .commands
            .submit(|id| {
                (
                    id,
                    Edit::Move {
                        x: 0.,
                        z: 1.,
                        ticks: 120,
                    },
                )
            })
            .unwrap();
        ops.commands.pop().unwrap();
        ops.movement = Some(Movement {
            id: first,
            direction: Vec2::new(0., 1.),
            remaining: 99,
        });
        let second = ops.commands.submit(|id| (id, Edit::Equip)).unwrap();
        ops.snapshot.publish(json!({"connection":"connected"}));
        ops.close();
        let final_state = ops.snapshot.json().unwrap();
        assert_eq!(final_state["closed"], true);
        assert_eq!(final_state["connection"], "closed");
        assert_eq!(final_state["input_ready"], false);
        assert!(final_state["active_movement"].is_null());
        for id in [first, second] {
            assert!(
                final_state["commands"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|c| c["command_id"] == id
                        && c["state"] == "cancelled"
                        && c["reason"] == "shutdown")
            );
        }
        assert!(ops.commands.is_closed());
    }
}
