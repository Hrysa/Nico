//! Stateful, runtime-independent playback of direct or retargeted clips.
//! Immutable clip sets are shared; each player owns its clocks and reusable poses.
use crate::humanoid::{HumanoidRig, HumanoidWorkspace, RootMotion};
use crate::{AnimationError, Playback, Pose, PoseBuffer};
use nico_assets::model::Model;
use std::{sync::Arc, time::Duration};

#[derive(Clone)]
enum Source {
    Direct(Arc<Model>),
    Humanoid {
        source: Arc<HumanoidRig>,
        target: Arc<HumanoidRig>,
    },
}
/// A validated clip binding. All entries in a set evaluate onto the same model.
#[derive(Clone)]
pub struct AnimationClip {
    name: String,
    index: usize,
    duration: f64,
    source: Source,
}
impl AnimationClip {
    pub fn direct(
        model: Arc<Model>,
        index: usize,
        name: impl Into<String>,
    ) -> Result<Self, AnimationError> {
        let duration = clip_duration(&model, index)?;
        Ok(Self {
            name: name.into(),
            index,
            duration,
            source: Source::Direct(model),
        })
    }
    pub fn humanoid(
        source: Arc<HumanoidRig>,
        target: Arc<HumanoidRig>,
        index: usize,
        name: impl Into<String>,
    ) -> Result<Self, AnimationError> {
        let duration = clip_duration(source.model(), index)?;
        Ok(Self {
            name: name.into(),
            index,
            duration,
            source: Source::Humanoid { source, target },
        })
    }
    pub fn name(&self) -> &str {
        &self.name
    }
    pub fn duration(&self) -> f64 {
        self.duration
    }
    fn target(&self) -> &Arc<Model> {
        match &self.source {
            Source::Direct(model) => model,
            Source::Humanoid { target, .. } => target.model(),
        }
    }
    fn evaluate(
        &self,
        time: f64,
        policy: RootMotion,
        scratch: &mut Evaluation,
        output: &mut PoseBuffer,
    ) -> Result<(), AnimationError> {
        match &self.source {
            Source::Direct(_) => output.sample(self.index, time as f32, Playback::Clamp),
            Source::Humanoid { source, target } => {
                if scratch
                    .source
                    .as_ref()
                    .is_none_or(|p| !std::ptr::eq(p.pose().model(), source.model().as_ref()))
                {
                    scratch.source = Some(PoseBuffer::new(source.model().clone()));
                }
                let pose = scratch.source.as_mut().unwrap();
                pose.sample(self.index, time as f32, Playback::Clamp)?;
                let motion = source.capture_into(&pose.pose(), &mut scratch.humanoid)?;
                target.apply_into(&motion, policy, output, &mut scratch.humanoid)
            }
        }
    }
}
fn clip_duration(model: &Model, index: usize) -> Result<f64, AnimationError> {
    let clip = model
        .data()
        .clips
        .get(index)
        .ok_or_else(|| error("clip", "clip index out of range"))?;
    let (start, end) = clip.time_range();
    let duration = end - start;
    if !duration.is_finite() || duration < 0. {
        return Err(error("clip_duration", "invalid clip time range"));
    }
    Ok(f64::from(duration))
}
fn error(code: &'static str, detail: &str) -> AnimationError {
    AnimationError::new(code, detail)
}

/// One immutable target model and its named direct/retargeted clip bindings.
/// Empty sets are valid and remain in reference pose until a clip is available.
pub struct AnimationSet {
    model: Arc<Model>,
    clips: Vec<AnimationClip>,
}
impl AnimationSet {
    pub fn new(model: Arc<Model>, clips: Vec<AnimationClip>) -> Result<Self, AnimationError> {
        if clips.len() > 4096 {
            return Err(error("clip_limit", "at most 4096 clip bindings per set"));
        }
        if clips.iter().any(|c| !Arc::ptr_eq(c.target(), &model)) {
            return Err(error("pose_model", "clip targets another model"));
        }
        Ok(Self { model, clips })
    }
    pub fn model(&self) -> &Arc<Model> {
        &self.model
    }
    pub fn clips(&self) -> &[AnimationClip] {
        &self.clips
    }
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PlayMode {
    Loop,
    Once,
}
#[derive(Clone, Copy, Debug, PartialEq)]
struct Cursor {
    clip: usize,
    time: f64,
    mode: PlayMode,
    finished: bool,
}
impl Cursor {
    fn advance(&mut self, delta: f64, duration: f64) -> Result<(), AnimationError> {
        let next = self.time + delta;
        if !next.is_finite() {
            return Err(error("time", "playback time overflow"));
        }
        self.time = match self.mode {
            PlayMode::Loop if duration > 0. => next.rem_euclid(duration),
            PlayMode::Loop => 0.,
            PlayMode::Once => next.min(duration),
        };
        self.finished = self.mode == PlayMode::Once && self.time >= duration;
        Ok(())
    }
}
#[derive(Clone, Copy, Debug)]
struct Transition {
    /// None means a frozen copy of the last visible pose (interrupted fade or rest).
    source: Option<Cursor>,
    elapsed: f64,
    duration: f64,
}
#[derive(Clone, Default)]
struct Evaluation {
    source: Option<PoseBuffer>,
    humanoid: HumanoidWorkspace,
}
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct PlaybackEvents {
    /// True only on the update that the destination one-shot reaches its end.
    /// Selecting or seeking to an endpoint exposes `finished` without emitting this event.
    pub just_finished: bool,
}

/// Stateful two-pose player. Fades advance in wall time while unpaused; speed
/// scales clip clocks only. Interrupted fades freeze the last visible mixed pose
/// as the next fade source, avoiding discontinuities or unbounded blend stacks.
/// Failed play/seek/update leaves public clocks and output pose unchanged.
#[derive(Clone)]
pub struct AnimationPlayer {
    set: Arc<AnimationSet>,
    current: Option<Cursor>,
    transition: Option<Transition>,
    paused: bool,
    speed: f64,
    policy: RootMotion,
    output: PoseBuffer,
    from: PoseBuffer,
    to: PoseBuffer,
    frozen: PoseBuffer,
    from_scratch: Evaluation,
    to_scratch: Evaluation,
}
impl AnimationPlayer {
    pub fn new(set: Arc<AnimationSet>) -> Self {
        Self {
            output: PoseBuffer::new(set.model.clone()),
            from: PoseBuffer::new(set.model.clone()),
            to: PoseBuffer::new(set.model.clone()),
            frozen: PoseBuffer::new(set.model.clone()),
            set,
            current: None,
            transition: None,
            paused: false,
            speed: 1.,
            policy: RootMotion::InPlace,
            from_scratch: Evaluation::default(),
            to_scratch: Evaluation::default(),
        }
    }
    pub fn set(&self) -> &Arc<AnimationSet> {
        &self.set
    }
    pub fn pose(&self) -> Pose<'_> {
        self.output.pose()
    }
    pub fn clip(&self) -> Option<usize> {
        self.current.map(|c| c.clip)
    }
    pub fn time(&self) -> f64 {
        self.current.map_or(0., |c| c.time)
    }
    pub fn duration(&self) -> f64 {
        self.current.map_or(0., |c| self.set.clips[c.clip].duration)
    }
    pub fn mode(&self) -> Option<PlayMode> {
        self.current.map(|c| c.mode)
    }
    pub fn finished(&self) -> bool {
        self.current.is_some_and(|c| c.finished)
    }
    pub fn paused(&self) -> bool {
        self.paused
    }
    pub fn speed(&self) -> f64 {
        self.speed
    }
    pub fn fade_weight(&self) -> Option<f64> {
        self.transition.map(|t| t.elapsed / t.duration)
    }
    pub fn set_paused(&mut self, paused: bool) {
        self.paused = paused;
    }
    /// Changes destination playback mode without restarting or cancelling a fade.
    pub fn set_mode(&mut self, mode: PlayMode) {
        if let Some(current) = &mut self.current {
            current.mode = mode;
            current.finished =
                mode == PlayMode::Once && current.time >= self.set.clips[current.clip].duration;
        }
    }
    pub fn set_speed(&mut self, speed: f64) -> Result<(), AnimationError> {
        if !speed.is_finite() || speed < 0. {
            return Err(error("speed", "expected finite nonnegative speed"));
        }
        self.speed = speed;
        Ok(())
    }
    /// Restarts the requested clip. A zero fade cuts immediately; otherwise both
    /// clips advance during the fade, unless interrupting an already active fade.
    pub fn play(
        &mut self,
        clip: usize,
        mode: PlayMode,
        fade: Duration,
    ) -> Result<(), AnimationError> {
        let binding = self
            .set
            .clips
            .get(clip)
            .ok_or_else(|| error("clip", "clip index out of range"))?;
        binding.evaluate(0., self.policy, &mut self.to_scratch, &mut self.to)?;
        let transition = if fade.is_zero() {
            None
        } else {
            let source = if self.transition.is_some() {
                None
            } else {
                self.current
            };
            if source.is_none() {
                self.frozen.copy_from(&self.output.pose())?;
            }
            Some(Transition {
                source,
                elapsed: 0.,
                duration: fade.as_secs_f64(),
            })
        };
        if transition.is_none() {
            self.output.copy_from(&self.to.pose())?;
        }
        self.current = Some(Cursor {
            clip,
            time: 0.,
            mode,
            finished: mode == PlayMode::Once && binding.duration == 0.,
        });
        self.transition = transition;
        Ok(())
    }
    /// Seek within the selected clip, preserving pause state and cancelling fades.
    pub fn seek(&mut self, seconds: f64) -> Result<(), AnimationError> {
        let mut current = self
            .current
            .ok_or_else(|| error("clip", "no selected clip"))?;
        let binding = &self.set.clips[current.clip];
        if !seconds.is_finite() || seconds < 0. || seconds > binding.duration {
            return Err(error("time", "seek outside clip duration"));
        }
        binding.evaluate(seconds, self.policy, &mut self.to_scratch, &mut self.to)?;
        self.output.copy_from(&self.to.pose())?;
        current.time = seconds;
        current.finished = current.mode == PlayMode::Once && seconds >= binding.duration;
        self.current = Some(current);
        self.transition = None;
        Ok(())
    }
    /// Clears playback and fades, preserving speed/pause/root policy.
    pub fn reference_pose(&mut self) {
        self.current = None;
        self.transition = None;
        self.output.reset();
    }
    pub fn set_root_motion(&mut self, policy: RootMotion) -> Result<(), AnimationError> {
        let previous = self.policy;
        self.policy = policy;
        if let Err(error) = self.evaluate(self.current, self.transition) {
            self.policy = previous;
            return Err(error);
        }
        Ok(())
    }
    pub fn update(&mut self, delta: Duration) -> Result<PlaybackEvents, AnimationError> {
        self.advance(delta, None)
    }
    /// Evaluate an externally clocked destination while advancing the outgoing
    /// pose and fade by delta. Unlike seek, this preserves an active transition.
    /// Position is clip-relative seconds in 0..=duration. Pause freezes all state.
    /// Completion is emitted when a one-shot first reaches its endpoint.
    pub fn update_at(
        &mut self,
        delta: Duration,
        position: f64,
    ) -> Result<PlaybackEvents, AnimationError> {
        if self.current.is_none()
            || !position.is_finite()
            || position < 0.
            || position > self.duration()
        {
            return Err(error("time", "external position outside selected clip"));
        }
        self.advance(delta, Some(position))
    }
    fn advance(
        &mut self,
        delta: Duration,
        position: Option<f64>,
    ) -> Result<PlaybackEvents, AnimationError> {
        if self.paused {
            return Ok(PlaybackEvents::default());
        }
        let scaled = delta.as_secs_f64() * self.speed;
        if !scaled.is_finite() {
            return Err(error("time", "scaled delta overflow"));
        }
        let mut current = self.current;
        let mut transition = self.transition;
        if let Some(c) = &mut current {
            if let Some(position) = position {
                c.time = position;
                c.finished =
                    c.mode == PlayMode::Once && position >= self.set.clips[c.clip].duration;
            } else {
                c.advance(scaled, self.set.clips[c.clip].duration)?;
            }
        }
        if let Some(t) = &mut transition {
            t.elapsed = (t.elapsed + delta.as_secs_f64()).min(t.duration);
            if let Some(c) = &mut t.source {
                c.advance(scaled, self.set.clips[c.clip].duration)?;
            }
            if t.elapsed >= t.duration {
                transition = None;
            }
        }
        self.evaluate(current, transition)?;
        let just_finished = current.is_some_and(|c| c.finished) && !self.finished();
        self.current = current;
        self.transition = transition;
        Ok(PlaybackEvents { just_finished })
    }
    fn evaluate(
        &mut self,
        current: Option<Cursor>,
        transition: Option<Transition>,
    ) -> Result<(), AnimationError> {
        let Some(current) = current else {
            self.output.reset();
            return Ok(());
        };
        self.set.clips[current.clip].evaluate(
            current.time,
            self.policy,
            &mut self.to_scratch,
            &mut self.to,
        )?;
        if let Some(t) = transition {
            if let Some(source) = t.source {
                self.set.clips[source.clip].evaluate(
                    source.time,
                    self.policy,
                    &mut self.from_scratch,
                    &mut self.from,
                )?;
            } else {
                self.from.copy_from(&self.frozen.pose())?;
            }
            self.output.blend(
                &self.from.pose(),
                &self.to.pose(),
                (t.elapsed / t.duration) as f32,
            )?;
        } else {
            self.output.copy_from(&self.to.pose())?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests;
