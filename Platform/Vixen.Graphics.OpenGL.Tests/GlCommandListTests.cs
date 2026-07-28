// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>The concession <c>docs/plan/05</c> declares up front, checked.</summary>
/// <remarks>
///     GL has no multithreaded recording. The RHI says a command list may be recorded on any thread,
///     and that contract is worth more than the cost of keeping it — so recording writes into managed
///     memory and the calls happen on the GL thread at submit. The claim only holds if recording
///     really touches nothing, which is the first test here.
/// </remarks>
public sealed class GlCommandListTests {
    /// <summary>Recording a whole frame's worth of work makes no GL call at all.</summary>
    [Fact]
    public void RecordsWithoutTouchingGl() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var pipeline = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled);
        var target = Target(device, out var view);
        gl.Clear();

        using var commands = device.BeginCommandList(QueueKind.Graphics, "frame");
        commands.PushDebugGroup("opaque");
        commands.BeginRenderPass(new([new(view, LoadAction.Clear, StoreAction.Store, new(1f, 0f, 0f, 1f))]));
        commands.SetViewport(new(0, 0, 64, 64));
        commands.BindPipeline(pipeline);
        commands.Draw(3);
        commands.EndRenderPass();
        commands.PopDebugGroup();
        commands.Finish();

        Assert.Empty(gl.Calls);
        Assert.False(target.Equals(TextureHandle.Null));
    }

    /// <summary>Render passes do not nest.</summary>
    /// <remarks>
    ///     Vulkan and D3D12 both forbid it and GL has no way to express it, so a nested pass would be
    ///     three different wrong things. Caught while recording, which is the stack frame that opened
    ///     the outer one.
    /// </remarks>
    [Fact]
    public void RefusesANestedPass() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        Target(device, out var view);

        using var commands = device.BeginCommandList();
        commands.BeginRenderPass(new([new(view)]));

        Assert.Throws<InvalidOperationException>(() => commands.BeginRenderPass(new([new(view)])));
    }

    /// <summary>A draw outside a pass is refused.</summary>
    /// <remarks>
    ///     GL would draw into whichever framebuffer happened to be bound, which is the class of bug
    ///     that renders into the previous pass's target and looks like a barrier problem.
    /// </remarks>
    [Fact]
    public void RefusesADrawOutsideAPass() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));

        using var commands = device.BeginCommandList();
        Assert.Throws<InvalidOperationException>(() => commands.Draw(3));
    }

    /// <summary>A dispatch inside a pass is refused.</summary>
    /// <remarks>
    ///     Vulkan allows it and GL does not — there is no compute stage inside a framebuffer's scope
    ///     — so the RHI takes the stricter of the two and this is rejected everywhere. Which is
    ///     exactly the kind of decision ADR-001 wanted the GL backend to surface early.
    /// </remarks>
    [Fact]
    public void RefusesADispatchInsideAPass() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        Target(device, out var view);

        using var commands = device.BeginCommandList();
        commands.BeginRenderPass(new([new(view)]));

        Assert.Throws<InvalidOperationException>(() => commands.Dispatch(1));
    }

    /// <summary>Finishing inside a pass is refused.</summary>
    [Fact]
    public void RefusesToFinishInsideAPass() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        Target(device, out var view);

        using var commands = device.BeginCommandList();
        commands.BeginRenderPass(new([new(view)]));

        Assert.Throws<InvalidOperationException>(commands.Finish);
    }

    /// <summary>Recording after finishing is refused.</summary>
    [Fact]
    public void RefusesToRecordAfterFinishing() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));

        using var commands = device.BeginCommandList();
        commands.Finish();

        Assert.Throws<InvalidOperationException>(() => commands.SetStencilReference(1));
    }

    /// <summary>Submitting an unfinished list is refused.</summary>
    [Fact]
    public void RefusesAnUnfinishedSubmission() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        using var commands = device.BeginCommandList();

        Assert.Throws<InvalidOperationException>(() => device.GraphicsQueue.Submit([commands]));
    }

    /// <summary>Submitting twice is refused.</summary>
    /// <remarks>A list is a one-shot recording; replaying it again would replay it against state the
    /// first replay left behind.</remarks>
    [Fact]
    public void RefusesASecondSubmission() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        using var commands = device.BeginCommandList();
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Throws<InvalidOperationException>(() => device.GraphicsQueue.Submit([commands]));
    }

    /// <summary>A disposed list comes back from the pool ready to record again.</summary>
    /// <remarks>
    ///     Disposing returns a list to the pool rather than destroying anything — the RHI says it is
    ///     a scratch buffer and not a resource with a lifetime — and a pooled list that came back
    ///     still marked finished would refuse the next frame's first call.
    /// </remarks>
    [Fact]
    public void RearmsAPooledList() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));

        var first = device.BeginCommandList();
        first.Finish();
        first.Dispose();

        var second = device.BeginCommandList();
        Assert.Same(first, second);
        Assert.False(second.IsRecorded);
        second.SetStencilReference(1);
        second.Finish();
    }

    /// <summary>A three-component origin survives the round trip through one packed field.</summary>
    /// <remarks>
    ///     A texture-to-texture copy is the one command with three separate vectors, and widening the
    ///     command struct for it would cost every other command the memory.
    /// </remarks>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 2, 3)]
    [InlineData(16383, 8191, 2047)]
    [InlineData(2097151, 2097151, 2097151)]
    public void PacksAndUnpacksAnOrigin(int x, int y, int z) {
        var origin = new Int3(x, y, z);
        Assert.Equal(origin, GlCommandList.Unpack(GlCommandList.Pack(origin)));
    }

    static TextureHandle Target(GlDevice device, out TextureViewHandle view) {
        var texture = device.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            64,
            64,
            TextureUsage.ColourTarget | TextureUsage.CopySource,
            Name: "target"
        ));

        view = device.CreateTextureView(texture);
        return texture;
    }
}
