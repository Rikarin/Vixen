// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;
using Xunit;
using GraphTexture = Vixen.Graphics.RenderGraph.GraphTexture;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The hand-written GLSL this suite renders with and the Raven every application draws through,
///     given the same frame and required to produce the same picture.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this closes, which #286 has carried open since it was filed.</b>
///         <c>Shaders/ui-box.frag</c> here and <c>Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn</c> are two
///         implementations of one specification in two languages, and everything written to compare
///         them so far compares their <i>text</i>: <c>UiShapeLayoutTests</c> parses the record out of
///         one and holds its lanes to <c>UiShape</c>'s, and
///         <c>SharedUiShaderTests.EveryConstantInTheGlslCopyIsOneTheRavenHoldsToo</c> requires every
///         number in the copy to be a number the Raven holds. Both are necessary conditions and
///         neither is sufficient — an expression rearranged around the same constants passes both,
///         and the issue's own remark says so.
///     </para>
///     <para>
///         ⚠ <b>And the sufficient check was believed to cost every reference image in this suite,
///         which is the assumption this file refutes.</b> Four comments on #286 say the only real
///         answer is "a golden image rendered through each", and a golden is a committed picture, so
///         adding a second renderer meant a second baseline for every fixture. It does not: what
///         answers the question is the two renderings compared <i>with each other</i>, on one device,
///         in one process, at one moment. There is no baseline here, nothing to regenerate, and no
///         way for a divergence to be accepted by editing a file — which is
///         <see cref="UiBoxAgreementTests" />' argument, applied to the pair it was not applied to.
///     </para>
///     <para>
///         ⚠ <b>Both arms go through <see cref="UiRenderer" />, and that is what makes the comparison
///         about the shaders.</b> Same <c>DrawList</c>, same <c>UiGeometryBuilder</c>, same glyph
///         cache, same buffers, same pass. The only difference between the two is which eight modules
///         the <see cref="UiShaders" /> table holds — and the Raven arm gets its table from
///         <see cref="UiShaderLibrary.Load" />, the call <c>UiApplication</c> and <c>EditorHost</c>
///         both make, rather than from a hand-assembled one. That matters for one specific reason:
///         Raven's <c>StreamPlan</c> puts a stage's own parameters after the shader's streams, so
///         <c>Ui.rvn</c>'s attributes are at locations 3..6 where the GLSL's are at 0..3. A table
///         written out here would have had to know that; the loader reads it out of the compiler's
///         own reflection.
///     </para>
///     <para>
///         ⚠ <b>All eight stages are reached, and it took two fixtures rather than one because a
///         box fixture provably cannot reach three of them.</b> The four a <see cref="UiShaders" />
///         table takes positionally — vertex, box, text and solid — are the ones
///         <see cref="TheGlslCopyAndTheRavenDrawTheSameBox" /> exercises; <c>ui-image.frag</c>,
///         <c>ui-blur.frag</c>, <c>ui-colour.frag</c> and <c>ui-mask.frag</c> only run on a group's
///         composite draw, so nothing that draws a flat frame can bind them at all. That is what
///         <see cref="TheGlslCopyAndTheRavenCompositeTheSameFrame" /> is for, and it borrows
///         <c>UiCompositingTests</c>' frame for the reason the box theory borrows
///         <see cref="UiBoxAgreementTests" />': a fixture written here would reach the branches its
///         author thought of.
///     </para>
///     <para>
///         ⚠ <b>Until it landed, those four had exactly what <c>ui-box.frag</c> had before #286
///         closed: a constants-containment check and no picture.</b>
///         <c>SharedUiShaderTests.EveryConstantInTheGlslCopyIsOneTheRavenHoldsToo</c> is satisfied by
///         an expression rearranged around the same numbers, which is the whole reason a text
///         comparison is a necessary condition and not a sufficient one.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class UiRavenAgreementTests {
    const int Side = Fixture.Side;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>What both arms start from. Opaque, so the clear is the same on each.</summary>
    static readonly Color4 Background = new(0.08f, 0.09f, 0.11f, 1f);

    /// <summary>
    ///     How far the two may be apart, and it is the tightest tolerance this repository has.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exact, and it is exact because it can be rather than because it would be nice.</b>
    ///         The two arms differ in nothing but the SPIR-V bound to the fragment stage: one device,
    ///         one driver, one geometry buffer, one <c>Rgba8UNorm</c> store each. There is no second
    ///         rounding for a tolerance to absorb, and the arithmetic in the two modules is meant to
    ///         be the same arithmetic — so any difference at all is either a real divergence between
    ///         the two sources or a compiler reassociating a float expression, and both of those are
    ///         things somebody should be told about rather than allowed a shade of.
    ///     </para>
    ///     <para>
    ///         If a driver is ever found that reorders one and not the other, the honest response is a
    ///         named tolerance with the measurement in its remark, not a quiet widening — this
    ///         repository has shipped an allow-list that outlived its reason more than once.
    ///     </para>
    /// </remarks>
    static ImageTolerance Agreement => ImageTolerance.Exact;

    /// <summary>Every branch of the box shader, drawn through both sources and compared.</summary>
    /// <param name="fixture">Which frame to draw. <see cref="UiBoxAgreementTests.Frame" /> builds it.</param>
    /// <remarks>
    ///     ⚠ <b>The fixtures are that file's, deliberately, and not a second set.</b> Each of the
    ///     seven was written to reach one branch of the box shader — the corner derivative, the five
    ///     paths through the elliptical distance, the border band, the shadow's blur, the three
    ///     gradient shapes and interpolation spaces, the three readings of the tiling lanes, and the
    ///     half-open fill rule on a half-pixel edge. A second set written here would reach the
    ///     branches its author thought of.
    /// </remarks>
    [Theory]
    [InlineData("corners")]
    [InlineData("elliptical")]
    [InlineData("bordered")]
    [InlineData("blurred")]
    [InlineData("gradient")]
    [InlineData("tiled")]
    [InlineData("halfpixel")]
    public void TheGlslCopyAndTheRavenDrawTheSameBox(string fixture) {
        if (!TryOpen(out var opened, out _)) {
            return;
        }

        using var owned = opened!;

        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var geometry = new UiGeometryBuilder().Build(UiBoxAgreementTests.Frame(fixture), cache, Viewport);

        // ⚠ The instrument, both halves. No layer, so this is the plain box path — a fixture that
        // opened a group would compare the compositing stages these tables do not differ in here.
        // And something has to have drawn: two modules that both emitted nothing agree perfectly.
        Assert.Empty(geometry.Layers);
        Assert.NotEmpty(geometry.Draws);

        var glsl = new UiShaders(
            owned.Shader("ui.vert.spv", ShaderStage.Vertex),
            owned.Shader("ui-box.frag.spv", ShaderStage.Fragment),
            owned.Shader("ui-text.frag.spv", ShaderStage.Fragment),
            owned.Shader("ui-solid.frag.spv", ShaderStage.Fragment)
        );

        var raven = UiShaderLibrary.Load(owned.Device);

        owned.Owns(() => Destroy(owned, raven));

        // ⚠ Both passes are declared before either is run, because the graph refuses to be declared
        // into once it has compiled — and that refusal is right: a frame whose passes have been
        // culled and whose memory has been assigned is not a frame anything may add to. So the two
        // arms are one frame with two targets rather than two frames, which is also the arrangement
        // that guarantees they saw the same uploaded geometry.
        var one = Declare(owned, geometry, glsl, $"ui-raven-{fixture}-glsl");
        var two = Declare(owned, geometry, raven, $"ui-raven-{fixture}-rvn");

        // ⚠ Both renderers upload on every frame, not one each. `Fixture.Render` runs the whole graph
        // and reads back one target, so reading two takes two frames — and a `UiRenderer`'s buffers
        // belong to the frame that uploaded them, so the arm that did not upload this time would bind
        // a handle whose generation counter has moved on. That is not a nuisance: it is the counter
        // doing exactly what it exists for, and a suite that swallowed it would be recording a picture
        // drawn from freed memory.
        void Upload(ICommandList commands) {
            one.Renderer.Upload(commands, geometry, cache.Atlas);
            two.Renderer.Upload(commands, geometry, cache.Atlas);
        }

        var copy = owned.Render(one.Target, Upload);
        var source = owned.Render(two.Target, Upload);

        var comparison = ImageComparer.Compare(copy, source, Agreement);

        Assert.True(
            comparison.Matches,
            $"'Shaders/ui-box.frag' and 'Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn' draw a '{fixture}' box "
            + $"differently, and the shipping applications draw through the second one: {comparison}. They "
            + "are two implementations of one specification, so this is drift between them — and the "
            + "reference images in this suite were rendered against the first."
        );
    }

    /// <summary>
    ///     The four compositing stages, drawn through both sources and compared — which needs a frame
    ///     that opens a group, because nothing else binds them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The remainder <see cref="TheGlslCopyAndTheRavenDrawTheSameBox" /> left, and it is
    ///         a different fixture rather than a different assertion.</b> <c>ui-image.frag</c>,
    ///         <c>ui-blur.frag</c>, <c>ui-colour.frag</c> and <c>ui-mask.frag</c> are bound only by a
    ///         group's composite draw, so a frame with no <see cref="UiLayer" /> in it cannot reach
    ///         them however many box branches it exercises. <c>UiCompositingTests.Groups</c> is the
    ///         frame that does: two nested translucent groups with a four-entry mask list, a colour
    ///         matrix on each, a blur on the inner one, a rotated blurred panel, two drop shadows and
    ///         two backdrop captures.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both arms compose as well as record, and both do it before either pass runs.</b>
    ///         <c>Compose</c> is not part of the graph — it records a render pass per group straight
    ///         onto the command list — and a group's pass draws from the buffers <c>Upload</c> just
    ///         wrote, so the order inside the callback is upload-then-compose, twice, and never
    ///         interleaved. Both arms also do both on <i>every</i> frame, for the reason the box case
    ///         gives: reading two targets takes two runs of the graph, and a renderer's buffers belong
    ///         to the frame that uploaded them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The counters are asserted on each arm before the pixels are, and they are the
    ///         only thing separating "the two agree" from "neither did anything".</b> A group that was
    ///         not composited still draws — it draws the un-isolated approximation, which on anything
    ///         opaque is the same picture — and a blur, a matrix and a mask each have several ways of
    ///         silently not happening. Two renderers that both declined would agree perfectly, which
    ///         is the one failure a differential provably cannot report. So this is the shape
    ///         <c>UiCompositingTests</c> uses, applied to the pair it was not applied to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exact, like the box case, and for the same reason: one device, one driver, one
    ///         geometry buffer, and nothing between the two arms but the SPIR-V.</b> A divergence here
    ///         is either real drift between the two sources or a compiler reassociating a float, and
    ///         both are things somebody should be told about rather than allowed a shade of. It is
    ///         emphatically <i>not</i> <c>UiCompositingTests</c>' tolerance, which is sized for a
    ///         software emulation of <c>fwidth</c> and has no business here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheGlslCopyAndTheRavenCompositeTheSameFrame() {
        if (!TryOpen(out var opened, out _)) {
            return;
        }

        using var owned = opened!;

        var cache = new GlyphFieldCache(new GlyphAtlas(256, 256));
        var geometry = new UiGeometryBuilder().Build(UiCompositingTests.Groups(), cache, Viewport);

        // ⚠ The instrument, and the half the box case states the other way round. A frame that opened
        // no group binds none of the four stages this exists to compare, and would pass.
        Assert.Equal(6, geometry.Layers.Count);
        Assert.NotEmpty(geometry.Draws);

        var glsl = new UiShaders(
            owned.Shader("ui.vert.spv", ShaderStage.Vertex),
            owned.Shader("ui-box.frag.spv", ShaderStage.Fragment),
            owned.Shader("ui-text.frag.spv", ShaderStage.Fragment),
            owned.Shader("ui-solid.frag.spv", ShaderStage.Fragment)
        ) {
            Image = owned.Shader("ui-image.frag.spv", ShaderStage.Fragment),
            Blur = owned.Shader("ui-blur.frag.spv", ShaderStage.Fragment),
            Colour = owned.Shader("ui-colour.frag.spv", ShaderStage.Fragment),
            Mask = owned.Shader("ui-mask.frag.spv", ShaderStage.Fragment)
        };

        var raven = UiShaderLibrary.Load(owned.Device);

        owned.Owns(() => Destroy(owned, raven));

        var one = Declare(owned, geometry, glsl, "ui-raven-composited-glsl");
        var two = Declare(owned, geometry, raven, "ui-raven-composited-rvn");

        void Frame(ICommandList commands) {
            // ⚠ <b>The same colour both passes clear to, handed to both captures.</b> A capture built
            // from the draw list alone is transparent where the window's ground should be, so a
            // backdrop group would composite a blurred *translucent* copy over the sharp original —
            // and it would do so identically on both arms, which is a divergence this file could not
            // see. See `UiBackdropSource`.
            one.Renderer.Upload(commands, geometry, cache.Atlas);
            one.Renderer.Compose(commands, geometry, new Int2(Side, Side), beneath: new UiBackdropSource(Background));

            two.Renderer.Upload(commands, geometry, cache.Atlas);
            two.Renderer.Compose(commands, geometry, new Int2(Side, Side), beneath: new UiBackdropSource(Background));
        }

        var copy = owned.Render(one.Target, Frame);
        var source = owned.Render(two.Target, Frame);

        // ⚠ Per arm rather than once. The whole point of the pair is that they are two pipelines, so
        // a count read off one of them says nothing about the other — and the four stages under test
        // are exactly the ones a renderer can decline to use while still drawing a plausible frame.
        foreach (var renderer in new[] { one.Renderer, two.Renderer }) {
            Assert.Equal(6, renderer.Composited);
            Assert.Equal(3, renderer.Blurred);
            Assert.Equal(2, renderer.Backdropped);
            Assert.Equal(10, renderer.Filtered);
            Assert.Equal(6, renderer.Masked);
            Assert.Equal(2, renderer.Shadowed);
        }

        var comparison = ImageComparer.Compare(copy, source, Agreement);

        Assert.True(
            comparison.Matches,
            "'Shaders/ui-image.frag', 'ui-blur.frag', 'ui-colour.frag' and 'ui-mask.frag' and the "
            + "matching stages of 'Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn' composite a group "
            + $"differently, and the shipping applications draw through the second: {comparison}. The "
            + "four stages are only reachable through a composite draw, so this is the only fixture "
            + "that compares them at all — a difference here is drift between two implementations of "
            + "one specification, and the reference images in this suite were rendered against the "
            + "first."
        );
    }

    /// <summary>
    ///     The two arms are two different pipelines, which is what stops the comparison above being
    ///     a picture compared with itself.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Because "the same modules twice" is the failure mode a differential invites, and it
    ///     is silent.</b> Every assertion next door is satisfied perfectly by an arm that loaded the
    ///     GLSL table twice — a wrong resource name, a loader that fell back, a table copied instead
    ///     of built. So the handles are required to differ. They are opaque values from two separate
    ///     <c>CreateShader</c> calls over two different byte arrays, which is exactly the fact worth
    ///     asserting: the comparison ran over two pipelines and not one.
    /// </remarks>
    [Fact]
    public void TheTwoArmsAreNotTheSameModules() {
        if (!TryOpen(out var opened, out _)) {
            return;
        }

        using var owned = opened!;

        var glsl = owned.Shader("ui-box.frag.spv", ShaderStage.Fragment);
        var raven = UiShaderLibrary.Load(owned.Device);

        owned.Owns(() => Destroy(owned, raven));

        Assert.NotEqual(glsl, raven.Box);
        Assert.NotEqual(owned.Shader("ui.vert.spv", ShaderStage.Vertex), raven.Vertex);
    }

    /// <summary>Declares one arm's target and pass, without running anything.</summary>
    /// <remarks>
    ///     ⚠ <b>A renderer per arm, and it cannot be otherwise.</b> A <see cref="UiRenderer" /> holds
    ///     the pipelines it built from its table, so one renderer cannot be handed a second table —
    ///     which is exactly why the comparison is worth making: two pipelines, two modules, one
    ///     geometry buffer.
    /// </remarks>
    static (GraphTexture Target, UiRenderer Renderer) Declare(
        Fixture owned,
        UiGeometry geometry,
        UiShaders shaders,
        string name
    ) {
        var colour = owned.ColourTarget(name);

        var renderer = new UiRenderer(owned.Device, shaders, new Rendering.RenderOutput([PixelFormat.Rgba8UNorm]));

        owned.Owns(renderer.Dispose);

        owned.Graph.AddPass(name, pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, Background);
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        return (colour, renderer);
    }

    /// <summary>Destroys the eight modules a loaded table holds.</summary>
    /// <remarks>
    ///     ⚠ <b>Eight, and this used to destroy four.</b> <see cref="UiShaderLibrary.Load" /> creates
    ///     the four positional stages <i>and</i> <c>Image</c>, <c>Blur</c>, <c>Colour</c> and
    ///     <c>Mask</c>; the summary said eight and the body freed half of them, so every case in this
    ///     file leaked four modules. Nothing said so — an undestroyed module is memory the device holds
    ///     until it goes away, which in a test is the end of the fixture, so the picture is right and
    ///     the leak is invisible. It matters now for a second reason as well: the compositing case
    ///     below is the first that actually binds the four.
    /// </remarks>
    static void Destroy(Fixture owned, UiShaders shaders) {
        owned.Device.Destroy(shaders.Vertex);
        owned.Device.Destroy(shaders.Box);
        owned.Device.Destroy(shaders.Text);
        owned.Device.Destroy(shaders.Solid);
        owned.Device.Destroy(shaders.Image);
        owned.Device.Destroy(shaders.Blur);
        owned.Device.Destroy(shaders.Colour);
        owned.Device.Destroy(shaders.Mask);
    }

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    static bool TryOpen(out Fixture? fixture, out string? reason) {
        if (Fixture.TryOpen(out fixture, out reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so this comparison may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }
}
