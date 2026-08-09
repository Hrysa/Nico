using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>
/// Selects portable swapchain settings from surface capabilities.
/// </summary>
internal static class SwapchainPolicy
{
    /// <summary>Selects the preferred SRGB surface format.</summary>
    /// <param name="available">Available surface formats.</param>
    /// <returns>The selected surface format.</returns>
    internal static SurfaceFormatKHR ChooseSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> available)
    {
        if (available.Count == 0)
            throw new InvalidOperationException("The surface exposes no swapchain formats.");

        foreach (var format in available)
        {
            if (format.Format == Format.B8G8R8A8Srgb
                && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                return format;
        }

        return available[0];
    }

    /// <summary>Selects the requested optional presentation mode or guaranteed FIFO fallback.</summary>
    /// <param name="available">Available presentation modes.</param>
    /// <param name="preference">Requested presentation latency policy.</param>
    /// <returns>The selected presentation mode.</returns>
    internal static PresentModeKHR ChoosePresentMode(
        IReadOnlyList<PresentModeKHR> available,
        PresentationModePreference preference)
    {
        ArgumentNullException.ThrowIfNull(available);
        var optionalMode = preference switch
        {
            PresentationModePreference.LowLatency => PresentModeKHR.MailboxKhr,
            PresentationModePreference.Immediate => PresentModeKHR.ImmediateKhr,
            _ => PresentModeKHR.FifoKhr
        };
        if (optionalMode != PresentModeKHR.FifoKhr)
        {
            for (var index = 0; index < available.Count; index++)
            {
                if (available[index] == optionalMode)
                    return optionalMode;
            }
        }
        return PresentModeKHR.FifoKhr;
    }

    /// <summary>Selects and clamps the swapchain extent.</summary>
    /// <param name="capabilities">Surface capabilities.</param>
    /// <param name="requestedWidth">Requested framebuffer width.</param>
    /// <param name="requestedHeight">Requested framebuffer height.</param>
    /// <returns>The selected extent.</returns>
    internal static Extent2D ChooseExtent(
        SurfaceCapabilitiesKHR capabilities,
        uint requestedWidth,
        uint requestedHeight)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
            return capabilities.CurrentExtent;

        return new Extent2D
        {
            Width = Math.Clamp(requestedWidth,
                capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp(requestedHeight,
                capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
        };
    }
}
