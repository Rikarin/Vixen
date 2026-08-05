// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.RenderGraph.Tests;

/// <summary>What the graph tells a profiler, and what a capture is left able to name.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These assert the wiring and nothing more, which is exactly the trap this feature fell
///         into.</b> A test proving <c>Begin</c> was called proves that a sink hears about a pass; it
///         cannot prove the numbers mean anything, because the clock they read is a real driver's.
///         The acceptance for that half is a populated timeline on a device — see the guide page.
///     </para>
///     <para>
///         What is worth pinning down here is the shape: that emission follows <em>culling</em> and
///         not declaration, that a scope contains its pass's barriers, that the debug groups balance,
///         and that nothing at all is recorded when no sink is attached.
///     </para>
/// </remarks>
public sealed class RenderGraphScopeTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly TransientResourcePool pool;
    readonly RenderGraph graph;

    public RenderGraphScopeTests() {
        pool = new(device);
        graph = new(device, pool);
    }

    public void Dispose() {
        pool.Dispose();
        device.Dispose();
    }

    static TextureDescription Target(string name) =>
        new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: name);

    /// <summary>The whole point: a document that adds a node gets a bar without opting in.</summary>
    [Fact]
    public void EveryPassIsScopedUnderItsOwnName() {
        RecordingSink sink = new();
        graph.Profiler = sink;

        var albedo = graph.CreateTexture(Target("albedo"));
        var lit = graph.CreateTexture(Target("lit"));

        graph.AddPass("gbuffer", pass => {
            pass.ColourAttachment(albedo);
            pass.Execute(_ => { });
        });

        graph.AddPass("cull", pass => {
            pass.Reads(albedo);
            pass.Writes(lit);
            pass.Execute(_ => { });
        });

        graph.AddPass("present", pass => {
            pass.Reads(lit);
            pass.ColourAttachment(graph.CreateTexture(Target("backbuffer")));
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        using TrackingCommandList list = new();
        graph.Execute(list);

        Assert.Equal(["gbuffer", "cull", "present"], sink.Opened);
        Assert.Equal(0, sink.Depth);
    }

    /// <summary>
    ///     ⚠ A culled pass is not a bar of zero, it is no bar. The graph decided it does not happen,
    ///     and a timeline that showed it would be reporting on a frame that was never recorded.
    /// </summary>
    [Fact]
    public void ACulledPassIsNotScoped() {
        RecordingSink sink = new();
        graph.Profiler = sink;

        var wasted = graph.CreateTexture(Target("wasted"));
        var kept = graph.CreateTexture(Target("kept"));

        graph.AddPass("wasteful", pass => {
            pass.ColourAttachment(wasted);
            pass.Execute(_ => { });
        });

        graph.AddPass("real", pass => {
            pass.ColourAttachment(kept);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        using TrackingCommandList list = new();
        graph.Execute(list);

        Assert.Equal(["real"], sink.Opened);
    }

    /// <summary>
    ///     A pass's barriers are its own cost. Opening the scope after them would leave every stall in
    ///     the frame outside any bar, which reads as an idle GPU rather than as a pass waiting for its
    ///     inputs — and is what makes the scopes fail to sum to the frame.
    /// </summary>
    [Fact]
    public void AScopeContainsTheBarriersItsPassNeeded() {
        RecordingSink sink = new();
        graph.Profiler = sink;

        var albedo = graph.CreateTexture(Target("albedo"));
        var lit = graph.CreateTexture(Target("lit"));

        graph.AddPass("gbuffer", pass => {
            pass.ColourAttachment(albedo);
            pass.Execute(_ => { });
        });

        // Reading what the first pass wrote as an attachment is a transition, so this pass has a
        // barrier of its own and the trace can say which side of the scope it lands on.
        graph.AddPass("lighting", pass => {
            pass.Reads(albedo);
            pass.ColourAttachment(lit);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        using TrackingCommandList list = new();
        graph.Execute(list);

        // One trace, because the question is an ordering between two things neither of which can see
        // the other: the sink's marks and the graph's barriers, interleaved by the command list.
        var trace = list.Order;
        var opened = trace.ToList().IndexOf("mark begin lighting");
        var closed = trace.ToList().IndexOf("mark close lighting");
        var barrier = -1;

        for (var index = opened + 1; index < closed; index++) {
            if (trace[index].StartsWith("barrier", StringComparison.Ordinal)) {
                barrier = index;
                break;
            }
        }

        var readable = string.Join(" | ", trace);

        Assert.True(opened >= 0 && closed > opened, readable);
        Assert.True(barrier > 0, $"the pass's barrier fell outside its scope: {readable}");
    }

    /// <summary>
    ///     ⚠ <b>The half of a capture that had no name.</b> A backend labels a render pass from its
    ///     description — Vulkan turns it into a debug group, WebGPU into a pass label — so an
    ///     attachment pass is already legible and a second group here would nest its name inside
    ///     itself. A compute dispatch has no description at all, which is why a capture of a real
    ///     frame was a wall of anonymous dispatches.
    /// </summary>
    [Fact]
    public void OnlyPassesWithoutAttachmentsGetADebugGroup() {
        var albedo = graph.CreateTexture(Target("albedo"));
        var culled = graph.CreateBuffer(new(1024, BufferUsage.Storage, Name: "visible"));

        graph.AddPass("gpu cull", pass => {
            pass.Writes(culled);
            pass.Execute(_ => { });
        });

        graph.AddPass("gbuffer", pass => {
            pass.Reads(culled);
            pass.ColourAttachment(albedo);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        using TrackingCommandList list = new();
        graph.Execute(list);

        Assert.Equal(["gpu cull"], list.Groups);
        Assert.Equal(["gbuffer"], list.Passes.Select(pass => pass.Name));

        // An unbalanced push is a capture whose tree never closes, and on a real backend a
        // validation error a frame away from the code that caused it.
        Assert.Equal(0, list.OpenGroups);
    }

    /// <summary>
    ///     ⚠ <b>Off is the default and off means nothing is recorded</b> — not a scope of zero
    ///     length, not a query written and discarded. A timestamp is a GPU write, and on a tiler it
    ///     can force a tile resolve; the only version of this feature that does not change the frame
    ///     it measures is the one that is genuinely absent when nobody asked.
    /// </summary>
    [Fact]
    public void WithNoSinkNothingIsRecordedButTheDebugGroups() {
        var albedo = graph.CreateTexture(Target("albedo"));
        var culled = graph.CreateBuffer(new(1024, BufferUsage.Storage, Name: "visible"));

        graph.AddPass("gpu cull", pass => {
            pass.Writes(culled);
            pass.Execute(_ => { });
        });

        graph.AddPass("gbuffer", pass => {
            pass.Reads(culled);
            pass.ColourAttachment(albedo);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        Assert.Null(graph.Profiler);

        using var list = device.BeginCommandList();
        device.BeginFrame();
        graph.Execute(list);
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        var commands = device.Recorder!.Commands;

        Assert.DoesNotContain(commands, command => command.Kind == RecordedCommandKind.WriteTimestamp);
        Assert.DoesNotContain(commands, command => command.Kind == RecordedCommandKind.ResetQueries);

        device.EndFrame();
    }

    /// <summary>
    ///     The graph adds no level of its own, so a host that wraps <c>Execute</c> in a scope of its
    ///     own gets the one level of grouping <see cref="GpuScope.Level" /> was designed for, and a
    ///     host that does not gets a flat list. Doc 13 asks for "timestamps around each render-graph
    ///     pass" and that is the unit the graph can honestly measure: a pass has no caller, and
    ///     nothing runs "inside" another pass in any sense a timestamp can see.
    /// </summary>
    [Fact]
    public void TheGraphNestsItsPassesUnderWhateverTheHostHasOpen() {
        RecordingSink sink = new();
        graph.Profiler = sink;

        var albedo = graph.CreateTexture(Target("albedo"));

        graph.AddPass("gbuffer", pass => {
            pass.ColourAttachment(albedo);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        using TrackingCommandList list = new();

        var frame = sink.Begin(list, "frame");
        graph.Execute(list);
        sink.Close(list, frame);

        Assert.Equal(["frame", "gbuffer"], sink.Opened);
        Assert.Equal([0, 1], sink.Levels);
    }

    /// <summary>A sink that writes down what it was told, and at what depth.</summary>
    /// <remarks>
    ///     Its marks go through the command list rather than into a list of its own, so that the
    ///     graph's barriers and this sink's scopes end up on one trace. Neither can see the other,
    ///     and the ordering between them is the thing worth asserting.
    /// </remarks>
    sealed class RecordingSink : IGpuScopeSink {
        readonly List<string> opened = [];
        readonly List<int> levels = [];

        public IReadOnlyList<string> Opened => opened;

        public IReadOnlyList<int> Levels => levels;

        /// <summary>How many scopes are open. Non-zero at the end is a scope that never closed.</summary>
        public int Depth { get; private set; }

        public int? Begin(ICommandList commands, string name) {
            ArgumentNullException.ThrowIfNull(commands);

            commands.InsertDebugMarker($"begin {name}");

            opened.Add(name);
            levels.Add(Depth++);

            return opened.Count - 1;
        }

        public void Close(ICommandList commands, int? token) {
            ArgumentNullException.ThrowIfNull(commands);

            if (token is not { } index) {
                return;
            }

            Depth--;
            commands.InsertDebugMarker($"close {opened[index]}");
        }
    }
}
