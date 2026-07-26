// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.Null.Tests;

public sealed class CommandRecorderTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    /// <summary>
    ///     The whole point of the backend: "did my render feature emit the right calls" is a
    ///     question about a sequence, and answering it by rendering an image and diffing it is
    ///     slower, flakier and says less about what went wrong.
    /// </summary>
    [Fact]
    public void AFramesCallsComeBackInOrder() {
        var pipeline = Pipeline();
        var vertices = device.CreateBuffer(new(1024, BufferUsage.Vertex, Name: "Mesh"));

        using var list = device.BeginCommandList(name: "Frame");
        list.PushDebugGroup("Opaque");
        list.BeginRenderPass(new RenderPassDescription(Attachments(), null, "Opaque"));
        list.SetViewport(new(0, 0, 1280, 720));
        list.BindPipeline(pipeline);
        list.BindVertexBuffer(0, vertices);
        list.Draw(3);
        list.EndRenderPass();
        list.PopDebugGroup();
        list.Finish();

        device.GraphicsQueue.Submit([list]);

        var kinds = device.Recorder!.Commands.Select(command => command.Kind).ToArray();

        Assert.Equal(
            [
                RecordedCommandKind.PushDebugGroup,
                RecordedCommandKind.BeginRenderPass,
                RecordedCommandKind.SetViewport,
                RecordedCommandKind.BindPipeline,
                RecordedCommandKind.BindVertexBuffer,
                RecordedCommandKind.Draw,
                RecordedCommandKind.EndRenderPass,
                RecordedCommandKind.PopDebugGroup
            ],
            kinds
        );
    }

    [Fact]
    public void ADrawKeepsItsArguments() {
        using var list = device.BeginCommandList();
        list.BeginRenderPass(new RenderPassDescription(Attachments(), null, "Opaque"));
        list.DrawIndexed(36, instanceCount: 4, firstIndex: 6, vertexOffset: 2, firstInstance: 1);
        list.EndRenderPass();
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        Assert.Equal(36, draw.A);
        Assert.Equal(4, draw.B);
        Assert.Equal(6, draw.C);
        Assert.Equal(2, draw.D);
        Assert.Equal(1, draw.E);
    }

    /// <summary>
    ///     Contiguous rather than "somewhere in the stream": a looser match would pass when a
    ///     barrier was inserted in the middle of a copy, which is exactly the mistake worth
    ///     catching.
    /// </summary>
    [Fact]
    public void ASubsequenceMatchesOnlyWhenItIsContiguous() {
        using var list = device.BeginCommandList();
        list.BeginRenderPass(new RenderPassDescription(Attachments(), null, "Opaque"));
        list.Draw(3);
        list.Draw(6);
        list.EndRenderPass();
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        var recorder = device.Recorder!;

        Assert.True(
            recorder.Contains(
                new RecordedCommand(RecordedCommandKind.Draw, 0, 3, 1),
                new RecordedCommand(RecordedCommandKind.Draw, 0, 6, 1)
            )
        );

        Assert.False(
            recorder.Contains(
                new RecordedCommand(RecordedCommandKind.Draw, 0, 3, 1),
                new RecordedCommand(RecordedCommandKind.EndRenderPass, 0)
            )
        );
    }

    /// <summary>
    ///     Lists are recorded on several threads at once, so a recorder written into as calls
    ///     happened would have an order that depended on the scheduler. Submission order is what the
    ///     GPU would see, and it is what a test asserts on.
    /// </summary>
    [Fact]
    public void TheStreamIsInSubmissionOrderNotRecordingOrder() {
        var first = device.BeginCommandList(name: "Shadows");
        var second = device.BeginCommandList(name: "Opaque");

        // Interleaved deliberately: the recorder must not see this order.
        first.PushDebugGroup("Shadows");
        second.PushDebugGroup("Opaque");
        second.PopDebugGroup();
        first.PopDebugGroup();

        first.Finish();
        second.Finish();

        device.GraphicsQueue.Submit([second, first]);

        var names = device.Recorder!
            .OfKind(RecordedCommandKind.PushDebugGroup)
            .Select(command => command.Text)
            .ToArray();

        Assert.Equal(["Opaque", "Shadows"], names.Select(name => name ?? string.Empty));

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void TheDumpIsIndentedByDebugGroup() {
        using var list = device.BeginCommandList();
        list.PushDebugGroup("Frame");
        list.PushDebugGroup("Shadows");
        list.InsertDebugMarker("Cascade 0");
        list.PopDebugGroup();
        list.PopDebugGroup();
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        var lines = device.Recorder!.Dump().Split('\n');

        Assert.StartsWith("#0 PushDebugGroup", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  #1 PushDebugGroup", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("    #2 InsertDebugMarker", lines[2], StringComparison.Ordinal);
    }

    // ── The validation, which is the other half of what this backend is for ──────────────────

    [Fact]
    public void ADrawOutsideARenderPassIsRefused() {
        using var list = device.BeginCommandList();

        var thrown = Assert.Throws<InvalidOperationException>(() => list.Draw(3));
        Assert.Contains("outside a render pass", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderPassesDoNotNest() {
        using var list = device.BeginCommandList();
        list.BeginRenderPass(new RenderPassDescription(Attachments(), null, "Opaque"));

        Assert.Throws<InvalidOperationException>(() => list.BeginRenderPass(new RenderPassDescription(Attachments(), null, "Opaque")));
    }

    /// <summary>
    ///     A tiled GPU would have to resolve the tile to run a dispatch or a copy mid-pass, which is
    ///     why no API allows it — and why catching it without a GPU is worth doing.
    /// </summary>
    [Fact]
    public void ComputeAndCopiesAreRefusedInsideARenderPass() {
        var buffer = device.CreateBuffer(new(64, BufferUsage.CopySource, Name: "A"));
        var other = device.CreateBuffer(new(64, BufferUsage.CopyDestination, Name: "B"));

        using var list = device.BeginCommandList();
        list.BeginRenderPass(new RenderPassDescription(Attachments(), null, "Opaque"));

        Assert.Throws<InvalidOperationException>(() => list.Dispatch(1));
        Assert.Throws<InvalidOperationException>(() => list.CopyBuffer(buffer, 0, other, 0, 64));
        Assert.Throws<InvalidOperationException>(() => list.Barrier(new([], [])));
    }

    [Fact]
    public void AListFinishedInsideAPassIsRefused() {
        using var list = device.BeginCommandList(name: "Broken");
        list.BeginRenderPass(new RenderPassDescription(Attachments(), null, "Opaque"));

        var thrown = Assert.Throws<InvalidOperationException>(list.Finish);
        Assert.Contains("inside a render pass", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnclosedDebugGroupIsRefused() {
        using var list = device.BeginCommandList(name: "Broken");
        list.PushDebugGroup("Opaque");

        Assert.Throws<InvalidOperationException>(list.Finish);
    }

    [Fact]
    public void AListSubmittedBeforeItIsFinishedIsRefused() {
        using var list = device.BeginCommandList();

        var thrown = Assert.Throws<InvalidOperationException>(() => device.GraphicsQueue.Submit([list]));
        Assert.Contains("before Finish()", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListSubmittedTwiceIsRefused() {
        using var list = device.BeginCommandList();
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        Assert.Throws<InvalidOperationException>(() => device.GraphicsQueue.Submit([list]));
    }

    [Fact]
    public void RecordingIntoAFinishedListIsRefused() {
        using var list = device.BeginCommandList();
        list.Finish();

        Assert.Throws<InvalidOperationException>(() => list.Draw(3));
    }

    [Fact]
    public void ACopyOntoItselfIsRefused() {
        var buffer = device.CreateBuffer(new(64, BufferUsage.CopySource | BufferUsage.CopyDestination, Name: "A"));

        using var list = device.BeginCommandList();

        Assert.Throws<ArgumentException>(() => list.CopyBuffer(buffer, 0, buffer, 32, 16));
    }

    [Fact]
    public void ARenderPassWithNoAttachmentsIsRefused() {
        using var list = device.BeginCommandList();

        Assert.Throws<InvalidOperationException>(
            () => list.BeginRenderPass(new RenderPassDescription([], null, "Empty"))
        );
    }

    [Fact]
    public void AnEmptyBarrierRecordsNothing() {
        using var list = device.BeginCommandList();
        list.Barrier(new([], []));
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        Assert.Equal(0, device.Recorder!.CountOf(RecordedCommandKind.Barrier));
    }

    public void Dispose() => device.Dispose();

    /// <summary>
    ///     The attachments a pass renders into, as an array rather than a collection expression:
    ///     <c>RenderPassDescription</c> is a <c>ref struct</c> over a span, so a span of a temporary
    ///     cannot outlive the method that built it — which is the compiler saying the right thing,
    ///     since a pass description is meant to be used and dropped inside one call.
    /// </summary>
    ColourAttachment[] Attachments() {
        var texture = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget, Name: "Target")
        );

        return [new ColourAttachment(device.CreateTextureView(texture))];
    }

    PipelineHandle Pipeline() {
        var vertex = device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "Triangle.vs");
        var fragment = device.CreateShader(ShaderStage.Fragment, [5, 6, 7, 8], "Triangle.fs");
        var layout = device.CreatePipelineLayout(new([], Name: "Empty"));

        return device.CreateGraphicsPipeline(
            new(
                vertex,
                fragment,
                layout,
                [new ColourTargetState(PixelFormat.Rgba8UNorm)],
                DepthStencil: DepthStencilState.Disabled,
                Name: "Triangle"
            )
        );
    }
}
