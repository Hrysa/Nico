using System.Numerics;

namespace Engine.Graphics;

/// <summary>Defines reusable details for playing one named animation clip.</summary>
/// <param name="Clip">Registered clip key.</param>
/// <param name="FadeDuration">Cross-fade duration in seconds.</param>
/// <param name="Speed">Signed playback-rate multiplier.</param>
/// <param name="StartNormalizedTime">Optional normalized starting time.</param>
/// <param name="Loop">Whether playback wraps at clip boundaries.</param>
public readonly record struct AnimationTransition(
    string Clip,
    float FadeDuration = 0.2f,
    float Speed = 1f,
    float? StartNormalizedTime = null,
    bool Loop = true);

/// <summary>Stores live playback and fade state for one reusable animation clip.</summary>
public sealed class AnimationState
{
    private readonly AnimationController _controller;
    private double _time;
    private float _speed = 1f;
    private bool _endedRaised;
    private bool _loop = true;
    private bool _isPlaying;

    /// <summary>Gets the stable registry key.</summary>
    public string Key { get; }

    /// <summary>Gets the immutable clip sampled by this state.</summary>
    public AnimationClipResource Clip { get; }

    /// <summary>Gets or sets clip-local playback time in seconds.</summary>
    public float Time
    {
        get => (float)_time;
        set
        {
            _controller.EnsureAlive();
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _time = Math.Clamp(value, 0f, Clip.Duration);
            _endedRaised = false;
            _controller.StateChanged(this);
        }
    }

    /// <summary>Gets or sets clip-local playback time normalized by duration.</summary>
    public float NormalizedTime
    {
        get => Clip.Duration <= 0f ? 0f : (float)(_time / Clip.Duration);
        set
        {
            _controller.EnsureAlive();
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            Time = Math.Clamp(value, 0f, 1f) * Clip.Duration;
        }
    }

    /// <summary>Gets or sets the signed playback-rate multiplier.</summary>
    public float Speed
    {
        get => _speed;
        set
        {
            _controller.EnsureAlive();
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _speed = value;
        }
    }

    /// <summary>Gets the current contribution to the base layer.</summary>
    public float Weight { get; internal set; }

    /// <summary>Gets the weight this state is currently fading toward.</summary>
    public float TargetWeight => FadeTargetWeight;

    /// <summary>Gets the remaining fade time in seconds.</summary>
    public float FadeRemaining => Math.Max(0f, FadeDuration - FadeElapsed);

    /// <summary>Gets whether this is the base layer's current state.</summary>
    public bool IsCurrent => ReferenceEquals(_controller.Current, this);

    /// <summary>Gets or sets whether playback wraps at clip boundaries.</summary>
    public bool Loop
    {
        get => _loop;
        set
        {
            _controller.EnsureAlive();
            _loop = value;
        }
    }

    /// <summary>Gets or sets whether clip time advances.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            _controller.EnsureAlive();
            _isPlaying = value;
        }
    }

    /// <summary>Occurs once when non-looping playback reaches its directional boundary.</summary>
    public event Action<AnimationState>? Ended;

    /// <summary>Gets reusable sampled local-transform storage.</summary>
    internal JointTransform[] LocalTransforms { get; }

    /// <summary>Gets or sets the fade's initial weight.</summary>
    internal float FadeStartWeight { get; set; }

    /// <summary>Gets or sets the fade's target weight.</summary>
    internal float FadeTargetWeight { get; set; }

    /// <summary>Gets or sets fade duration in seconds.</summary>
    internal float FadeDuration { get; set; }

    /// <summary>Gets or sets elapsed fade time in seconds.</summary>
    internal float FadeElapsed { get; set; }

    /// <summary>Creates a state owned by one controller.</summary>
    /// <param name="controller">Owning controller.</param>
    /// <param name="key">Stable registry key.</param>
    /// <param name="clip">Clip sampled by the state.</param>
    internal AnimationState(AnimationController controller, string key,
        AnimationClipResource clip)
    {
        _controller = controller;
        Key = key;
        Clip = clip;
        LocalTransforms = new JointTransform[controller.Skeleton.JointCount];
        SkeletonPose.SampleLocalTransforms(controller.Skeleton, clip, 0f, LocalTransforms);
    }

    /// <summary>Advances time and queues a completion when a non-looping boundary is reached.</summary>
    /// <param name="deltaTime">Elapsed scaled time.</param>
    internal bool Advance(double deltaTime)
    {
        if (!IsPlaying || Speed == 0f || deltaTime == 0d)
            return false;
        var duration = Clip.Duration;
        if (duration <= 0f)
        {
            _time = 0d;
            Complete();
            return true;
        }
        var next = _time + deltaTime * Speed;
        if (Loop)
        {
            next %= duration;
            if (next < 0d)
                next += duration;
            _time = next;
            _endedRaised = false;
        }
        else if (Speed > 0f && next >= duration)
        {
            _time = duration;
            Complete();
        }
        else if (Speed < 0f && next <= 0d)
        {
            _time = 0d;
            Complete();
        }
        else
        {
            _time = Math.Clamp(next, 0d, duration);
        }
        return true;
    }

    /// <summary>Samples current time into retained local transforms.</summary>
    internal void Sample()
    {
        SkeletonPose.SampleLocalTransforms(
            _controller.Skeleton, Clip, (float)_time, LocalTransforms);
    }

    /// <summary>Invokes the deferred completion callback.</summary>
    internal void RaiseEnded() => Ended?.Invoke(this);

    /// <summary>Stops playback and queues one completion callback.</summary>
    private void Complete()
    {
        IsPlaying = false;
        if (_endedRaised)
            return;
        _endedRaised = true;
        _controller.QueueEnded(this);
    }

    /// <summary>Resets completion tracking after an explicit play operation.</summary>
    internal void ResetCompletion() => _endedRaised = false;

    /// <summary>Releases callbacks when the owning runtime controller is destroyed.</summary>
    internal void ClearCallbacks() => Ended = null;
}

/// <summary>Owns active override states and their cross-fade weights.</summary>
public sealed class AnimationLayer
{
    private readonly AnimationController _controller;
    private readonly List<AnimationState> _activeStates = [];

    /// <summary>Gets the most recently played state.</summary>
    public AnimationState? Current { get; internal set; }

    /// <summary>Gets the number of states currently contributing or fading.</summary>
    public int ActiveStateCount => _activeStates.Count;

    /// <summary>Gets whether playback time or a fade can change on the next update.</summary>
    internal bool RequiresUpdate
    {
        get
        {
            for (var index = 0; index < _activeStates.Count; index++)
            {
                var state = _activeStates[index];
                if (state.IsPlaying && state.Speed != 0f ||
                    state.FadeElapsed < state.FadeDuration)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Creates a base layer for one controller.</summary>
    /// <param name="controller">Owning controller.</param>
    internal AnimationLayer(AnimationController controller) => _controller = controller;

    /// <summary>Cross-fades to one state without changing its current time.</summary>
    /// <param name="state">State becoming current.</param>
    /// <param name="fadeDuration">Fade duration in seconds.</param>
    internal void Play(AnimationState state, float fadeDuration)
    {
        if (!float.IsFinite(fadeDuration) || fadeDuration < 0f)
            throw new ArgumentOutOfRangeException(nameof(fadeDuration));
        if (!_activeStates.Contains(state))
            _activeStates.Add(state);
        Current = state;
        state.IsPlaying = true;
        state.ResetCompletion();
        for (var index = 0; index < _activeStates.Count; index++)
        {
            var active = _activeStates[index];
            active.FadeStartWeight = active.Weight;
            active.FadeTargetWeight = ReferenceEquals(active, state) ? 1f : 0f;
            active.FadeDuration = fadeDuration;
            active.FadeElapsed = 0f;
            if (fadeDuration == 0f)
                active.Weight = active.FadeTargetWeight;
        }
        RemoveInactive();
        _controller.EvaluatePose();
    }

    /// <summary>Fades all active states out.</summary>
    /// <param name="fadeDuration">Fade duration in seconds.</param>
    internal void Stop(float fadeDuration)
    {
        if (!float.IsFinite(fadeDuration) || fadeDuration < 0f)
            throw new ArgumentOutOfRangeException(nameof(fadeDuration));
        Current = null;
        for (var index = 0; index < _activeStates.Count; index++)
        {
            var state = _activeStates[index];
            state.FadeStartWeight = state.Weight;
            state.FadeTargetWeight = 0f;
            state.FadeDuration = fadeDuration;
            state.FadeElapsed = 0f;
            if (fadeDuration == 0f)
                state.Weight = 0f;
        }
        RemoveInactive();
        _controller.EvaluatePose();
    }

    /// <summary>Advances all active state times and fades without allocating.</summary>
    /// <param name="deltaTime">Elapsed scaled time.</param>
    internal bool Update(double deltaTime)
    {
        var changed = false;
        for (var index = 0; index < _activeStates.Count; index++)
        {
            var state = _activeStates[index];
            changed |= state.Advance(deltaTime);
            if (state.FadeElapsed < state.FadeDuration)
            {
                state.FadeElapsed = Math.Min(state.FadeDuration,
                    state.FadeElapsed + (float)deltaTime);
                var amount = state.FadeDuration <= 0f
                    ? 1f : state.FadeElapsed / state.FadeDuration;
                state.Weight = state.FadeStartWeight +
                    (state.FadeTargetWeight - state.FadeStartWeight) * amount;
                changed = true;
            }
        }
        RemoveInactive();
        return changed;
    }

    /// <summary>Blends active local poses into caller-owned storage.</summary>
    /// <param name="destination">One transform per target joint.</param>
    internal void Blend(Span<JointTransform> destination)
    {
        var activeWeight = 0f;
        for (var index = 0; index < _activeStates.Count; index++)
            activeWeight += Math.Max(0f, _activeStates[index].Weight);
        var totalWeight = Math.Max(0f, 1f - activeWeight);
        if (totalWeight > 0f)
        {
            var bindSkeleton = _controller.Skeleton;
            for (var index = 0; index < bindSkeleton.JointCount; index++)
                destination[index] = bindSkeleton.Joints[index].BindTransform;
        }
        for (var stateIndex = 0; stateIndex < _activeStates.Count; stateIndex++)
        {
            var state = _activeStates[stateIndex];
            if (state.Weight <= 0f)
                continue;
            state.Sample();
            var nextTotal = totalWeight + state.Weight;
            var amount = totalWeight <= 0f ? 1f : state.Weight / nextTotal;
            for (var jointIndex = 0; jointIndex < destination.Length; jointIndex++)
            {
                var source = state.LocalTransforms[jointIndex];
                if (totalWeight <= 0f)
                {
                    destination[jointIndex] = source;
                    continue;
                }
                var current = destination[jointIndex];
                var sourceRotation = source.Rotation;
                if (Quaternion.Dot(current.Rotation, sourceRotation) < 0f)
                    sourceRotation = new Quaternion(-sourceRotation.X, -sourceRotation.Y,
                        -sourceRotation.Z, -sourceRotation.W);
                destination[jointIndex] = new JointTransform(
                    Vector3.Lerp(current.Translation, source.Translation, amount),
                    Quaternion.Normalize(Quaternion.Slerp(
                        current.Rotation, sourceRotation, amount)),
                    Vector3.Lerp(current.Scale, source.Scale, amount));
            }
            totalWeight = nextTotal;
        }
        if (totalWeight > 0f)
            return;
        var skeleton = _controller.Skeleton;
        for (var index = 0; index < skeleton.JointCount; index++)
            destination[index] = skeleton.Joints[index].BindTransform;
    }

    /// <summary>Removes stopped zero-weight states from the active evaluation list.</summary>
    private void RemoveInactive()
    {
        for (var index = _activeStates.Count - 1; index >= 0; index--)
        {
            var state = _activeStates[index];
            if (state.Weight > 0f || state.FadeTargetWeight > 0f)
                continue;
            state.IsPlaying = false;
            _activeStates.RemoveAt(index);
        }
    }
}

/// <summary>Provides reusable state-oriented playback and local-pose cross-fading.</summary>
public sealed class AnimationController : IDisposable
{
    private readonly Dictionary<string, AnimationState> _states =
        new(StringComparer.Ordinal);
    private readonly List<AnimationState> _endedQueue = [];
    private readonly JointTransform[] _blendedLocals;
    private bool _disposed;
    private float _defaultFadeDuration;

    /// <summary>Gets the target skinned resource.</summary>
    public SkinnedMeshResource Resource { get; }

    /// <summary>Gets the target skeleton.</summary>
    public SkeletonResource Skeleton => Resource.Skeleton;

    /// <summary>Gets the reusable evaluated output pose.</summary>
    public SkeletonPose Pose { get; }

    /// <summary>Gets the base override layer.</summary>
    public AnimationLayer BaseLayer { get; }

    /// <summary>Gets the base layer's current state.</summary>
    public AnimationState? Current => BaseLayer.Current;

    /// <summary>Gets a revision incremented whenever the output pose is evaluated.</summary>
    public ulong PoseRevision { get; private set; }

    /// <summary>Gets whether this controller remains bound to its runtime scene.</summary>
    public bool IsValid => !_disposed;

    /// <summary>Gets whether playback or fading needs another simulation update.</summary>
    public bool RequiresUpdate => !_disposed && BaseLayer.RequiresUpdate;

    /// <summary>Gets or sets the fade used by the single-argument Play overload.</summary>
    public float DefaultFadeDuration
    {
        get => _defaultFadeDuration;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _defaultFadeDuration = value;
        }
    }

    /// <summary>Creates a controller and registers every embedded clip by exact name.</summary>
    /// <param name="resource">Target skinned resource and clips.</param>
    public AnimationController(SkinnedMeshResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Resource = resource;
        Pose = new SkeletonPose(resource.Skeleton);
        _blendedLocals = new JointTransform[resource.Skeleton.JointCount];
        BaseLayer = new AnimationLayer(this);
        for (var index = 0; index < resource.Animations.Count; index++)
        {
            var clip = resource.Animations[index];
            if (!_states.TryAdd(clip.Name, new AnimationState(this, clip.Name, clip)))
                throw new ArgumentException(
                    $"Animation clip name '{clip.Name}' is duplicated.", nameof(resource));
        }
    }

    /// <summary>Gets or creates the reusable state registered for one clip key.</summary>
    /// <param name="clip">Exact registered clip key.</param>
    /// <returns>The persistent state.</returns>
    public AnimationState GetOrCreate(string clip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(clip);
        return _states.TryGetValue(clip, out var state)
            ? state : throw new KeyNotFoundException($"Animation clip '{clip}' was not found.");
    }

    /// <summary>Attempts to find a registered state without creating or playing it.</summary>
    /// <param name="clip">Exact registered clip key.</param>
    /// <param name="state">Matching persistent state.</param>
    /// <returns>True when the clip is registered.</returns>
    public bool TryGet(string clip, out AnimationState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(clip);
        return _states.TryGetValue(clip, out state);
    }

    /// <summary>Plays one clip immediately without restarting an existing state.</summary>
    /// <param name="clip">Exact registered clip key.</param>
    /// <returns>The persistent state.</returns>
    public AnimationState Play(string clip) => Play(clip, DefaultFadeDuration);

    /// <summary>Cross-fades to one clip without restarting an existing state.</summary>
    /// <param name="clip">Exact registered clip key.</param>
    /// <param name="fadeDuration">Fade duration in seconds.</param>
    /// <returns>The persistent state.</returns>
    public AnimationState Play(string clip, float fadeDuration)
    {
        var state = GetOrCreate(clip);
        if (ReferenceEquals(Current, state))
        {
            state.IsPlaying = true;
            return state;
        }
        BaseLayer.Play(state, fadeDuration);
        return state;
    }

    /// <summary>Attempts to play a registered clip without throwing for an unknown key.</summary>
    /// <param name="clip">Exact registered clip key.</param>
    /// <param name="state">Played persistent state when found.</param>
    /// <returns>True when the clip was registered and played.</returns>
    public bool TryPlay(string clip, out AnimationState? state)
    {
        return TryPlay(clip, out state, DefaultFadeDuration);
    }

    /// <summary>Attempts to play a registered clip with an explicit fade duration.</summary>
    /// <param name="clip">Exact registered clip key.</param>
    /// <param name="state">Played persistent state when found.</param>
    /// <param name="fadeDuration">Fade duration in seconds.</param>
    /// <returns>True when the clip was registered and played.</returns>
    public bool TryPlay(string clip, out AnimationState? state, float fadeDuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(clip);
        if (!_states.TryGetValue(clip, out state))
            return false;
        state = Play(clip, fadeDuration);
        return true;
    }

    /// <summary>Applies serialized transition details and plays their clip.</summary>
    /// <param name="transition">Transition details.</param>
    /// <returns>The configured persistent state.</returns>
    public AnimationState Play(AnimationTransition transition)
    {
        if (!float.IsFinite(transition.Speed))
            throw new ArgumentOutOfRangeException(nameof(transition));
        var state = GetOrCreate(transition.Clip);
        state.Speed = transition.Speed;
        state.Loop = transition.Loop;
        if (transition.StartNormalizedTime is { } start)
            state.NormalizedTime = start;
        return Play(transition.Clip, transition.FadeDuration);
    }

    /// <summary>Restarts one clip at its directional boundary and plays it.</summary>
    /// <param name="clip">Exact registered clip key.</param>
    /// <param name="fadeDuration">Fade duration in seconds.</param>
    /// <returns>The restarted persistent state.</returns>
    public AnimationState PlayFromStart(string clip, float fadeDuration = 0f)
    {
        var state = GetOrCreate(clip);
        state.Time = state.Speed < 0f ? state.Clip.Duration : 0f;
        if (ReferenceEquals(Current, state))
        {
            state.IsPlaying = true;
            state.ResetCompletion();
            EvaluatePose();
            return state;
        }
        BaseLayer.Play(state, fadeDuration);
        return state;
    }

    /// <summary>Stops every active state immediately or with a fade.</summary>
    /// <param name="fadeDuration">Fade duration in seconds.</param>
    public void Stop(float fadeDuration = 0f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BaseLayer.Stop(fadeDuration);
    }

    /// <summary>Advances playback, evaluates the blended pose, and dispatches deferred endings.</summary>
    /// <param name="deltaTime">Elapsed scaled simulation time.</param>
    public void Update(double deltaTime)
    {
        Advance(deltaTime);
        DispatchEvents();
    }

    /// <summary>Advances playback and evaluates the blended pose without invoking callbacks.</summary>
    /// <param name="deltaTime">Elapsed scaled simulation time.</param>
    public void Advance(double deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(deltaTime) || deltaTime < 0d)
            throw new ArgumentOutOfRangeException(nameof(deltaTime));
        if (BaseLayer.Update(deltaTime))
            EvaluatePose();
    }

    /// <summary>Invokes completion callbacks queued by the preceding advance phase.</summary>
    public void DispatchEvents()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var index = 0; index < _endedQueue.Count; index++)
            _endedQueue[index].RaiseEnded();
        _endedQueue.Clear();
    }

    /// <summary>Invalidates this runtime-only controller and clears callbacks through state release.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _endedQueue.Clear();
        foreach (var state in _states.Values)
            state.ClearCallbacks();
        _states.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>Queues a state completion for mutation-safe dispatch after evaluation.</summary>
    /// <param name="state">Completed state.</param>
    internal void QueueEnded(AnimationState state) => _endedQueue.Add(state);

    /// <summary>Immediately resamples the output after externally changing a state time.</summary>
    /// <param name="state">Changed owned state.</param>
    internal void StateChanged(AnimationState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (state.Weight > 0f)
            EvaluatePose();
    }

    /// <summary>Throws when a retained state mutates after its controller was destroyed.</summary>
    internal void EnsureAlive() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Blends active states and composes the resulting skin palette.</summary>
    internal void EvaluatePose()
    {
        BaseLayer.Blend(_blendedLocals);
        Pose.ApplyLocalTransforms(Skeleton, _blendedLocals);
        PoseRevision++;
    }
}
