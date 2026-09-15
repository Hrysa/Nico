//! Optional arena tool catalog. Register alongside ArenaPlugin; engine hosts own
//! transport. Handlers only access bounded requests, outcomes, and owned snapshots.
mod schema;
#[cfg(test)]
mod tests;

use crate::{Action, Arena, RunState, Snapshot, StepReport, TickInput, Vec2};
use nico_ops::mcp::{CallToolResult, Map, Tool, ToolExtensions, Value};
use nico_runtime::{AppBuilder, Plugin, RuntimeResult, Stage};
use serde_json::json;
use std::{
    io,
    sync::{Arc, Mutex},
};

const HISTORY: usize = 128;
const NAMES: [&str; 6] = [
    "game_state",
    "game_move",
    "game_attack",
    "game_dodge",
    "game_restart",
    "game_command",
];

/// Adds publication/shutdown integration and the six discoverable game tools.
/// Add ArenaPlugin to the builder as well. No service or transport is started here.
pub fn register(builder: AppBuilder) -> io::Result<(AppBuilder, ToolExtensions)> {
    let operations = Operations::default();
    let mut extensions = ToolExtensions::default();
    for name in NAMES {
        let tool = Tool::new(
            name,
            schema::description(name),
            schema::input(name).as_object().unwrap().clone(),
        )
        .with_raw_output_schema(schema::output(name).as_object().unwrap().clone().into());
        let endpoint = operations.clone();
        extensions.register(tool, move |arguments| endpoint.call(name, arguments))?;
    }
    Ok((builder.add_plugin(ToolsPlugin(operations)), extensions))
}

struct ToolsPlugin(Operations);
impl Plugin for ToolsPlugin {
    fn build(&self, builder: &mut AppBuilder) -> RuntimeResult<()> {
        builder.insert_resource(self.0.clone());
        let operations = self.0.clone();
        builder.add_system(Stage::Startup, "arena::publish_initial", move |context| {
            operations.publish(context.world.resource::<Arena>()?.snapshot());
            Ok(())
        });
        let operations = self.0.clone();
        builder.add_system(Stage::Shutdown, "arena::close_operations", move |context| {
            operations.close(context.world.resource::<Arena>()?.snapshot());
            Ok(())
        });
        Ok(())
    }
}

#[derive(Clone, Default)]
pub(crate) struct Operations(Arc<Mutex<State>>);
struct State {
    published: nico_ops::publication::Publication<Snapshot>,
    commands: nico_ops::commands::CommandBook<Request, Record, 3>,
}
impl Default for State {
    fn default() -> Self {
        Self {
            published: Default::default(),
            commands: nico_ops::commands::CommandBook::new(HISTORY),
        }
    }
}
#[derive(Clone, Copy)]
enum Kind {
    Move(Vec2, u16),
    Attack(f64),
    Dodge(Vec2),
    Restart,
}
impl Kind {
    fn slot(self) -> usize {
        match self {
            Self::Move(..) => 0,
            Self::Attack(_) | Self::Dodge(_) => 1,
            Self::Restart => 2,
        }
    }
}
struct Request {
    kind: Kind,
    record: Record,
}
#[derive(Clone, Debug)]
struct Record {
    id: u64,
    source_run: u64,
    result_run: u64,
    status: &'static str,
    start: Option<u64>,
    end: Option<u64>,
    applied: u16,
    reason: Option<&'static str>,
}
impl Record {
    fn value(&self) -> Value {
        json!({"command_id":self.id,"source_run_id":self.source_run,"result_run_id":self.result_run,
            "state":self.status,"start_tick":self.start,"end_tick":self.end,"applied_ticks":self.applied,"reason":self.reason})
    }
}
impl State {
    fn publish(&mut self, snapshot: &Snapshot) {
        self.published.publish(snapshot.clone());
    }
    fn finish(
        &mut self,
        mut request: Request,
        status: &'static str,
        reason: Option<&'static str>,
        snapshot: &Snapshot,
    ) {
        request.record.status = status;
        request.record.reason = reason;
        request.record.end = Some(snapshot.tick);
        request.record.result_run = snapshot.run_id;
        self.commands.record(request.record);
    }
    fn cancel(&mut self, slot: usize, reason: &'static str, snapshot: &Snapshot) {
        if let Some(request) = self.commands[slot].take() {
            self.finish(request, "cancelled", Some(reason), snapshot);
        }
    }
}
fn error(code: &'static str) -> CallToolResult {
    CallToolResult::structured_error(json!({"error":{"code":code}}))
}
fn exact(arguments: &Map<String, Value>, keys: &[&str]) -> bool {
    arguments.len() == keys.len() && keys.iter().all(|key| arguments.contains_key(*key))
}
fn positive(arguments: &Map<String, Value>, key: &str) -> Option<u64> {
    arguments.get(key)?.as_u64().filter(|v| *v > 0)
}
fn direction(arguments: &Map<String, Value>) -> Option<Vec2> {
    let x = arguments.get("x")?.as_f64()?;
    let z = arguments.get("z")?.as_f64()?;
    (x.is_finite() && z.is_finite() && x.abs() <= 1.0 && z.abs() <= 1.0).then_some(Vec2::new(x, z))
}
fn mutation(name: &str, args: &Map<String, Value>) -> Option<(u64, Kind)> {
    let run_id = positive(args, "run_id")?;
    let kind = match name {
        "game_move" if exact(args, &["run_id", "x", "z", "ticks"]) => {
            let ticks = args.get("ticks")?.as_u64()?;
            if !(1..=120).contains(&ticks) {
                return None;
            }
            Kind::Move(direction(args)?, ticks as u16)
        }
        "game_attack" if exact(args, &["run_id", "yaw"]) => {
            let yaw = args.get("yaw")?.as_f64()?;
            if !yaw.is_finite() || yaw.abs() > std::f64::consts::PI {
                return None;
            }
            Kind::Attack(yaw)
        }
        "game_dodge" if exact(args, &["run_id", "x", "z"]) => {
            let d = direction(args)?;
            if d.dot(d) == 0.0 {
                return None;
            }
            Kind::Dodge(d)
        }
        "game_restart" if exact(args, &["run_id"]) => Kind::Restart,
        _ => return None,
    };
    Some((run_id, kind))
}
impl Operations {
    fn publish(&self, snapshot: &Snapshot) {
        self.0
            .lock()
            .expect("arena operations poisoned")
            .publish(snapshot);
    }
    fn call(&self, name: &str, args: Map<String, Value>) -> CallToolResult {
        // Parse outside the lock. Serialized state never contains world references.
        if name == "game_state" {
            if !args.is_empty() {
                return error("invalid_arguments");
            }
            let state = self.0.lock().expect("arena operations poisoned");
            let Some(snapshot) = state.published.get() else {
                return error("not_ready");
            };
            let mut value = snapshot_value(
                snapshot,
                state.published.sequence(),
                state
                    .published
                    .age()
                    .unwrap()
                    .as_millis()
                    .min(u64::MAX as u128) as u64,
                state.commands[0]
                    .as_ref()
                    .filter(|r| r.record.status == "running")
                    .map(|r| r.record.id),
            );
            value["closed"] = json!(state.published.is_closed());
            return CallToolResult::structured(value);
        }
        if name == "game_command" {
            if !exact(&args, &["command_id"]) {
                return error("invalid_arguments");
            }
            let Some(id) = positive(&args, "command_id") else {
                return error("invalid_arguments");
            };
            let state = self.0.lock().expect("arena operations poisoned");
            if let Some(record) = state
                .commands
                .pending()
                .map(|r| &r.record)
                .chain(state.commands.history().iter())
                .find(|r| r.id == id)
            {
                return CallToolResult::structured(record.value());
            }
            return error(if id <= state.commands.last_id() {
                "expired_command"
            } else {
                "unknown_command"
            });
        }
        let Some((run_id, kind)) = mutation(name, &args) else {
            return error("invalid_arguments");
        };
        let mut state = self.0.lock().expect("arena operations poisoned");
        if state.commands.is_closed() {
            return error("shutting_down");
        }
        let Some(snapshot) = state.published.get() else {
            return error("not_ready");
        };
        if run_id != snapshot.run_id {
            return error("stale_run");
        }
        if snapshot.intermission_ticks > 0 && !matches!(kind, Kind::Restart) {
            return error("intermission");
        }
        if snapshot.state != RunState::Playing && !matches!(kind, Kind::Restart) {
            return error("run_finished");
        }
        let slot = kind.slot();
        let id = match state.commands.submit(slot, |id| Request {
            kind,
            record: Record {
                id,
                source_run: run_id,
                result_run: run_id,
                status: "pending",
                start: None,
                end: None,
                applied: 0,
                reason: None,
            },
        }) {
            Ok(id) => id,
            Err(_) => return error("busy"),
        };
        CallToolResult::structured(json!({"command_id":id,"run_id":run_id,"accepted":true}))
    }
    pub(crate) fn advance(
        &self,
        arena: &mut Arena,
        human: TickInput,
        focus_lost: bool,
    ) -> StepReport {
        // One bounded four-actor step makes publication and outcomes atomic to readers.
        // No I/O, callbacks, or unbounded queue processing occurs while locked.
        let mut state = self.0.lock().expect("arena operations poisoned");
        let before = arena.snapshot().clone();
        let human_rejection = if human.run_id != before.run_id {
            Some(crate::Rejection::StaleRun)
        } else if !human.valid() {
            Some(crate::Rejection::InvalidArguments)
        } else {
            None
        };
        let mut input = if human.run_id == before.run_id && human.valid() {
            human
        } else {
            TickInput::idle(before.run_id)
        };
        for slot in 0..3 {
            if state.commands[slot]
                .as_ref()
                .is_some_and(|r| r.record.source_run != before.run_id)
            {
                let request = state.commands[slot].take().unwrap();
                state.finish(request, "rejected", Some("stale_run"), &before);
            }
        }
        if input.restart || state.commands[2].is_some() {
            input = TickInput::idle(before.run_id);
            input.restart = true;
            let report = arena.step(input);
            for slot in 0..2 {
                state.cancel(slot, "restarted", arena.snapshot());
            }
            if let Some(mut request) = state.commands[2].take() {
                request.record.start = Some(before.tick);
                request.record.applied = 1;
                state.finish(request, "completed", None, arena.snapshot());
            }
            state.publish(arena.snapshot());
            return report;
        }
        if focus_lost {
            arena.clear_buffered_input();
            input = TickInput::idle(before.run_id);
            state.cancel(0, "focus_lost", &before);
            state.cancel(1, "focus_lost", &before);
        } else {
            if input.movement != Vec2::default() {
                state.cancel(0, "human_input", &before);
            }
            if input.attack_yaw.is_some() || input.dodge.is_some() {
                arena.clear_buffered_input();
                state.cancel(1, "human_input", &before);
            }
        }
        if before.state != RunState::Playing {
            for slot in 0..2 {
                state.cancel(slot, "run_finished", &before);
            }
        }
        if let Some(request) = &mut state.commands[0] {
            if let Kind::Move(direction, _) = request.kind {
                input.movement = direction;
            }
            request.record.status = "running";
            request.record.start.get_or_insert(before.tick + 1);
        }
        if let Some(request) = &state.commands[1] {
            match request.kind {
                Kind::Attack(yaw) => input.attack_yaw = Some(yaw),
                Kind::Dodge(d) if request.record.status != "running" => input.dodge = Some(d),
                Kind::Dodge(_) => {}
                _ => unreachable!(),
            }
        }
        let mut report = arena.step(input);
        let after = arena.snapshot();
        if let Some(mut request) = state.commands[1].take() {
            if matches!(request.kind, Kind::Dodge(_)) && report.dodge_buffered {
                request.record.status = "running";
                state.commands[1] = Some(request);
            } else if matches!(request.kind, Kind::Dodge(_))
                && !report.dodge_started
                && report.rejection.is_none()
            {
                state.finish(
                    request,
                    "cancelled",
                    Some(if after.intermission_ticks > 0 {
                        "wave_cleared"
                    } else {
                        "run_finished"
                    }),
                    after,
                );
            } else if let Some(rejection) = report.rejection {
                state.finish(request, "rejected", Some(rejection_code(rejection)), after);
            } else {
                request.record.start = Some(before.tick + 1);
                request.record.applied = 1;
                state.finish(request, "completed", None, after);
            }
        }
        if let Some(mut request) = state.commands[0].take() {
            request.record.applied += 1;
            let Kind::Move(_, ticks) = request.kind else {
                unreachable!()
            };
            if request.record.applied == ticks {
                state.finish(request, "completed", None, after);
            } else if after.intermission_ticks > 0 {
                state.finish(request, "cancelled", Some("wave_cleared"), after);
            } else if after.state != RunState::Playing {
                state.finish(request, "cancelled", Some("run_finished"), after);
            } else {
                state.commands[0] = Some(request);
            }
        }
        state.publish(after);
        if report.rejection.is_none() {
            report.rejection = human_rejection;
        }
        report
    }
    fn close(&self, snapshot: &Snapshot) {
        let mut state = self.0.lock().expect("arena operations poisoned");
        state.commands.close();
        for slot in 0..3 {
            state.cancel(slot, "shutdown", snapshot);
        }
        state.publish(snapshot);
        state.published.close();
    }
}
fn rejection_code(rejection: crate::Rejection) -> &'static str {
    match rejection {
        crate::Rejection::InvalidArguments => "invalid_arguments",
        crate::Rejection::StaleRun => "stale_run",
        crate::Rejection::RunFinished => "run_finished",
        crate::Rejection::ActionLocked => "action_locked",
        crate::Rejection::Cooldown => "cooldown",
        crate::Rejection::Intermission => "intermission",
    }
}
fn vector(v: Vec2) -> Value {
    json!({"x":v.x,"z":v.z})
}
fn snapshot_value(s: &Snapshot, sequence: u64, age: u64, active: Option<u64>) -> Value {
    let actors: Vec<_> = s.actors.iter().map(|a| {
        let (kind, phase, id, elapsed, remaining) = match a.action {
            Action::Idle => ("idle","idle",None,0,0),
            Action::Dodge {elapsed,..} => ("dodge","dodge",None,elapsed,18-elapsed),
            Action::Attack {id,elapsed,..} => {
                let stats = a.kind.stats();
                let windup = stats.windup;
                let end = stats.windup + stats.active + stats.recovery;
                let (phase,boundary) = if elapsed < windup {("windup",windup)} else if elapsed < windup+6 {("active",windup+6)} else {("recovery",end)};
                ("attack",phase,Some(id),elapsed,boundary-elapsed)
            }
        };
        json!({"id":a.id,"kind":a.kind.name(),"max_health":a.kind.stats().max_health,"attack_range":a.kind.stats().range,"windup_ticks":a.kind.stats().windup,"position":vector(a.position),"facing":vector(a.facing),"health":a.health,"dodge_cooldown":a.dodge_cooldown,
            "action":{"kind":kind,"phase":phase,"id":id,"elapsed_ticks":elapsed,"phase_ticks_remaining":remaining}})
    }).collect();
    json!({"snapshot_sequence":sequence,"snapshot_age_ms":age,"run_id":s.run_id,"tick":s.tick,
        "state":match s.state {RunState::Playing=>"playing",RunState::Won=>"won",RunState::Lost=>"lost"},
        "buffered_dodge":s.buffered_dodge.map(vector),"next_monster_strike_tick":s.next_monster_strike_tick,"wave":s.wave,"total_waves":crate::TOTAL_WAVES,"wave_tick":s.wave_tick,"intermission_ticks":s.intermission_ticks,"actors":actors,"monsters_remaining":s.monsters_remaining(),"active_movement_command_id":active})
}
