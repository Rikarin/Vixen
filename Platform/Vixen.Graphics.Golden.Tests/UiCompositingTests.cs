// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The two executors of the compositing model draw the same frame, and this is what says so.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The whole defence behind the viewport-sized-surface decision, and it could not be
///         written until there were two executors.</b> <see cref="UiLayer" />'s remarks argue that a
///         surface the size of the group would need every vertex inside it translated by the group's
///         origin, on both paths, in the same direction, with the same rounding — and that a
///         disagreement there is a subtree drawn a pixel off, which no unit test would be looking at
///         and which the goldens would report as a diff somewhere else entirely. That argument is
///         only worth anything if something checks the two paths against each other. Nothing could,
///         while <c>SoftwareUiRasterizer</c> was the only one that composited.
///     </para>
///     <para>
///         ⚠ <b>A comparison and not a committed picture, which is a deliberate choice about what
///         this suite's references are for.</b> A reference image says "this is what the frame looked
///         like on the day a human approved it"; it does not say the two renderers agree, because a
///         reference is made by <i>one</i> of them. Both are asserted here — the software frame is
///         computed in the test, the device frame is rendered beside it, and the assertion is that
///         they are the same picture. There is no baseline to regenerate and so no way for a
///         divergence to be accepted by accident.
///     </para>
///     <para>
///         ⚠ <b>Compositing has to have actually happened, and a frame where it did not looks almost
///         identical.</b> The un-isolated approximation — fading each of a group's children
///         separately — differs from the isolated one only where the children overlap and only where
///         coverage is partial. So <see cref="UiRenderer.Composited" /> is asserted before the pixels
///         are: without it, two renderers that both silently declined to composite would agree
///         perfectly and this file would be checking nothing. That is the failure mode
///         <c>verify the instrument first</c> is about.
///     </para>
///     <para>
///         ⚠ <b>What this file <i>cannot</i> police, checked by breaking it rather than assumed.</b>
///         Anything the two executors read out of the <i>same</i> plan is invisible here, because a
///         wrong answer is wrong identically on both sides. The bounds outset a blur needs is the
///         live example: deleting it entirely still leaves the device frame and the software frame
///         matching to the pixel, since both composite through <c>UiLayer.Bounds</c> and both
///         therefore clip the halo in the same place. So the <i>shape</i> of a blur is asserted in
///         <c>Vixen.Ui.Controls.Tests.FilterBlurTests</c>, against arithmetic, and only the agreement
///         is asserted here. Deleting the blur from one executor <i>is</i> caught — that was checked
///         too, and it comes out at 12.65% of pixels differing by up to 57 levels.
///     </para>
///     <para>
///         ⚠ <b>The drop shadow divides the same way, and both halves were measured rather than
///         assumed.</b> Its <i>displacement</i> is invisible here — the offset is spent in
///         <c>UiGeometryBuilder.Layer</c>, on a quad both executors then draw, so a shadow moved the
///         wrong way is moved the wrong way twice and the two agree. That is
///         <c>Vixen.Ui.Controls.Tests.FilterDropShadowTests</c>' half. Its <i>tint</i> is not:
///         replacing <c>UiDropShadow.Tint</c> with the identity on the device alone — which is the
///         untinted copy of the element the machinery makes easiest to produce — comes out at
///         <b>2.46% of pixels differing by up to 187 levels</b>, checked by breaking it.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class UiCompositingTests {
    const int Side = Fixture.Side;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>What both paths start from. Opaque, so the clear and the software fill are the same.</summary>
    /// <remarks>
    ///     ⚠ <b>Alpha one, and not for the look of it.</b> <c>SoftwareUiRasterizer</c> premultiplies
    ///     its background into a float target; a render pass writes the clear colour to the attachment
    ///     as it stands. The two agree exactly when the alpha is one and differ by the alpha when it
    ///     is not — which would be a difference in the harness rather than in the thing under test,
    ///     sitting on every pixel of the frame.
    /// </remarks>
    static readonly Color4 Background = new(0.08f, 0.09f, 0.11f, 1f);

    /// <summary>
    ///     A translucent group with another inside it, both holding overlapping children, drawn twice.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nested, because a single group would not exercise the ordering either path had to
    ///         get right.</b> <c>UiGeometry.Layers</c> is in pre-order and the two consumers walk it
    ///         differently — the software one recurses into a group as it meets it, the device one
    ///         renders the passes <i>post-order</i> so that a group's children are finished before the
    ///         pass that samples them <i>and</i> its earlier siblings before a backdrop can capture
    ///         them. Those are the same order only when there is something nested to order.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And text inside the groups, which is where the premultiply bug lives.</b> A layer
    ///         surface holds premultiplied colour; an ordinary image holds straight alpha. Sampling
    ///         one as the other multiplies the coverage in twice — and at full coverage that is the
    ///         identical answer, so a fixture of opaque rectangles passes with the shader wrong.
    ///         Every glyph edge is partial coverage, and so is every rounded corner, which is why
    ///         both are here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheDeviceAndTheSoftwareRendererCompositeTheSameFrame() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("ui-composited");

        var cache = new GlyphFieldCache(new GlyphAtlas(256, 256));
        var geometry = new UiGeometryBuilder().Build(Groups(), cache, Viewport);

        // ⚠ Before either renderer runs. A frame that opened no group is one where the whole
        // comparison below is between two flat walks, which would agree whatever the compositing
        // code did — including if it were deleted.
        Assert.Equal(6, geometry.Layers.Count);

        var renderer = new UiRenderer(
            owned.Device,
            new(
                owned.Shader("ui.vert.spv", ShaderStage.Vertex),
                owned.Shader("ui-box.frag.spv", ShaderStage.Fragment),
                owned.Shader("ui-text.frag.spv", ShaderStage.Fragment),
                owned.Shader("ui-solid.frag.spv", ShaderStage.Fragment)
            ) {
                Image = owned.Shader("ui-image.frag.spv", ShaderStage.Fragment),
                Blur = owned.Shader("ui-blur.frag.spv", ShaderStage.Fragment),
                Colour = owned.Shader("ui-colour.frag.spv", ShaderStage.Fragment),
                Mask = owned.Shader("ui-mask.frag.spv", ShaderStage.Fragment)
            },
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        owned.Graph.AddPass("ui-composited", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, Background);
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var rendered = owned.Render(
            colour,
            commands => {
                // ⚠ Both outside the graph's pass, and `Compose` after `Upload` rather than before.
                // A group's pass draws from the buffers `Upload` writes and through the descriptor
                // set `Upload` rebinds for this frame's region, so composing first would render every
                // group from the previous frame's geometry.
                renderer.Upload(commands, geometry, cache.Atlas);
                // ⚠ <b>The same colour the graph's pass clears to, and handing over nothing is a
                // visibly different frame rather than an unspecified one.</b> A capture built from
                // the draw list alone is transparent where the window's ground should be, so the
                // backdrop group would composite a blurred *translucent* copy over the sharp original
                // instead of replacing it — a double image along every edge inside the panel, which
                // the software renderer would not reproduce because its capture is a clone of a
                // buffer that already holds the background. See `UiBackdropSource`.
                renderer.Compose(
                    commands,
                    geometry,
                    new Int2(Side, Side),
                    beneath: new UiBackdropSource(Background)
                );
            }
        );

        // The instrument, before the measurement. See the class remarks.
        Assert.Equal(6, renderer.Composited);

        // ⚠ <b>And the second instrument, because a blur has three separate ways of not happening
        // and all of them draw a correct sharp picture.</b> No blur stage handed over, no
        // `UiLayer.Blur` on the geometry, a `KernelRadius` that came out zero — each leaves
        // `Composited` at two and the comparison below passing, since the software renderer would
        // then be being compared against a device that agreed with it about doing nothing.
        // ⚠ Three, and they are three different kinds of blur over three different pictures: the
        // inner group's own surface, the fourth group's *backdrop*, and the fifth group's rotated
        // surface. A renderer that ran only the first would leave a sharp scene behind the glass
        // panel, which is a picture a comparison can see only because the panel sits over content
        // that has structure in it.
        //
        // ⚠ The third is counted here and swept differently there: a transformed group takes
        // `BlurSurface`'s full-region path rather than drawing through its own composite quad, and
        // this count is what says the fallback still blurred rather than quietly returning false.
        // Every early return in that method is a blur that did not happen, and an unblurred rotated
        // panel is a perfectly plausible-looking picture.
        Assert.Equal(3, renderer.Blurred);

        // ⚠ <b>And the counter without which the whole backdrop is invisible to this file.</b> With
        // nothing behind an element every backdrop filter is the identity, so a capture that never
        // ran and a capture that ran perfectly are the same frame over most fixtures — and both
        // executors would be reproducing the same nothing, which is the one failure a comparison of
        // the two provably cannot report. This is the only assertion here that separates them.
        Assert.Equal(2, renderer.Backdropped);

        // ⚠ <b>And the third, because a colour matrix has a fourth way of not happening that a blur
        // does not: the frame can be <i>identical</i> without it.</b> A blur that did not run leaves a
        // sharp picture, which is at least a different picture; a matrix that did not run leaves the
        // right picture wherever the group's colours happen to be near the matrix's fixed points.
        // Both of these are chosen not to be — see `Groups` — but the assertion is what makes that a
        // fact rather than a hope, and it is also the only thing that would notice a host handing
        // over no `UiShaders.Colour`, which composites through the image pipeline and says nothing.
        //
        // ⚠ Four, and no two of them are the same shape. The outer group's is submitted by the
        // frame's pass and the inner group's by the *outer group's*, which is the half of the count
        // `Record` alone could not see. The other two are the drop shadows' quads: a shadow is a tint
        // over a silhouette, so it reaches the frame through a stage that applies a matrix — and a
        // shadow surface composited through the image pipeline instead would be a full-colour copy of
        // the element under itself, which increments `Shadowed` below and not this. ⚠ The two arrive
        // through *different* modules: the top-level shadow is unmasked and goes to
        // `ui-colour.frag`, and the inner one is masked and goes to `ui-mask.frag`, which carries the
        // matrix as well. `EnsureSurfaces` picks between them per layer, and picking wrong is a
        // shadow that is not allocated at all rather than one drawn badly.
        // ⚠ <b>Seven, and the two above five are the surprise this feature brings to every counter
        // on the class.</b> Five draws are the frame's own: the two groups' composites, the two drop
        // shadows' quads, and the fourth group's *backdrop* quad, whose sepia is a matrix of its own
        // and emphatically not the group's — an element may carry a `filter` and a `backdrop-filter`
        // that do different things, and a renderer that reused one for the other would tint the scene
        // as well as the panel.
        //
        // ⚠ The other two are the <i>same</i> draws submitted a second time, inside the backdrop's
        // capture pass: a capture is a replay of the prefix behind the group, and the outer group's
        // composite and the third group's shadow quad are both in that prefix and both carry a
        // matrix. That is real work and the counter is right to see it — a backdrop costs the frame a
        // second pass over everything behind the element, which is the price of the feature and is
        // exactly what these numbers exist to make visible. It also means <c>Filtered</c> and
        // <c>Masked</c> are no longer bounded by the layer count, and a reader who assumed they were
        // would find that surprising here rather than in production.
        Assert.Equal(10, renderer.Filtered);

        // ⚠ <b>And the fourth instrument, for the reason the third one gives and one more.</b> A mask
        // shares all four of a colour matrix's ways of not happening, and it adds a fifth that is
        // peculiar to the two-in-one pipeline: `maskPipeline` serves masked groups *and* carries the
        // matrix, so a `SubmitDraw` that preferred `colourPipeline` would draw both of these groups
        // correctly filtered and entirely unmasked — and `Filtered` above would still read four. This
        // is the only assertion that separates those two states.
        // ⚠ Three: the two groups, and the inner group's *shadow*. A masked element's drop shadow is
        // masked too — see `UiRenderer.Compose`, which states the frame it is cut in and how that
        // differs from CSS. A shadow left out of the mask map would escape the ramp entirely, which
        // is a hard-edged silhouette under a faded element and the one thing a mask exists to stop.
        // ⚠ Four rather than three: the two groups, the inner group's shadow, and the outer group's
        // composite a *second* time — replayed into the fourth group's backdrop capture, for the
        // reason `Filtered` gives one assertion up.
        Assert.Equal(6, renderer.Masked);

        // ⚠ <b>And the fifth, because a drop shadow has every one of a blur's ways of not happening
        // and one that is peculiar to it.</b> No `UiShaders.Blur`, no `UiLayer.Shadow`, a group
        // collapsed before it became a layer — and a host with a blur stage and no colour stage,
        // which gets no shadow at all because `EnsureSurfaces` declines to allocate a surface whose
        // tint nothing could apply. Every one of those leaves a correct, shadowless picture, and the
        // software renderer would have to be broken in the same way for the comparison below to
        // notice. This is the only assertion that separates a shadow that ran from one that did not.
        Assert.Equal(2, renderer.Shadowed);

        var software = SoftwareUiRasterizer.Render(geometry, cache.Atlas, Side, Side, Background);

        var comparison = ImageComparer.Compare(rendered, software, Agreement);

        Assert.True(
            comparison.Matches,
            "the device and the software renderer disagree about a composited frame, and one of them "
            + $"is wrong: {comparison}. A dark ring around everything inside a group is the image "
            + "shader premultiplying a surface that was already premultiplied — see `shape.x` in "
            + "`ui-image.frag`. A subtree drawn twice, opaque underneath and faded on top, is "
            + "`UiRenderer.Submit` failing to skip a group whose surface it had already rendered."
        );
    }

    /// <summary>
    ///     ⚠ A group that is composited is not the same picture as one that is not, and this is what
    ///     makes the comparison above worth running.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Without this, "the two agree" is compatible with both of them being wrong in the
    ///         same way</b> — and the way they would both be wrong is the cheap one: fade each child
    ///         separately and never allocate a surface. So the frame above is rendered a second time
    ///         with the groups' brackets removed and the same opacity folded onto each child, which
    ///         is precisely the approximation <c>DrawListBuilder.Compositing</c> being off produces,
    ///         and the two pictures are required to <i>differ</i>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>On the software renderer alone, deliberately.</b> What this asserts is a property
    ///         of the model — that isolating a group changes the frame — and the model is the same on
    ///         both paths. Rendering it on the device as well would need a second fixture, a second
    ///         set of passes and a second readback to answer a question that has nothing to do with
    ///         the device.
    ///     </para>
    /// </remarks>
    [Fact]
    public void IsolatingAGroupIsNotTheSameAsFadingItsChildren() {
        var isolated = new GlyphFieldCache(new GlyphAtlas(256, 256));
        var flattened = new GlyphFieldCache(new GlyphAtlas(256, 256));

        var withGroups = new UiGeometryBuilder().Build(Groups(), isolated, Viewport);
        var withoutGroups = new UiGeometryBuilder().Build(Groups(isolate: false), flattened, Viewport);

        Assert.Equal(6, withGroups.Layers.Count);
        Assert.Empty(withoutGroups.Layers);

        var a = SoftwareUiRasterizer.Render(withGroups, isolated.Atlas, Side, Side, Background);
        var b = SoftwareUiRasterizer.Render(withoutGroups, flattened.Atlas, Side, Side, Background);

        var comparison = ImageComparer.Compare(a, b, ImageTolerance.Exact);

        Assert.False(
            comparison.Matches,
            "isolating the groups changed nothing, so the fixture has no overlap the isolation can "
            + "show and the comparison test next door would pass with compositing switched off "
            + $"entirely: {comparison}."
        );
    }

    /// <summary>
    ///     How far the two may be apart, and every part of it is a property of the target rather than
    ///     of the compositing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The device blends in eight bits and the software renderer blends in floats, and a
    ///         composited pixel is blended more times than an ordinary one.</b> A group's contents are
    ///         blended into an <c>Rgba8UNorm</c> surface, the surface is quantised, and the composite
    ///         is then blended into an <c>Rgba8UNorm</c> frame — three roundings against the software
    ///         path's one, and a nested group adds two more. That is a handful of least-significant
    ///         bits wherever coverage is partial, which for this fixture is every glyph edge and every
    ///         rounded corner.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fraction used to be twelve times this, and what it was absorbing was a
    ///         defect rather than a property of the target.</b> <c>SoftwareUiRasterizer</c> took
    ///         <c>fwidth</c> as a forward difference to the next pixel, which is not what a GPU
    ///         computes — a derivative belongs to the 2×2 quad, so half the fragments get a
    ///         *backward* difference — and around a rounded corner, where the distance field is
    ///         curved, the two straddle the arc in opposite directions. That was worth up to
    ///         seventeen levels of 255 on the corner arcs of a frame with no group in it at all, and
    ///         seventeen pixels of this fixture over the channel bound. Emulating the quad closed it:
    ///         <b>one</b> pixel of 16384 now exceeds the bound, and that one is the 8-bit store the
    ///         paragraph above describes. See the derivative in <c>SoftwareUiRasterizer.Box</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So why not zero.</b> <c>fwidth</c> is implementation-defined between a fine and a
    ///         coarse derivative, and the emulation is of the fine one because that is what this
    ///         device does. A driver reporting coarse derivatives would put ten pixels of this
    ///         fixture back over the bound — measured, by running the coarse emulation against the
    ///         device — so the fraction is sized to clear that with room and no more. Anything above
    ///         sixteen pixels is a real divergence and not a driver's choice of derivative.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Tighter than <c>Tolerance.Edges</c>, which this suite's committed pictures
    ///         use.</b> That one is sized to absorb two conformant drivers disagreeing about an sRGB
    ///         conversion; there is no conversion here — the format is <c>UNorm</c> and the colours
    ///         are linear. What compositing could get wrong is far larger than either allowance: with
    ///         the shader premultiplying twice, this fixture differs on 8.6% of its pixels by up to
    ///         41 levels, and with a group's contents drawn as well as its composite, on 32.6% by up
    ///         to 164. Both were checked by breaking it.
    ///     </para>
    /// </remarks>
    static ImageTolerance Agreement => new(4, 0.001);

    /// <summary>The fixture: two translucent groups, one inside the other, each with overlap in it.</summary>
    /// <param name="isolate">
    ///     Whether to bracket the groups. False folds each group's opacity onto its children instead,
    ///     which is what the frame looks like with compositing off.
    /// </param>
    static DrawList Groups(bool isolate = true) {
        var font = Font();
        var list = new DrawList();
        list.BeginFrame();

        // Something outside every group, so the frame is not entirely one surface and a group that
        // composited the whole frame by mistake would be visible rather than merely different.
        list.Add(new(DrawCommandKind.Rectangle, 4, 4, 40, 16, new Color4(0.85f, 0.55f, 0.15f, 1f), 4, 0));

        const float Outer = 0.6f;
        const float Inner = 0.5f;
        const float InnerBlur = 3f;

        // ⚠ <b>A mask <i>list</i> on the outer group, of three entries, and every part of its shape
        // is doing a job.</b> A single ramp with real dynamic range is what makes a disagreement
        // about *where* the mask is resolved show up as pixels rather than as rounding — that much
        // was already true. Three entries add the three things a list can get wrong and one mask
        // cannot: an index into the storage buffer that is off by one, an operator read from the
        // wrong entry, and a fold run in the wrong direction.
        //
        // ⚠ <b>The middle entry composites with <c>subtract</c>, and it is the only operator that
        // could.</b> `add`, `intersect` and `exclude` are all symmetric in their two arguments, so a
        // fold that walked the list top-down instead of bottom-up would produce the identical picture
        // under any of them and this fixture would say the two executors agreed about an order
        // neither had been asked to have. `subtract` is `s(1 - b)`, which is not `b(1 - s)`: with
        // these three the reversed fold falls to under a fifth of the coverage over most of the box.
        //
        // ⚠ <b>And the arrangement keeps the group bright.</b> The `subtract` sits above the entry
        // with the *smallest* coverage, so what it punches out is small and the composed ramp still
        // runs from about a tenth to one across the group's box. A list that composed to nearly
        // nothing would be a fixture whose pixels all agree because there is nothing left of them.
        Push(
            list,
            isolate,
            8,
            24,
            112,
            96,
            Outer,
            filter: OuterFilter,
            mask: [
                Ramp(8, 24, 112, 96, 1f, 0.35f, MaskComposite.Intersect),
                Ramp(8, 24, 112, 96, 1f, 0.45f, MaskComposite.Subtract) with { Axis = new Vector2(0f, 1f) },
                Round(8, 24, 112, 96, 0.3f, 0f)
            ]
        );

        // ⚠ Two overlapping children of the outer group, and the overlap is the whole point: isolated,
        // they do not show through each other; faded separately, they do. Both rounded, so the pixels
        // the two disagree about include partly covered ones.
        list.Add(new(DrawCommandKind.Rectangle, 12, 28, 56, 44, Fade(new Color4(0.2f, 0.6f, 0.95f, 1f), isolate, Outer), 10, 0));
        list.Add(new(DrawCommandKind.Rectangle, 40, 44, 56, 44, Fade(new Color4(0.95f, 0.3f, 0.4f, 1f), isolate, Outer), 10, 0));

        // ⚠ Glyphs inside a group, which is where a doubled premultiply shows. Every edge of every
        // glyph is partial coverage, and at full coverage the bug is arithmetically invisible.
        Text(list, font, "AB", 16, 66, Fade(Color4.White, isolate, Outer));

        // ⚠ <b>The blur is on the <i>inner</i> group, so what the outer group's surface receives is a
        // blurred composite rather than a sharp one.</b> On the device that is a pass writing into a
        // surface that a later pass samples and blurs again; in the software renderer it is a
        // convolved buffer being sampled by the recursion one level up. Those are the two orderings
        // the post-order walk exists to keep straight, and a blur on the outermost group would
        // exercise neither.
        //
        // ⚠ Three, not thirty. `UiLayer.KernelRadius` is three sigma, so this is a nine-pixel outset
        // and a nineteen-tap kernel on a hundred-and-twenty-eight-pixel fixture — wide enough that the
        // halo lands well outside the group's unblurred silhouette, and short of the truncation at
        // `UiLayer.MaximumKernel`, which is a case worth having somewhere and not here.
        // ⚠ <b>And a round one on the inner group, which is the group that is also blurred — so this
        // is the assertion that the two executors agree about the mask's <i>seam</i> and not merely
        // about its arithmetic.</b> A mask does not commute with a Gaussian, so an executor that
        // folded the mask into the surface before convolving it would differ from one that applied it
        // at the composite everywhere the ramp is not flat across the kernel: a ring of the wrong
        // brightness just inside the blurred edge. That is the one divergence `UiMask`'s rule exists
        // to prevent, and this is the only thing in the repository that can see it.
        //
        // ⚠ Radial rather than linear, so that the pair of masks in this fixture do not share a shape
        // — a `mask_progress` wired to one branch for every kind would otherwise draw both correctly.
        //
        // ⚠ One entry here and three on the outer group, deliberately: the one-entry path is the one
        // every `mask-linear-*` in the engine takes, and a list implementation that only ever ran with
        // several would leave it to be exercised by nothing.
        //
        // ⚠ <b>And a drop shadow on it too, which is the <i>other</i> arm of every choice the third
        // group below takes.</b> That one is top level, unmasked and hard-edged; this one is nested,
        // masked and blurred. So between them they cover: a shadow submitted by the frame's pass and
        // one submitted by a parent group's; a tint arriving through `ui-colour.frag` and one
        // arriving through `ui-mask.frag`, which is the module that wins when a group has both; and
        // `ShadowSurface`'s two-pass separable sweep against its one-pass copy — the branch
        // `KernelRadius` answers zero for, where the shader is handed a sigma it cannot be handed
        // truthfully. Neither arm is exercised by the other.
        //
        // ⚠ The same sigma as the group's own blur, deliberately: the bounds are outset by the
        // *wider* of the two and not their sum, so an equal pair leaves this fixture's rectangles
        // exactly where they were and the only thing that moves is the silhouette.
        Push(
            list,
            isolate,
            44,
            56,
            72,
            60,
            Inner,
            InnerBlur,
            InnerFilter,
            [Round(44, 56, 72, 60, 1f, 0.15f)],
            new UiDropShadow(new Vector2(4f, 5f), InnerBlur, new Color4(0.03f, 0.01f, 0.02f, 0.6f))
        );

        // The nested group's own overlapping pair, offset from the outer one's so that the two
        // groups' ink is not the same rectangle — a surface sized from the wrong group's bounds
        // would then be indistinguishable.
        var nested = isolate ? Inner : Inner * Outer;

        list.Add(new(DrawCommandKind.Rectangle, 48, 60, 40, 36, Fade(new Color4(0.3f, 0.9f, 0.4f, 1f), isolate, nested), 8, 0));
        list.Add(new(DrawCommandKind.Rectangle, 70, 74, 40, 36, Fade(new Color4(0.95f, 0.85f, 0.2f, 1f), isolate, nested), 8, 0));

        Text(list, font, "C", 52, 104, Fade(Color4.White, isolate, nested));

        Pop(list, isolate);

        // ⚠ <b>A glass panel <i>inside</i> the outer group, and the one thing it is here for is the
        // clear its capture pass starts from.</b> Filter Effects 2 § 2 makes every `UiLayer` a
        // backdrop root, so a nested group's backdrop is its parent's own surface so far — which
        // starts from *transparent black*, not from whatever the host painted. `SoftwareUiRasterizer`
        // gets that for free by cloning the buffer its recursion was handed; `UiRenderer.Capture` has
        // to be told, and a version that cleared to `UiBackdropSource.Colour` here would paint the
        // window's ground inside a translucent panel — checked by breaking it, and it comes out at
        // 541 of 16384 pixels differing by up to 43 levels.
        //
        // ⚠ <b>And it is placed where the outer group's surface is <i>transparent</i>, which is the
        // whole of why it can see that.</b> The first position tried sat inside the blue rectangle,
        // where the parent's prefix covers every texel the panel samples — so the clear underneath it
        // made no difference at all and the sabotage above passed. A backdrop test has to read a pixel
        // whose value comes from the clear rather than from the replay, and inside a group that means
        // a gap in the parent's own ink.
        //
        // ⚠ <b>And its prefix is the outer group's, not the frame's.</b> It sits after the inner
        // group's composite, so the capture replays the outer group's two rectangles, its text and the
        // whole nested composite — a walk with a `stop` that has to start at the *parent's* first draw
        // rather than at zero. Starting at zero would pull the orange bar and the frame's ground into
        // a nested panel.
        //
        // ⚠ Unblurred, deliberately, so that this arm is the *copy* branch of `Capture` while the
        // top-level panel below is the two-sweep one. An invert rather than something subtler because
        // the outer group's own quarter-inversion is already on this ground, and an invert of an
        // inverted thing is the one transform that is obviously not the identity here.
        Push(list, isolate, 86, 26, 30, 18, 1f, backdrop: new UiBackdrop(0f, 1f, UiColorMatrix.Invert(1f)));

        list.Add(new(DrawCommandKind.Rectangle, 86, 26, 30, 18, new Color4(1f, 1f, 1f, 0.1f), 4, 0));

        Pop(list, isolate);
        Pop(list, isolate);

        // ⚠ <b>A third group, at the top level, carrying a drop shadow and nothing else — and every
        // one of those three words is what the other two groups could not supply.</b>
        //
        // ⚠ <b>Top level, so the shadow's quad is submitted by the <i>frame's</i> pass.</b> The two
        // groups above are one nest, so everything in this fixture except the orange bar has so far
        // been drawn inside a group's pass. A shadow is two extra passes recorded in `Compose` and a
        // quad submitted in `Record`, and those halves are wired through different code — the second
        // one has never been exercised for a composite that is not a nested group's.
        //
        // ⚠ <b>Neither blurred nor masked, so the tint has to arrive through
        // <c>colourPipeline</c>.</b> A shadow on the inner group would reach `ui-mask.frag`, which
        // carries the matrix as well, and would therefore say nothing about the branch every
        // unmasked shadow in the engine takes. <c>UiRenderer.EnsureSurfaces</c> chooses between the
        // two per layer; this is the arm that would otherwise be chosen by nothing.
        //
        // ⚠ <b>Offset diagonally and <i>not</i> blurred, over ground that is already composited.</b>
        // The displacement puts the silhouette across the outer group's own surface rather than over
        // the background, so a shadow drawn in the wrong order — after its group, or after the whole
        // frame — lands somewhere this comparison can see. A zero offset would hide it behind the
        // element that cast it, which is the fixture mistake that makes a drop shadow untestable.
        // The zero *blur* is the other half of the pairing with the inner group: it is the branch
        // `UiRenderer.ShadowSurface` takes as a single-tap copy rather than a separable sweep, and
        // the one where the shader is handed a sigma of one because a sigma of zero is a NaN.
        //
        // ⚠ <b>And a translucent colour, because the alpha is the half of the arithmetic that does
        // not live in the matrix.</b> <c>UiDropShadow.Tint</c> has three rows and cannot scale alpha,
        // so it rides the quad — and an executor that put it in both places would square it. At 0.75
        // against 1.0 that is a difference of sixteen levels over the whole silhouette, which is four
        // times this comparison's channel bound.
        Push(
            list,
            isolate,
            64,
            4,
            56,
            18,
            1f,
            shadow: new UiDropShadow(new Vector2(5f, 6f), 0f, new Color4(0.02f, 0.02f, 0.04f, 0.75f))
        );

        list.Add(new(DrawCommandKind.Rectangle, 64, 4, 56, 18, new Color4(0.55f, 0.85f, 0.95f, 1f), 6, 0));
        Text(list, font, "D", 70, 18, Color4.White);

        Pop(list, isolate);

        // ⚠ <b>A fourth group, carrying a <c>backdrop-filter</c> and drawn last — and every one of
        // those three words is a constraint the other three groups could not supply.</b>
        //
        // ⚠ <b>Last, so that its backdrop is a replay of everything above.</b> A capture is the
        // parent's draws from its first up to this group's first, and at the top level that is the
        // orange bar, the whole nested pair and the third group's shadow — three composites this
        // group's own pass has to find already in <c>ShaderRead</c>. That is the constraint that made
        // <c>UiRenderer.Compose</c>'s walk post-order: the reverse pre-order it used to run renders
        // a group's <i>later</i> siblings first, so under the old loop this capture would have
        // sampled surfaces nothing had written. ⚠ Nothing else in this fixture can see that change —
        // the other three groups are correct in either order.
        //
        // ⚠ <b>Over the busiest part of the frame rather than over the background.</b> With nothing
        // behind it every backdrop filter is the identity — a blur of a flat field is the field — so
        // a glass panel on plain ground would make a working capture and no capture at all the same
        // picture, on both executors at once. This one straddles the outer group's blurred, masked,
        // quarter-inverted composite and the bare background beside it, which is coverage that varies
        // over the panel in both axes.
        //
        // ⚠ <b>A blur <i>and</i> a matrix <i>and</i> an alpha, because the three land in three
        // different places and only one of them is the backdrop's surface.</b> The Gaussian is two
        // passes over the capture, the matrix rides the backdrop quad's fragment stage through
        // `ui-colour.frag`, and `UiBackdrop.Alpha` rides the quad's vertex alpha — which is the one
        // a three-row colour matrix cannot carry. An executor that put the alpha in both places would
        // square it, and this is the fixture that would say so.
        //
        // ⚠ <b>And the panel's own paint is nearly transparent</b>, so what the comparison is looking
        // at is the filtered backdrop rather than a white rectangle over it. It cannot be *fully*
        // transparent: a group that paints nothing is discarded before it becomes a layer.
        Push(
            list,
            isolate,
            6,
            86,
            64,
            36,
            1f,
            backdrop: new UiBackdrop(2.5f, 0.85f, UiColorMatrix.Sepia(1f))
        );

        list.Add(new(DrawCommandKind.Rectangle, 6, 86, 64, 36, new Color4(1f, 1f, 1f, 0.12f), 6, 0));

        Pop(list, isolate);

        // ⚠ <b>A fifth group, rotated and blurred, and the pairing of those two is the only reason it
        // is here.</b> A transform on its own would say very little: the matrix is baked into the
        // composite quad's four vertex positions by <c>UiGeometryBuilder</c>, so both executors draw
        // it through the path they draw every quad through — the device's rasteriser and
        // <c>SoftwareUiRasterizer.Triangle</c>'s barycentrics — and there is no second implementation
        // for them to disagree about. What each transform <i>is</i> is asserted against pixels in
        // <c>Vixen.Ui.Controls.Tests.TransformPaintTests</c>, which needs no device.
        //
        // ⚠ <b>The blur is what makes the two paths differ, and it is a divergence this fixture is
        // the only thing that can see.</b> <c>UiRenderer.BlurSurface</c> convolves a group's surface
        // by drawing <i>through</i> its composite quad, which is correct only while the quad and the
        // surface share a space — and under a transform they do not. So a rotated group falls back to
        // the full-region sweep, and a renderer that did not would convolve a rotated footprint of an
        // upright picture: everything outside the tilted quad left sharp, with a hard diagonal across
        // the halo. <c>SoftwareUiRasterizer</c> convolves the whole buffer and would be unaffected,
        // which is precisely why only a two-executor comparison catches it.
        //
        // ⚠ Thirty degrees rather than a right angle, so that the quad is genuinely off-axis: every
        // multiple of ninety leaves an axis-aligned rectangle, which is the one family of transforms
        // under which the wrong sweep is still correct.
        //
        // ⚠ <b>And a non-uniform scale composed in, so the matrix is not a similarity.</b> A rotation
        // alone preserves lengths, so a path that had normalised the matrix — or applied it to the
        // quad's centre and size rather than to its corners — would come out identical. This one
        // shears the sampled grid, which is where a per-vertex texture coordinate and a
        // per-quad one stop agreeing.
        //
        // ⚠ Well clear of the other four groups' ink, because the point is the halo's shape rather
        // than how it blends: a tilted blur over a busy ground is a comparison whose failures are hard
        // to attribute.
        Push(
            list,
            isolate,
            76,
            92,
            44,
            28,
            0.9f,
            InnerBlur,
            transform: UiTransform.Scale(1.25f, 0.8f, new Vector2(98f, 106f))
                .Then(UiTransform.Rotation(30f, new Vector2(98f, 106f)))
        );

        list.Add(new(DrawCommandKind.Rectangle, 76, 92, 44, 28, Fade(new Color4(0.9f, 0.45f, 0.8f, 1f), isolate, 0.9f), 6, 0));
        Text(list, font, "E", 84, 112, Fade(Color4.White, isolate, 0.9f));

        Pop(list, isolate);

        list.EndFrame();
        return list;
    }

    /// <summary>The outer group's colour transform: a quarter of the way to its own complement.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An <c>invert</c> rather than anything prettier, because it is the only one of the
    ///         seven with a non-zero <i>offset</i>, and the offset is the half of the arithmetic that
    ///         can be wrong in a way nothing else catches.</b> A colour matrix on premultiplied colour
    ///         is <c>M·c + o·a</c>, and an implementation that forgot the <c>·a</c> would be exactly
    ///         right on every fully opaque pixel and wrong on every partly covered one — which is
    ///         every glyph edge and every rounded corner in this fixture, and nothing at all in a
    ///         fixture of opaque rectangles. It is the same shape as the premultiply bug the text in
    ///         here exists to catch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A quarter and not a full inversion, and the reason is the tolerance rather than
    ///         taste.</b> The device applies the matrix to a surface that has been quantised to eight
    ///         bits and the software renderer applies it to floats, so whatever the matrix amplifies,
    ///         it amplifies that quantisation too. <c>invert(0.25)</c> scales by <c>0.5</c> — a
    ///         contraction — so the difference between the two paths shrinks rather than grows, and
    ///         <see cref="Agreement" /> is measuring compositing rather than the matrix's gain. A
    ///         <c>brightness(4)</c> here would put pixels over the bound and prove nothing.
    ///     </para>
    /// </remarks>
    static UiColorMatrix OuterFilter => UiColorMatrix.Invert(0.25f);

    /// <summary>The inner group's: fully grey, on top of its blur.</summary>
    /// <remarks>
    ///     ⚠ <b>On the group that is <i>also</i> blurred, which is the interaction neither renderer
    ///     runs the same way.</b> The device blurs the surface in two passes and then applies the
    ///     matrix in the composite's fragment stage; the software renderer blurs the surface and then
    ///     transforms the whole buffer at the seam. Those give the same picture only because the
    ///     transform is linear in premultiplied colour and so commutes with the Gaussian — see
    ///     <c>UiLayer.Filter</c> — and this is the fixture that would report it if the argument were
    ///     wrong. A filter on the unblurred group alone would exercise neither path against the other.
    ///     <para>
    ///         ⚠ And it is a <i>different</i> matrix from the outer group's, so a renderer that took
    ///         one group's filter and applied it to both — the shape of bug a per-frame push constant
    ///         instead of a per-draw one would produce — is a visible difference rather than a
    ///         coincidence. Grey inside a quarter-inverted parent is not quarter-inverted grey.
    ///     </para>
    /// </remarks>
    static UiColorMatrix InnerFilter => UiColorMatrix.Grayscale(1f);

    /// <summary>Opens a group, or does nothing when the fixture is being flattened.</summary>
    static void Push(
        DrawList list,
        bool isolate,
        float x,
        float y,
        float width,
        float height,
        float alpha,
        float blur = 0f,
        UiColorMatrix? filter = null,
        ReadOnlySpan<UiMask> mask = default,
        UiDropShadow? shadow = null,
        UiBackdrop? backdrop = null,
        UiTransform? transform = null
    ) {
        if (isolate) {
            list.Add(
                new DrawCommand(DrawCommandKind.LayerPush, x, y, width, height, new Color4(1f, 1f, 1f, alpha), 0, 0) {
                    Blur = blur,
                    Filter = filter,
                    Shadow = shadow,
                    Backdrop = backdrop,
                    Transform = transform,

                    // ⚠ A range of the draw list's own side buffer, which is the only way a group can
                    // carry a mask now that `mask-image` is a list. See `DrawList.Masks`.
                    Offset = mask.Length > 0 ? list.AddMasks(mask) : 0,
                    Length = mask.Length
                }
            );
        }
    }

    /// <summary>A linear ramp across a box, as <c>DrawListBuilder</c> would build one for it.</summary>
    /// <remarks>
    ///     ⚠ <b>Constructed here rather than parsed from CSS, because this suite has no style engine
    ///     — and that is the same reason the matrices next door are built from
    ///     <see cref="UiColorMatrix" /> factories.</b> What is being compared is two executors, so the
    ///     mask has to reach both of them from one place; whether <c>mask-image</c> resolves to this
    ///     mask is <c>MaskGradientTests</c>' question and is asked against pixels there.
    /// </remarks>
    static UiMask Ramp(
        float x,
        float y,
        float width,
        float height,
        float from,
        float to,
        MaskComposite composite = MaskComposite.Add
    ) =>
        new(
            new Vector2(x + (width / 2f), y + (height / 2f)),
            new Vector2(width / 2f, height / 2f),
            new Vector2(1f, 0f),
            new Vector3(from, 0f, to),
            GradientStops.Default,
            GradientShape.Linear,
            Via: false
        ) {
            Composite = composite
        };

    /// <summary>A round ramp from the centre of a box outwards.</summary>
    static UiMask Round(float x, float y, float width, float height, float from, float to) =>
        new(
            new Vector2(x + (width / 2f), y + (height / 2f)),
            new Vector2(width / 2f, height / 2f),
            Vector2.Zero,
            new Vector3(from, 0f, to),
            GradientStops.Default,
            GradientShape.Radial,
            Via: false
        );

    static void Pop(DrawList list, bool isolate) {
        if (isolate) {
            list.Add(new(DrawCommandKind.LayerPop, 0, 0, 0, 0, Color4.White, 0, 0));
        }
    }

    /// <summary>A child's colour: untouched inside a group, and pre-faded when there is no group.</summary>
    static Color4 Fade(Color4 colour, bool isolate, float alpha) =>
        isolate ? colour : new Color4(colour.R, colour.G, colour.B, colour.A * alpha);

    /// <summary>One run of glyphs, positioned along the run rather than on the surface.</summary>
    static void Text(DrawList list, FontFace font, string text, float x, float y, Color4 colour) {
        const float Size = 26f;

        var glyphs = new List<PositionedGlyph>();
        var pen = 0f;

        foreach (var character in text) {
            glyphs.Add(new(font.GlyphFor(character), pen, 0));
            pen += 24f;
        }

        list.Add(
            new DrawCommand(DrawCommandKind.Text, x, y, pen, Size, colour, 0, 0) {
                Offset = list.AddGlyphs(glyphs),
                Length = glyphs.Count,
                Font = list.AddFont(font),
                FontSize = Size
            }
        );
    }

    static FontFace? loaded;

    static FontFace Font() {
        if (loaded is not null) {
            return loaded;
        }

        using var stream = typeof(UiCompositingTests).Assembly
                               .GetManifestResourceStream("Vixen.Graphics.Golden.Tests.TestShapeLana.ttf")
                           ?? throw new InvalidOperationException("no test font is embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        loaded = FontFace.Load(memory.ToArray(), name: "golden");

        return loaded;
    }

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    static bool TryOpen(out Fixture? fixture, out string? reason) {
        if (Fixture.TryOpen(out fixture, out reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the golden images may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }
}
