using Engine.Core;

namespace Engine.Scripting;

/// <summary>
/// Base class for game code attached to a scene node.
/// </summary>
public abstract class SceneScript
{
    private static readonly ObservedPropertyDescriptor[] EmptyObservedProperties = [];

    /// <summary>Requests Inspector exposure and Editor-facing change observation.</summary>
    protected const ObserveScope Editor = ObserveScope.Editor;

    /// <summary>Requests runtime-facing change observation.</summary>
    protected const ObserveScope Runtime = ObserveScope.Runtime;

    /// <summary>Gets the node that owns this script instance.</summary>
    public Node Owner { get; internal set; } = null!;

    /// <summary>Gets the authored component that created this script instance.</summary>
    public ScriptComponent Component { get; internal set; } = null!;

    /// <summary>Gets services for querying and changing the active scene.</summary>
    public SceneContext Scene { get; internal set; } = null!;

    /// <summary>Gets generated properties exposed by this script type.</summary>
    public virtual IReadOnlyList<ObservedPropertyDescriptor> ObservedProperties =>
        EmptyObservedProperties;

    /// <summary>Occurs after a generated observed property changes value.</summary>
    public event Action<ObservedPropertyChange>? ObservedPropertyChanged;

    /// <summary>Reads one generated observed property without reflection or value-type boxing.</summary>
    /// <param name="propertyId">Generated stable property identifier.</param>
    /// <param name="value">Current value when the identifier is recognized.</param>
    /// <returns>True when the property was found.</returns>
    public virtual bool TryGetObservedValue(int propertyId, out ObservedValue value)
    {
        value = default;
        return false;
    }

    /// <summary>Writes one generated observed property through its normal setter.</summary>
    /// <param name="propertyId">Generated stable property identifier.</param>
    /// <param name="value">New typed value.</param>
    /// <returns>True when the identifier and value kind were accepted.</returns>
    public virtual bool TrySetObservedValue(int propertyId, ObservedValue value) => false;

    /// <summary>Publishes one generated property transition to attached consumers.</summary>
    /// <param name="propertyId">Generated stable property identifier.</param>
    /// <param name="scope">Consumers receiving the transition.</param>
    protected void NotifyObservedPropertyChanged(int propertyId, ObserveScope scope)
    {
        ObservedPropertyChanged?.Invoke(new ObservedPropertyChange(propertyId, scope));
    }

    /// <summary>Combines inherited and locally generated descriptors once per script instance.</summary>
    /// <param name="inherited">Descriptors exposed by the base script.</param>
    /// <param name="local">Descriptors generated for the current script.</param>
    /// <returns>Ordered combined metadata.</returns>
    protected static IReadOnlyList<ObservedPropertyDescriptor> CombineObservedProperties(
        IReadOnlyList<ObservedPropertyDescriptor> inherited,
        IReadOnlyList<ObservedPropertyDescriptor> local)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        ArgumentNullException.ThrowIfNull(local);
        if (inherited.Count == 0)
            return local;
        if (local.Count == 0)
            return inherited;
        var combined = new ObservedPropertyDescriptor[inherited.Count + local.Count];
        for (var index = 0; index < inherited.Count; index++)
            combined[index] = inherited[index];
        for (var index = 0; index < local.Count; index++)
        {
            var descriptor = local[index];
            for (var inheritedIndex = 0; inheritedIndex < inherited.Count; inheritedIndex++)
            {
                if (inherited[inheritedIndex].Id == descriptor.Id)
                {
                    throw new InvalidOperationException(
                        $"Observed property ID {descriptor.Id} is duplicated in a script hierarchy.");
                }
            }
            combined[inherited.Count + index] = descriptor;
        }
        return combined;
    }

    /// <summary>Runs once after this script and all other scene scripts are attached.</summary>
    public virtual void OnReady()
    {
    }

    /// <summary>Runs once for each game update.</summary>
    /// <param name="deltaTime">Elapsed time in seconds since the previous update.</param>
    public virtual void OnUpdate(double deltaTime)
    {
    }

    /// <summary>Runs after the current frame's physics update and before rendering.</summary>
    /// <param name="deltaTime">Elapsed time in seconds since the previous update.</param>
    public virtual void OnLateUpdate(double deltaTime)
    {
    }

    /// <summary>Runs before this script is detached from its scene.</summary>
    public virtual void OnDestroy()
    {
    }
}
