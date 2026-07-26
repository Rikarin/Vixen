// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.RenderGraph.Tests;

/// <summary>What the graph decides, and what it refuses.</summary>
public sealed class RenderGraphTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly TransientResourcePool pool;
    readonly RenderGraph graph;

    public RenderGraphTests() {
        pool = new(device);
        graph = new(device, pool);
    }

    public void Dispose() {
        pool.Dispose();
        device.Dispose();
    }

    static TextureDescription Target(string name, int size = 256) =>
        new(PixelFormat.Rgba8UNorm, size, size, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: name);

    static TextureDescription Depth(string name, int size = 256) =>
        new(PixelFormat.Depth32Float, size, size, TextureUsage.DepthStencilTarget, Name: name);

    // ── Culling ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A pass whose output nothing reads and which leaves nothing behind does not run.</summary>
    [Fact]
    public void APassNobodyNeedsIsCulled() {
        var wasted = graph.CreateTexture(Target("wasted"));
        var ran = false;

        graph.AddPass("wasteful", pass => {
            pass.Writes(wasted, ResourceState.ColourTarget);
            pass.Execute(_ => ran = true);
        });

        graph.Execute(new TrackingCommandList());

        Assert.False(ran, "A pass with no consumer ran anyway.");
        Assert.Equal(0, graph.SurvivingPassCount);
    }

    /// <summary>
    ///     Removing a pass can orphan the one that fed it, so culling has to iterate. A chain of
    ///     three where only the last is useless must lose all three.
    /// </summary>
    [Fact]
    public void CullingPropagatesBackwardsThroughAChain() {
        var first = graph.CreateTexture(Target("first"));
        var second = graph.CreateTexture(Target("second"));
        var third = graph.CreateTexture(Target("third"));
        var ran = new List<string>();

        graph.AddPass("a", pass => {
            pass.Writes(first, ResourceState.ColourTarget);
            pass.Execute(_ => ran.Add("a"));
        });

        graph.AddPass("b", pass => {
            pass.Reads(first);
            pass.Writes(second, ResourceState.ColourTarget);
            pass.Execute(_ => ran.Add("b"));
        });

        graph.AddPass("c", pass => {
            pass.Reads(second);
            pass.Writes(third, ResourceState.ColourTarget);
            pass.Execute(_ => ran.Add("c"));
        });

        graph.Execute(new TrackingCommandList());

        Assert.Empty(ran);
        Assert.Equal(0, graph.SurvivingPassCount);
    }

    /// <summary>A pass that writes something the graph did not create is kept: it is the output.</summary>
    [Fact]
    public void APassWritingAnImportedResourceSurvives() {
        var description = Target("backbuffer");
        var texture = device.CreateTexture(description);
        var view = device.CreateTextureView(texture);
        var imported = graph.ImportTexture(texture, view, description, exitState: ResourceState.Present);
        var ran = false;

        graph.AddPass("present", pass => {
            pass.ColourAttachment(imported);
            pass.Execute(_ => ran = true);
        });

        graph.Execute(new TrackingCommandList());

        Assert.True(ran);
        Assert.Equal(1, graph.SurvivingPassCount);
    }

    /// <summary>
    ///     A readback, a timestamp, a debug overlay: work whose whole point is outside the graph. It
    ///     has no consumer the graph can see and would otherwise be removed for being useless.
    /// </summary>
    [Fact]
    public void ASideEffectPassSurvivesWithNoConsumer() {
        var scratch = graph.CreateTexture(Target("scratch"));
        var ran = false;

        graph.AddPass("readback", pass => {
            pass.Writes(scratch, ResourceState.CopyDestination);
            pass.SideEffect();
            pass.Execute(_ => ran = true);
        });

        graph.Execute(new TrackingCommandList());

        Assert.True(ran);
    }

    /// <summary>Culling keeps a chain that ends somewhere real.</summary>
    [Fact]
    public void AChainThatReachesTheOutputIsKeptWhole() {
        var description = Target("backbuffer");
        var texture = device.CreateTexture(description);
        var view = device.CreateTextureView(texture);
        var backbuffer = graph.ImportTexture(texture, view, description, exitState: ResourceState.Present);

        var shadow = graph.CreateTexture(Depth("shadow"));
        var lit = graph.CreateTexture(Target("lit"));
        var unused = graph.CreateTexture(Target("unused"));
        var ran = new List<string>();

        graph.AddPass("shadow", pass => {
            pass.DepthAttachment(shadow);
            pass.Execute(_ => ran.Add("shadow"));
        });

        graph.AddPass("dead", pass => {
            pass.Writes(unused, ResourceState.ColourTarget);
            pass.Execute(_ => ran.Add("dead"));
        });

        graph.AddPass("lighting", pass => {
            pass.Reads(shadow, ResourceState.ShaderRead);
            pass.ColourAttachment(lit);
            pass.Execute(_ => ran.Add("lighting"));
        });

        graph.AddPass("blit", pass => {
            pass.Reads(lit);
            pass.ColourAttachment(backbuffer);
            pass.Execute(_ => ran.Add("blit"));
        });

        graph.Execute(new TrackingCommandList());

        Assert.Equal(["shadow", "lighting", "blit"], ran);
    }

    // ── Barriers ────────────────────────────────────────────────────────────────────────────

    /// <summary>A resource that changes use between passes gets a transition, once.</summary>
    [Fact]
    public void ATransitionIsEmittedWhereTheUseChanges() {
        var description = Target("backbuffer");
        var texture = device.CreateTexture(description);
        var view = device.CreateTextureView(texture);
        var backbuffer = graph.ImportTexture(texture, view, description, exitState: ResourceState.Present);
        var scene = graph.CreateTexture(Target("scene"));

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(scene);
            pass.Execute(_ => { });
        });

        graph.AddPass("blit", pass => {
            pass.Reads(scene);
            pass.ColourAttachment(backbuffer);
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var sceneBarriers = list.Barriers.Where(barrier => barrier.Texture == graph.TextureOf(scene)).ToArray();

        Assert.Equal(2, sceneBarriers.Length);
        Assert.Equal(ResourceState.Undefined, sceneBarriers[0].Before);
        Assert.Equal(ResourceState.ColourTarget, sceneBarriers[0].After);
        Assert.Equal(ResourceState.ColourTarget, sceneBarriers[1].Before);
        Assert.Equal(ResourceState.ShaderRead, sceneBarriers[1].After);
    }

    /// <summary>
    ///     A resource read by two passes in a row is transitioned once, not twice. Re-emitting a
    ///     no-op transition costs a pipeline stall for nothing, and nothing about the picture says so.
    /// </summary>
    [Fact]
    public void AnUnchangedStateIsNotTransitionedAgain() {
        var scene = graph.CreateTexture(Target("scene"));
        var a = graph.CreateTexture(Target("a"));
        var b = graph.CreateTexture(Target("b"));

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(scene);
            pass.Execute(_ => { });
        });

        graph.AddPass("read once", pass => {
            pass.Reads(scene);
            pass.ColourAttachment(a);
            pass.Execute(_ => { });
        });

        graph.AddPass("read twice", pass => {
            pass.Reads(scene);
            pass.ColourAttachment(b);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var reads = list.Barriers
            .Where(barrier => barrier.Texture == graph.TextureOf(scene) && barrier.After == ResourceState.ShaderRead)
            .ToArray();

        Assert.Single(reads);
    }

    /// <summary>
    ///     Two passes writing the same target back to back is a write-after-write hazard. The states
    ///     are equal, which is exactly why the naive "transition only when it changes" rule gets it
    ///     wrong — nothing about them being equal makes the first write visible to the second.
    /// </summary>
    [Fact]
    public void AWriteAfterWriteStillGetsABarrier() {
        var target = graph.CreateTexture(Target("target"));

        graph.AddPass("first", pass => {
            pass.ColourAttachment(target);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.AddPass("second", pass => {
            pass.ColourAttachment(target, LoadAction.Load);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var writes = list.Barriers
            .Where(barrier => barrier.After == ResourceState.ColourTarget)
            .ToArray();

        Assert.Equal(2, writes.Length);
        Assert.Equal(ResourceState.ColourTarget, writes[1].Before);
    }

    /// <summary>
    ///     An imported resource is handed back in the state its owner expects. A swapchain image left
    ///     in <c>ColourTarget</c> is a validation error at present time, a frame away from here.
    /// </summary>
    [Fact]
    public void AnImportedResourceIsRestoredToItsExitState() {
        var description = Target("backbuffer");
        var texture = device.CreateTexture(description);
        var view = device.CreateTextureView(texture);
        var backbuffer = graph.ImportTexture(texture, view, description, exitState: ResourceState.Present);

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(backbuffer);
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var last = list.Barriers[^1];
        Assert.Equal(texture, last.Texture);
        Assert.Equal(ResourceState.ColourTarget, last.Before);
        Assert.Equal(ResourceState.Present, last.After);
    }

    /// <summary>Everything one pass needs arrives in one group, not one call each.</summary>
    [Fact]
    public void APassesTransitionsArriveInOneGroup() {
        var a = graph.CreateTexture(Target("a"));
        var b = graph.CreateTexture(Target("b"));
        var c = graph.CreateTexture(Target("c"));

        graph.AddPass("produce", pass => {
            pass.Writes(a, ResourceState.ColourTarget);
            pass.Writes(b, ResourceState.ColourTarget);
            pass.Execute(_ => { });
        });

        graph.AddPass("consume", pass => {
            pass.Reads(a);
            pass.Reads(b);
            pass.ColourAttachment(c);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        Assert.Equal(2, list.BarrierGroups);
        Assert.Equal(2, list.Barriers.Count(barrier => barrier.Group == 0));
        Assert.Equal(3, list.Barriers.Count(barrier => barrier.Group == 1));
    }

    // ── Attachments ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A target nothing reads afterwards never has to reach memory. On tiled hardware that is the
    ///     difference between a bandwidth-bound frame and one that is not, and it is the decision
    ///     nobody remembers to make by hand.
    /// </summary>
    [Fact]
    public void AnAttachmentNothingReadsIsNotStored() {
        var scene = graph.CreateTexture(Target("scene"));
        var depth = graph.CreateTexture(Depth("depth"));
        var final = graph.CreateTexture(Target("final"));

        graph.AddPass("scene", pass => {
            pass.ColourAttachment(scene);
            pass.DepthAttachment(depth);
            pass.Execute(_ => { });
        });

        graph.AddPass("tonemap", pass => {
            pass.Reads(scene);
            pass.ColourAttachment(final);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var scenePass = list.Passes[0];

        // Colour is read by the tonemap pass, so it has to survive.
        Assert.Equal(StoreAction.Store, scenePass.Colour[0].Store);

        // Depth is not read by anything. It never leaves the tile.
        Assert.Equal(StoreAction.DontCare, scenePass.Depth!.Value.DepthStore);

        // The final target is not read either — but it is the last pass and has a side effect, so
        // "nothing reads it" is still the honest answer. A caller who wants it kept says so.
        Assert.Equal(StoreAction.DontCare, list.Passes[1].Colour[0].Store);
    }

    /// <summary>An imported attachment is always stored: discarding a swapchain image is a black screen.</summary>
    [Fact]
    public void AnImportedAttachmentIsAlwaysStored() {
        var description = Target("backbuffer");
        var texture = device.CreateTexture(description);
        var view = device.CreateTextureView(texture);
        var backbuffer = graph.ImportTexture(texture, view, description, exitState: ResourceState.Present);

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(backbuffer);
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        Assert.Equal(StoreAction.Store, list.Passes[0].Colour[0].Store);
    }

    /// <summary>A caller who states a store action gets it, derivation or not.</summary>
    [Fact]
    public void AnExplicitStoreActionIsHonoured() {
        var scene = graph.CreateTexture(Target("scene"));

        graph.AddPass("scene", pass => {
            pass.ColourAttachment(scene, store: StoreAction.Store);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        Assert.Equal(StoreAction.Store, list.Passes[0].Colour[0].Store);
    }

    /// <summary>Zero is far under reversed-Z, and it is what a depth attachment clears to.</summary>
    [Fact]
    public void DepthClearsToFarUnderReversedZ() {
        var depth = graph.CreateTexture(Depth("depth"));

        graph.AddPass("depth only", pass => {
            pass.DepthAttachment(depth);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        Assert.Equal(0f, list.Passes[0].Depth!.Value.ClearDepth);
    }

    /// <summary>A pass with attachments knows how big they are, so it need not be told twice.</summary>
    [Fact]
    public void ThePassIsToldItsRenderArea() {
        var target = graph.CreateTexture(Target("target", 640));
        var area = Int2.Zero;

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(target);
            pass.SideEffect();
            pass.Execute(context => area = context.RenderArea);
        });

        graph.Execute(new TrackingCommandList());

        Assert.Equal(new Int2(640, 640), area);
    }

    // ── Aliasing ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Two targets that never coexist cost one allocation between them — the saving doc 05
    ///     describes, and the reason a deferred pipeline's peak memory is not the sum of its parts.
    /// </summary>
    [Fact]
    public void ResourcesWhoseLifetimesDoNotOverlapShareMemory() {
        var first = graph.CreateTexture(Target("gbuffer"));
        var second = graph.CreateTexture(Target("postfx"));
        var third = graph.CreateTexture(Target("output"));

        graph.AddPass("a", pass => {
            pass.ColourAttachment(first);
            pass.Execute(_ => { });
        });

        graph.AddPass("b", pass => {
            pass.Reads(first);
            pass.ColourAttachment(second);
            pass.Execute(_ => { });
        });

        graph.AddPass("c", pass => {
            pass.Reads(second);
            pass.ColourAttachment(third);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Execute(new TrackingCommandList());

        // 'first' dies at pass b, so 'third' — which is not needed until pass c — takes its memory.
        Assert.Equal(graph.TextureOf(first), graph.TextureOf(third));
        Assert.NotEqual(graph.TextureOf(first), graph.TextureOf(second));
        Assert.Equal(2, pool.Count);
    }

    /// <summary>
    ///     A resource taking over aliased memory is transitioned <em>from</em> <c>Undefined</c>, not
    ///     from whatever the previous occupant left behind.
    /// </summary>
    /// <remarks>
    ///     Legal from any state, and it means "the contents may be discarded" — which is exactly
    ///     right for memory being taken over. Stating the true previous state would ask the driver to
    ///     preserve garbage, and on hardware with compressed render targets that is a decompress for
    ///     nothing. It falls out of tracking state per virtual resource rather than per physical one,
    ///     so it is asserted here rather than left to be true by accident.
    /// </remarks>
    [Fact]
    public void AResourceTakingOverAliasedMemoryDiscardsWhatWasThere() {
        var first = graph.CreateTexture(Target("first"));
        var second = graph.CreateTexture(Target("second"));
        var third = graph.CreateTexture(Target("third"));

        graph.AddPass("a", pass => {
            pass.ColourAttachment(first);
            pass.Execute(_ => { });
        });

        graph.AddPass("b", pass => {
            pass.Reads(first);
            pass.ColourAttachment(second);
            pass.Execute(_ => { });
        });

        graph.AddPass("c", pass => {
            pass.Reads(second);
            pass.ColourAttachment(third);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var shared = graph.TextureOf(third);
        Assert.Equal(graph.TextureOf(first), shared);

        // Its last transition is the one that hands it to 'third'. The physical texture was in
        // ShaderRead at that point; the barrier says Undefined, because the contents are garbage.
        var handover = list.Barriers.Last(barrier => barrier.Texture == shared);

        Assert.Equal(ResourceState.Undefined, handover.Before);
        Assert.Equal(ResourceState.ColourTarget, handover.After);
    }

    /// <summary>Two resources alive at the same time never share, whatever their descriptions.</summary>
    [Fact]
    public void ResourcesThatCoexistNeverShareMemory() {
        var a = graph.CreateTexture(Target("a"));
        var b = graph.CreateTexture(Target("b"));
        var c = graph.CreateTexture(Target("c"));

        graph.AddPass("produce", pass => {
            pass.Writes(a, ResourceState.ColourTarget);
            pass.Writes(b, ResourceState.ColourTarget);
            pass.Execute(_ => { });
        });

        graph.AddPass("consume", pass => {
            pass.Reads(a);
            pass.Reads(b);
            pass.ColourAttachment(c);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Execute(new TrackingCommandList());

        Assert.NotEqual(graph.TextureOf(a), graph.TextureOf(b));
        Assert.NotEqual(graph.TextureOf(a), graph.TextureOf(c));
        Assert.NotEqual(graph.TextureOf(b), graph.TextureOf(c));
    }

    /// <summary>
    ///     The second frame allocates nothing. A graph that recreated its targets every frame would be
    ///     a driver allocation per target per frame, and nothing about the picture would say so.
    /// </summary>
    [Fact]
    public void TheSecondFrameReusesEveryResource() {
        for (var frame = 0; frame < 3; frame++) {
            var target = graph.CreateTexture(Target("target"));

            graph.AddPass("draw", pass => {
                pass.ColourAttachment(target);
                pass.SideEffect();
                pass.Execute(_ => { });
            });

            graph.Execute(new TrackingCommandList());
            graph.Reset();
        }

        Assert.Equal(1, pool.Count);
        Assert.Equal(2, pool.Reuses);
    }

    /// <summary>A resource no surviving pass touches is never created.</summary>
    [Fact]
    public void ACulledPassesResourcesAreNeverAllocated() {
        var wasted = graph.CreateTexture(Target("wasted"));

        graph.AddPass("wasteful", pass => {
            pass.Writes(wasted, ResourceState.ColourTarget);
            pass.Execute(_ => { });
        });

        graph.Execute(new TrackingCommandList());

        Assert.Equal(0, pool.Count);
    }

    // ── Validation ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Reading a transient nothing wrote reads undefined memory, which on most drivers is last
    ///     frame's contents and therefore looks almost right.
    /// </summary>
    [Fact]
    public void ReadingSomethingNobodyWroteIsRefused() {
        var never = graph.CreateTexture(Target("never written"));

        graph.AddPass("reader", pass => {
            pass.Reads(never);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var thrown = Assert.Throws<RenderGraphException>(graph.Compile);

        Assert.Contains("reader", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("never written", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Passes run in declaration order, so a producer declared later does not count.</summary>
    [Fact]
    public void AProducerDeclaredAfterItsConsumerDoesNotCount() {
        var texture = graph.CreateTexture(Target("late"));

        graph.AddPass("consumer", pass => {
            pass.Reads(texture);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.AddPass("producer", pass => {
            pass.Writes(texture, ResourceState.ColourTarget);
            pass.Execute(_ => { });
        });

        Assert.Throws<RenderGraphException>(graph.Compile);
    }

    [Fact]
    public void APassThatDeclaresNothingIsRefused() {
        var thrown = Assert.Throws<RenderGraphException>(
            () => graph.AddPass("empty", pass => pass.Execute(_ => { }))
        );

        Assert.Contains("SideEffect", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APassWithNoWorkIsRefused() {
        var target = graph.CreateTexture(Target("target"));

        Assert.Throws<RenderGraphException>(
            () => graph.AddPass("no body", pass => pass.Writes(target, ResourceState.ColourTarget))
        );
    }

    /// <summary>
    ///     A handle from a previous frame would address whatever resource took its slot. The
    ///     generation counter is what makes that a readable error rather than a wrong picture.
    /// </summary>
    [Fact]
    public void AHandleFromBeforeAResetIsRefused() {
        var stale = graph.CreateTexture(Target("stale"));
        graph.Reset();

        var thrown = Assert.Throws<RenderGraphException>(
            () => graph.AddPass("late", pass => {
                pass.Reads(stale);
                pass.Execute(_ => { });
            })
        );

        Assert.Contains("Reset", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaringIntoACompiledGraphIsRefused() {
        var target = graph.CreateTexture(Target("target"));

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(target);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        Assert.Throws<RenderGraphException>(() => graph.CreateTexture(Target("too late")));
    }

    /// <summary>A texture handle used where a buffer is expected is a mistake worth naming.</summary>
    [Fact]
    public void AHandleOfTheWrongKindIsRefused() {
        var texture = graph.CreateTexture(Target("texture"));

        Assert.Throws<RenderGraphException>(
            () => graph.AddPass("confused", pass => {
                pass.Reads(new GraphBuffer(texture.Index, texture.Generation));
                pass.Execute(_ => { });
            })
        );
    }

    // ── Diagnostics ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     "Why did my pass not run" is what this is most often opened to answer, so a culled pass is
    ///     drawn dashed rather than left out.
    /// </summary>
    [Fact]
    public void TheGraphvizDumpShowsCulledPassesAsWellAsLiveOnes() {
        var kept = graph.CreateTexture(Target("kept"));
        var dropped = graph.CreateTexture(Target("dropped"));

        graph.AddPass("live", pass => {
            pass.ColourAttachment(kept);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.AddPass("culled", pass => {
            pass.Writes(dropped, ResourceState.ColourTarget);
            pass.Execute(_ => { });
        });

        var dot = graph.ToGraphviz();

        Assert.StartsWith("digraph RenderGraph {", dot, StringComparison.Ordinal);
        Assert.Contains("label=\"live\"", dot, StringComparison.Ordinal);
        Assert.Contains("label=\"culled\"", dot, StringComparison.Ordinal);
        Assert.Contains("style=dashed", dot, StringComparison.Ordinal);
        Assert.Contains("label=\"kept\"", dot, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The number worth watching: a graph whose barriers grow faster than its passes is one whose
    ///     passes are ping-ponging a resource between states, which is invisible from the picture.
    /// </summary>
    [Fact]
    public void TheBarrierCountIsReported() {
        var scene = graph.CreateTexture(Target("scene"));
        var final = graph.CreateTexture(Target("final"));

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(scene);
            pass.Execute(_ => { });
        });

        graph.AddPass("blit", pass => {
            pass.Reads(scene);
            pass.ColourAttachment(final);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        Assert.Equal(list.Barriers.Count, graph.BarrierCount);
    }

    // ── End to end ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The graph drives a real backend, and the command stream it produces is the one a
    ///     hand-written frame would have produced.
    /// </summary>
    [Fact]
    public void TheGraphDrivesTheNullBackend() {
        var description = Target("backbuffer");
        var texture = device.CreateTexture(description);
        var view = device.CreateTextureView(texture);
        var backbuffer = graph.ImportTexture(texture, view, description, exitState: ResourceState.Present);
        var shadow = graph.CreateTexture(Depth("shadow"));

        graph.AddPass("shadow", pass => {
            pass.DepthAttachment(shadow);
            pass.Execute(context => context.CommandList.Draw(3));
        });

        graph.AddPass("main", pass => {
            pass.Reads(shadow, ResourceState.DepthStencilRead);
            pass.ColourAttachment(backbuffer, LoadAction.Clear, new(0.1f, 0.2f, 0.3f, 1f));
            pass.Execute(context => context.CommandList.Draw(6));
        });

        device.BeginFrame();
        using var list = device.BeginCommandList();
        graph.Execute(list);
        list.Finish();

        // The Null backend flushes into its recorder at submit rather than while recording, which is
        // the point of it being a shipping backend: a server that never submits accumulates nothing.
        device.GraphicsQueue.Submit([list]);

        var commands = device.Recorder!.Commands;

        Assert.Equal(2, commands.Count(command => command.Kind == RecordedCommandKind.BeginRenderPass));
        Assert.Equal(2, commands.Count(command => command.Kind == RecordedCommandKind.EndRenderPass));
        Assert.Equal(2, commands.Count(command => command.Kind == RecordedCommandKind.Draw));

        // Every barrier is outside a pass. The Null backend refuses one inside, which is what a real
        // backend's validation layers would say too — so this passing at all is the assertion.
        Assert.True(commands.Count(command => command.Kind == RecordedCommandKind.Barrier) >= 3);

        device.EndFrame();
    }
}
