// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Editor.Debugger.Tests;

/// <summary>Building the tree, stepping the draws, and replaying the state.</summary>
public sealed class FrameCaptureTests {
    static CapturedCommand Command(CaptureCommandKind kind, string? label = null, long a = 0, long b = 0, long c = 0) =>
        new(0, kind, label, a, b, c);

    static FrameCapture Frame() =>
        new(
            "test",
            [
                Command(CaptureCommandKind.PushGroup, "shadows"),
                Command(CaptureCommandKind.BeginPass, "shadow pass", a: 0, b: 1),
                Command(CaptureCommandKind.BindPipeline, a: 7),
                Command(CaptureCommandKind.Draw, a: 300, b: 1),
                Command(CaptureCommandKind.EndPass),
                Command(CaptureCommandKind.PopGroup),
                Command(CaptureCommandKind.BeginPass, "ui", a: 1),
                Command(CaptureCommandKind.BindPipeline, a: 9),
                Command(CaptureCommandKind.BindVertexBuffer, a: 0, b: 42),
                Command(CaptureCommandKind.Draw, a: 6, b: 1)
            ]
        );

    [Fact]
    public void PassesAndGroupsBecomeTheTreeAndTheirEndsDoNot() {
        var capture = Frame();

        Assert.Equal(2, capture.Roots.Count);

        var group = capture.Roots[0];
        Assert.Equal(CaptureCommandKind.PushGroup, group.Command.Kind);

        var pass = Assert.Single(group.Children);
        Assert.Equal(CaptureCommandKind.BeginPass, pass.Command.Kind);

        // Bind and draw, and no "end pass" row: it says nothing the opening row did not.
        Assert.Equal(2, pass.Children.Count);
    }

    [Fact]
    public void ANodeKnowsWhereItsScopeEnds() {
        var capture = Frame();
        var group = capture.Roots[0];

        Assert.Equal(5, group.EndSequence);
        Assert.Equal(4, group.Children[0].EndSequence);
    }

    [Fact]
    public void WorkIsCountedThroughTheWholeSubtree() {
        var capture = Frame();

        Assert.Equal(2, capture.WorkCount);
        Assert.Equal(1, capture.Roots[0].WorkCount);
        Assert.Equal([3, 9], capture.Work);
    }

    /// <summary>
    ///     ⚠ Stepping moves between draws. A step of one command would take forty presses to reach
    ///     the next thing that put a pixel anywhere.
    /// </summary>
    [Fact]
    public void SteppingMovesBetweenDrawsRatherThanCommands() {
        var capture = Frame();

        Assert.Equal(3, capture.NextWork(0));
        Assert.Equal(9, capture.NextWork(4));
        Assert.Null(capture.NextWork(10));

        Assert.Equal(9, capture.PreviousWork(9));
        Assert.Equal(3, capture.PreviousWork(8));
        Assert.Null(capture.PreviousWork(2));
    }

    [Fact]
    public void StateIsReplayedUpToAndIncludingTheNamedCall() {
        var capture = Frame();
        var state = capture.StateAt(3);

        Assert.Equal("shadow pass", state.Pass);
        Assert.True(state.HasDepth);
        Assert.Equal(7, state.Pipeline);
        Assert.Equal(["shadows"], state.Groups);
    }

    /// <summary>
    ///     ⚠ Everything a pass scopes goes when the pass ends, because the RHI says so — a state
    ///     pane carrying a pipeline across a boundary would show a binding the next pass has not got.
    /// </summary>
    [Fact]
    public void EndingAPassClearsEverythingItScoped() {
        var capture = Frame();
        var state = capture.StateAt(5);

        Assert.Null(state.Pass);
        Assert.Null(state.Pipeline);
        Assert.Empty(state.VertexBuffers);
    }

    [Fact]
    public void TheSecondPassSeesItsOwnBindingsAndNotTheFirstsPipeline() {
        var capture = Frame();
        var state = capture.StateAt(9);

        Assert.Equal("ui", state.Pass);
        Assert.Equal(9, state.Pipeline);
        Assert.Equal(42, state.VertexBuffers[0]);
        Assert.Empty(state.Groups);
    }

    [Fact]
    public void TheStateRowsAreGroupedAndSorted() {
        var rows = Frame().StateAt(9).Rows();

        Assert.Contains(rows, row => row is { Group: "Target", Label: "Render pass", Value: "ui" });
        Assert.Contains(rows, row => row is { Group: "Pipeline", Label: "Pipeline", Value: "#9" });
        Assert.Contains(rows, row => row.Group == "Geometry" && row.Value == "#42");
    }

    /// <summary>
    ///     ⚠ A capture from a frame that threw halfway is exactly the one somebody needs to open, so
    ///     an unclosed pass ends at the stream rather than making the capture unopenable.
    /// </summary>
    [Fact]
    public void AnUnbalancedStreamIsToleratedRatherThanRefused() {
        FrameCapture capture = new(
            "torn",
            [
                Command(CaptureCommandKind.BeginPass, "half a pass"),
                Command(CaptureCommandKind.Draw, a: 3)
            ]
        );

        var pass = Assert.Single(capture.Roots);

        Assert.Equal(1, pass.EndSequence);
        Assert.Single(pass.Children);
    }

    /// <summary>
    ///     ⚠ Renumbered, because a capture is assembled from several lists that each numbered their
    ///     own calls from zero — and `StateAt` replays a prefix of *this* array.
    /// </summary>
    [Fact]
    public void SequenceNumbersAreThisCapturesRatherThanTheSources() {
        FrameCapture capture = new(
            "joined",
            [
                new(0, CaptureCommandKind.BeginPass, "first"),
                new(0, CaptureCommandKind.EndPass),
                new(0, CaptureCommandKind.BeginPass, "second")
            ]
        );

        Assert.Equal(2, capture.Roots[1].Command.Sequence);
        Assert.Equal("second", capture.StateAt(2).Pass);
    }

    [Fact]
    public void AnEmptyCaptureClampsRatherThanThrowing() {
        Assert.True(FrameCapture.Empty.IsEmpty);
        Assert.Null(FrameCapture.Empty.StateAt(50).Pass);
        Assert.Null(FrameCapture.Empty.NextWork(0));
    }

    /// <summary>The adapter doc 20 names as the shape a capture takes.</summary>
    [Fact]
    public void TheNullRecordersStreamConvertsIntoACapture() {
        using NullDevice device = new(new() { Record = true });

        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.ColourTarget))
        );

        using (var list = device.BeginCommandList()) {
            list.PushDebugGroup("ui");
            list.BeginRenderPass(new([new(target)], name: "ui pass"));
            list.Draw(6);
            list.EndRenderPass();
            list.PopDebugGroup();
            list.Finish();

            device.GraphicsQueue.Submit([list]);
        }

        var capture = NullFrameCapture.From(device.Recorder!, "editor frame");

        Assert.Equal("editor frame", capture.Name);
        Assert.Equal(1, capture.WorkCount);

        var group = Assert.Single(capture.Roots);
        var pass = Assert.Single(group.Children);

        Assert.Equal("ui pass", pass.Command.Label);

        // The pass and the debug group are two different names in the same state, which is the whole
        // point of carrying both: a capture with one "ui" in it cannot say which layer it came from.
        var state = capture.StateAt(capture.Work[0]);

        Assert.Equal("ui pass", state.Pass);
        Assert.Equal(["ui"], state.Groups);
    }

    /// <summary>
    ///     ⚠ The profiler's own timestamp writes are dropped, or a frame debugger would report two
    ///     extra calls per pass that are not in the frame anybody shipped.
    /// </summary>
    [Fact]
    public void TheProfilersOwnTimestampsAreNotPartOfTheCapture() {
        using NullDevice device = new(new() { Record = true });

        var pool = device.CreateQueryPool(new(QueryKind.Timestamp, 4, "profiler"));

        using (var list = device.BeginCommandList()) {
            list.ResetQueries(pool, 0, 4);
            list.WriteTimestamp(pool, 0);
            list.WriteTimestamp(pool, 1);
            list.Finish();

            device.GraphicsQueue.Submit([list]);
        }

        Assert.True(NullFrameCapture.From(device.Recorder!).IsEmpty);
    }
}
