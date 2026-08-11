using Engine.Core;
using Xunit;

namespace Engine.Core.Tests;

public sealed class AnimatorComponentTests
{
    /// <summary>Exposes a useful nonzero script-driven cross-fade default.</summary>
    [Fact]
    public void Constructor_Defaults_ArePlayable()
    {
        var animator = new AnimatorComponent();

        Assert.Equal(0.2f, animator.DefaultFadeDuration);
        Assert.True(animator.PlayAutomatically);
        Assert.True(animator.Loop);
        Assert.Equal(1f, animator.Speed);
    }

    /// <summary>Rejects invalid fade durations at the authored component boundary.</summary>
    [Theory]
    [InlineData(-0.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void DefaultFadeDuration_InvalidValue_Throws(float value)
    {
        var animator = new AnimatorComponent();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            animator.DefaultFadeDuration = value);
    }
}
