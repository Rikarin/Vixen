// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Texturing.Layers;

/// <summary>Which of doc 48 § D10's four kinds a layer is.</summary>
enum LayerKind {
    /// <summary>A constant, a texture or a graph, projected.</summary>
    Fill = 0,

    /// <summary>
    ///     Painted pixels, held in a <c>.vxpaint</c> beside the stack. ⚠ A placeholder in this
    ///     build — <see cref="LayerStackGraph" /> refuses one and names
    ///     <a href="https://github.com/Rikarin/Vixen/issues/574">#574</a>, which is M9.
    /// </summary>
    Paint = 1,

    /// <summary>An adjustment over everything under it.</summary>
    Filter = 2,

    /// <summary>A stack with one mask, which is how twenty layers stay legible.</summary>
    Group = 3
}

/// <summary>How a fill layer's source is put onto the surface.</summary>
/// <remarks>
///     ⚠ <b>Modelled, and only <see cref="Uv" /> compiles in this build.</b> Triplanar and planar are
///     a projection of a <em>world</em> position onto a UV atlas, so they need the position mesh map
///     § D12 bakes and a node that reads it — which is M8's
///     <a href="https://github.com/Rikarin/Vixen/issues/573">#573</a>. The field exists here because
///     a <c>.vxlayers</c> is a file people merge and adding a member to it later rewrites every one
///     that exists; refusing the two values is a message rather than a silent UV projection.
/// </remarks>
enum LayerProjection {
    /// <summary>The mesh's own UVs — the atlas, one to one.</summary>
    Uv = 0,

    /// <summary>Three planar projections blended by the world normal. M8.</summary>
    Triplanar = 1,

    /// <summary>One planar projection along an axis. M8.</summary>
    Planar = 2
}

/// <summary>Where a fill layer's pixels come from.</summary>
enum LayerFillSource {
    /// <summary>One colour per channel, from <see cref="LayerAsset.Values" />.</summary>
    Constant = 0,

    /// <summary>An imported image per channel, from <see cref="LayerAsset.Textures" />.</summary>
    Texture = 1,

    /// <summary>
    ///     A published <c>.vxtexgraph</c>, by reference. ⚠ Not compiled in this build — a graph fill
    ///     is a sub-graph inlined into the stack's graph, which needs the
    ///     <c>ISubGraphSource</c> a project supplies, and that arrives with M8's generators
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/573">#573</a>).
    /// </summary>
    Graph = 2
}

/// <summary>Which adjustment a filter layer applies to everything under it.</summary>
/// <remarks>
///     Doc 48 § D10 names "levels, HSL, blur, a graph with an <c>Input</c>". The first three are node
///     types this build has; the fourth is <see cref="LayerFillSource.Graph" />'s question and has the
///     same answer.
/// </remarks>
enum LayerFilterKind {
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
enum LayerMaskSource {
    /// <summary>No mask: the layer writes wherever its own alpha lets it.</summary>
    None = 0,

    /// <summary>One number over the whole surface.</summary>
    /// <remarks>
    ///     ⚠ <b>Compiled as a real <c>Source/Uniform</c> and a real shuffle rather than folded into
    ///     the opacity.</b> The two are arithmetically the same and folding would make the mask path
    ///     unreachable for exactly the case a test can check without a texture — which is how a mask
    ///     that never worked ships green. The fold is worth having and is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/789">#789</a>.
    /// </remarks>
    Constant = 1,

    /// <summary>An imported image, by the reference a host resolves.</summary>
    Texture = 2,

    /// <summary>
    ///     Another layer's evaluated result, by <see cref="LayerAsset.Id" /> — what makes the stack a
    ///     DAG rather than a list.
    /// </summary>
    Anchor = 3,

    /// <summary>
    ///     A generator: a shipped <c>.vxtexgraph</c> reading the mesh maps by usage. M8,
    ///     <a href="https://github.com/Rikarin/Vixen/issues/573">#573</a>.
    /// </summary>
    Generator = 4
}

/// <summary>Which of doc 48 § 4.2's sixteen operators a layer composites with.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The names and the numbers are <c>Vixen.Editor.TextureGraph</c>'s
///         <c>TextureBlendMode</c>, which is <c>internal</c> — so this is a second declaration of one
///         list and the two can drift.</b> Nothing in the compilation would notice: a layer hands
///         <c>Colour/Blend</c>'s <c>Mode</c> setting the <em>name</em>, and a name that assembly does
///         not know falls back to its own default, which is <c>Copy</c> — a stack whose every overlay
///         silently became a copy.
///     </para>
///     <para>
///         <b>So the agreement is derived rather than declared.</b>
///         <c>LayerBlendModeTests</c> reflects the real enum out of the evaluator assembly and
///         compares both directions, and a second test compiles one layer per mode and asserts the
///         compiler reported nothing — because reflection proves the names match and only a
///         compilation proves the name is the one the node reads.
///     </para>
/// </remarks>
enum LayerBlendMode {
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
    SignedAdd = 15
}

/// <summary>One layer's mask: where it comes from, and what it is worth.</summary>
/// <remarks>
///     ⚠ <b>One source and not yet a stack.</b> Doc 48 § D10 says a mask is itself a small stack —
///     a paint mask, a generator, a filter and an anchor — and M8
///     (<a href="https://github.com/Rikarin/Vixen/issues/573">#573</a>) is where that stack lands.
///     What is here is the one shape M7 needs to prove the compile: a single source, multiplied into
///     the layer's alpha before the blend.
/// </remarks>
sealed record MaskAsset {
    /// <summary>Where the mask comes from.</summary>
    public LayerMaskSource Source { get; init; } = LayerMaskSource.None;

    /// <summary>The number, for <see cref="LayerMaskSource.Constant" />.</summary>
    public float Value { get; init; } = 1f;

    /// <summary>The imported image, for <see cref="LayerMaskSource.Texture" />.</summary>
    public string Asset { get; init; } = "";

    /// <summary>The <see cref="LayerAsset.Id" /> read, for <see cref="LayerMaskSource.Anchor" />.</summary>
    public string Anchor { get; init; } = "";

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
sealed record LayerAsset {
    /// <summary>A stable identity, unique in the stack. What an anchor names.</summary>
    /// <remarks>
    ///     ⚠ <b>Stable across renames and across reorders, which is why it is not the name and not
    ///     the index.</b> An anchor is a reference into the same file, and both of the obvious keys
    ///     move when an artist drags a layer.
    /// </remarks>
    public string Id { get; init; } = "";

    /// <summary>What the artist calls it.</summary>
    public string Name { get; init; } = "";

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

    /// <summary>Which adjustment a <see cref="LayerKind.Filter" /> layer applies.</summary>
    public LayerFilterKind Filter { get; init; } = LayerFilterKind.Levels;

    /// <summary>The filter's numbers, by the port name the node declares.</summary>
    /// <remarks>
    ///     ⚠ <b>By port name rather than as a typed record per filter.</b> Five filters with five
    ///     records is five more shapes in a file format, and the node the number reaches already
    ///     names its own ports — so a wrong name is a compiler diagnostic against the node rather
    ///     than a silently ignored member. <c>LayerStackGraph</c> writes only the ports the chosen
    ///     filter declares and reports the rest.
    /// </remarks>
    public Dictionary<string, float[]> Settings { get; init; } = [];

    /// <summary>The mask.</summary>
    public MaskAsset Mask { get; init; } = new();

    /// <summary>
    ///     The <c>.vxpaint</c> holding a <see cref="LayerKind.Paint" /> layer's pixels, relative to
    ///     the stack.
    /// </summary>
    public string Paint { get; init; } = "";

    /// <summary>A group's layers, bottom first.</summary>
    public List<LayerAsset> Children { get; init; } = [];

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
sealed record ChannelAsset {
    /// <summary>
    ///     What the map is for. One of the nine <c>Output/Output</c> accepts; a spelling it does not
    ///     know is a compiler diagnostic against that node rather than a second list here.
    /// </summary>
    public string Usage { get; init; } = "baseColor";

    /// <summary>What the stack starts from, before any layer. Four numbers, linear.</summary>
    public float[] Default { get; init; } = [0f, 0f, 0f, 1f];
}

/// <summary>One material slot on the mesh, and the maps it produces.</summary>
sealed record TextureSetAsset {
    /// <summary>The slot's name on the mesh.</summary>
    public string Name { get; init; } = "";

    /// <summary>The maps this set produces, in the order a bake writes them.</summary>
    public List<ChannelAsset> Channels { get; init; } = [];

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
sealed record LayerStackAsset {
    /// <summary>What this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The format's version.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>What the stack is called.</summary>
    public string Name { get; init; } = "";

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
