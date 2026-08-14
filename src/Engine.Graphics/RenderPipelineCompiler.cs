namespace Engine.Graphics;

/// <summary>Validates SRP resource access and compiles sequential hazard barriers.</summary>
internal static class RenderPipelineCompiler
{
    private const int ResourceCount = 5;
    private const RenderPipelineResource KnownResources =
        RenderPipelineResource.CameraColor |
        RenderPipelineResource.CameraDepth |
        RenderPipelineResource.DirectionalShadowAtlas |
        RenderPipelineResource.LocalShadowAtlas |
        RenderPipelineResource.PresentedColor;

    /// <summary>Compiles resource dependencies for one recorded pipeline execution.</summary>
    /// <param name="commands">Commands in semantic execution order.</param>
    /// <param name="barriers">Reusable destination for compiled barriers.</param>
    public static void Compile(
        ReadOnlySpan<RenderPipelineCommand> commands,
        List<RenderPipelineBarrier> barriers)
    {
        ArgumentNullException.ThrowIfNull(barriers);
        barriers.Clear();
        Span<byte> lastAccess = stackalloc byte[ResourceCount];
        Span<RenderPipelineStage> lastStage = stackalloc RenderPipelineStage[ResourceCount];
        var initialized = RenderPipelineResource.DirectionalShadowAtlas |
            RenderPipelineResource.LocalShadowAtlas;
        var cameraCleared = false;
        var outputProduced = false;
        for (var commandIndex = 0; commandIndex < commands.Length; commandIndex++)
        {
            var command = commands[commandIndex];
            ValidateCommand(command);
            if (command.Kind == RenderPipelineCommandKind.ClearCamera)
            {
                if (cameraCleared || commandIndex != 0)
                {
                    throw new InvalidOperationException(
                        "The camera must be cleared exactly once as the first pipeline command.");
                }
                cameraCleared = true;
            }
            else if (command.Kind == RenderPipelineCommandKind.ApplyPostProcess)
            {
                if (outputProduced || commandIndex != commands.Length - 1)
                {
                    throw new InvalidOperationException(
                        "Presentation output must be produced exactly once by the final command.");
                }
                outputProduced = true;
            }
            var missing = command.Reads & ~initialized;
            if (missing != RenderPipelineResource.None)
            {
                throw new InvalidOperationException(
                    $"Pipeline command {commandIndex} reads uninitialized resources: {missing}.");
            }
            for (var resourceIndex = 0; resourceIndex < ResourceCount; resourceIndex++)
            {
                var resource = (RenderPipelineResource)(1 << resourceIndex);
                var reads = (command.Reads & resource) != 0;
                var writes = (command.Writes & resource) != 0;
                if (!reads && !writes)
                    continue;
                var previousAccess = lastAccess[resourceIndex];
                if (previousAccess == 2 || writes && previousAccess == 1)
                {
                    barriers.Add(new RenderPipelineBarrier(
                        commandIndex, resource, lastStage[resourceIndex], command.Stage));
                }
                lastAccess[resourceIndex] = writes ? (byte)2 : (byte)1;
                lastStage[resourceIndex] = command.Stage;
            }
            initialized |= command.Writes;
        }
        if (!cameraCleared)
        {
            throw new InvalidOperationException(
                "The render pipeline must initialize camera color and depth.");
        }
        if (!outputProduced)
        {
            throw new InvalidOperationException(
                "The render pipeline must produce presentation output.");
        }
    }

    /// <summary>Validates one command's resource and semantic contract.</summary>
    /// <param name="command">Recorded SRP command.</param>
    private static void ValidateCommand(RenderPipelineCommand command)
    {
        var referenced = command.Reads | command.Writes;
        if ((referenced & ~KnownResources) != 0)
            throw new InvalidOperationException("A pipeline command references an unknown resource.");
        switch (command.Kind)
        {
            case RenderPipelineCommandKind.ClearCamera:
                if (command.Stage != RenderPipelineStage.CameraSetup ||
                    command.Reads != RenderPipelineResource.None ||
                    command.Writes != (RenderPipelineResource.CameraColor |
                        RenderPipelineResource.CameraDepth))
                {
                    throw new InvalidOperationException(
                        "A camera-clear command must initialize camera color and depth.");
                }
                break;
            case RenderPipelineCommandKind.DirectionalShadows:
                if (command.Stage != RenderPipelineStage.Shadows ||
                    command.Writes != RenderPipelineResource.DirectionalShadowAtlas)
                {
                    throw new InvalidOperationException(
                        "A directional-shadow command must write the shadow atlas.");
                }
                break;
            case RenderPipelineCommandKind.LocalShadows:
                if (command.Stage != RenderPipelineStage.Shadows ||
                    command.Writes != RenderPipelineResource.LocalShadowAtlas)
                {
                    throw new InvalidOperationException(
                        "A local-shadow command must write the local-shadow atlas.");
                }
                break;
            case RenderPipelineCommandKind.DrawRenderers:
                if (command.QueueFilter == RenderQueueFilter.None ||
                    command.Writes == RenderPipelineResource.None)
                {
                    throw new InvalidOperationException(
                        "A renderer command requires a queue filter and output resource.");
                }
                break;
            case RenderPipelineCommandKind.ApplyPostProcess:
                if (command.Stage != RenderPipelineStage.PostProcess ||
                    command.Reads != RenderPipelineResource.CameraColor ||
                    command.Writes != RenderPipelineResource.PresentedColor)
                {
                    throw new InvalidOperationException(
                        "A post-process command must transform camera color into presented color.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command.Kind));
        }
    }
}
