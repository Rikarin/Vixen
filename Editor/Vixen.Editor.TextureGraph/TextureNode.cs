// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph;

/// <summary>How many channels one image edge carries: doc 48 § Part 4's <em>format</em>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the half of § Part 4's first rule that could not live where the document puts
///         it.</b> The rule reads "grey and colour are one port kind, and grey promotes" — and the
///         kind is <see cref="PortKind.Image" />, which carries no format at all, deliberately: a
///         <c>PortKind</c> is shared by three graphs and <see cref="PortKinds.Accepts" /> therefore
///         says yes to every image-to-image wire. So the promotion and the refusal are decided
///         <em>here</em>, by the compiler that knows what each edge is carrying — exactly the
///         division <see cref="PortKind.Dynamic" /> already makes, whose width is resolved by a
///         compiler rather than by the enum.
///     </para>
///     <para>
///         <b>Ordered by width, and that is not decoration.</b>
///         <see cref="TextureGraphCompiler" /> resolves a node's channels as the <c>Max</c> over what
///         arrives at its image inputs, which is <see cref="PortKinds.Resolve" />'s rule reused
///         rather than a second type system.
///     </para>
/// </remarks>
enum TextureChannels {
    /// <summary>One channel: a mask, a height, a roughness. Stored as <see cref="TextureFormat.R16Float" />.</summary>
    Grey = 1,

    /// <summary>Four: a colour with its alpha. Stored as <see cref="TextureFormat.Rgba16Float" />.</summary>
    Colour = 4
}

/// <summary>
///     Where a node writes its dispatches, and what it may ask the plan for.
/// </summary>
/// <remarks>
///     <para>
///         <b>A node appends ops; it does not build a plan.</b> It has no idea what index its images
///         got, what ran before it or what a pool schedule is — it asks for the image arriving at one
///         of its ports, asks for one to write, and lists the dispatches between them. Everything
///         structural is <see cref="TextureGraphCompiler" />'s, which is what lets a node be twenty
///         lines and a plugin's node be twenty lines too. It is <c>RavenEmitter</c>'s shape one graph
///         over.
///     </para>
///     <para>
///         ⚠ <b>Numbers arrive as numbers rather than as the port field's text.</b> A plan's
///         parameter is a <see cref="float" />, and parsing one back out of a literal the compiler
///         had just formatted would be absurd — <see cref="NodeBinding.Value" /> says so in its own
///         remarks, and this is the consumer it was describing. The <c>[Input] public Scalar</c>
///         field is still what the generator reads the port's name, kind and default off; what it
///         holds during a compilation is text nothing in this graph interpolates.
///     </para>
/// </remarks>
sealed class TextureEmitter {
    readonly TextureGraphCompiler compiler;

    GraphNode node = null!;
    NodeBinding binding = NodeBinding.Empty;

    internal TextureEmitter(TextureGraphCompiler compiler) => this.compiler = compiler;

    /// <summary>Points the emitter at the node about to run.</summary>
    internal void Enter(GraphNode current, NodeBinding bound, TextureChannels resolved, int level) {
        node = current;
        binding = bound;
        Resolved = resolved;
        Level = level;
    }

    /// <summary>What this node's image ports resolved to: the widest thing arriving at one.</summary>
    /// <remarks>
    ///     <see cref="TextureChannels.Grey" /> for a node with no image input connected, which is the
    ///     narrowest and therefore the one that promotes into anything later — the same answer
    ///     <see cref="PortKinds.Resolve" /> gives for a node with nothing wired.
    /// </remarks>
    public TextureChannels Resolved { get; private set; } = TextureChannels.Grey;

    /// <summary>What this node's image ports resolved to in <em>size</em>: the finest thing arriving.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="Resolved" />'s twin, and
    ///         <a href="https://github.com/Rikarin/Vixen/issues/779">#779</a> is what a library
    ///         without it looks like.</b> Every allocation a node made was at the graph's base level,
    ///         so a node downstream of a <c>Space/Resample</c> wrote a base-sized image while reading
    ///         a smaller one — and every pointwise kernel reads coordinate-for-coordinate with a
    ///         clamp, so three quarters of the output was the source's clamped edge. It was not one
    ///         node: the two nodes that had learned to ask were the exception, and about forty had
    ///         not.
    ///     </para>
    ///     <para>
    ///         <b>The finest of what arrives, because a promotion must not throw detail away</b> —
    ///         which is <see cref="Resolved" />'s "widest wins" over the other axis. An input coarser
    ///         than this is resampled up by an op the compiler inserts, exactly as a grey image
    ///         feeding a colour port is splatted by one.
    ///     </para>
    ///     <para>
    ///         Zero — the graph's base — for a node with no image input connected, which is every
    ///         source node.
    ///     </para>
    /// </remarks>
    public int Level { get; private set; }

    /// <summary>The width of the image this node writes by default, in texels at this bake.</summary>
    /// <remarks>
    ///     ⚠ <b>At <see cref="Level" /> rather than at the graph's base.</b> The two nodes that read
    ///     this — a jump flood and a flood fill — use it for the <em>length</em> of the chain they
    ///     emit and for <see cref="TextureOp.EmittedForExtent" />, so a base-sized answer under a
    ///     half-resolution input would be too many halvings and a plan
    ///     <see cref="TexturePlan.Validate" /> refuses.
    /// </remarks>
    public int Width => compiler.Extent(compiler.AuthoredWidth, Level);

    /// <summary>Its height.</summary>
    public int Height => compiler.Extent(compiler.AuthoredHeight, Level);

    /// <summary>What an author typed into an unconnected scalar port, or its declared default.</summary>
    /// <param name="port">The port's name.</param>
    /// <returns>Its first lane, or zero when the port carries nothing.</returns>
    public float Number(string port) => Number(port, 0);

    /// <summary>One lane of a vector port's value.</summary>
    /// <param name="port">The port's name.</param>
    /// <param name="lane">Which lane, counted from zero.</param>
    /// <returns>The lane, or the last one the port carries when it is shorter than that.</returns>
    /// <remarks>
    ///     Padded with the last lane rather than with zero, which is
    ///     <c>ShaderGraphCompiler.Constant</c>'s rule and means the same thing here: a two-lane
    ///     default read as a colour is "and the same again", not "and then black".
    /// </remarks>
    public float Number(string port, int lane) {
        // ⚠ Before the port's own value, and only for the first lane. Doc 48 § Part 4's second rule —
        // every scalar parameter accepts a Raven expression over the graph's exposed parameters — is
        // implemented entirely here and in `TextureGraphCompiler.Bind`: a node asks for its number
        // the way it always did, and a port with an expression on it answers with what Raven folded.
        // A vector port's lanes are four fields and an expression is one number, so an expression on
        // one of those fills lane 0 and leaves the rest as authored rather than splatting silently.
        if (lane == 0 && compiler.Expression(node, port) is { } expression) {
            return expression;
        }

        var lanes = binding.Value(port);

        return lanes.Length == 0 ? 0f : lanes[Math.Min(lane, lanes.Length - 1)];
    }

    /// <summary>The same, rounded, for a port whose kind is <see cref="PortKind.Int" />.</summary>
    /// <param name="port">The port's name.</param>
    /// <returns>The integer.</returns>
    public int Integer(string port) => (int)MathF.Round(Number(port));

    /// <summary>The same, for a port whose kind is <see cref="PortKind.Bool" />.</summary>
    /// <param name="port">The port's name.</param>
    /// <returns>Whether it is set.</returns>
    public bool Flag(string port) => Number(port) != 0f;

    /// <summary>The text a setting was given on this node, or its declared default.</summary>
    /// <param name="setting">The setting's name.</param>
    /// <returns>What the author typed.</returns>
    public string Text(string setting) => binding.Text(setting);

    /// <summary>
    ///     The image arriving at one input, promoted to <see cref="Resolved" /> if it has to be.
    /// </summary>
    /// <param name="port">The port's name.</param>
    /// <returns>Its index in the plan's image table, or −1 when nothing usable arrives.</returns>
    /// <remarks>
    ///     <b>A grey image feeding a node that resolved to colour is splatted</b>, by a
    ///     <c>ChannelShuffle</c> op the compiler inserts and shares between every port that wanted the
    ///     same promotion — which is doc 48 § Part 4's "grey into a colour port splats", and what
    ///     stops the library needing a <c>BlendGrayscale</c> beside every <c>Blend</c>.
    /// </remarks>
    public int Read(string port) => compiler.Read(node, port, binding, Resolved, Level, strict: false);

    /// <summary>The image arriving at one input, which has to be a single channel.</summary>
    /// <param name="port">The port's name.</param>
    /// <returns>Its index in the plan's image table, or −1 when nothing usable arrives.</returns>
    /// <remarks>
    ///     <b>The other half of § Part 4's rule: colour into a grey port is a type error naming the
    ///     port.</b> For the nodes that <em>measure</em> rather than composite — a distance field, a
    ///     flood fill — there is no promotion that would mean anything, because there is no
    ///     luminance a colour and a mask agree on. A graph that wants one says so with a
    ///     <c>Grayscale</c> node.
    /// </remarks>
    public int ReadGrey(string port) =>
        compiler.Read(node, port, binding, TextureChannels.Grey, Level, strict: true);

    /// <summary>Allocates the image one output port carries, at this node's resolved channels.</summary>
    /// <param name="port">The output port's name.</param>
    /// <returns>Its index in the plan's image table.</returns>
    public int Write(string port) => Write(port, Resolved);

    /// <summary>Allocates the image one output port carries, at a stated number of channels.</summary>
    /// <param name="port">The output port's name.</param>
    /// <param name="channels">What it carries, for a node whose output is not its input's shape.</param>
    /// <param name="levelOffset">
    ///     How its size relates to the plan's base: <c>0</c> is the base, <c>1</c> is half,
    ///     <c>-1</c> is double. Left out, it is <see cref="Level" /> — the size of what this node
    ///     reads.
    /// </param>
    /// <returns>Its index in the plan's image table.</returns>
    /// <remarks>
    ///     <para>
    ///         For the nodes whose answer has a shape of its own whatever went in — a distance field
    ///         is grey however colourful its mask was, and a normal map is three channels however
    ///         grey its height was.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The level is what makes a <c>Resample</c> node possible at all</b>, and until
    ///         <a href="https://github.com/Rikarin/Vixen/issues/733">#733</a> there was no way to
    ///         spell it: every image a node allocated was at the plan's base, so a resample writing
    ///         its output at its input's size was an identity copy — the kernel takes no ratio,
    ///         because the target's size <em>is</em> the scale.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Absolute when it is given, and <see cref="Level" /> when it is not —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/779">#779</a>.</b> Defaulting to zero
    ///         made every node that had not heard of #733 write a base-sized image over a smaller
    ///         source, which is a corner crop with an edge smear in about forty nodes. A node that
    ///         means "half of what arrived" still spells it as <see cref="LevelOf" /> plus one,
    ///         because a bare <c>1</c> is half of the <em>graph's</em> base and the two differ the
    ///         moment anything upstream is scaled.
    ///     </para>
    /// </remarks>
    public int Write(string port, TextureChannels channels, int? levelOffset = null) =>
        compiler.Write(node, port, channels, levelOffset ?? Level);

    /// <summary>An image nothing outside this node reads: the middle of a separable filter.</summary>
    /// <param name="format">What it stores.</param>
    /// <param name="levelOffset">
    ///     Its size relative to the plan's base, as <see cref="Write(string,TextureChannels,int?)" />
    ///     — and <see cref="Level" /> when it is left out, because the middle of a filter is the size
    ///     of its ends.
    /// </param>
    /// <returns>Its index in the plan's image table.</returns>
    /// <remarks>
    ///     ⚠ <b>Named rather than reused, because an image in a plan is written exactly once</b> —
    ///     that is what makes its liveness the op order, and it is why a two-pass blur asks for one
    ///     of these rather than writing its output twice. The pool is what stops the count from
    ///     mattering: a scratch is freed the moment its last reader has run.
    /// </remarks>
    public int Scratch(TextureFormat format, int? levelOffset = null) =>
        compiler.Scratch(format, levelOffset ?? Level);

    /// <summary>Where one image this node can see sits, in levels from the authoring base.</summary>
    /// <param name="image">Its index in the plan's image table.</param>
    /// <returns>Its own level offset, without this bake's.</returns>
    /// <remarks>
    ///     What a node adds to when it wants an image <em>relative</em> to the one arriving at a
    ///     port. Zero for an image nobody has allocated and for every external one.
    /// </remarks>
    public int LevelOf(int image) => compiler.LevelOf(image);

    /// <summary>How big one image this node can see is at this bake, in texels.</summary>
    /// <param name="image">Its index in the plan's image table.</param>
    /// <returns>Its width and height.</returns>
    /// <remarks>
    ///     ⚠ <b>Nominal for an external image.</b> An imported bitmap's size is the caller's and is
    ///     not known during a compilation at all — every kernel clamps its taps to the source's own
    ///     dimensions precisely so that it need not be.
    /// </remarks>
    public Int2 SizeOf(int image) => compiler.SizeOf(image);

    /// <summary>Allocates an image the caller supplies, filled with texels this node baked.</summary>
    /// <param name="format">What it stores. ⚠ Need not be storable — nothing writes it.</param>
    /// <param name="channels">What it carries, for the promotion rule.</param>
    /// <param name="width">The picture's width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="texels">The bytes, tightly packed, top row first, in <paramref name="format" />.</param>
    /// <returns>Its index in the plan's image table, or −1 when the byte count does not fit.</returns>
    /// <remarks>
    ///     <b>Doc 48 § 4.1's ramps and tables.</b> A gradient and a curve are baked on the CPU by
    ///     <c>TextureRamp</c>, out of the one evaluator the editor already has for each, so that the
    ///     kernel reading the strip can never disagree with the control an artist dragged. The
    ///     compiler carries the bytes — see <c>TextureGraphExternal</c> — and
    ///     <c>TextureGraphExternals.Upload</c> is what puts them on a device.
    /// </remarks>
    public int External(
        TextureFormat format,
        TextureChannels channels,
        int width,
        int height,
        ReadOnlySpan<byte> texels
    ) =>
        compiler.External(node, format, channels, "", width, height, texels);

    /// <summary>Allocates an image the caller supplies, naming the asset that fills it.</summary>
    /// <param name="format">What the picture is expected to be stored as.</param>
    /// <param name="channels">What it carries, for the promotion rule.</param>
    /// <param name="asset">What the graph references — an imported image.</param>
    /// <returns>Its index in the plan's image table.</returns>
    /// <remarks>
    ///     ⚠ <b>The reference crosses and the pixels do not, because a compilation must not read an
    ///     asset database.</b> Compiling is what runs on every edit and what a preview is; opening a
    ///     file in it would make an edit cost an import. So a host walks
    ///     <c>TextureGraphCompiler.Externals</c>, reads what each entry names, and uploads it —
    ///     which is also what makes the same plan re-usable over a different picture.
    /// </remarks>
    public int External(TextureFormat format, TextureChannels channels, string asset) =>
        compiler.External(node, format, channels, asset, 0, 0, []);

    /// <summary>The storage one number of channels is kept in.</summary>
    /// <param name="channels">Grey or colour.</param>
    /// <returns>The format.</returns>
    public static TextureFormat FormatOf(TextureChannels channels) =>
        channels == TextureChannels.Grey ? TextureFormat.R16Float : TextureFormat.Rgba16Float;

    /// <summary>Appends one dispatch.</summary>
    /// <param name="op">The op.</param>
    public void Dispatch(TextureOp op) => compiler.Dispatch(op);

    /// <summary>Appends several, in the order given.</summary>
    /// <param name="ops">The ops.</param>
    public void Dispatch(ImmutableArray<TextureOp> ops) {
        foreach (var op in ops) {
            compiler.Dispatch(op);
        }
    }

    /// <summary>Records a kernel this node authored, so a host can find its source.</summary>
    /// <param name="kernel">The shader's name, which is what the op running it names.</param>
    /// <param name="source">The Raven.</param>
    /// <remarks>
    ///     For doc 48 § D6's Pixel Processor, and for nothing else so far: every other node in the
    ///     catalogue runs a kernel this assembly ships. See <c>TextureGraphKernel</c> for what a plan
    ///     holding one can and cannot do today.
    /// </remarks>
    public void Declare(string kernel, string source) => compiler.Declare(node, kernel, source);

    /// <summary>Keeps an image past the evaluation, under a usage a bake writes it by.</summary>
    /// <param name="image">Its index in the plan's image table.</param>
    /// <param name="usage">What the map is — <c>baseColor</c>, <c>normal</c>, <c>roughness</c>.</param>
    public void Keep(int image, string usage) => compiler.Keep(node, image, usage);

    /// <summary>Says something about this node, to somebody who can select it.</summary>
    /// <param name="id">A stable code.</param>
    /// <param name="message">What is wrong, as a person would say it.</param>
    /// <param name="port">Which of its ports, when it is about one.</param>
    /// <param name="severity">How much it matters.</param>
    /// <param name="span">
    ///     Which lines of a generated file it is about — for a node that <em>writes</em> one, which
    ///     so far is only the Pixel Processor. Empty for a complaint about the graph.
    /// </param>
    public void Report(
        string id,
        string message,
        string port = "",
        NodeSeverity severity = NodeSeverity.Error,
        NodeSpan span = default
    ) =>
        compiler.Say(new(id, message, node.Id, port, severity, span));
}

/// <summary>
///     A node of a texture graph: something that appends dispatches to a plan.
/// </summary>
/// <remarks>
///     Derives from <see cref="Node" /> and adds exactly one thing — what the node <i>does</i>. The
///     port machinery, the binding and the metadata are all the framework's, which is
///     <c>ShaderNode</c>'s bargain and the reason there is one node-graph framework rather than three.
/// </remarks>
abstract class TextureNode : Node {
    /// <summary>Appends whatever this node contributes.</summary>
    /// <param name="emitter">Where to append it.</param>
    /// <remarks>
    ///     Called once per instance per compilation, in dependency order, with every port field
    ///     already filled and every upstream node's images already allocated.
    /// </remarks>
    protected internal abstract void Compile(TextureEmitter emitter);
}
