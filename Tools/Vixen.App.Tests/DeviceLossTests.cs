// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>What the host does when the device goes, which until now nothing had ever asked it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>doc 05 § Testing asks for a fault-injection mode "proving the engine recreates the
///         device and reloads resources rather than crashing", and the engine does no such thing —
///         deliberately.</b> <c>AppGraphics.IsLost</c> says so in its own remarks: "a latch rather
///         than a recovery… rebuilding every device resource a game holds after a driver reset is a
///         whole feature — one this records honestly as absent rather than pretending at with a
///         half-measure that leaves handles dangling." So what is provable here is the latch, and
///         these prove it. #303 asked for the other thing and its premise is the plan's, not the
///         tree's.
///     </para>
///     <para>
///         ⚠ <b>The injection point was not missing either.</b> <c>NullSwapChain.NextStatus</c> has
///         been a fault switch since it was written, citing doc 05 in its own comment. What was
///         missing was reach: a host builds its own swapchain on the first frame and keeps it
///         private, so nothing driving a host could set it — which is why both
///         <c>SwapChainStatus.DeviceLost</c> branches in <c>AppGraphics</c> had never executed.
///         <c>NullDevice.NextSwapChainStatus</c> is the same switch one level up, where a test can
///         reach it before, between or <em>during</em> frames.
///     </para>
///     <para>
///         Both branches are covered, and they are not the same branch: the acquire one is reached
///         by losing the device between frames, the present one by losing it inside
///         <c>OnRender</c> — which runs after <c>Begin</c> and before <c>End</c>, and is the only
///         moment in the tree where "mid-frame" can be spelled.
///     </para>
/// </remarks>
public sealed class DeviceLossTests : IDisposable {
    /// <summary>The record the host writes when the device has gone.</summary>
    const int DeviceLostEvent = 13014;

    readonly TemporaryFileSystemHost files = new();

    /// <inheritdoc />
    public void Dispose() => files.Dispose();

    /// <summary>A frame with no fault in it draws, which is what makes the rest mean anything.</summary>
    /// <remarks>
    ///     ⚠ The instrument. Every assertion below is about a host that stopped drawing, and a host
    ///     that never started would satisfy all of them — on a platform whose windows cannot present,
    ///     for instance, which is what the headless one is and why <see cref="PresentingPlatform" />
    ///     exists.
    /// </remarks>
    [Fact]
    public void AFrameWithNoFaultInItIsDrawn() {
        using var device = new NullDevice(new() { Record = true });
        using var application = Presenting(device, new WindowedGame());

        application.Initialise();

        var graphics = application.Services.Graphics!;

        Assert.True(graphics.Begin());
        Assert.NotNull(graphics.Commands);

        graphics.End();

        Assert.False(graphics.IsLost);
        Assert.DoesNotContain(application.Services.Logs.Snapshot(), entry => entry.EventId == DeviceLostEvent);
    }

    /// <summary>A device lost between frames is caught on the acquire, and latches.</summary>
    [Fact]
    public void ADeviceLostOnAcquireLatchesTheHost() {
        using var device = new NullDevice(new() { Record = true });
        using var application = Presenting(device, new WindowedGame());

        application.Initialise();

        var graphics = application.Services.Graphics!;

        // A real frame first, so the swapchain exists and the fault lands on a host that was working.
        Assert.True(graphics.Begin());
        graphics.End();

        device.NextSwapChainStatus = SwapChainStatus.DeviceLost;

        Assert.False(graphics.Begin());
        Assert.True(graphics.IsLost);
        Assert.Null(graphics.Commands);

        Assert.Contains(application.Services.Logs.Snapshot(), entry => entry.EventId == DeviceLostEvent);
    }

    /// <summary>A device lost during the frame is caught on the present, and latches.</summary>
    /// <remarks>
    ///     ⚠ A different branch, and it had to be reached differently: <c>Begin</c> acquires and
    ///     <c>End</c> presents, so a fault set before the frame is answered by the first and never
    ///     reaches the second. <c>OnRender</c> is what runs between them.
    /// </remarks>
    [Fact]
    public void ADeviceLostDuringTheFrameLatchesOnThePresent() {
        using var device = new NullDevice(new() { Record = true });
        using var application = Presenting(device, new LosingGame(device));

        application.Initialise();
        application.RunFrame();

        var graphics = application.Services.Graphics!;

        Assert.True(graphics.IsLost);
        Assert.Contains(application.Services.Logs.Snapshot(), entry => entry.EventId == DeviceLostEvent);
    }

    /// <summary>The latch holds, and it says so once rather than once a frame.</summary>
    /// <remarks>
    ///     <para>
    ///         Expressed as work and order rather than as elapsed time: after the loss, further
    ///         frames open no command list and write no second record. A latch that had been written
    ///         as "log and carry on" passes every other assertion in this file and fails this one,
    ///         at sixty error records a second.
    ///     </para>
    ///     <para>
    ///         ⚠ And clearing the injection does not bring it back. That is the deliberate absence,
    ///         asserted so that a later recovery feature cannot land silently: whoever builds one
    ///         has to come here and say the premise moved.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ALatchedHostStaysLatchedAndSaysSoOnce() {
        using var device = new NullDevice(new() { Record = true });
        using var application = Presenting(device, new WindowedGame());

        application.Initialise();

        var graphics = application.Services.Graphics!;

        Assert.True(graphics.Begin());
        graphics.End();

        device.NextSwapChainStatus = SwapChainStatus.DeviceLost;
        Assert.False(graphics.Begin());

        for (var frame = 0; frame < 5; frame++) {
            Assert.False(graphics.Begin());
            Assert.Null(graphics.Commands);
        }

        // The device comes back and the host does not, which is what "a latch rather than a
        // recovery" means.
        device.NextSwapChainStatus = SwapChainStatus.Ready;

        Assert.False(graphics.Begin());
        Assert.True(graphics.IsLost);

        Assert.Single(application.Services.Logs.Snapshot(), entry => entry.EventId == DeviceLostEvent);
    }

    /// <summary>The same application on a platform whose window can actually be presented to.</summary>
    VixenApplication Presenting(IGraphicsDevice device, Game game) =>
        VixenApp.Create(["--vixen-workers", "1", "--vixen-frame-limit", "0"])
            .WithPlatform(new PresentingPlatform(new HeadlessPlatform(new HeadlessPlatformOptions { FileSystem = files })))
            .WithGraphics(device)
            .Build(game);

    /// <summary>A window and nothing else, which is the ordinary desktop run.</summary>
    sealed class WindowedGame : Game {
        protected internal override void OnConfigure(AppConfig config) => config.Window = new();
    }

    /// <summary>The same, losing its device in the middle of its own frame.</summary>
    /// <param name="device">The device to lose.</param>
    sealed class LosingGame(NullDevice device) : Game {
        protected internal override void OnConfigure(AppConfig config) => config.Window = new();

        protected internal override void OnRender(GameTime time) =>
            device.NextSwapChainStatus = SwapChainStatus.DeviceLost;
    }
}
