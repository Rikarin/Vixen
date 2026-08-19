// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.RenderGraph.Tests;

/// <summary>
///     What the graph does with a multisampled attachment and the target it resolves into.
/// </summary>
/// <remarks>
///     <para>
///         <b>The half of MSAA that did not exist.</b> <see cref="ColourAttachment.ResolveView" /> and
///         <see cref="StoreAction.Resolve" /> have been honoured by the Vulkan and WebGPU backends for
///         a while, a texture's sample count reaches <c>vkCreateImage</c> and a pipeline's reaches
///         <c>RasterizationSamples</c> — and nothing between the two ever named a pair, so no frame
///         could resolve anything.
///     </para>
///     <para>
///         The two properties worth pinning are both about what the <em>rest</em> of the graph makes of
///         the pair, not about the attachment struct: a resolve target has to be a producer the way any
///         other write is, or every reader of it fails validation and the pass that filled it is culled;
///         and the store has to be a resolve even though nothing reads the multisampled texture, which
///         is the one thing <see cref="RenderGraph" />'s own store derivation would get wrong.
///     </para>
/// </remarks>
public sealed class ResolveAttachmentTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly TransientResourcePool pool;
    readonly RenderGraph graph;

    public ResolveAttachmentTests() {
        pool = new(device);
        graph = new(device, pool);
    }

    public void Dispose() {
        pool.Dispose();
        device.Dispose();
    }

    static TextureDescription Multisampled(string name, int samples = 4, int size = 64) =>
        new(
            PixelFormat.Rgba8UNorm,
            size,
            size,
            TextureUsage.ColourTarget,
            SampleCount: samples,
            Name: name
        );

    static TextureDescription Single(string name, int size = 64) =>
        new(
            PixelFormat.Rgba8UNorm,
            size,
            size,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: name
        );

    /// <summary>
    ///     The pass stores a resolve, and hands the backend the view to resolve into.
    /// </summary>
    [Fact]
    public void AResolvedAttachmentStoresAResolveAndCarriesTheView() {
        var samples = graph.CreateTexture(Multisampled("samples"));
        var resolved = graph.CreateTexture(Single("resolved"));

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(samples, resolve: resolved);
            pass.Execute(context => context.CommandList.Draw(3));
        });

        // Somebody has to read the resolve or the whole thing is culled — which is itself the point
        // of the test below.
        graph.AddPass("present", pass => {
            pass.Reads(resolved);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var drawn = Assert.Single(list.Passes, pass => pass.Name == "draw");
        var attachment = Assert.Single(drawn.Colour);

        Assert.Equal(StoreAction.Resolve, attachment.Store);
        Assert.True(attachment.ResolveView.IsValid, "The resolve target's view never reached the backend.");
        Assert.NotEqual(attachment.View, attachment.ResolveView);
    }

    /// <summary>
    ///     <b>The derivation would have thrown the samples away, and this is why the resolve overrides
    ///     it.</b>
    /// </summary>
    /// <remarks>
    ///     <c>DeriveStore</c> asks whether anything reads the attachment later, and for a multisampled
    ///     target the answer is almost always no — what the next pass reads is the resolve beside it.
    ///     Left to the derivation the store is <see cref="StoreAction.DontCare" />, which resolves
    ///     nothing: a correctly multisampled pass whose result never leaves the tile, and no error
    ///     anywhere. Here nothing reads <c>samples</c> at all, so the derivation is at its most wrong.
    /// </remarks>
    [Fact]
    public void TheResolveWinsOverTheStoreTheGraphWouldHaveDerived() {
        var samples = graph.CreateTexture(Multisampled("samples"));
        var resolved = graph.CreateTexture(Single("resolved"));

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(samples, resolve: resolved);
            pass.Execute(_ => { });
        });

        graph.AddPass("read", pass => {
            pass.Reads(resolved);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var drawn = Assert.Single(list.Passes, pass => pass.Name == "draw");
        Assert.Equal(StoreAction.Resolve, Assert.Single(drawn.Colour).Store);
    }

    /// <summary>
    ///     A resolve is a write, so what reads it has a producer and the pass that filled it survives.
    /// </summary>
    /// <remarks>
    ///     Both halves at once, and they are the same declaration. Without the write, <c>read</c> is a
    ///     read of something nothing produces — which the graph refuses outright — and <c>draw</c>
    ///     writes only a multisampled texture nobody wants, which culling removes.
    /// </remarks>
    [Fact]
    public void TheResolveTargetIsAProducerAndKeepsItsPassAlive() {
        var samples = graph.CreateTexture(Multisampled("samples"));
        var resolved = graph.CreateTexture(Single("resolved"));
        var ran = false;

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(samples, resolve: resolved);
            pass.Execute(_ => ran = true);
        });

        graph.AddPass("read", pass => {
            pass.Reads(resolved);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Execute(new TrackingCommandList());

        Assert.True(ran, "The pass that filled the resolve was culled for writing nothing anybody wanted.");
        Assert.Equal(2, graph.SurvivingPassCount);
    }

    /// <summary>
    ///     The resolve target is barriered into the state a render pass writes it in, not left in
    ///     whatever it was.
    /// </summary>
    [Fact]
    public void TheResolveTargetIsTransitionedForTheWrite() {
        var samples = graph.CreateTexture(Multisampled("samples"));
        var resolved = graph.CreateTexture(Single("resolved"));

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(samples, resolve: resolved);
            pass.Execute(_ => { });
        });

        graph.AddPass("read", pass => {
            pass.Reads(resolved);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        // Two textures move into the colour-target state before the pass, not one: the samples and
        // the target they resolve into. A resolve destination is written by the render pass rather
        // than by a transfer, which is why the state is ColourTarget and not CopyDestination.
        var written = list.Barriers
            .Where(barrier => barrier.After == ResourceState.ColourTarget)
            .Select(barrier => barrier.Texture)
            .Distinct()
            .Count();

        Assert.Equal(2, written);

        // And it moves on to shader-read for the pass that samples it, which is only possible
        // because the resolve was declared as a use in the first place.
        Assert.Contains(list.Barriers, barrier => barrier.After == ResourceState.ShaderRead);
    }

    // ── What it refuses ─────────────────────────────────────────────────────────────────────

    /// <summary>A single-sampled attachment has nothing to resolve.</summary>
    [Fact]
    public void ASingleSampledAttachmentCannotBeResolved() {
        var source = graph.CreateTexture(Single("source"));
        var resolved = graph.CreateTexture(Single("resolved"));

        var thrown = Assert.Throws<RenderGraphException>(
            () => graph.AddPass("draw", pass => pass.ColourAttachment(source, resolve: resolved))
        );

        Assert.Contains("one sample", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A resolve target is where the samples stop.</summary>
    [Fact]
    public void TheResolveTargetCannotItselfBeMultisampled() {
        var samples = graph.CreateTexture(Multisampled("samples"));
        var alsoSamples = graph.CreateTexture(Multisampled("alsoSamples", samples: 2));

        var thrown = Assert.Throws<RenderGraphException>(
            () => graph.AddPass("draw", pass => pass.ColourAttachment(samples, resolve: alsoSamples))
        );

        Assert.Contains("samples stop", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A resolve averages samples; it does not convert.</summary>
    [Fact]
    public void AResolveCannotChangeFormat() {
        var samples = graph.CreateTexture(Multisampled("samples"));

        var resolved = graph.CreateTexture(
            new TextureDescription(
                PixelFormat.Rgba16Float,
                64,
                64,
                TextureUsage.ColourTarget | TextureUsage.Sampled,
                Name: "resolved"
            )
        );

        var thrown = Assert.Throws<RenderGraphException>(
            () => graph.AddPass("draw", pass => pass.ColourAttachment(samples, resolve: resolved))
        );

        Assert.Contains("does not convert", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And the one that is worth the check: a resolve between differently sized attachments is
    ///     undefined rather than a scale, so it reads as a subtly cropped picture rather than an error.
    /// </summary>
    [Fact]
    public void AResolveCannotChangeSize() {
        var samples = graph.CreateTexture(Multisampled("samples"));
        var resolved = graph.CreateTexture(Single("resolved", size: 32));

        var thrown = Assert.Throws<RenderGraphException>(
            () => graph.AddPass("draw", pass => pass.ColourAttachment(samples, resolve: resolved))
        );

        Assert.Contains("not a blit", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An attachment with no resolve named is untouched — the store is still the graph's to derive.
    /// </summary>
    /// <remarks>
    ///     The regression guard for the override above. A change that always stored a resolve would
    ///     hand every single-sampled pass in the engine a resolve action with no view behind it.
    /// </remarks>
    [Fact]
    public void AnAttachmentWithNoResolveKeepsTheDerivedStore() {
        var target = graph.CreateTexture(Single("target"));

        graph.AddPass("draw", pass => {
            pass.ColourAttachment(target);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var list = new TrackingCommandList();
        graph.Execute(list);

        var attachment = Assert.Single(Assert.Single(list.Passes).Colour);

        Assert.NotEqual(StoreAction.Resolve, attachment.Store);
        Assert.False(attachment.ResolveView.IsValid);
    }
}
