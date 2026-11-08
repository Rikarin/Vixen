// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Texturing.Layers;

/// <summary>Which of doc 48 § D10's four kinds a layer is.</summary>
public enum LayerKind {
    /// <summary>A constant, a texture or a graph, projected.</summary>
    Fill = 0,

    /// <summary>
    ///     Painted pixels, held in a <c>.vxpaint</c> beside the stack. <see cref="LayerStackGraph" />
    ///     compiles one to a bitmap source per channel, over a <c>vxpaint:</c> reference the host
    ///     resolves — <a href="https://github.com/Rikarin/Vixen/issues/852">#852</a>.
    /// </summary>
    Paint = 1,

    /// <summary>An adjustment over everything under it.</summary>
    Filter = 2,

    /// <summary>A stack with one mask, which is how twenty layers stay legible.</summary>
    /// <remarks>
    ///     ⚠ <b>A group whose <see cref="LayerAsset.Paint" /> names a canvas is <em>flattened</em></b> (Patina #107):
    ///     it composites that canvas, per channel, where its children's composite would go, and its children are kept,
    ///     unread, as the way back. The canvas is a cache of what <see cref="LayerStackGraph.Capture" /> evaluated when
    ///     it was flattened, so editing a child of a flattened group changes nothing until it is unflattened.
    /// </remarks>
    Group = 3,

    /// <summary>
    ///     Another layer, by reference: <see cref="LayerAsset.Instance" /> names it, in this set or another of the stack,
    ///     and <see cref="LayerStackInstances.Resolve" /> puts it in this one's place at compile (Patina #108).
    /// </summary>
    /// <remarks>
    ///     <b>What stays the instance's own</b> is its name, whether it is on, its opacity, its blend and the anchor point
    ///     it publishes; its own mask entries narrow the source's mask. Everything else — what the layer is, its channels,
    ///     its mask, its effects and a folder's layers — is the source's, so an edit to the source shows in every instance.
    /// </remarks>
    Instance = 4
}

/// <summary>How a fill layer's source is put onto the surface.</summary>
/// <remarks>
///     <para>
///         <b>All three compile</b> — <a href="https://github.com/Rikarin/Vixen/issues/815">#815</a>.
///         Triplanar and planar are a projection of a <em>world</em> position onto the UV atlas, so
///         they need the <c>position</c> and <c>world</c> mesh maps § D12 bakes and a node that reads
///         them; <c>Space/Triplanar</c> is that node and <c>LayerStackGraph.Project</c> is the
///         wiring. ⚠ These three members were modelled and two of them refused for two milestones,
///         which this remark used to say: keeping the enum whole while the compiler said no was what
///         let them land without rewriting every <c>.vxlayers</c> that exists.
///     </para>
///     <para>
///         ⚠ <b>A projection means something only on a <see cref="LayerKind.Fill" /> that is not a
///         constant</b>, and <c>LayerStackGraph.Project</c> warns rather than ignoring it anywhere
///         else: a paint layer's pixels are authored in the atlas, a filter reads whatever is under
///         it, and a constant is the same colour at every world position.
///     </para>
/// </remarks>
public enum LayerProjection {
    /// <summary>The mesh's own UVs — the atlas, one to one.</summary>
    Uv = 0,

    /// <summary>Three planar projections blended by the world normal.</summary>
    Triplanar = 1,

    /// <summary>
    ///     One planar projection along <see cref="LayerAsset.PlanarAxis" /> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1032">#1032</a>. ⚠ That axis used to be
    ///     nowhere in the file, so the compiler chose y for every planar layer that has ever been
    ///     authored: a valid planar projection along the wrong axis, which on anything box-shaped is
    ///     the same picture rotated and reads as a bug in the fill rather than as a missing member.
    /// </summary>
    Planar = 2,

    /// <summary>Wrapped around a sphere at the middle of the mesh's bounds.</summary>
    Spherical = 3,

    /// <summary>Wrapped around a cylinder along <see cref="LayerAsset.PlanarAxis" />.</summary>
    Cylindrical = 4
}

/// <summary>Where a fill sits on the surface, in whichever projection it uses.</summary>
/// <remarks>
///     ⚠ <b>Its own record, not entries in <see cref="LayerAsset.Settings" />, because a graph fill
///     hands its settings to the graph.</b> A graph with its own <c>Scale</c> parameter would otherwise
///     have one number doing two jobs. A <see cref="LayerProjection.Uv" /> fill reads everything here
///     but <see cref="Sharpness" />; a planar, spherical or cylindrical one reads the same.
/// </remarks>
public sealed record LayerPlacement {
    /// <summary>How many times the picture repeats. Above zero.</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>How far the picture turns, in radians.</summary>
    public float Rotation { get; init; }

    /// <summary>How far the picture moves along its first axis, in repeats.</summary>
    public float OffsetX { get; init; }

    /// <summary>How far the picture moves along its second axis, in repeats.</summary>
    public float OffsetY { get; init; }

    /// <summary>How hard the seam between a triplanar's planes is.</summary>
    public float Sharpness { get; init; } = 4f;
}

/// <summary>Which world axis a <see cref="LayerProjection.Planar" /> layer projects along.</summary>
/// <remarks>
///     <para>
///         <b>A member on <see cref="LayerAsset" /> rather than three more
///         <see cref="LayerProjection" /> members</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1032">#1032</a>. Splitting
///         <c>Planar</c> into <c>PlanarX</c>/<c>PlanarY</c>/<c>PlanarZ</c> renumbers an enum that
///         every <c>.vxlayers</c> in every project already writes by name, and the whole reason
///         those three members exist as they do — kept whole while two of them were refused — was to
///         avoid rewriting files later. A second member costs one optional key.
///     </para>
///     <para>
///         ⚠ <b>The default is <see cref="Y" /> and it is deliberately <em>not</em> this enum's
///         zero.</b> Y is what the compiler has always chosen and what a planar fill nearly always
///         wants — dirt, snow, dust on horizontal faces — so it has to survive as the default; but
///         writing it as the zero would make "the author said y" and "the author said nothing" the
///         same state, and that is the shape this repository loses features to. Both readers name
///         <see cref="Y" /> as their fallback explicitly instead, so an axis of <see cref="X" /> on a
///         layer that is not planar is a thing <c>LayerStackGraph.Project</c> can see and warn about.
///     </para>
///     <para>
///         The names are <c>Space/Triplanar</c>'s own <c>Axis</c> setting, which already takes
///         <c>X</c>, <c>Y</c>, <c>Z</c> and <c>Triplanar</c> — so nothing translates between the two
///         and a fourth axis would be one word in each file rather than a mapping table.
///     </para>
/// </remarks>
public enum LayerAxis {
    /// <summary>Down world x.</summary>
    X = 0,

    /// <summary>Down world y, the up axis. What every planar layer authored before #1032 has.</summary>
    Y = 1,

    /// <summary>Down world z.</summary>
    Z = 2
}

/// <summary>How a set's painted height becomes the normal its surface is lit with.</summary>
public enum LayerHeightNormal {
    /// <summary>The height's slope read a texel either side, so a hard brush edge is a hard crease.</summary>
    Sharp = 0,

    /// <summary>
    ///     The height blurred a little first, so eight-bit paint's steps and a hard edge's single-texel cliff read as
    ///     slopes. The default, because a painted height is eight bits and its steps otherwise show as contour lines.
    /// </summary>
    Smooth = 1
}

/// <summary>Where a fill layer's pixels come from.</summary>
public enum LayerFillSource {
    /// <summary>One colour per channel, from <see cref="LayerAsset.Values" />.</summary>
    Constant = 0,

    /// <summary>An imported image per channel, from <see cref="LayerAsset.Textures" />.</summary>
    Texture = 1,

    /// <summary>
    ///     A published <c>.vxtexgraph</c>, by reference: a sub-graph inlined into the stack's graph.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This said "not compiled in this build" and it has been compiled, on a device, since
    ///     M8</b> — <a href="https://github.com/Rikarin/Vixen/issues/1045">#1045</a>. The
    ///     <c>ISubGraphSource</c> the old remark said was missing is what
    ///     <c>LayerStackCompiler.Library</c> supplies and what both <c>Compile</c> overloads take;
    ///     <c>LayerStackGraph.Fill</c> has a complete case that resolves the path against the
    ///     registry and refuses a graph with more than one image output by name; and
    ///     <c>ProjectCompoundDeviceTests</c> bakes one on a real adapter.
    ///     ⚠ <b>A doc comment saying a member does not work is read as a decision</b>, which is why
    ///     this is worth a line rather than a deletion: the next author who wants a graph fill reads
    ///     it, believes the feature is owed, and builds a second path to it.
    /// </remarks>
    Graph = 2,

    /// <summary>
    ///     A named anchor point, read through <see cref="LayerAsset.Anchor" />: the stack as it stood at that point, in
    ///     the channel <see cref="AnchorReference.Channel" /> names or, when it names none, in the channel being written.
    /// </summary>
    Anchor = 3
}

/// <summary>Which adjustment a filter layer applies to everything under it.</summary>
/// <remarks>
///     <para>
///         Doc 48 § D10 names "levels, HSL, blur, a graph with an <c>Input</c>". The first three are
///         node types this build has.
///     </para>
///     <para>
///         ⚠ <b>The fourth used to be described here as "<see cref="LayerFillSource.Graph" />'s
///         question, with the same answer", and that answer has been <em>yes</em> since M8</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1045">#1045</a>. A graph fill compiles,
///         on a device, in two test files; so a reader who took this line to mean "a graph
///         adjustment is owed for the same reason a graph fill is" was reading a claim that had
///         stopped being true of either half.
///     </para>
///     <para>
///         ⚠ <b>What is genuinely not expressible is a graph adjustment on a <em>filter layer</em>,
///         and it is not for the reason the old line gave.</b> <c>LayerStackGraph.Adjustment</c>
///         takes its node type from this enum and from nothing else, so a filter layer can only be
///         one of these five. A mask's adjustments have no such limit —
///         <see cref="MaskEffectAsset.Node" /> names a node type, which is any published compound —
///         so § D10's fourth was already sayable of a mask and not of a layer.
///     </para>
///     <para>
///         <b>Closed by <see cref="LayerAsset.FilterNode" />, which is the member
///         <see cref="MaskEffectAsset" /> already had</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1068">#1068</a>. Not a sixth member
///         here: an enum has to grow one per effect and still cannot reach a published compound.
///         These five stay exactly what they are, and compile to exactly what they compiled to,
///         because a differential in <c>LayerStackExplodeTests</c> is asserting that byte for byte.
///     </para>
/// </remarks>
public enum LayerFilterKind {
    /// <summary>An input range remapped through a gamma into an output range.</summary>
    Levels = 0,

    /// <summary>Hue rotation, saturation and lightness.</summary>
    Hsl = 1,

    /// <summary>A separable box blur, in texels at the base resolution.</summary>
    Blur = 2,

    /// <summary>Each channel flipped about a half.</summary>
    Invert = 3,

    /// <summary>Colour to a single channel.</summary>
    Grayscale = 4
}

/// <summary>Where a layer's mask comes from — doc 48 § 4.10's four mask sources.</summary>
public enum LayerMaskSource {
    /// <summary>No mask: the layer writes wherever its own alpha lets it.</summary>
    None = 0,

    /// <summary>One number over the whole surface.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Folded into the layer's opacity and compiled to nothing at all, when it is the
    ///         whole mask</b> — <a href="https://github.com/Rikarin/Vixen/issues/789">#789</a>. A
    ///         mask multiplies into the foreground's coverage
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/832">#832</a>) and
    ///         <c>Blend.rvn</c>'s <c>amount</c> is <c>opacity · mask · alpha</c>, so the fold is a
    ///         reassociation rather than an approximation. It saves five ops per masked layer per
    ///         channel. <c>LayerStackGraph.Folds</c> carries the two conditions.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This member used to say the opposite, and half of the reason was real.</b>
    ///         Folding was refused partly because it would make the mask path unreachable for the
    ///         one case a device-free test can build "which is how a mask that never worked ships
    ///         green". A bake mask, an anchor mask and any mask with two entries all reach the full
    ///         path with no imported image, and the suite is built on those instead.
    ///     </para>
    /// </remarks>
    Constant = 1,

    /// <summary>An imported image, by the reference a host resolves.</summary>
    Texture = 2,

    /// <summary>
    ///     A named anchor point — <see cref="LayerAsset.AnchorPoint" /> — read in one channel the reference chooses, so a
    ///     mask is one picture whatever channel is being built. What makes the stack a DAG rather than a list.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It named a layer by id and read it in the channel being built</b> (Patina #111), so one mask was a
    ///     different picture under every channel — under roughness it was the stack's roughness — and a layer that
    ///     wrote nothing to one channel refused the whole compile. A reference now names an anchor point and a channel.
    /// </remarks>
    Anchor = 3,

    /// <summary>
    ///     A generator: a published <c>.vxtexgraph</c> reading the mesh maps by usage, named by
    ///     <see cref="MaskLayerAsset.Generator" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The reason one generator works on every mesh is that it names no image at all.</b>
    ///     A compound like <c>Generators/Dirt</c> is a graph of <c>Source/Mesh Map</c> nodes bound by
    ///     what they <em>measure</em> — curvature, occlusion — so the same compound over a different
    ///     mesh reads that mesh's own bakes with no rewiring. Doc 48 § D10's claim, and the whole
    ///     argument for binding by usage rather than by asset.
    /// </remarks>
    Generator = 4,

    /// <summary>One baked mesh map on its own, by what it measures.</summary>
    /// <remarks>
    ///     Doc 48 § 4.10's fourth mask source. A generator without the graph: curvature, occlusion or
    ///     thickness used directly as a mask, which is what an artist reaches for before reaching for
    ///     a compound.
    /// </remarks>
    Bake = 5,

    /// <summary>
    ///     Painted pixels, from the <c>.vxpaint</c> named beside it. ⚠ Read for its <b>red</b>, like
    ///     every other mask source — so a mask canvas is painted in white and its coverage is the
    ///     channel's value rather than an alpha.
    ///     <a href="https://github.com/Rikarin/Vixen/issues/852">#852</a>.
    /// </summary>
    Paint = 6
}

/// <summary>Which of the thirty operators a layer composites with.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The names and the numbers are <c>Vixen.TextureGraph</c>'s
///         <c>TextureBlendMode</c>, which is <c>internal</c> — so this is a second declaration of one
///         list and the two can drift.</b> Nothing in the compilation would notice: a layer hands
///         <c>Colour/Blend</c>'s <c>Mode</c> setting the <em>name</em>, and a name that assembly does
///         not know falls back to its own default, which is <c>Copy</c> — a stack whose every overlay
///         silently became a copy.
///     </para>
///     <para>
///         <b>So the agreement is derived rather than declared.</b>
///         <c>LayerStackCompileTests</c> reflects the real enum out of the evaluator assembly and
///         compares both directions, and a second test there compiles one layer per mode and asserts
///         the compiler reported nothing — because reflection proves the names match and only a
///         compilation proves the name is the one the node reads. ⚠ This paragraph named a
///         <c>LayerBlendModeTests</c>, which does not exist and never has: the tests are real and the
///         class they are in is not the one that was written down.
///     </para>
/// </remarks>
public enum LayerBlendMode {
    /// <summary>The foreground, under the opacity and its own alpha. The mode with no neutral.</summary>
    Copy = 0,

    /// <summary><c>a · b</c>. Neutral at white.</summary>
    Multiply = 1,

    /// <summary><c>1 − (1 − a)(1 − b)</c>. Neutral at black.</summary>
    Screen = 2,

    /// <summary>Multiply where the backdrop is dark, screen where it is light. Neutral at mid-grey.</summary>
    Overlay = 3,

    /// <summary><c>a + b</c>. Neutral at black.</summary>
    Add = 4,

    /// <summary><c>a − b</c>. Neutral at black.</summary>
    Subtract = 5,

    /// <summary><c>min(a, b)</c>. Neutral at white.</summary>
    Darken = 6,

    /// <summary><c>max(a, b)</c>. Neutral at black.</summary>
    Lighten = 7,

    /// <summary><c>a / b</c>, capped at white. Neutral at white.</summary>
    Divide = 8,

    /// <summary>Overlay with the operands swapped. Neutral at mid-grey.</summary>
    HardLight = 9,

    /// <summary>The Photoshop form, with the <c>sqrt</c> half. Neutral at mid-grey.</summary>
    SoftLight = 10,

    /// <summary><c>|a − b|</c>. Neutral at black.</summary>
    Difference = 11,

    /// <summary><c>a + b − 2ab</c>. Neutral at black.</summary>
    Exclusion = 12,

    /// <summary><c>a / (1 − b)</c>, capped. Neutral at black.</summary>
    ColourDodge = 13,

    /// <summary><c>1 − (1 − a) / b</c>, capped. Neutral at white.</summary>
    ColourBurn = 14,

    /// <summary><c>a + (b − ½) · 2</c>. Neutral at mid-grey.</summary>
    SignedAdd = 15,

    /// <summary>
    ///     Copy that does not read the foreground's own alpha, so a transparent texel of it replaces the backdrop too.
    ///     Opacity, and a layer's mask, still decide how much. No neutral.
    /// </summary>
    Replace = 16,

    /// <summary><c>b / a</c>: divide with the operands exchanged. No neutral foreground.</summary>
    InverseDivide = 17,

    /// <summary><c>b − a</c>: subtract with the operands exchanged. No neutral foreground.</summary>
    InverseSubtract = 18,

    /// <summary><c>a + b − 1</c>. Neutral at white.</summary>
    LinearBurn = 19,

    /// <summary>Colour burn below mid-grey and colour dodge above, over twice the distance. Neutral at mid-grey.</summary>
    VividLight = 20,

    /// <summary><c>a + 2b − 1</c>, clipped to 0 to 1, which is what tells it from signed add. Neutral at mid-grey.</summary>
    LinearLight = 21,

    /// <summary>Darken with <c>2b</c> below mid-grey, lighten with <c>2b − 1</c> above. Neutral at mid-grey.</summary>
    PinLight = 22,

    /// <summary>The foreground's hue with the backdrop's saturation and value, in HSV.</summary>
    Hue = 23,

    /// <summary>The foreground's saturation with the backdrop's hue and value, in HSV.</summary>
    Saturation = 24,

    /// <summary>The foreground's hue and saturation with the backdrop's value, in HSV.</summary>
    Colour = 25,

    /// <summary>The foreground's value with the backdrop's hue and saturation, in HSV.</summary>
    Value = 26,

    /// <summary>Two normal maps by whiteout: slopes added, heights multiplied. Neutral at the flat normal.</summary>
    NormalMapCombine = 27,

    /// <summary>
    ///     The foreground normal map reoriented to follow the backdrop's surface (reoriented normal mapping). Neutral at
    ///     the flat normal.
    /// </summary>
    NormalMapDetail = 28,

    /// <summary>The backdrop's normals reoriented to follow the foreground's surface. Neutral at the flat normal.</summary>
    NormalMapInverseDetail = 29
}

/// <summary>A reference to a named anchor point, in one of the set's channels.</summary>
/// <remarks>
///     <para>
///         <b>What an anchor point is.</b> A layer whose <see cref="LayerAsset.AnchorPoint" /> names one publishes the
///         stack as it stands just above that layer — the composite of everything up to and including it, in its own
///         group — in every channel of the set, whether or not that layer writes the channel. A reference reads that
///         picture in <see cref="Channel" />.
///     </para>
///     <para>
///         ⚠ <b>The name and never the layer's id</b> (Patina #111). An id is stable and unreadable; a name is what an
///         artist chose and what a list of references can show, and it travels with a copy of the layer into another
///         stack, where ids are re-minted.
///     </para>
/// </remarks>
public sealed record AnchorReference {
    /// <summary>The <see cref="LayerAsset.AnchorPoint" /> read.</summary>
    public string Anchor { get; init; } = "";

    /// <summary>
    ///     The channel read, by usage. Empty means the channel being built, which only a fill or a filter layer's
    ///     input may say: a mask, a generator or a mask effect reading it would be another picture in every channel.
    /// </summary>
    public string Channel { get; init; } = "";
}

/// <summary>Which lane of its source's picture a mask, or an entry of one, reads (Patina #110).</summary>
/// <remarks>
///     <b>Red is every mask's lane by default</b>, which is right for a grey and for a canvas painted in white. A mesh map
///     that measures a direction — <c>world</c>, <c>position</c>, <c>normal</c>, <c>bent</c> — keeps its other axes in
///     green and blue, which a mask could not reach before this.
/// </remarks>
public enum LayerMaskLane {
    /// <summary>The red lane: X of a direction.</summary>
    Red = 0,

    /// <summary>The green lane: Y of a direction.</summary>
    Green = 1,

    /// <summary>The blue lane: Z of a direction.</summary>
    Blue = 2,

    /// <summary>The alpha lane.</summary>
    Alpha = 3
}

/// <summary>The layer an <see cref="LayerKind.Instance" /> stands for: a layer's id, in a set of the stack.</summary>
public sealed record InstanceReference {
    /// <summary>The texture set the source is in, by name, or empty for the instance's own set.</summary>
    public string Set { get; init; } = "";

    /// <summary>The source layer's <see cref="LayerAsset.Id" />.</summary>
    public string Layer { get; init; } = "";
}

/// <summary>One entry in a mask's own small stack.</summary>
/// <remarks>
///     <para>
///         <b>The same flat-record-with-a-discriminator shape <see cref="LayerAsset" /> uses</b>, one
///         file over and for the same reason: a <c>.vxlayers</c> is YAML people merge, and a merge
///         conflict in a flat record is one a person can resolve.
///     </para>
///     <para>
///         ⚠ <b>It carries a blend mode and an opacity, which is the whole of what makes a mask a
///         stack rather than a source.</b> Curvature multiplied by occlusion is a dirt mask; either
///         one alone is not. Doc 48 § D10.
///     </para>
/// </remarks>
public sealed record MaskLayerAsset {
    /// <summary>Where this entry's pixels come from.</summary>
    public LayerMaskSource Source { get; init; } = LayerMaskSource.Constant;

    /// <summary>The number, for <see cref="LayerMaskSource.Constant" />.</summary>
    public float Value { get; init; } = 1f;

    /// <summary>The imported image, for <see cref="LayerMaskSource.Texture" />.</summary>
    public string Asset { get; init; } = "";

    /// <summary>The anchor point read, and in which channel, for <see cref="LayerMaskSource.Anchor" />.</summary>
    public AnchorReference Anchor { get; init; } = new();

    /// <summary>
    ///     The published compound's node-type path, for <see cref="LayerMaskSource.Generator" /> —
    ///     <c>Generators/Dirt</c>, say.
    /// </summary>
    public string Generator { get; init; } = "";

    /// <summary>The generator's numbers, by the port name its compound exposes.</summary>
    public Dictionary<string, float[]> GeneratorValues { get; init; } = [];

    /// <summary>The generator's settings, by the setting name its compound exposes.</summary>
    public Dictionary<string, string> GeneratorTexts { get; init; } = [];

    /// <summary>The generator's image inputs, by port name, each read from an anchor point.</summary>
    public Dictionary<string, AnchorReference> GeneratorInputs { get; init; } = [];

    /// <summary>What the map measures, for <see cref="LayerMaskSource.Bake" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Not validated here.</b> <c>Source/Mesh Map</c> refuses a name nothing bakes, with a
    ///     diagnostic naming the setting, so a list in this file would be a second opinion about
    ///     <c>TextureMeshMaps.Known</c> — the shape five exact-equality roll calls in this workstream
    ///     have gone red on.
    /// </remarks>
    public string Map { get; init; } = "";

    /// <summary>The <c>.vxpaint</c> this entry's painted pixels live in. M9.</summary>
    public string Paint { get; init; } = "";

    /// <summary>Which lane of its picture this entry reads.</summary>
    public LayerMaskLane Lane { get; init; } = LayerMaskLane.Red;

    /// <summary>How this entry composites over the entries beneath it.</summary>
    public LayerBlendMode Blend { get; init; } = LayerBlendMode.Copy;

    /// <summary>How much of the result is this entry's.</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>Whether it contributes at all.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>One adjustment over a mask's composited result.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A node type and a bag of numbers, rather than a C# type per effect.</b> Doc 48 § 4.10
///         names "Levels · Blur · Warp · <em>any single-input graph</em>", and the last of those is
///         the specification: an effect is anything with one image in and one image out, which
///         includes every published compound. A <c>MaskEffectKind</c> enum would have to grow a
///         member per effect and would still not reach the compounds, which is the same
///         "a compound is content" rule the generators already follow.
///     </para>
///     <para>
///         <b>Which port is the image is asked of the registry rather than assumed.</b>
///         <c>LayerStackGraph</c> finds the type's single <c>Image</c> input and its single
///         <c>Image</c> output; <see cref="Values" /> may name any port that is <em>not</em> one of
///         them. That is what stops a setting called <c>Input</c> from overwriting the wire carrying
///         the mask with a constant — the defect <c>LayerFilterKind</c>'s hand-written port list
///         exists to prevent, derived here instead of listed.
///     </para>
/// </remarks>
public sealed record MaskEffectAsset {
    /// <summary>The node type: <c>Colour/Levels</c>, <c>Filters/Blur</c>, a compound's path.</summary>
    public string Node { get; init; } = "";

    /// <summary>Whether it runs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Its numbers, by the port name the node declares.</summary>
    public Dictionary<string, float[]> Values { get; init; } = [];

    /// <summary>Its settings, by the setting name the node declares.</summary>
    public Dictionary<string, string> Texts { get; init; } = [];

    /// <summary>
    ///     Its other image inputs, by port name, each read from an anchor point. The mask goes into the one input left.
    /// </summary>
    public Dictionary<string, AnchorReference> Inputs { get; init; } = [];
}

/// <summary>One layer's mask: a base, a small stack over it, and the effects on the result.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D10: a mask is itself a small stack</b> — a paint mask, a generator, a bake,
///         a filter and an anchor. <see cref="Source" /> and the members beside it are its
///         <em>base</em>; <see cref="Layers" /> composite over that, bottom first, each with its own
///         operator; <see cref="Effects" /> then adjust the whole result.
///     </para>
///     <para>
///         ⚠ <b>The base is the legacy single source, kept rather than folded into
///         <see cref="Layers" />.</b> Every <c>.vxlayers</c> that exists names its mask this way, and
///         a stack with a plain constant or texture mask must keep compiling to exactly the ops it
///         compiled to before — which is what makes the explode differential's byte-identity a claim
///         about this change rather than a re-blessing.
///     </para>
/// </remarks>
public sealed record MaskAsset {
    /// <summary>Where the mask comes from.</summary>
    public LayerMaskSource Source { get; init; } = LayerMaskSource.None;

    /// <summary>The number, for <see cref="LayerMaskSource.Constant" />.</summary>
    public float Value { get; init; } = 1f;

    /// <summary>The imported image, for <see cref="LayerMaskSource.Texture" />.</summary>
    public string Asset { get; init; } = "";

    /// <summary>The anchor point read, and in which channel, for <see cref="LayerMaskSource.Anchor" />.</summary>
    public AnchorReference Anchor { get; init; } = new();

    /// <summary>
    ///     The <c>.vxpaint</c> holding this mask's painted pixels, relative to the stack. Empty for a
    ///     mask nobody has painted on.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A path, never the pixels.</b> Doc 48 Part 5: a stack is a file people merge and a
    ///     paint layer is not. <c>LayerStackShapeTests</c> walks this record's type closure and
    ///     refuses any member that could hold a buffer.
    /// </remarks>
    public string Paint { get; init; } = "";

    /// <summary>
    ///     The published compound's node-type path, for a base of
    ///     <see cref="LayerMaskSource.Generator" />.
    /// </summary>
    public string Generator { get; init; } = "";

    /// <summary>The generator's numbers, by the port name its compound exposes.</summary>
    public Dictionary<string, float[]> GeneratorValues { get; init; } = [];

    /// <summary>The generator's settings, by the setting name its compound exposes.</summary>
    public Dictionary<string, string> GeneratorTexts { get; init; } = [];

    /// <summary>The generator's image inputs, by port name, each read from an anchor point.</summary>
    public Dictionary<string, AnchorReference> GeneratorInputs { get; init; } = [];

    /// <summary>What the map measures, for a base of <see cref="LayerMaskSource.Bake" />.</summary>
    public string Map { get; init; } = "";

    /// <summary>Which lane of its picture the base reads.</summary>
    public LayerMaskLane Lane { get; init; } = LayerMaskLane.Red;

    /// <summary>The entries composited over the base, bottom first.</summary>
    /// <remarks>
    ///     ⚠ <b>The first entry has nothing beneath it when there is no base, so its own operator
    ///     does nothing</b> — reported as a warning rather than silently ignored, because an artist
    ///     who set the bottom of a mask stack to <c>Multiply</c> meant something by it.
    /// </remarks>
    public List<MaskLayerAsset> Layers { get; init; } = [];

    /// <summary>The adjustments over the composited result, in order.</summary>
    public List<MaskEffectAsset> Effects { get; init; } = [];
}

/// <summary>One layer of a texture set's stack.</summary>
/// <remarks>
///     <para>
///         <b>One flat record with a <see cref="Kind" /> discriminator rather than four types.</b>
///         <c>GraphNodeAsset</c>'s reason, one file over: a <c>.vxlayers</c> is YAML people merge,
///         and a merge conflict in a flat record is one a person can resolve. It also means the
///         serialiser needs no polymorphism, which
///         <c>Vixen.Core.Yaml</c> deliberately does not have.
///     </para>
///     <para>
///         <b><see cref="Children" /> is what makes a <see cref="LayerKind.Group" /> a group</b>, and
///         it is the one recursive member. A group's children composite over whatever is beneath the
///         group, and the group's own opacity, mode and mask then apply to the whole of that result —
///         which is what makes a group's mask worth having.
///     </para>
/// </remarks>
public sealed record LayerAsset {
    /// <summary>A stable identity, unique in the stack. What every editing command names.</summary>
    /// <remarks>
    ///     ⚠ <b>Stable across renames and across reorders, which is why it is not the name and not
    ///     the index.</b> ⚠ An anchor used to name a layer by this id; it names an <see cref="AnchorPoint" /> now
    ///     (Patina #111), which is what an artist can read and what survives a copy into another stack.
    /// </remarks>
    public string Id { get; init; } = "";

    /// <summary>What the artist calls it.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    ///     The name of the anchor point this layer publishes, or empty for none. Unique in the set. See
    ///     <see cref="AnchorReference" /> for what a reference reads.
    /// </summary>
    public string AnchorPoint { get; init; } = "";

    /// <summary>Which of the four kinds.</summary>
    public LayerKind Kind { get; init; } = LayerKind.Fill;

    /// <summary>Whether it contributes at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How much of the result is this layer's, before its own alpha and its mask.</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>Which operator composites it over what is under it.</summary>
    public LayerBlendMode Blend { get; init; } = LayerBlendMode.Copy;

    /// <summary>How a fill is put onto the surface.</summary>
    public LayerProjection Projection { get; init; } = LayerProjection.Uv;

    /// <summary>Which world axis a <see cref="LayerProjection.Planar" /> fill projects along.</summary>
    /// <remarks>
    ///     ⚠ <b>Read by <see cref="LayerProjection.Planar" /> and by nothing else</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1032">#1032</a>. A triplanar layer blends
    ///     all three planes by the world normal and a UV layer is in the atlas, so an axis means
    ///     nothing on either; <c>LayerStackGraph.Project</c> says so rather than ignoring it, for the
    ///     reason every other member of this record that can be set where it does nothing says so.
    ///     ⚠ Except <see cref="LayerAxis.Y" />, which is the default and therefore indistinguishable
    ///     from an author who said nothing — the one case that cannot be warned about and is stated
    ///     on <see cref="LayerAxis" /> instead.
    /// </remarks>
    public LayerAxis PlanarAxis { get; init; } = LayerAxis.Y;

    /// <summary>Where a fill sits on the surface.</summary>
    public LayerPlacement Placement { get; init; } = new();

    /// <summary>
    ///     Which of the set's channels this layer writes, by usage. Empty means every one of them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Empty means <em>all</em>, and that is the one defaulting decision here that a reader
    ///     could get wrong.</b> The alternative — an explicit list always — makes a channel added to
    ///     the set later invisible to every layer that already exists, so the artist adds "height" to
    ///     a texture set and the whole stack stops writing it. Empty-means-all is the behaviour that
    ///     matches what an artist means by "this layer is not restricted".
    /// </remarks>
    public List<string> Channels { get; init; } = [];

    /// <summary>The constant per channel, by usage, for a <see cref="LayerFillSource.Constant" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Four numbers — red, green, blue, alpha, linear — and a channel with no entry is a
    ///     channel this layer does not write.</b> It used to take the channel's own
    ///     <see cref="ChannelAsset.Default" /> instead, which is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/807">#807</a> · 2: because
    ///     <see cref="Channels" /> empty means <em>all</em>, a fill that sets roughness alone also
    ///     reached base colour, height and emissive and stamped each one's base default over
    ///     whatever was beneath it. So "a fill that only sets roughness is one entry rather than
    ///     seven" is now true of the picture as well as of the file.
    /// </remarks>
    public Dictionary<string, float[]> Values { get; init; } = [];

    /// <summary>The imported image per channel, by usage, for a <see cref="LayerFillSource.Texture" />.</summary>
    public Dictionary<string, string> Textures { get; init; } = [];

    /// <summary>Where a fill's pixels come from.</summary>
    public LayerFillSource Fill { get; init; } = LayerFillSource.Constant;

    /// <summary>The published graph a <see cref="LayerFillSource.Graph" /> fill reads.</summary>
    public string Graph { get; init; } = "";

    /// <summary>The anchor point a <see cref="LayerFillSource.Anchor" /> fill reads.</summary>
    public AnchorReference Anchor { get; init; } = new();

    /// <summary>
    ///     Image inputs of a graph fill or a <see cref="FilterNode" /> filter, by port name, each read from an anchor
    ///     point. A filter's own input is the one image input left, which reads the stack beneath it.
    /// </summary>
    public Dictionary<string, AnchorReference> Inputs { get; init; } = [];

    /// <summary>Which adjustment a <see cref="LayerKind.Filter" /> layer applies.</summary>
    /// <remarks>
    ///     ⚠ <b>Read only when <see cref="FilterNode" /> is empty</b>, which is what makes doc 48
    ///     § D10's fourth kind sayable of a layer without adding a sixth member here.
    /// </remarks>
    public LayerFilterKind Filter { get; init; } = LayerFilterKind.Levels;

    /// <summary>
    ///     The node type a <see cref="LayerKind.Filter" /> layer applies, when it is not one of the
    ///     five <see cref="LayerFilterKind" /> members: <c>Colour/Levels</c>, a compound's path.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48 § D10's fourth kind — "a graph with an <c>Input</c>" — and the member
    ///         <see cref="MaskEffectAsset.Node" /> has had all along</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1068">#1068</a>). A mask's
    ///         adjustments were never limited to a list, because an effect is specified as anything
    ///         with one image in and one image out; a filter layer took its node type from
    ///         <see cref="Filter" /> and from nothing else, so the same sentence was true of a mask
    ///         and false of a layer. The asymmetry was invisible from either file alone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A second member rather than a sixth <see cref="LayerFilterKind" />, and the
    ///         argument is the one <c>MaskEffectAsset</c> already makes:</b> an enum has to grow a
    ///         member per effect and still cannot reach a published compound, because a compound is
    ///         content. ⚠ <b>And it takes precedence rather than sitting beside</b>
    ///         <see cref="Filter" />, whose default is a real value — <c>Levels</c> is zero, so a
    ///         file naming a compound and saying nothing about <c>filter</c> would otherwise be two
    ///         instructions at once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The five enum members must keep compiling to exactly the ops they compile to
    ///         now</b>, or <c>LayerStackExplodeTests</c>' byte-identical differential becomes a
    ///         re-blessing rather than a check. <c>LayerStackGraph.Adjustment</c> therefore branches
    ///         before the existing path rather than through it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its <em>settings</em> are <see cref="Texts" />, which this remark used to say a
    ///         layer did not have</b> — <a href="https://github.com/Rikarin/Vixen/issues/1079">#1079</a>.
    ///         <see cref="Settings" /> is numbers by port name, so until that member arrived a
    ///         compound whose behaviour is chosen by a string took that setting's default here and
    ///         said nothing about it.
    ///     </para>
    /// </remarks>
    public string FilterNode { get; init; } = "";

    /// <summary>The layer's numbers, by the port name the node declares.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>By port name rather than as a typed record per filter.</b> Five filters with
    ///         five records is five more shapes in a file format, and the node the number reaches
    ///         already names its own ports — so a wrong name is a compiler diagnostic against the
    ///         node rather than a silently ignored member. <c>LayerStackGraph</c> writes only the
    ///         ports the chosen filter declares and reports the rest.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two readers, and this said "the filter's" while a second one arrived</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1032">#1032</a>. A filter layer's
    ///         ports are read by <c>LayerStackGraph.Adjustment</c>; a <em>projected</em> layer's
    ///         <c>Scale</c> and <c>Sharpness</c> are read by <c>LayerStackGraph.Project</c>, on a
    ///         fill layer, which the old sentence excluded by name. Both warn on a key no port
    ///         answers to, so the dictionary is still checked rather than open.
    ///     </para>
    /// </remarks>
    public Dictionary<string, float[]> Settings { get; init; } = [];

    /// <summary>The layer's settings, by the setting name the node declares.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="MaskEffectAsset.Texts" />, on a layer</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1079">#1079</a>. A mask effect has
    ///         carried two dictionaries all along — numbers by port and settings by name — because
    ///         an effect is any published compound and a compound's behaviour is often chosen by a
    ///         string. <see cref="FilterNode" /> made a filter layer the same kind of thing and this
    ///         record had only the numbers, so a layer naming such a compound took that setting's
    ///         default, silently. ⚠ The gap was invisible from this file: the member that was
    ///         missing is one you had to have read <see cref="MaskEffectAsset" /> to know should be
    ///         here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read on the <see cref="FilterNode" /> path only</b>, which is not tidiness. The
    ///         five <see cref="LayerFilterKind" /> members have no settings at all — levels, HSL,
    ///         blur, invert and grayscale are numbers throughout — so <c>Settings</c> alone was
    ///         complete for as long as the enum was the only way to name a filter's node, and the
    ///         five must go on compiling to exactly the ops they compile to now or
    ///         <c>LayerStackExplodeTests</c>' byte-identical differential becomes a re-blessing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two letters from <see cref="Textures" /> and nothing to do with it.</b> That one
    ///         is an imported image per <em>channel</em> for a texture fill; this one is a node's
    ///         settings by name. The name is <see cref="MaskEffectAsset.Texts" />' because the two
    ///         are the same member read by the same resolution — <c>LayerStackGraph.Published</c> is
    ///         <c>Effect</c> pointed at a layer — and a third spelling for one concept is worse than
    ///         a near-collision that is written down.
    ///     </para>
    /// </remarks>
    public Dictionary<string, string> Texts { get; init; } = [];

    /// <summary>The mask.</summary>
    public MaskAsset Mask { get; init; } = new();

    /// <summary>
    ///     The <c>.vxpaint</c> holding a <see cref="LayerKind.Paint" /> layer's pixels, relative to
    ///     the stack — or, on a <see cref="LayerKind.Group" />, the canvas that group is flattened into.
    /// </summary>
    public string Paint { get; init; } = "";

    /// <summary>Whether this is a group flattened into a canvas, whose children are kept and not read.</summary>
    public bool Flattened => Kind == LayerKind.Group && Paint.Trim().Length > 0;

    /// <summary>A group's layers, bottom first.</summary>
    public List<LayerAsset> Children { get; init; } = [];

    /// <summary>The layer an <see cref="LayerKind.Instance" /> stands for. Read on an instance and nothing else.</summary>
    public InstanceReference Instance { get; init; } = new();

    /// <summary>Whether this layer writes a given channel.</summary>
    /// <param name="usage">The channel's usage.</param>
    /// <returns><see langword="true" /> when it does.</returns>
    public bool Writes(string usage) {
        if (Channels.Count == 0) {
            return true;
        }

        foreach (var channel in Channels) {
            if (string.Equals(channel, usage, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }
}

/// <summary>One output map a texture set produces.</summary>
public sealed record ChannelAsset {
    /// <summary>
    ///     What the map is for. One of the nine <c>Output/Output</c> accepts; a spelling it does not
    ///     know is a compiler diagnostic against that node rather than a second list here.
    /// </summary>
    public string Usage { get; init; } = "baseColor";

    /// <summary>What the stack starts from, before any layer. Four numbers, linear.</summary>
    public float[] Default { get; init; } = [0f, 0f, 0f, 1f];
}

/// <summary>One material slot on the mesh, and the maps it produces.</summary>
public sealed record TextureSetAsset {
    /// <summary>The slot's name on the mesh.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    ///     Which mesh of <see cref="LayerStackAsset.Model" /> this slot is, by the name the model
    ///     file gives it. Empty means every mesh in the model.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A part of the stack's <em>one</em> model and never a second model, which is the
    ///         half of <a href="https://github.com/Rikarin/Vixen/issues/920">#920</a>'s design
    ///         question that is not answered by putting the model on the stack.</b> A model file
    ///         splits into one <c>MeshData</c> per material slot — Assimp splits an <c>aiMesh</c>
    ///         that way and <c>ModelReader</c> keeps it — so "two texture sets are two slots of one
    ///         mesh" and "each set is its own <c>MeshData</c>" are the same sentence. Without this
    ///         member a two-set stack has one coverage map, the union of every island in the model,
    ///         and painting the <c>Body</c> set would be allowed anywhere the <c>Head</c> set has
    ///         surface.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty means <em>every</em> mesh rather than the mesh this set is named after.</b>
    ///         <see cref="Name" /> is the slot's name and a model's mesh names are the exporter's;
    ///         they agree on a single-mesh model and are unrelated on anything a person has renamed,
    ///         so matching on it would bind the coverage map of a stack to a coincidence. Every mesh
    ///         is the honest answer for a stack nobody has narrowed, and it is right for the ordinary
    ///         case, which is one mesh.
    ///     </para>
    /// </remarks>
    public string Mesh { get; init; } = "";

    /// <summary>The maps this set produces, in the order a bake writes them.</summary>
    public List<ChannelAsset> Channels { get; init; } = [];

    /// <summary>How a <c>height</c> channel becomes the set's <c>normal</c> output, which it does whenever it has one.</summary>
    /// <remarks>
    ///     ⚠ <b>Height reaches shading only through the normal.</b> A set with a height channel writes a
    ///     <c>normal</c> output whether or not it declares a normal channel: the height's slope, combined over the
    ///     normal channel when there is one. Without that, painted height changed the files and never the surface.
    /// </remarks>
    public LayerHeightNormal HeightNormal { get; init; } = LayerHeightNormal.Smooth;

    /// <summary>The layers, <b>bottom first</b>.</summary>
    /// <remarks>
    ///     ⚠ <b>Bottom first, which is the opposite of how a layers panel draws them.</b> The file's
    ///     order is the composite order, so reading it top to bottom is reading the arithmetic in the
    ///     order it happens; a panel reverses it for display. Storing the panel's order would put the
    ///     reversal in the compiler, where a reader of the file cannot see it.
    /// </remarks>
    public List<LayerAsset> Layers { get; init; } = [];
}

/// <summary>A <c>.vxlayers</c>: the stack, per texture set, and no pixels.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 Part 5.</b> Layers, masks, anchors and parameters live here; the painted pixels
///         live in a <c>.vxpaint</c> beside it, one per paint layer or mask, named by
///         <see cref="LayerPaint" />. They are separate files because a stack is a file people merge
///         and a paint layer is not.
///     </para>
///     <para>
///         ⚠ <b>This shape holds no buffer of any kind, and that is asserted rather than intended.</b>
///         <c>LayerStackShapeTests</c> walks the whole type closure from here and fails on a member
///         whose type could carry texels.
///     </para>
/// </remarks>
public sealed record LayerStackAsset {
    /// <summary>What this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The format's version.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>What the stack is called.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    ///     The model this stack is painted on, as a path relative to the project root. Empty for a
    ///     stack nobody has bound one to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/920">#920</a>, and the design
    ///         question it poses is answered here rather than on the set.</b> A stack's texture sets
    ///         are the material slots of <em>one</em> model: <see cref="TextureSetAsset.Name" />
    ///         already reads "the slot's name on the mesh", a bake writes one <c>.vxmat</c> for the
    ///         stack, and a mesh map is measured once and bound by usage — three things that all
    ///         stop meaning anything if two sets could name two models. So the model is the stack's
    ///         and <see cref="TextureSetAsset.Mesh" /> narrows a set to one of its meshes.
    ///     </para>
    ///     <para>
    ///         <b>The pair is <c>IMeshMapBaker.Bake</c>'s own.</b> That seam takes an
    ///         <c>AssetId model</c> and a <c>string mesh</c> together, because a bake is of one mesh
    ///         of one model; this file holds the same pair split across the two levels each half
    ///         belongs to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A project-relative path and not an <c>AssetId</c>, which is the shape every other
    ///         reference in this file already has.</b> <see cref="LayerAsset.Textures" /> and
    ///         <see cref="MaskAsset.Asset" /> are paths the host resolves through
    ///         <c>AssetDatabase.TryGetByPath</c> — <c>LayerStackPreview.Resolve</c> is that host half
    ///         — so a GUID here would be a second scheme in one file, and a <c>.vxlayers</c> is a
    ///         file people read and merge.
    ///     </para>
    /// </remarks>
    public string Model { get; init; } = "";

    /// <summary>The width the stack is authored at, in texels.</summary>
    public int BaseWidth { get; init; } = 1024;

    /// <summary>The height the stack is authored at, in texels.</summary>
    public int BaseHeight { get; init; } = 1024;

    /// <summary>The seed every procedural op in the compiled plan derives from.</summary>
    public uint Seed { get; init; }

    /// <summary>The material slots, each with its own channels and layers.</summary>
    public List<TextureSetAsset> Sets { get; init; } = [];

    /// <summary>The set of that name, or <see langword="null" />.</summary>
    /// <param name="name">The slot's name.</param>
    /// <returns>The set.</returns>
    public TextureSetAsset? SetNamed(string name) {
        foreach (var set in Sets) {
            if (string.Equals(set.Name, name, StringComparison.Ordinal)) {
                return set;
            }
        }

        return null;
    }
}
