// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.RenderGraph.Tests;

/// <summary>
///     A two-target rotation, and the dependency the graph has to be told about —
///     [docs/plan/35 § B5].
/// </summary>
/// <remarks>
///     Every failure here is silent in a picture. A pair that does not rotate is a simulation that
///     reads what it just wrote; a pair the graph was not told about is a read of a write with no
///     barrier between them, which produces a field that is current in some tiles and one step stale
///     in others — noise, rather than anything that looks like a race.
/// </remarks>
public sealed class PingPongTextureTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly TransientResourcePool pool;
    readonly RenderGraph graph;

    public PingPongTextureTests() {
        pool = new(device);
        graph = new(device, pool);
    }

    public void Dispose() {
        pool.Dispose();
        device.Dispose();
    }

    static TextureDescription Field(string name = "ripples") =>
        new(
            PixelFormat.Rgba16Float,
            64,
            64,
            TextureUsage.Storage | TextureUsage.Sampled | TextureUsage.ColourTarget,
            Name: name
        );

    /// <summary>The two are distinct textures, and the pair names them by role.</summary>
    [Fact]
    public void ThePairIsTwoDistinctTextures() {
        using var pingPong = new PingPongTextures(device, Field());

        Assert.NotEqual(pingPong.ReadTexture, pingPong.WriteTexture);

        var pair = pingPong.Import(graph);

        Assert.NotEqual(pair.Read, pair.Write);
        Assert.Equal(2, graph.ResourceCount);
    }

    /// <summary>
    ///     ⚠ Advancing makes this step's output the next step's input, and nothing else moves.
    /// </summary>
    /// <remarks>
    ///     The property the whole type is for. A rotation that did not happen is a step reading what
    ///     it just wrote, which for a damped wave equation converges to a flat field — so the symptom
    ///     is water that never ripples rather than water that ripples wrongly.
    /// </remarks>
    [Fact]
    public void AdvancingMakesThisStepsWriteTheNextStepsRead() {
        using var pingPong = new PingPongTextures(device, Field());

        var first = pingPong.WriteTexture;
        var wasRead = pingPong.ReadTexture;

        pingPong.Advance();

        Assert.Equal(first, pingPong.ReadTexture);
        Assert.Equal(wasRead, pingPong.WriteTexture);
        Assert.Equal(1, pingPong.StepCount);

        pingPong.Advance();

        Assert.Equal(wasRead, pingPong.ReadTexture);
        Assert.Equal(first, pingPong.WriteTexture);
        Assert.Equal(2, pingPong.StepCount);
    }

    /// <summary>
    ///     ⚠ The graph puts a barrier between the step's write and the next step's read of it.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § W0]'s exit criterion, and the reason a ping-pong is a type rather than two
    ///     fields: the dependency crosses a frame boundary, so it is not visible in either frame's
    ///     pass list. What makes it visible to the graph is that both halves are imported with the
    ///     state they are actually in — so frame two's read declares a transition <em>from</em> what
    ///     frame one left it as.
    /// </remarks>
    [Fact]
    public void TheGraphIsToldAboutTheDependencyAcrossTheSwap() {
        using var pingPong = new PingPongTextures(device, Field());

        // ── Step one: write the half that will be read next time.
        var written = pingPong.WriteTexture;
        var first = pingPong.Import(graph);

        graph.AddPass("step one", pass => {
            pass.Kind = PassKind.Compute;
            pass.Writes(first.Write, ResourceState.ShaderWrite);
            pass.Execute(_ => { });
        });

        var recorded = new TrackingCommandList();
        graph.Execute(recorded);

        // Written as a storage image, and handed back resting so the next frame knows where it is.
        Assert.Contains(
            recorded.Barriers,
            barrier => barrier.Texture == written
                && barrier.Before == ResourceState.Undefined
                && barrier.After == ResourceState.ShaderWrite
        );

        Assert.Contains(
            recorded.Barriers,
            barrier => barrier.Texture == written
                && barrier.Before == ResourceState.ShaderWrite
                && barrier.After == PingPongTextures.RestingState
        );

        // ── Step two: read it.
        graph.Reset();
        pingPong.Advance();

        Assert.Equal(written, pingPong.ReadTexture);

        var second = pingPong.Import(graph);

        graph.AddPass("step two", pass => {
            pass.Kind = PassKind.Compute;
            pass.Reads(second.Read);
            pass.Writes(second.Write, ResourceState.ShaderWrite);
            pass.Execute(_ => { });
        });

        var again = new TrackingCommandList();
        graph.Execute(again);

        // ⚠ The claim, and it is a *negative* one. An import that had not tracked its own state would
        // enter as Undefined, and Undefined is not a neutral placeholder: it tells the driver the
        // previous contents may be discarded, which on hardware with compressed targets they will be.
        // That is the whole ping-pong thrown away, silently, on the frame it first mattered.
        Assert.DoesNotContain(
            again.Barriers,
            barrier => barrier.Texture == written && barrier.Before == ResourceState.Undefined
        );

        // And the read costs no barrier at all, because the graph was told it was already resting in
        // the state the read wants. The half being written this time is what shows the tracking is
        // doing something rather than the texture simply being absent from the frame.
        Assert.DoesNotContain(again.Barriers, barrier => barrier.Texture == written);

        Assert.Contains(
            again.Barriers,
            barrier => barrier.Texture == pingPong.WriteTexture
                && barrier.Before == PingPongTextures.RestingState
                && barrier.After == ResourceState.ShaderWrite
        );
    }

    /// <summary>A fresh pair has no history, and says so rather than being trusted.</summary>
    /// <remarks>
    ///     ⚠ The graph cannot catch a first-frame read: an import counts as produced, so reading one
    ///     nothing has written passes validation and samples whatever the allocation held. On most
    ///     drivers that is zeroes — which is exactly what a settled height field looks like.
    /// </remarks>
    [Fact]
    public void AFreshPairHasNoHistoryUntilSomethingWritesIt() {
        using var pingPong = new PingPongTextures(device, Field());

        Assert.False(pingPong.HasHistory);
        Assert.Equal(0, pingPong.StepCount);

        pingPong.Advance();

        Assert.True(pingPong.HasHistory);
    }

    /// <summary>Clearing primes both halves, and its passes survive culling.</summary>
    /// <remarks>
    ///     ⚠ Nothing in the frame that clears reads what the clear wrote — the next frame does — so
    ///     without a declared side effect the graph would remove both passes for having no consumer,
    ///     and the pair would report history it does not have.
    /// </remarks>
    [Fact]
    public void ClearingPrimesBothHalvesAndIsNotCulled() {
        using var pingPong = new PingPongTextures(device, Field());

        pingPong.Clear(graph);

        Assert.True(pingPong.HasHistory);
        Assert.Equal(2, graph.PassCount);

        var recorded = new TrackingCommandList();
        graph.Execute(recorded);

        Assert.Equal(2, graph.SurvivingPassCount);
        Assert.Equal(2, recorded.Passes.Count);

        foreach (var pass in recorded.Passes) {
            Assert.Equal(LoadAction.Clear, Assert.Single(pass.Colour).Load);
        }
    }

    /// <summary>
    ///     ⚠ A pair that cannot be a colour attachment is refused a clear rather than left dirty.
    /// </summary>
    /// <remarks>
    ///     There is no clear-texture operation on <see cref="ICommandList" />, so clearing is a render
    ///     pass. A pair declared for storage alone has no way to be one, and the two available answers
    ///     are a message naming the usage and a silent no-op — the second would leave
    ///     <see cref="PingPongTextures.HasHistory" /> claiming a field that was never written.
    /// </remarks>
    [Fact]
    public void ClearingAStorageOnlyPairSaysWhyItCannot() {
        using var pingPong = new PingPongTextures(
            device,
            new(PixelFormat.Rgba16Float, 64, 64, TextureUsage.Storage | TextureUsage.Sampled, Name: "storage only")
        );

        var thrown = Assert.Throws<RenderGraphException>(() => pingPong.Clear(graph));

        Assert.Contains("ColourTarget", thrown.Message, StringComparison.Ordinal);
        Assert.False(pingPong.HasHistory);
    }

    /// <summary>Both halves are imported every step, whether or not the step touches them.</summary>
    /// <remarks>
    ///     Importing only the half a step uses is what breaks the barrier across the swap — the other
    ///     half is then a resource the graph has never heard of, and the next frame's entry state for
    ///     it is a guess.
    /// </remarks>
    [Fact]
    public void BothHalvesAreImportedEvenWhenOnlyOneIsUsed() {
        using var pingPong = new PingPongTextures(device, Field());

        var pair = pingPong.Import(graph);

        graph.AddPass("write only", pass => {
            pass.Writes(pair.Write, ResourceState.ShaderWrite);
            pass.Execute(_ => { });
        });

        var recorded = new TrackingCommandList();
        graph.Execute(recorded);

        // The untouched half is still transitioned to its resting state, which is what makes the next
        // frame's entry state a fact rather than an assumption.
        Assert.Contains(
            recorded.Barriers,
            barrier => barrier.Texture == pingPong.ReadTexture
                && barrier.After == PingPongTextures.RestingState
        );
    }
}
