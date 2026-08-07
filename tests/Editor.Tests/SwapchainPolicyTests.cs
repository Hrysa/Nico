using Engine.Graphics;
using Silk.NET.Vulkan;
using Xunit;

namespace Editor.Tests;

public class SwapchainPolicyTests
{
    /// <summary>Verifies low-latency windows prefer mailbox when the surface supports it.</summary>
    [Fact]
    public void ChoosePresentMode_LowLatencyWithMailbox_UsesMailbox()
    {
        var selected = SwapchainPolicy.ChoosePresentMode(
            [PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr],
            PresentationModePreference.LowLatency);

        Assert.Equal(PresentModeKHR.MailboxKhr, selected);
    }

    /// <summary>Verifies low-latency preference safely falls back to guaranteed FIFO.</summary>
    [Fact]
    public void ChoosePresentMode_LowLatencyWithoutMailbox_UsesFifo()
    {
        var selected = SwapchainPolicy.ChoosePresentMode(
            [PresentModeKHR.FifoKhr], PresentationModePreference.LowLatency);

        Assert.Equal(PresentModeKHR.FifoKhr, selected);
    }

    /// <summary>Verifies default windows retain FIFO even when mailbox is available.</summary>
    [Fact]
    public void ChoosePresentMode_FifoPreference_UsesFifo()
    {
        var selected = SwapchainPolicy.ChoosePresentMode(
            [PresentModeKHR.MailboxKhr, PresentModeKHR.FifoKhr],
            PresentationModePreference.Fifo);

        Assert.Equal(PresentModeKHR.FifoKhr, selected);
    }
}
