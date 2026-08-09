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

    /// <summary>Verifies immediate preference disables synchronized presentation when supported.</summary>
    [Fact]
    public void ChoosePresentMode_ImmediateWithSupport_UsesImmediate()
    {
        var selected = SwapchainPolicy.ChoosePresentMode(
            [PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr],
            PresentationModePreference.Immediate);

        Assert.Equal(PresentModeKHR.ImmediateKhr, selected);
    }

    /// <summary>Verifies immediate preference safely falls back when the surface lacks support.</summary>
    [Fact]
    public void ChoosePresentMode_ImmediateWithoutSupport_UsesFifo()
    {
        var selected = SwapchainPolicy.ChoosePresentMode(
            [PresentModeKHR.FifoKhr], PresentationModePreference.Immediate);

        Assert.Equal(PresentModeKHR.FifoKhr, selected);
    }
}
