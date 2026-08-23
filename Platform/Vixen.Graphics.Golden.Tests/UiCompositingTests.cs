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
    ///         renders the passes in <i>reverse</i> pre-order so that a group's children are finished
    ///         before the pass that samples them. Those are the same order only when there is
    ///         something nested to order.
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
        Assert.Equal(2, geometry.Layers.Count);

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
                renderer.Compose(commands, geometry, new Int2(Side, Side));
            }
        );

        // The instrument, before the measurement. See the class remarks.
        Assert.Equal(2, renderer.Composited);

        // ⚠ <b>And the second instrument, because a blur has three separate ways of not happening
        // and all of them draw a correct sharp picture.</b> No blur stage handed over, no
        // `UiLayer.Blur` on the geometry, a `KernelRadius` that came out zero — each leaves
        // `Composited` at two and the comparison below passing, since the software renderer would
        // then be being compared against a device that agreed with it about doing nothing.
        Assert.Equal(1, renderer.Blurred);

        // ⚠ <b>And the third, because a colour matrix has a fourth way of not happening that a blur
        // does not: the frame can be <i>identical</i> without it.</b> A blur that did not run leaves a
        // sharp picture, which is at least a different picture; a matrix that did not run leaves the
        // right picture wherever the group's colours happen to be near the matrix's fixed points.
        // Both of these are chosen not to be — see `Groups` — but the assertion is what makes that a
        // fact rather than a hope, and it is also the only thing that would notice a host handing
        // over no `UiShaders.Colour`, which composites through the image pipeline and says nothing.
        //
        // ⚠ Two, and the inner one is submitted inside the *outer group's* pass rather than the
        // frame's. That is the half of the count `Record` alone could not see.
        Assert.Equal(2, renderer.Filtered);

        // ⚠ <b>And the fourth instrument, for the reason the third one gives and one more.</b> A mask
        // shares all four of a colour matrix's ways of not happening, and it adds a fifth that is
        // peculiar to the two-in-one pipeline: `maskPipeline` serves masked groups *and* carries the
        // matrix, so a `SubmitDraw` that preferred `colourPipeline` would draw both of these groups
        // correctly filtered and entirely unmasked — and `Filtered` above would still read two. This
        // is the only assertion that separates those two states.
        Assert.Equal(2, renderer.Masked);

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

        Assert.Equal(2, withGroups.Layers.Count);
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

        // ⚠ <b>A mask on the outer group, and it runs to nearly nothing at the far edge on purpose.</b>
        // A mask that only dimmed the group slightly would be a difference the tolerance could
        // absorb; a ramp with real dynamic range across the group's own box is what makes a
        // disagreement about *where* the mask is resolved show up as pixels rather than as rounding.
        Push(list, isolate, 8, 24, 112, 96, Outer, filter: OuterFilter, mask: Ramp(8, 24, 112, 96, 1f, 0.1f));

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
        // the reverse-pre-order walk exists to keep straight, and a blur on the outermost group would
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
        Push(list, isolate, 44, 56, 72, 60, Inner, InnerBlur, InnerFilter, Round(44, 56, 72, 60, 1f, 0.15f));

        // The nested group's own overlapping pair, offset from the outer one's so that the two
        // groups' ink is not the same rectangle — a surface sized from the wrong group's bounds
        // would then be indistinguishable.
        var nested = isolate ? Inner : Inner * Outer;

        list.Add(new(DrawCommandKind.Rectangle, 48, 60, 40, 36, Fade(new Color4(0.3f, 0.9f, 0.4f, 1f), isolate, nested), 8, 0));
        list.Add(new(DrawCommandKind.Rectangle, 70, 74, 40, 36, Fade(new Color4(0.95f, 0.85f, 0.2f, 1f), isolate, nested), 8, 0));

        Text(list, font, "C", 52, 104, Fade(Color4.White, isolate, nested));

        Pop(list, isolate);
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
        UiMask? mask = null
    ) {
        if (isolate) {
            list.Add(
                new DrawCommand(DrawCommandKind.LayerPush, x, y, width, height, new Color4(1f, 1f, 1f, alpha), 0, 0) {
                    Blur = blur,
                    Filter = filter,
                    Mask = mask
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
    static UiMask Ramp(float x, float y, float width, float height, float from, float to) =>
        new(
            new Vector2(x + (width / 2f), y + (height / 2f)),
            new Vector2(width / 2f, height / 2f),
            new Vector2(1f, 0f),
            new Vector3(from, 0f, to),
            GradientStops.Default,
            GradientShape.Linear,
            Via: false
        );

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
