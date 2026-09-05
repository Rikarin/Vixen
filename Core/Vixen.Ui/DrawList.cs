// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text;

namespace Vixen.Ui;

/// <summary>What a draw command draws.</summary>
public enum DrawCommandKind : byte {
    /// <summary>A filled rectangle, with optional rounded corners.</summary>
    Rectangle,

    /// <summary>An outline drawn inside a rectangle's edges.</summary>
    Border,

    /// <summary>A blurred rectangle, drawn behind the element that cast it.</summary>
    /// <remarks>
    ///     The same quad and the same shader as <see cref="Rectangle" />, with a blur radius that
    ///     turns the distance field's one-pixel edge into a soft one. It is a separate kind rather
    ///     than a flag because the geometry differs: a shadow's quad is larger than the box it is
    ///     cast by, and the blur has to reach the edge of it.
    /// </remarks>
    Shadow,

    /// <summary>A run of positioned glyphs in one font.</summary>
    Text,

    /// <summary>A texture, stretched over a rectangle.</summary>
    /// <remarks>
    ///     An image, a video frame, a viewport's render target. What the texture <i>is</i> belongs to
    ///     the renderer — see <see cref="DrawCommand.Image" /> for why this layer names it with a
    ///     number rather than a handle.
    /// </remarks>
    Image,

    /// <summary>The inside of a path.</summary>
    Path,

    /// <summary>A line along a path.</summary>
    PathStroke,

    /// <summary>The inside of a small path, drawn from a distance field instead of tessellated.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same picture as <see cref="Path" />, and a completely different cost.</b> An
    ///         icon is a glyph that is not in a font: small vector art, one colour at a time, asked for
    ///         again every frame. Text has been drawn from an atlased distance field since there was
    ///         text here — four vertices whatever the shape — and a path drawn this way is four too,
    ///         where tessellating a hand-drawn editor glyph measured 1,611.
    ///     </para>
    ///     <para>
    ///         A kind of its own rather than a flag, because it decides the <i>pipeline</i>: it batches
    ///         with the text around it and is drawn by the field shader, where a filled path is drawn
    ///         by the solid one. See <c>IconAtlas</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A request rather than an instruction.</b> Whoever emits one is claiming the path is
    ///         small vector art, which is a claim only a control can make; whether it <i>fits</i> is
    ///         the atlas's answer, and a path it refuses is tessellated instead and draws identically.
    ///         So this is always safe to ask for and never guaranteed.
    ///     </para>
    /// </remarks>
    Field,

    /// <summary>Everything after this is clipped to a rectangle, until the matching pop.</summary>
    ClipPush,

    /// <summary>Ends the clip the last push started.</summary>
    ClipPop,

    /// <summary>Everything after this is drawn into a surface of its own, until the matching pop.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A group, which is a different thing from a clip even though it brackets the same
    ///         way.</b> CSS Compositing 1 § 3 renders a subtree that needs one into an isolated
    ///         surface and composites the result <i>once</i>; a clip only narrows the scissor. The
    ///         difference is visible the moment the subtree overlaps itself — two overlapping children
    ///         of a half-opaque panel do not show through each other, where fading each of them
    ///         separately makes them do exactly that.
    ///     </para>
    ///     <para>
    ///         The command's rectangle is the element's border box and its <c>Color</c>'s alpha is the
    ///         group's opacity. The rectangle is <i>informational</i>: what a layer actually needs is
    ///         the bounds of everything drawn inside it, which is not known until the geometry is
    ///         emitted, so <see cref="Rendering.UiGeometryBuilder" /> computes it there. Carried anyway
    ///         because a filter — see the guide — needs the box rather than the ink.
    ///     </para>
    /// </remarks>
    LayerPush,

    /// <summary>Ends the layer the last push started, and composites it.</summary>
    LayerPop
}

/// <summary>One thing to draw, in document space.</summary>
/// <remarks>
///     <para>
///         <b>One flat struct rather than a class per primitive</b>, with the fields a given kind
///         does not use left at zero. A draw list is walked once per frame in order and never
///         polymorphically dispatched on; a hierarchy would cost a pointer chase and an allocation
///         per command to model something the consumer answers with a <c>switch</c> anyway.
///     </para>
///     <para>
///         Comparable by value, which is what makes the frame diff a memcmp rather than a visitor.
///     </para>
/// </remarks>
/// <param name="Kind">What it draws.</param>
/// <param name="X">Its left edge in document space.</param>
/// <param name="Y">Its top edge.</param>
/// <param name="Width">Its width.</param>
/// <param name="Height">Its height.</param>
/// <param name="Color">Its colour, in linear space. Unused by the clip commands.</param>
/// <param name="Radius">Its corner radius. Zero for square corners.</param>
/// <param name="Thickness">A border's width. Zero for the other kinds.</param>
public readonly record struct DrawCommand(
    DrawCommandKind Kind,
    float X,
    float Y,
    float Width,
    float Height,
    Color4 Color,
    float Radius,
    float Thickness
) {
    /// <summary>Where this command's data starts in the side buffer its kind uses.</summary>
    /// <remarks>
    ///     <para>
    ///         Some things to draw are not a fixed number of numbers. A run of glyphs is as long as
    ///         its text, so it lives in <see cref="DrawList.Glyphs" /> and the command names a range
    ///         of it — which keeps the command a flat comparable struct and keeps the variable-length
    ///         part in one contiguous array a renderer can upload in a single copy.
    ///     </para>
    ///     <para>
    ///         Init-only rather than positional so that the kinds that use nothing here are still
    ///         written as the eight arguments they always were. They are still fields, so they are
    ///         still part of the value equality the frame diff is built on.
    ///     </para>
    /// </remarks>
    public int Offset { get; init; }

    /// <summary>How many entries of the side buffer this command uses.</summary>
    public int Length { get; init; }

    /// <summary>Which font, as an index into <see cref="DrawList.Fonts" />.</summary>
    /// <remarks>
    ///     An index rather than the face itself, so that the command stays a struct with no reference
    ///     to trace and so that batching can compare fonts with an integer. Only meaningful for
    ///     <see cref="DrawCommandKind.Text" />; zero and unread for everything else, like the other
    ///     fields a kind does not use.
    /// </remarks>
    public int Font { get; init; }

    /// <summary>Which texture, as the renderer's own name for one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Opaque on purpose, exactly as <c>Viewport.RenderTarget</c> is.</b> A texture view
    ///         is <c>Vixen.Graphics</c>' vocabulary and this assembly does not reference it — the
    ///         whole bargain <c>Vixen.Ui</c> makes is that it describes what to draw and knows nothing
    ///         about how. A renderer registers a number for a texture it owns and is handed the number
    ///         back; nothing in between has to know what it stands for.
    ///     </para>
    ///     <para>
    ///         Zero is "no image", which is what a command of any other kind carries.
    ///     </para>
    /// </remarks>
    public ulong Image { get; init; }

    /// <summary>Which part of the texture to draw, as UVs from the top-left.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero-to-one and not pixels</b>, because the command does not know how big the texture
    ///     is — only the renderer does. A sub-rectangle is how a sprite sheet, a nine-slice and a
    ///     flipped video frame are all expressed, and the default covers the whole texture.
    /// </remarks>
    public Rectangle Source { get; init; } = new(0f, 0f, 1f, 1f);

    /// <summary>How the destination is cut for a nine-slice, in document pixels.</summary>
    /// <remarks>
    ///     <para>
    ///         Empty — the default — draws the image as one stretched quad, which is what every
    ///         image was before there was a nine-slice. Anything else cuts this command's rectangle
    ///         into nine and draws each cell from the matching cell of <see cref="Source" />, so a
    ///         panel's corners keep their size at any box size while its edges and middle stretch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not a new command kind, and that is the point.</b> A nine-sliced image is the same
    ///         texture through the same pipeline as a stretched one, so it carries the same
    ///         <see cref="DrawCommandKind.Image" /> and batches with the images around it — nine quads
    ///         in a run rather than a draw of its own. Making it a kind would have split every batch
    ///         it appeared in, to describe geometry the renderer never sees.
    ///     </para>
    /// </remarks>
    public NineSlice Slice { get; init; }

    /// <summary>How <see cref="Source" /> is cut to match <see cref="Slice" />, in UVs.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second inset rather than the same one, because the two are in different spaces
    ///         and neither can be derived from the other here.</b> The destination is in document
    ///         pixels and the source is in UVs, and converting between them needs the texture's size
    ///         in texels — which this assembly does not know, for exactly the reason <see cref="Source" />
    ///         is not in pixels either. Whoever registered the texture converts.
    ///     </para>
    ///     <para>
    ///         Empty falls back to one stretched quad even when <see cref="Slice" /> is set: a source
    ///         with no border to preserve is a plain image, and cutting it anyway would smear
    ///         zero-width strips of it along eight of the nine cells.
    ///     </para>
    /// </remarks>
    public NineSlice SourceSlice { get; init; }

    /// <summary>Whether a nine-slice leaves its middle cell undrawn.</summary>
    /// <remarks>
    ///     ⚠ <b>Inverted from how it reads, because a struct's default is all-zeroes.</b> Every other
    ///     framework spells this "fill centre" and defaults it to true; a <c>bool</c> that defaulted
    ///     to false under that name would make the ordinary nine-slice — a panel with a background —
    ///     the one a caller had to opt into, and a hollow frame the one they got by saying nothing.
    ///     The same argument <see cref="MiterLimit" /> makes about sentinels, answered by naming the
    ///     unusual case instead.
    /// </remarks>
    public bool HollowCentre { get; init; }

    /// <summary>The font size in pixels, which is what scales a glyph's outline.</summary>
    /// <remarks>
    ///     Carried even though the glyph positions are already in pixels, because a position is not a
    ///     size: the renderer still has to know how big to draw the glyph it has been told where to
    ///     put.
    /// </remarks>
    public float FontSize { get; init; }

    /// <summary>
    ///     Whether this box has a <see cref="BoxStyle" /> in <see cref="DrawList.Boxes" />, and where.
    /// </summary>
    /// <remarks>
    ///     ⚠ A box's side buffer holds at most one entry, so <see cref="Length" /> is zero or one —
    ///     which is the same <c>Offset</c>/<c>Length</c> pair a glyph run and a path use, meaning the
    ///     same thing, rather than a third convention for the one kind that needs a single record.
    ///     Zero means a plain box: a flat colour and the uniform <see cref="Radius" />.
    /// </remarks>
    public bool HasStyle => Length > 0;

    /// <summary>How a filled path decides what is inside it.</summary>
    /// <remarks>Only meaningful for <see cref="DrawCommandKind.Path" />.</remarks>
    public PathFillRule FillRule { get; init; }

    /// <summary>How a stroked path turns a corner.</summary>
    /// <remarks>
    ///     Only meaningful for <see cref="DrawCommandKind.PathStroke" />. On the command rather than
    ///     on whatever tessellates it, because a join is part of the stroke somebody asked for — the
    ///     same argument as <see cref="Thickness" />, which nobody would have put anywhere else.
    /// </remarks>
    public LineJoin Join { get; init; }

    /// <summary>How a stroked path's open ends are finished.</summary>
    public LineCap Cap { get; init; }

    /// <summary>How far a miter may reach, as a multiple of the half width.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero means the default</b>, which is four — CSS's and SVG's. A sentinel rather than a
    ///     real value because this is a struct: its default is all-zeroes, and a miter limit of zero
    ///     means every corner bevels, so a caller who set the thickness and nothing else would get a
    ///     shape with no corners at all. <see cref="Radius" /> gets away with a real zero because a
    ///     square corner is a sensible default; this does not.
    /// </remarks>
    public float MiterLimit { get; init; }

    /// <summary>
    ///     How far a composited group's surface is blurred, as a Gaussian standard deviation in
    ///     document pixels. Zero and unread on every kind but <see cref="DrawCommandKind.LayerPush" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A field of its own rather than a ride in <see cref="Thickness" />, which is free
    ///         on a layer command and was the obvious place for it.</b> <see cref="Thickness" /> is
    ///         already a blur on a <see cref="DrawCommandKind.Shadow" /> — a <i>half-extent</i>, half
    ///         the CSS length, because that is what the box shader's falloff wants — and
    ///         <c>filter: blur()</c> is a standard deviation, the full CSS length. Two different
    ///         conventions in one field is a factor of two nothing would report, on the one kind
    ///         where the reader has to remember which it is holding.
    ///     </para>
    ///     <para>
    ///         Init-only for the reason every field here is: a caller emitting a layer for
    ///         <c>opacity</c> alone is entitled to say nothing about a filter, and the eight
    ///         positional arguments stay eight.
    ///     </para>
    /// </remarks>
    public float Blur { get; init; }

    /// <summary>
    ///     How a composited group's result is mixed with what is under it. <see cref="UiBlendMode.Normal" />
    ///     and unread on every kind but <see cref="DrawCommandKind.LayerPush" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>On the layer bracket and <i>not</i> on the commands inside it, which is the whole
    ///         of what <c>mix-blend-mode</c> means and the mistake this field is shaped to refuse.</b>
    ///         CSS Compositing 1 § 5.1 blends an element's <i>rendered result</i> with its backdrop —
    ///         so a bordered element's background, its border and its text composite source-over with
    ///         each other first, and only the finished group blends. A blend carried on each command
    ///         would multiply the border into the background instead, and every bordered element has
    ///         two commands, so the two are told apart by the commonest element there is. This is the
    ///         same argument <see cref="DrawCommandKind.LayerPush" />'s own remark makes for opacity,
    ///         arriving at the same seam.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which makes a blend the <i>sixth</i> reason to open a group</b>, and the first one
    ///         that is a function of two pictures rather than one — see <see cref="Rendering.UiBlend" />.
    ///     </para>
    /// </remarks>
    public UiBlendMode Blend { get; init; }

    /// <summary>
    ///     The colour transform a composited group's <c>filter</c> applies to its surface, or null
    ///     where there is none. Unread on every kind but <see cref="DrawCommandKind.LayerPush" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nullable, and it is the one field on this struct where the default is not the
    ///         absence.</b> <see cref="Blur" /> can be a plain <c>float</c> because a zero-width
    ///         Gaussian is the identity, and <see cref="MiterLimit" /> gets a sentinel because zero
    ///         is a real value with an absurd meaning. A zeroed <see cref="UiColorMatrix" /> is worse
    ///         than either: it maps every colour to black, so a command that said nothing about a
    ///         filter would silently ask for one. See <c>UiColorMatrix</c>'s own remark.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Beside <see cref="Blur" /> rather than composed with it, because the two are
    ///         executed by different machinery and only one of them moves ink.</b> A blur needs a
    ///         second surface, two passes and a bounds outset; a colour matrix needs none of the
    ///         three — see <c>UiRenderer.Compose</c>, where it rides the composite draw the group was
    ///         going to make anyway. Folding them into one "filter" field would have made the cheap
    ///         one look like it costs what the expensive one costs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Order between the two is not carried, and that is a fact about the arithmetic
    ///         rather than a simplification.</b> CSS's <c>filter</c> is ordered and
    ///         <c>DrawListBuilder</c> honours the order <i>within</i> the colour functions, where it
    ///         matters. It does not matter between a colour matrix and a Gaussian: both are linear in
    ///         premultiplied colour with weights that sum to one, so
    ///         <c>M(Σ wᵢ sᵢ) = Σ wᵢ M(sᵢ)</c> exactly, and <c>grayscale(1) blur(4px)</c> and
    ///         <c>blur(4px) grayscale(1)</c> are the same picture. <c>UiColorMatrix.Apply</c>'s clamp
    ///         is the one part that does not commute, and it is applied once, at the end, on both
    ///         executors.
    ///     </para>
    /// </remarks>
    public UiColorMatrix? Filter { get; init; }

    /// <summary>
    ///     The shadow a composited group's <c>filter: drop-shadow()</c> casts from its own alpha, or
    ///     null where there is none. Unread on every kind but <see cref="DrawCommandKind.LayerPush" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nullable for <see cref="Filter" />'s reason and a stronger one.</b> A zeroed
    ///         <see cref="UiDropShadow" /> is a shadow at no offset with no blur in transparent black
    ///         — which happens to paint nothing, so the default is <i>harmless</i> here where a zeroed
    ///         matrix is not. It is still nullable, because "no shadow" and "a shadow that came out
    ///         invisible" are answers <c>DrawListBuilder.Settle</c> has to be able to give
    ///         separately: the second is what every element carrying an assembled <c>filter</c> reads,
    ///         and collapsing the two would leave a group open for a function nobody wrote.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Beside <see cref="Blur" /> and not folded into it, and the order between them is
    ///         <i>not</i> free the way <see cref="Filter" />'s is.</b> That field's last remark turns
    ///         on a colour matrix and a Gaussian commuting exactly. A Gaussian and a
    ///         <i>shadow</i> do not: the alpha channel is blurred twice one way round and once the
    ///         other. Both executors run the shadow over the group's finished surface, after its own
    ///         blur, which is the order <c>UtilityComposition.Filter</c> assembles — see
    ///         <see cref="UiDropShadow" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing about it rides <see cref="DrawCommandKind.Shadow" />.</b> That kind is a
    ///         blurred rounded rectangle the shape of the border box, resolved analytically by
    ///         <c>ui-box.frag</c>; this is a Gaussian over whatever coverage the subtree actually
    ///         rasterised, which is a different picture wherever the group is not a plain filled box
    ///         — text, an icon, a masked child. Two names for one word, in two fields, on purpose.
    ///     </para>
    /// </remarks>
    public UiDropShadow? Shadow { get; init; }

    /// <summary>
    ///     What a composited group's <c>backdrop-filter</c> does to the picture behind it, or null
    ///     where there is none. Unread on every kind but <see cref="DrawCommandKind.LayerPush" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Beside <see cref="Filter" /> and pointed at a different picture, which is the one
    ///         thing about it that has to be understood before anything else.</b> That field transforms
    ///         what the group <i>drew</i>; this transforms what is <i>behind</i> the group. They are
    ///         independent — <c>filter: grayscale(1) </c> and <c>backdrop-filter: blur(8px)</c> on one
    ///         element is a grey panel over a blurred scene — so they are two fields and not one, and a
    ///         consumer reading either as the other draws a picture that is wrong in a way no amount of
    ///         inspecting the draw list reveals.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nullable for <see cref="Filter" />'s reason.</b> A zeroed <see cref="UiBackdrop" />
    ///         has an <see cref="UiBackdrop.Alpha" /> of zero, which erases the backdrop rather than
    ///         leaving it alone — so the default of the struct is emphatically not the absence of the
    ///         feature, and only the null is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The element's own box is read off this command's rectangle and not carried
    ///         again.</b> A <see cref="DrawCommandKind.LayerPush" /> is emitted at the element's border
    ///         box, which is exactly the rectangle CSS clips a backdrop filter to —
    ///         <c>UiGeometryBuilder.Layer</c> takes it from there into
    ///         <see cref="Vixen.Ui.Rendering.UiLayer.BackdropBounds" />. The group's <i>ink</i> bounds
    ///         are a different rectangle and are the wrong one; see that member.
    ///     </para>
    /// </remarks>
    public UiBackdrop? Backdrop { get; init; }

    /// <summary>
    ///     The affine a composited group's <c>rotate</c> and <c>scale</c> place its surface under, or
    ///     null where there is none. Unread on every kind but <see cref="DrawCommandKind.LayerPush" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>On the group and not on each command, which is what makes a transform representable
    ///         at all.</b> A <see cref="DrawCommand" /> is an axis-aligned rectangle and there is no
    ///         rotating one — that is not an omission to be fixed, it is what the kinds mean, and it is
    ///         why this was refused for as long as there was nowhere else to put it. A composited group
    ///         gives it somewhere else: the subtree rasterises into a surface at exactly the
    ///         coordinates it always had, every command in it still axis-aligned and every clip in it
    ///         still a rectangle, and the matrix moves the four vertices of the <i>composite</i>. CSS
    ///         agrees that this is the right seam — Transforms 1 § 3 makes any transform other than
    ///         <c>none</c> a stacking context, in the sentence shape Filter Effects uses for
    ///         <c>filter</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Absolute, with <c>transform-origin</c> already folded in</b>, so that neither this
    ///         type nor its consumers carry a second opinion about where an element turns about. See
    ///         <see cref="UiTransform" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nullable for <see cref="Filter" />'s reason, and the default is worse here than
    ///         there.</b> A zeroed <see cref="UiTransform" /> collapses the group to a point, so a
    ///         consumer that read a default as "no transform" would draw nothing at all rather than
    ///         something slightly wrong.
    ///     </para>
    /// </remarks>
    public UiTransform? Transform { get; init; }

    /// <summary>
    ///     The coverage a composited group's <c>mask-image</c> multiplies its surface by, as a range
    ///     of <see cref="DrawList.Masks" />. Unread on every kind but
    ///     <see cref="DrawCommandKind.LayerPush" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A range of the side buffer and not a field, because <c>mask-image</c> is a
    ///         <i>list</i>.</b> Nine of Tailwind's mask roots need one entry and twelve more —
    ///         <c>mask-t-from-*</c> and its siblings — need up to four, composed by
    ///         <c>mask-composite</c>. A single nullable field could hold the first group and could
    ///         not hold the second, and widening it to a collection would have cost the frame diff
    ///         its value comparison: see <see cref="DrawList.Masks" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="Offset" /> and <see cref="Length" /> rather than a pair of their own,
    ///         which is the convention those two already state.</b> A <c>LayerPush</c> has no glyphs,
    ///         no path and no box style, so its side buffer is unambiguously this one — the same way
    ///         a box's is <see cref="DrawList.Boxes" /> and a text run's is
    ///         <see cref="DrawList.Glyphs" />. A third pair would have widened every command in the
    ///         frame to describe a kind that is a handful of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty and not a zeroed <see cref="UiMask" />, which is the same trap the old
    ///         nullable field guarded.</b> A zeroed mask has zero coverage at every stop, so a
    ///         command that said nothing about a mask would ask for the element to be erased
    ///         entirely — a blank rectangle, which is much easier to mistake for a layout bug than
    ///         the black one a zeroed colour matrix gives.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Beside <see cref="Filter" /> rather than a member of it, because a mask is not a
    ///         filter in CSS and does not compose like one.</b> <c>filter</c> is an ordered list
    ///         whose colour functions fold into one matrix; <c>mask-image</c> is a separate property
    ///         applied to the <i>result</i> of the filter. The order between them is fixed by the
    ///         specification rather than by arithmetic, which is the opposite of the situation between
    ///         <see cref="Blur" /> and <see cref="Filter" /> — see that field's last remark, whose
    ///         commutation argument does <b>not</b> extend here. A mask varies with position, so it
    ///         passes through neither a Gaussian nor a bilinear tap; <see cref="UiMask" /> spells out
    ///         the consequence, which is that both executors apply it at the composite draw.
    ///     </para>
    /// </remarks>
    public bool HasMask => Kind == DrawCommandKind.LayerPush && Length > 0;
}

/// <summary>A frame's worth of drawing, and whether it differs from the last one.</summary>
/// <remarks>
///     <para>
///         Doc 09 asks for the list to be diffed at the <i>command</i> level so that a static user
///         interface re-submits a cached command buffer instead of rebuilding one. That is what
///         <see cref="Version" /> is: it changes when the drawing changes and not when the drawing
///         is merely rebuilt, so a renderer can compare one integer instead of a list.
///     </para>
///     <para>
///         ⚠ The comparison is against the <i>previous content</i>, not against a dirty flag. A flag
///         says what the framework believes changed; this says what actually did — and the two part
///         company exactly when something is invalidated too eagerly, which is the failure a cache
///         is supposed to absorb rather than propagate.
///     </para>
/// </remarks>
public sealed class DrawList {
    // ⚠ <b>Not `readonly`, and the five pairs below are a double buffer rather than a list and a copy
    // of it.</b> `BeginFrame` swaps the references; see it for why the copy it used to make was a
    // memcpy of the whole finished frame that no allocation counter could see.
    List<DrawCommand> commands = [];
    List<DrawCommand> previous = [];
    List<PositionedGlyph> glyphs = [];
    List<PositionedGlyph> previousGlyphs = [];
    List<PathSegment> segments = [];
    List<PathSegment> previousSegments = [];
    List<BoxStyle> boxes = [];
    List<BoxStyle> previousBoxes = [];
    List<UiMask> masks = [];
    List<UiMask> previousMasks = [];
    readonly List<FontFace> fonts = [];
    readonly List<DrawBatch> batches = [];

    /// <summary>The commands, in the order they are drawn.</summary>
    public IReadOnlyList<DrawCommand> Commands => commands;

    /// <summary>Every glyph of every text command, back to back.</summary>
    /// <remarks>
    ///     One array rather than one per run, because a renderer uploads it whole and because a run
    ///     of eight glyphs is not worth an allocation of its own several hundred times a frame.
    /// </remarks>
    public IReadOnlyList<PositionedGlyph> Glyphs => glyphs;

    /// <summary>Every step of every path, back to back.</summary>
    public IReadOnlyList<PathSegment> Segments => segments;

    /// <summary>The styles of the boxes that needed one.</summary>
    /// <remarks>
    ///     Only the boxes that are more than a colour, a size and one radius are in here, which in a
    ///     real interface is a small minority of them.
    /// </remarks>
    public IReadOnlyList<BoxStyle> Boxes => boxes;

    /// <summary>Every mask of every composited group, back to back.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A side buffer rather than a field on the command, and the reason is the frame
    ///         diff rather than the size of the struct.</b> <c>mask-image</c> is a list, so the
    ///         obvious shape is a collection on <see cref="DrawCommand" /> — and a collection there
    ///         would be compared by reference, so a frame that rebuilt an identical list would count
    ///         as a change and <see cref="Version" /> would rise every single frame. That is the one
    ///         thing the version exists not to do. In the side buffer the entries are compared the
    ///         way the glyphs and the box styles are: element by element, by value.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>CSS order, topmost layer first, exactly as written.</b> The fold that turns a
    ///         range of this into one coverage runs bottom-up — see <see cref="UiMask.Coverage(System.ReadOnlySpan{UiMask},Vixen.Core.Mathematics.Vector2)" /> —
    ///         and reversing the entries here instead would put the reversal somewhere the two
    ///         executors could disagree about.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<UiMask> Masks => masks;

    /// <summary>The faces the text commands refer to, in the order they were first used.</summary>
    public IReadOnlyList<FontFace> Fonts => fonts;

    /// <summary>The commands grouped into runs a renderer can submit together.</summary>
    /// <remarks>
    ///     A partition of <see cref="Commands" />: every command is in exactly one batch, in order,
    ///     so a consumer walks this alone.
    /// </remarks>
    public IReadOnlyList<DrawBatch> Batches => batches;

    /// <summary>How many times the batches have been rebuilt.</summary>
    /// <remarks>
    ///     Exposed for the same reason <c>UiDocument.StylesApplied</c> is: the claim is that a frame
    ///     that drew the same thing does no batching work, and a claim about work avoided that cannot
    ///     be measured is one nobody can check.
    /// </remarks>
    public int Batched { get; private set; }

    /// <summary>Bumped whenever the commands differ from the previous frame's.</summary>
    public int Version { get; private set; }

    /// <summary>Whether the last <see cref="EndFrame" /> changed anything.</summary>
    public bool ChangedLastFrame { get; private set; }

    /// <summary>Starts collecting a frame.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A swap and not a copy, and the copy it replaces was the largest piece of work in a
    ///         frame that does nothing.</b> Keeping the finished frame for <see cref="EndFrame" /> to
    ///         compare against used to be <c>previous.Clear(); previous.AddRange(commands)</c> five
    ///         times over — and a <see cref="DrawCommand" /> is 320 bytes, so the editor shell's 1 389
    ///         commands were a 444 KB <c>memcpy</c> every frame, plus 33 KB for its 1 181
    ///         <see cref="PathSegment" />s, on a <i>settled</i> frame that changed nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Invisible to the gate that ought to have caught it, which is why it survived.</b>
    ///         <c>A_settled_frame_allocates_nothing</c> measures bytes allocated, and after the first
    ///         growth the copy lands in capacity that already exists — so it allocates nothing and
    ///         costs half a megabyte of memory traffic. This repository's own rule is to state a cost
    ///         as <i>work</i> rather than as milliseconds, and the work here is now O(1).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the swap costs is that the object behind <see cref="Commands" /> alternates
    ///         between two instances, where it used to be stable for the life of the list.</b> That is
    ///         sound only because nothing outside this class holds one of those properties across a
    ///         frame boundary — every consumer in the tree indexes it through a <see cref="DrawList" />
    ///         it was handed, and a stored reference would silently read the previous frame on every
    ///         other frame. It is asserted rather than assumed: see
    ///         <c>DrawListTests.The_frame_is_the_frame_on_both_parities_of_the_frame_counter</c>, which
    ///         is a fixture a single-frame one cannot replace.
    ///     </para>
    /// </remarks>
    public void BeginFrame() {
        Swap(ref previous, ref commands);
        Swap(ref previousGlyphs, ref glyphs);
        Swap(ref previousSegments, ref segments);
        Swap(ref previousBoxes, ref boxes);
        Swap(ref previousMasks, ref masks);

        // The fonts are not kept for comparison, because a command referring to a different face
        // refers to it by a different index and the commands are compared. Rebuilt each frame so
        // that a face nothing draws with any more is not held alive by the list that stopped using
        // it.
        fonts.Clear();
    }

    /// <summary>Exchanges a buffer with its previous-frame twin and empties it for this frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The <see cref="List{T}.EnsureCapacity" /> is not a micro-optimisation and dropping it
    ///     triples the one number this change was supposed to reduce.</b> The copy this replaced sized
    ///     its destination exactly once, because <c>AddRange</c> over a counted source asks for the
    ///     whole length in one allocation. A list filled by <c>Add</c> instead doubles its way up, so
    ///     the buffer swapped in on the second frame of a list's life allocates the geometric series
    ///     rather than the array — measured on the editor shell as 1 471 296 bytes where the copy cost
    ///     479 280. Sizing it to the frame before it gets the single allocation back, and is a no-op on
    ///     every frame after the first two.
    /// </remarks>
    static void Swap<T>(ref List<T> kept, ref List<T> collecting) {
        (kept, collecting) = (collecting, kept);
        collecting.Clear();
        collecting.EnsureCapacity(kept.Count);
    }

    /// <summary>How many commands the frame has so far.</summary>
    /// <remarks>
    ///     ⚠ <b>Live rather than final</b> — it is the count <i>during</i> a build, which is what makes
    ///     it useful: <see cref="DrawListBuilder" /> marks the position before a subtree and reads this
    ///     after it to find out how much the subtree drew. <see cref="Commands" /> answers the same
    ///     question afterwards; this one exists so that asking it mid-frame does not read as reaching
    ///     into the finished list.
    /// </remarks>
    public int Count => commands.Count;

    /// <summary>Adds a command.</summary>
    /// <param name="command">The command.</param>
    public void Add(DrawCommand command) => commands.Add(command);

    /// <summary>Undoes a group that turned out not to need one, fading its one command instead.</summary>
    /// <param name="push">Where the <see cref="DrawCommandKind.LayerPush" /> was added.</param>
    /// <param name="alpha">The group's opacity.</param>
    /// <remarks>
    ///     ⚠ <b>The peephole that keeps the common case free, and it is an identity rather than an
    ///     approximation.</b> Compositing a single premultiplied fragment <c>F</c> through a surface and
    ///     then blending that surface at <c>a</c> gives <c>a·F</c>; multiplying the one command's alpha
    ///     by <c>a</c> before it is premultiplied gives <c>(rgb·A·a·cov, A·a·cov)</c>, which is the same
    ///     <c>a·F</c>. So the two paths cannot disagree here — the group is only ever needed when there
    ///     are <i>two</i> fragments that might overlap.
    ///     <para>
    ///         It matters rather than being a nicety: <c>EditorTheme.vcss</c> puts <c>opacity: 0.18</c>
    ///         on every hidden-and-locked toggle in the outliner, and a tree of a hundred rows would
    ///         otherwise ask for two hundred offscreen surfaces and two hundred render passes a frame to
    ///         fade two hundred single icons.
    ///     </para>
    /// </remarks>
    internal void Collapse(int push, float alpha) {
        var only = commands[push + 1];
        commands[push + 1] = only with { Color = DrawListBuilder.Fade(only.Color, alpha) };
        commands.RemoveAt(push);
    }

    /// <summary>Drops a group that drew nothing at all.</summary>
    internal void Discard(int push) => commands.RemoveAt(push);

    /// <summary>Whether the one command a group produced can carry the fade on its own colour.</summary>
    /// <remarks>
    ///     ⚠ <b>The side buffer is what decides it, and only for the box kinds.</b> A box with a
    ///     <see cref="BoxStyle" /> behind it holds two more colours there — a gradient's far and middle
    ///     stops — so fading its <c>Color</c> alone would fade one end of a ramp and leave the other at
    ///     full strength. A glyph run's <c>Length</c> counts glyphs and a path's counts segments;
    ///     neither is a colour, so those fade on the command like everything else.
    /// </remarks>
    internal static bool Fadeable(in DrawCommand command) =>
        command.Kind is not (DrawCommandKind.Rectangle or DrawCommandKind.Border or DrawCommandKind.Shadow)
        || !command.HasStyle;

    /// <summary>Puts a run of glyphs in the side buffer.</summary>
    /// <param name="run">The glyphs.</param>
    /// <returns>Where they start, for the command that refers to them.</returns>
    public int AddGlyphs(List<PositionedGlyph> run) {
        ArgumentNullException.ThrowIfNull(run);

        var offset = glyphs.Count;
        glyphs.AddRange(run);

        return offset;
    }

    /// <summary>Puts a box's style in the side buffer.</summary>
    /// <param name="style">The style.</param>
    /// <returns>Where it went, for the command that refers to it.</returns>
    public int AddBox(BoxStyle style) {
        boxes.Add(style);
        return boxes.Count - 1;
    }

    /// <summary>Puts a group's mask list in the side buffer.</summary>
    /// <param name="list">The masks, topmost layer first.</param>
    /// <returns>Where they start, for the <see cref="DrawCommandKind.LayerPush" /> that refers to them.</returns>
    public int AddMasks(ReadOnlySpan<UiMask> list) {
        var offset = masks.Count;

        foreach (var mask in list) {
            masks.Add(mask);
        }

        return offset;
    }

    /// <summary>Puts a path in the side buffer.</summary>
    /// <param name="path">The path.</param>
    /// <returns>Where it starts, for the command that refers to it.</returns>
    public int AddPath(PathBuilder path) {
        ArgumentNullException.ThrowIfNull(path);

        var offset = segments.Count;
        segments.AddRange(path.Segments);

        return offset;
    }

    /// <summary>Finds or adds a font.</summary>
    /// <param name="font">The face.</param>
    /// <returns>Its index, for the command that draws with it.</returns>
    /// <remarks>
    ///     A linear search, because an interface uses a handful of faces and a dictionary would cost
    ///     a hash per text command to avoid comparing four pointers.
    /// </remarks>
    public int AddFont(FontFace font) {
        ArgumentNullException.ThrowIfNull(font);

        var index = fonts.IndexOf(font);
        if (index >= 0) {
            return index;
        }

        fonts.Add(font);
        return fonts.Count - 1;
    }

    /// <summary>Finishes a frame and works out whether anything moved.</summary>
    /// <returns>Whether the drawing differs from the previous frame's.</returns>
    public bool EndFrame() {
        ChangedLastFrame = Differs();

        if (ChangedLastFrame) {
            Version++;

            // Behind the diff rather than beside it. Batching is a walk of every command in the
            // interface, and a frame that drew the same thing has the same batches by construction —
            // so the cached command buffer the version exists to protect keeps its batches with it.
            DrawBatcher.Build(commands, batches);
            Batched++;
        }

        return ChangedLastFrame;
    }

    /// <summary>Whether this frame's commands differ from the last one's.</summary>
    /// <remarks>
    ///     A loop rather than <c>SequenceEqual</c>, because this runs once per frame over every
    ///     command in the interface and the LINQ form allocates two enumerators to do the same
    ///     comparison. The early exit on length is worth having for the same reason: a frame that
    ///     added an element does not need to compare the elements that did not change.
    /// </remarks>
    bool Differs() {
        if (commands.Count != previous.Count
            || glyphs.Count != previousGlyphs.Count
            || segments.Count != previousSegments.Count
            || boxes.Count != previousBoxes.Count
            || masks.Count != previousMasks.Count) {
            return true;
        }

        for (var i = 0; i < commands.Count; i++) {
            if (commands[i] != previous[i]) {
                return true;
            }
        }

        // ⚠ The side buffer has to be compared too, and it is not a formality. A command names a
        // range of it, so two frames whose text changed from one string to another of the same
        // length hold byte-identical commands and completely different glyphs — the label would
        // change and the version would not, and a renderer trusting the version would keep drawing
        // the old word.
        for (var i = 0; i < glyphs.Count; i++) {
            if (glyphs[i] != previousGlyphs[i]) {
                return true;
            }
        }

        // The same argument as the glyphs, and it bites harder: a control that animates a chart
        // emits the same command over the same range every frame and moves only the points.
        for (var i = 0; i < segments.Count; i++) {
            if (segments[i] != previousSegments[i]) {
                return true;
            }
        }

        // And once more for the box styles. A button whose gradient is being animated emits the same
        // command over the same range every frame and moves only the end colour — so a diff that read
        // the commands alone would report the frame unchanged and keep drawing the old gradient.
        for (var i = 0; i < boxes.Count; i++) {
            if (boxes[i] != previousBoxes[i]) {
                return true;
            }
        }

        // ⚠ And once more for the masks, which is the loop a `mask-image` being animated needs: a
        // group whose ramp is being tuned emits the same `LayerPush` over the same range every frame
        // and moves only the stop positions. Without this the version would say the frame had not
        // changed and a renderer trusting it would keep compositing yesterday's fade.
        for (var i = 0; i < masks.Count; i++) {
            if (masks[i] != previousMasks[i]) {
                return true;
            }
        }

        return false;
    }
}
