// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.RenderGraph.Tests;

/// <summary>
///     What the graph does with a multisampled <em>depth</em> attachment and the target it resolves
///     into.
/// </summary>
/// <remarks>
///     <para>
///         <b>The other half of MSAA, and the half that is not symmetric with colour.</b> A colour
///         resolve is fully described by <see cref="StoreAction.Resolve" /> plus a view, because there
///         is only one sensible way to combine colour samples and every backend does it: average them.
///         Depth has no such default. The average of four depths is a surface behind nothing and in
///         front of nothing, so the combining rule has to be named — which is why
///         <see cref="DepthResolveMode" /> exists and why the depth attachment carries one.
///     </para>
///     <para>
///         ⚠ <b>The assertion that earns this file is the one about the default being
///         <see cref="DepthResolveMode.Max" />.</b> The engine is reversed-Z: the near plane is depth
///         1 and the far plane is 0, so the sample <em>nearest the camera</em> is the largest value,
///         not the smallest. Both choices resolve without an error and render a picture that looks
///         right; only the passes that read the resolved depth — reprojection, screen-space tracing,
///         fog — go quietly wrong. A test is the only thing that notices.
///     </para>
/// </remarks>
public sealed class DepthResolveTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly TransientResourcePool pool;
    readonly RenderGraph graph;

    public DepthResolveTests() {
        pool = new(device);
        graph = new(device, pool);
    }

    public void Dispose() {
        pool.Dispose();
        device.Dispose();
    }

    static TextureDescription Multisampled(string name, int samples = 4, int size = 64) =>
        new(
            PixelFormat.Depth32Float,
            size,
            size,
            TextureUsage.DepthStencilTarget,
            SampleCount: samples,
            Name: name
        );

    static TextureDescription Single(string name, int size = 64) =>
        new(
            PixelFormat.Depth32Float,
            size,
            size,
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            Name: name
        );

    /// <summary>Declares a pass that resolves depth, and a reader so nothing is culled.</summary>
    /// <param name="mode">
    ///     The mode to name, or <see langword="null" /> to leave it unnamed and take whatever
    ///     <see cref="RenderGraphPassBuilder.DepthAttachment" /> defaults to.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>The <see langword="null" /> is the whole point of this signature.</b> Giving this
    ///     helper a default of its own would shadow the one under test: every call would name a mode
    ///     explicitly, and a test claiming to pin the default would in fact pin nothing — it would go
    ///     on passing with the real default flipped. That is not hypothetical; it is what the first
    ///     draft of this file did, and the sabotage run is what caught it.
    /// </remarks>
    DepthStencilAttachment Run(GraphTexture samples, GraphTexture resolved, DepthResolveMode? mode = null) {
        graph.AddPass("draw", pass => {
            if (mode is { } named) {
                pass.DepthAttachment(samples, resolve: resolved, resolveMode: named);
            } else {
                pass.DepthAttachment(samples, resolve: resolved);
            }

            pass.Execute(context => context.CommandList.Draw(3));
        });

        graph.AddPass("read", pass => {
            pass.Reads(resolved);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var drawn = Assert.Single(list.Passes, pass => pass.Name == "draw");

        return Assert.NotNull(drawn.Depth);
    }

    /// <summary>
    ///     ⚠ <b>The reversed-Z assertion.</b> Left alone, a depth resolve keeps the sample nearest the
    ///     camera, and under this engine's convention that is the <em>largest</em> depth value.
    /// </summary>
    /// <remarks>
    ///     Written as an assertion about <see cref="DepthResolveMode.Max" /> specifically rather than
    ///     about "the default", so that flipping the default to <see cref="DepthResolveMode.Min" />
    ///     fails here with the reason in the name instead of passing a test that only says the default
    ///     is whatever it is.
    /// </remarks>
    [Fact]
    public void TheDefaultResolveKeepsTheNearestSampleWhichUnderReversedZIsTheMaximum() {
        var samples = graph.CreateTexture(Multisampled("depth"));
        var resolved = graph.CreateTexture(Single("resolved"));

        var depth = Run(samples, resolved);

        Assert.Equal(DepthResolveMode.Max, depth.ResolveMode);
    }

    /// <summary>The pass stores a resolve, and hands the backend the view to resolve into.</summary>
    [Fact]
    public void AResolvedDepthAttachmentStoresAResolveAndCarriesTheView() {
        var samples = graph.CreateTexture(Multisampled("depth"));
        var resolved = graph.CreateTexture(Single("resolved"));

        var depth = Run(samples, resolved);

        Assert.Equal(StoreAction.Resolve, depth.DepthStore);
        Assert.True(depth.ResolveView.IsValid, "The resolve target's view never reached the backend.");
        Assert.NotEqual(depth.View, depth.ResolveView);
    }

    /// <summary>The mode the caller named is the mode the backend is given.</summary>
    [Theory]
    [InlineData(DepthResolveMode.Min)]
    [InlineData(DepthResolveMode.Max)]
    [InlineData(DepthResolveMode.SampleZero)]
    public void TheModeReachesTheBackend(DepthResolveMode mode) {
        var samples = graph.CreateTexture(Multisampled("depth"));
        var resolved = graph.CreateTexture(Single("resolved"));

        Assert.Equal(mode, Run(samples, resolved, mode).ResolveMode);
    }

    /// <summary>
    ///     <b>The derivation would have thrown the samples away, and this is why the resolve overrides
    ///     it.</b>
    /// </summary>
    /// <remarks>
    ///     Exactly the failure the colour path has: nothing reads the multisampled depth buffer, so
    ///     the store derivation answers <see cref="StoreAction.DontCare" /> and the resolve never
    ///     happens. The target then holds whatever was in the memory, which on most drivers is the
    ///     previous frame — a picture that is almost right and no error anywhere.
    /// </remarks>
    [Fact]
    public void TheResolveWinsOverTheStoreTheGraphWouldHaveDerived() {
        var samples = graph.CreateTexture(Multisampled("depth"));
        var resolved = graph.CreateTexture(Single("resolved"));

        // Nothing reads `samples` at all, so the derivation is at its most wrong.
        Assert.Equal(StoreAction.Resolve, Run(samples, resolved).DepthStore);
    }

    /// <summary>
    ///     The resolve target is a write, so a later pass may read it and the pass that filled it
    ///     survives culling.
    /// </summary>
    /// <remarks>
    ///     Without the declared write the resolve has no producer: every reader of it fails
    ///     validation, and the pass that filled it is culled for writing something nobody wanted.
    /// </remarks>
    [Fact]
    public void TheResolveTargetIsAProducerSoAReaderSurvives() {
        var samples = graph.CreateTexture(Multisampled("depth"));
        var resolved = graph.CreateTexture(Single("resolved"));

        graph.AddPass("draw", pass => {
            pass.DepthAttachment(samples, resolve: resolved);
            pass.Execute(context => context.CommandList.Draw(3));
        });

        graph.AddPass("read", pass => {
            pass.Reads(resolved);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        Assert.Contains(list.Passes, pass => pass.Name == "draw");
    }

    /// <summary>
    ///     ⚠ Stencil is not resolved alongside depth, and must not claim to be.
    /// </summary>
    /// <remarks>
    ///     Vulkan requires the depth and stencil resolve modes to agree when both resolve, and there
    ///     is no meaningful "nearest" for a stencil value. Saying <see cref="StoreAction.Resolve" />
    ///     for stencil would be a promise the backend cannot keep.
    /// </remarks>
    [Fact]
    public void TheStencilStoreIsNotAResolve() {
        var samples = graph.CreateTexture(Multisampled("depth"));
        var resolved = graph.CreateTexture(Single("resolved"));

        Assert.NotEqual(StoreAction.Resolve, Run(samples, resolved).StencilStore);
    }

    /// <summary>
    ///     A read-only depth attachment has nothing to resolve, and saying both is refused rather
    ///     than silently doing neither.
    /// </summary>
    [Fact]
    public void AReadOnlyDepthAttachmentCannotResolve() {
        var samples = graph.CreateTexture(Multisampled("depth"));
        var resolved = graph.CreateTexture(Single("resolved"));

        var error = Assert.Throws<RenderGraphException>(
            () => graph.AddPass("draw", pass => {
                pass.DepthAttachment(samples, readOnly: true, resolve: resolved);
                pass.Execute(_ => { });
            })
        );

        Assert.Contains("read-only", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A single-sampled depth attachment has nothing to resolve.</summary>
    [Fact]
    public void ASingleSampledDepthAttachmentCannotResolve() {
        var samples = graph.CreateTexture(Single("depth"));
        var resolved = graph.CreateTexture(Single("resolved"));

        Assert.Throws<RenderGraphException>(
            () => graph.AddPass("draw", pass => {
                pass.DepthAttachment(samples, resolve: resolved);
                pass.Execute(_ => { });
            })
        );
    }

    /// <summary>A resolve target is where the samples stop, so it may not be multisampled itself.</summary>
    [Fact]
    public void AMultisampledResolveTargetIsRefused() {
        var samples = graph.CreateTexture(Multisampled("depth"));
        var resolved = graph.CreateTexture(Multisampled("resolved", 2));

        Assert.Throws<RenderGraphException>(
            () => graph.AddPass("draw", pass => {
                pass.DepthAttachment(samples, resolve: resolved);
                pass.Execute(_ => { });
            })
        );
    }

    /// <summary>A resolve is per texel, not a blit.</summary>
    [Fact]
    public void AResolveTargetOfADifferentSizeIsRefused() {
        var samples = graph.CreateTexture(Multisampled("depth"));
        var resolved = graph.CreateTexture(Single("resolved", 32));

        Assert.Throws<RenderGraphException>(
            () => graph.AddPass("draw", pass => {
                pass.DepthAttachment(samples, resolve: resolved);
                pass.Execute(_ => { });
            })
        );
    }

    /// <summary>An unresolved depth attachment is left exactly as it was.</summary>
    /// <remarks>
    ///     The control. Every assertion above is about a difference from this, and without it they
    ///     would all pass against a graph that marked every depth attachment as resolving.
    /// </remarks>
    [Fact]
    public void ADepthAttachmentWithNoResolveTargetCarriesNoResolveView() {
        var depthTexture = graph.CreateTexture(Single("depth"));

        graph.AddPass("draw", pass => {
            pass.DepthAttachment(depthTexture);
            pass.SideEffect();
            pass.Execute(context => context.CommandList.Draw(3));
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var depth = Assert.NotNull(Assert.Single(list.Passes).Depth);

        Assert.False(depth.ResolveView.IsValid);
        Assert.NotEqual(StoreAction.Resolve, depth.DepthStore);
    }
}
