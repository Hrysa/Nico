using Editor;
using Xunit;

namespace Editor.Tests;

public class EditorRenderSchedulerTests
{
    /// <summary>Verifies every render target starts dirty for the initial frame.</summary>
    [Fact]
    public void NewScheduler_StartsWithAllTargetsInvalid()
    {
        var scheduler = new EditorRenderScheduler();

        Assert.Equal(RenderInvalidation.All, scheduler.Pending);
    }

    /// <summary>Verifies consuming one target preserves independent pending targets.</summary>
    [Fact]
    public void Consume_ClearsOnlyRequestedTarget()
    {
        var scheduler = new EditorRenderScheduler();

        Assert.True(scheduler.Consume(RenderInvalidation.SceneViewport));

        Assert.False(scheduler.Consume(RenderInvalidation.SceneViewport));
        Assert.True(scheduler.Consume(RenderInvalidation.GameViewport));
        Assert.True(scheduler.Consume(RenderInvalidation.UI));
        Assert.Equal(RenderInvalidation.None, scheduler.Pending);
    }

    /// <summary>Verifies invalidation restores a target after it was consumed.</summary>
    [Fact]
    public void Invalidate_RestoresConsumedTarget()
    {
        var scheduler = new EditorRenderScheduler();
        scheduler.Consume(RenderInvalidation.SceneViewport);

        scheduler.Invalidate(RenderInvalidation.SceneViewport);

        Assert.True(scheduler.Consume(RenderInvalidation.SceneViewport));
    }
}
