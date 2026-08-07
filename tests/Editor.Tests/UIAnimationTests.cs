using System.Numerics;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UIAnimationTests
{
    /// <summary>Verifies scalar animation applies initial, intermediate, and final values.</summary>
    [Fact]
    public void FloatAnimation_AdvancesAndCompletes()
    {
        var element = new UIElement();
        var value = -1f;
        var completed = 0;
        var animation = new UIFloatAnimation(2f, 6f, 2d, result => value = result);
        animation.Completed += _ => completed++;

        element.StartAnimation("value", animation);
        Assert.Equal(2f, value);
        Assert.True(element.AdvanceTime(1d));
        Assert.Equal(4f, value);
        Assert.True(element.AdvanceTime(1d));

        Assert.Equal(6f, value);
        Assert.True(animation.IsCompleted);
        Assert.False(animation.IsRunning);
        Assert.Equal(0, element.ActiveAnimationCount);
        Assert.Equal(1, completed);
    }

    /// <summary>Verifies starting the same key cancels and replaces its previous animation.</summary>
    [Fact]
    public void StartAnimation_SameKey_CancelsPreviousRun()
    {
        var element = new UIElement();
        var first = new UIFloatAnimation(0f, 1f, 1d, _ => { });
        var second = new UIFloatAnimation(1f, 2f, 1d, _ => { });
        var cancelled = 0;
        first.Cancelled += _ => cancelled++;

        element.StartAnimation("opacity", first);
        element.StartAnimation("opacity", second);

        Assert.True(first.IsCancelled);
        Assert.False(first.IsRunning);
        Assert.True(second.IsRunning);
        Assert.Equal(1, cancelled);
        Assert.Equal(1, element.ActiveAnimationCount);
    }

    /// <summary>Verifies reduced motion immediately resolves non-essential animation to its stable state.</summary>
    [Fact]
    public void StartAnimation_ReducedMotion_CompletesNonEssentialRun()
    {
        var element = new UIElement { MotionPreference = UIMotionPreference.Reduced };
        var value = 0f;
        var animation = new UIFloatAnimation(1f, 7f, 10d, result => value = result);

        element.StartAnimation("motion", animation);

        Assert.Equal(7f, value);
        Assert.True(animation.IsCompleted);
        Assert.Equal(0, element.ActiveAnimationCount);
    }

    /// <summary>Verifies animations independently select scaled and unscaled host deltas.</summary>
    [Fact]
    public void AdvanceTime_UsesPerAnimationClock()
    {
        var element = new UIElement();
        var unscaled = Vector2.Zero;
        var scaled = Vector2.Zero;
        element.StartAnimation("unscaled", new UIVector2Animation(
            Vector2.Zero, new Vector2(10f), 1d, value => unscaled = value));
        element.StartAnimation("scaled", new UIVector2Animation(
            Vector2.Zero, new Vector2(10f), 1d, value => scaled = value)
        {
            Clock = UIClockKind.Scaled
        });

        element.AdvanceTime(0.5d, 0d);

        Assert.Equal(new Vector2(5f), unscaled);
        Assert.Equal(Vector2.Zero, scaled);
        Assert.Equal(2, element.ActiveAnimationCount);
    }

    /// <summary>Verifies animation instances can be reused after cancellation and completion.</summary>
    [Fact]
    public void Animation_CanBeReusedAcrossRuns()
    {
        var element = new UIElement();
        var value = 0f;
        var animation = new UIColorAnimation(Color.Black, Color.White, 1d, color => value = color.R);

        element.StartAnimation("color", animation);
        Assert.True(element.CancelAnimation("color"));
        element.StartAnimation("color", animation);
        element.AdvanceTime(1d);
        element.StartAnimation("color", animation);

        Assert.True(animation.IsRunning);
        Assert.False(animation.IsCompleted);
        Assert.False(animation.IsCancelled);
        Assert.Equal(0f, value);
    }

    /// <summary>Verifies an unchanged element time walk remains allocation-free after animation support.</summary>
    [Fact]
    public void AdvanceTime_WithoutAnimations_AllocatesNothing()
    {
        var root = new Panel(Color.Black);
        root.AddChild(new Label("Static"));
        root.AdvanceTime(0.016d, 0.008d);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
            root.AdvanceTime(0.016d, 0.008d);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
