// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Rendering.Compositor;

namespace Vixen.Editor.AssetEditors.Compositor;

/// <summary>What kind of editor one of a compositor node's settings wants.</summary>
public enum CompositorFieldKind {
    /// <summary>One name — a target, a shader, a stage.</summary>
    Text,

    /// <summary>Several names, comma separated.</summary>
    Names,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A flag.</summary>
    Toggle,

    /// <summary>One of a fixed set of names.</summary>
    Choice
}

/// <summary>One of a compositor node's settings, as its type declares it.</summary>
/// <param name="Key">What it is stored under — a port name in <c>Texts</c> or in <c>Values</c>.</param>
/// <param name="Label">What the row is called.</param>
/// <param name="Kind">Which editor it wants.</param>
/// <param name="Help">One sentence, shown on hover.</param>
/// <param name="Options">The choices, for <see cref="CompositorFieldKind.Choice" />.</param>
/// <param name="Fallback">The number a <see cref="CompositorFieldKind.Number" /> starts at.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Declared on the node type rather than emitted by the generator.</b> The node-graph
///         generator reads <c>[Input]</c> and <c>[Output]</c> fields, whose declared type <i>is</i>
///         the port's kind — and every kind it has is a number or a texture, because that is what a
///         shader graph and a VFX graph are made of. A compositor's settings are mostly names, which
///         are not ports at all: nothing connects to them and no edge carries them. Rather than
///         teach the generator a port kind that cannot be wired, each node says what it holds.
///     </para>
///     <para>
///         The cost is a list per node type that has to agree with what <c>Emit</c> reads. It is one
///         file, and the alternative — a node kind that appears on the canvas as a socket nobody can
///         connect a wire to — is a worse thing to explain.
///     </para>
/// </remarks>
public sealed record CompositorField(
    string Key,
    string Label,
    CompositorFieldKind Kind,
    string Help = "",
    string[]? Options = null,
    float Fallback = 0f
);

/// <summary>What the nodes that declare rather than draw contribute to a frame.</summary>
/// <remarks>
///     A resource, a buffer, a stage and the view block are all things a frame <i>has</i> rather than
///     things it <i>does</i>, so they are nodes with no flow ports: they sit on the canvas, are
///     collected wherever they are, and take no part in the chain. Modelling them as a side panel
///     instead would have meant a second editor, a second undo path and a second thing to serialise.
/// </remarks>
public sealed class CompositorDeclarations {
    /// <summary>The transient targets.</summary>
    public List<RenderResourceAsset> Resources { get; } = [];

    /// <summary>The transient buffers.</summary>
    public List<RenderBufferAsset> Buffers { get; } = [];

    /// <summary>The stages nodes refer to by name.</summary>
    public List<RenderStageAsset> Stages { get; } = [];

    /// <summary>The per-view block, if a node declared one.</summary>
    public ViewBlockAsset? ViewBlock { get; set; }
}

/// <summary>A node of a graphics-compositor graph: something that becomes part of a frame.</summary>
/// <remarks>
///     <para>
///         <b>The graph is a chain, and a container is a branch off it.</b> Every other graph this
///         framework carries is data flow — a node hands the next one a value — and a frame is not
///         that: a frame is a <i>sequence</i>, and a render pass is a sequence nested inside one. So
///         a compositor node has one <see cref="Flow" /> input and one <see cref="Flow" /> output,
///         and the chain of them is the order; a node that contains others has a second flow output
///         that starts an inner chain.
///     </para>
///     <para>
///         ⚠ <b>Order comes from the edges and not from where the nodes sit.</b> Laying a graph out
///         by eye and having that decide the frame would make dragging a node for legibility a change
///         to the rendering — which is the bug every "the order is top to bottom" editor has.
///     </para>
/// </remarks>
public abstract class CompositorNode : Node {
    /// <summary>The settings this node type holds, in the order a panel should draw them.</summary>
    public virtual IReadOnlyList<CompositorField> Fields => [];

    /// <summary>Adds whatever this node declares to the frame. Nothing, for a node that draws.</summary>
    /// <param name="declarations">What is being collected.</param>
    protected internal virtual void Contribute(CompositorDeclarations declarations) {
    }

    /// <summary>Produces this node's part of the tree.</summary>
    /// <param name="children">What this node's inner chain produced, empty for a node with none.</param>
    /// <returns>The renderer, or <see langword="null" /> for a node that only declares.</returns>
    protected internal virtual ISceneRendererAsset? Emit(IReadOnlyList<ISceneRendererAsset> children) => null;

    /// <summary>The text one of this node's settings carries.</summary>
    /// <param name="key">The setting's key.</param>
    /// <returns>What the author typed, trimmed.</returns>
    protected string Text(string key) => Binding.Text(key).Trim();

    /// <summary>A setting read as a list of names.</summary>
    /// <param name="key">The setting's key.</param>
    /// <returns>The names, empty entries dropped.</returns>
    /// <remarks>
    ///     Comma separated, for the reason <c>AddressableEdits.Labels</c> gives: a list editor is a
    ///     real gap in the inspector, and inventing a bespoke one per node would be worse than the
    ///     form people already type.
    /// </remarks>
    protected string[] Names(string key) =>
        Binding.Text(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A numeric setting as an integer.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="fallback">What to use when nothing has been set.</param>
    /// <returns>The number.</returns>
    protected int Whole(string key, int fallback) {
        var lanes = Binding.Value(key);
        return lanes.Length == 0 ? fallback : (int) MathF.Round(lanes[0]);
    }

    /// <summary>A numeric setting as a float.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="fallback">What to use when nothing has been set.</param>
    /// <returns>The number.</returns>
    protected float Number(string key, float fallback) {
        var lanes = Binding.Value(key);
        return lanes.Length == 0 ? fallback : lanes[0];
    }

    /// <summary>A flag setting.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="fallback">What to use when nothing has been set.</param>
    /// <returns>Whether it is on.</returns>
    protected bool Flag(string key, bool fallback) {
        var lanes = Binding.Value(key);
        return lanes.Length == 0 ? fallback : lanes[0] != 0f;
    }

    /// <summary>An enum setting, resolved from the name the author chose.</summary>
    /// <typeparam name="TEnum">The enum.</typeparam>
    /// <param name="key">The setting's key.</param>
    /// <param name="fallback">What to use when the text names nothing.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    ///     ⚠ <b>By name rather than by ordinal.</b> An ordinal in a saved graph is a number that
    ///     moves when somebody inserts a member into the enum, which would silently change what every
    ///     saved frame does — with no diff to look at, because the file did not change.
    /// </remarks>
    protected TEnum Choice<TEnum>(string key, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(Text(key), ignoreCase: true, out var value) ? value : fallback;

    /// <summary>What this node is called, falling back to something rather than to nothing.</summary>
    /// <param name="fallback">What to call it when the author has not.</param>
    /// <returns>The name.</returns>
    /// <remarks>
    ///     A name is a debug group and a line in a frame capture, so an unnamed node producing an
    ///     empty string would make a capture a column of blanks.
    /// </remarks>
    protected string Named(string fallback) {
        var name = Text("Name");
        return name.Length > 0 ? name : fallback;
    }
}
