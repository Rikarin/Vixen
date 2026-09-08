// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.NodeGraph;

/// <summary>Which way a port faces.</summary>
public enum PortDirection {
    /// <summary>Into the node. At most one edge may arrive at it.</summary>
    Input,

    /// <summary>Out of it. Any number of edges may leave.</summary>
    Output
}

/// <summary>One port of a node type.</summary>
/// <param name="Name">What it is called. A saved graph names its edges by this.</param>
/// <param name="Direction">Which way it faces.</param>
/// <param name="Kind">What it carries.</param>
/// <param name="Default">
///     The value an unconnected input takes, one float per lane, or empty when it has none.
/// </param>
/// <param name="Summary">One line saying what it means.</param>
/// <remarks>
///     <b>Named rather than numbered, and that is a compatibility decision.</b> A node type that gains
///     a port must not renumber the ones a saved graph is already connected to, and an author who
///     renames a field would otherwise silently move every edge that pointed at it. Ports are matched
///     by name on load; a name that is gone is an edge that is dropped, with a diagnostic, which is
///     the failure that can be understood.
/// </remarks>
public sealed record PortDefinition(
    string Name,
    PortDirection Direction,
    PortKind Kind,
    ImmutableArray<float> Default = default,
    string Summary = ""
) {
    /// <summary>The default, or an empty span when the port has none.</summary>
    public ImmutableArray<float> Default { get; } = Default.IsDefault ? [] : Default;
}

/// <summary>What a setting's text means, and therefore what a row edits it with.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A setting is stored as text whatever this says, and that is not a compromise.</b> The
///         storage is <see cref="GraphNode.Texts" /> — one string per key, which is what survives a
///         save, a merge and a node type that later renamed a member. This says how to
///         <em>read</em> that string, so an inspector can offer a checkbox instead of a box in which
///         <c>ture</c> is a value.
///     </para>
///     <para>
///         <b>Four rather than one per <see cref="PortKind" />.</b> A setting that held a vector or a
///         colour would be a port; what settings actually hold across the three graphs in this
///         repository is a name, a flag, a count or a knob.
///     </para>
/// </remarks>
public enum SettingKind {
    /// <summary>A name, a path or a Raven expression. The default, and what every setting was.</summary>
    Text,

    /// <summary>A flag, written <c>true</c> or <c>false</c>.</summary>
    Bool,

    /// <summary>A whole number.</summary>
    Int,

    /// <summary>A number, which with a range is a slider.</summary>
    Float
}

/// <summary>One of a node type's settings: a name it was given rather than a value it was wired.</summary>
/// <param name="Name">What it is called, and the key it is stored under in <see cref="GraphNode.Texts" />.</param>
/// <param name="Default">What it says when the author has not said anything.</param>
/// <param name="Summary">One line saying what it means.</param>
/// <param name="Kind">How its text is read.</param>
/// <param name="Minimum">The bottom of its range, or negative infinity for none.</param>
/// <param name="Maximum">The top of it, or positive infinity for none.</param>
/// <param name="Group">Which section of an inspector it belongs to, or empty for the ungrouped ones.</param>
/// <param name="Accepted">
///     Every value this setting may hold, or empty when it may hold any name.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>Deliberately not a <see cref="PortDefinition" /> with a tenth
///         <see cref="PortKind" />.</b> A setting has no direction, no socket and no edge — see
///         <see cref="SettingAttribute" /> — and folding it into the port list would mean every
///         consumer that walks ports had to remember which of them could not be connected to.
///     </para>
///     <para>
///         ⚠ <b>The last four exist because a published graph's parameters lost them crossing this
///         boundary — <a href="https://github.com/Rikarin/Vixen/issues/730">#730</a>.</b> Doc 48 § D9
///         says an exposed parameter is "a name, a type, a default, a range and a group"; three of
///         the five fitted here, so a parameter declared <c>0…1</c> drew as a text box and a
///         <c>bool</c> one drew as a box in which <c>ture</c> is a value. The workaround was folding
///         the range into the <see cref="Summary" />, which put it in a tooltip and nowhere a row
///         could act on.
///     </para>
///     <para>
///         <b>Every one of them is optional and the record's old three-argument shape still
///         compiles</b>, which is what keeps a setting that is genuinely a name — a menu path, an
///         expression, an asset reference — exactly as cheap to declare as it was.
///     </para>
///     <para>
///         ⚠ <b><see cref="Accepted" /> is the fifth, and it exists because a set of legal names had
///         no way across this boundary at all —
///         <a href="https://github.com/Rikarin/Vixen/issues/964">#964</a>.</b> A node whose setting is
///         one of nine measurements said so in its <see cref="Summary" /> prose and in the sentence it
///         refuses a tenth with, and every consumer that wanted to <em>offer</em> the nine — the node
///         inspector, a plugin's own panel — had to write them down again. Five exact-equality roll
///         calls in this workstream have gone red on a second transcription of a known set, and a
///         picker that disagrees with the compiler's refusal is worse than a text box, because the
///         disagreement is silent in the direction the author cannot see.
///     </para>
/// </remarks>
public sealed record SettingDefinition(
    string Name,
    string Default = "",
    string Summary = "",
    SettingKind Kind = SettingKind.Text,
    float Minimum = float.NegativeInfinity,
    float Maximum = float.PositiveInfinity,
    string Group = "",
    ImmutableArray<string> Accepted = default
) {
    /// <summary>Every value this setting may hold, in declaration order, or empty for any name.</summary>
    /// <remarks>
    ///     ⚠ <b>Normalised out of <c>default</c>, for <see cref="NodeTypeDefinition.Settings" />'s
    ///     reason.</b> An <see cref="ImmutableArray{T}" /> parameter with no argument is not an empty
    ///     array but an uninitialised one, and every member on it throws — so a consumer asking a
    ///     setting that declares nothing what it accepts would fault rather than be told "anything".
    /// </remarks>
    public ImmutableArray<string> Accepted { get; } = Accepted.IsDefault ? [] : Accepted;

    /// <summary>Whether this setting is edited between two stated numbers.</summary>
    /// <remarks>
    ///     ⚠ <b>Both ends finite <em>and</em> a numeric kind.</b> A range on a
    ///     <see cref="SettingKind.Text" /> setting is a declaration that disagrees with itself, and a
    ///     slider drawn for one would write numbers into a field a compiler reads as a name.
    /// </remarks>
    public bool IsBounded =>
        Kind is SettingKind.Int or SettingKind.Float && float.IsFinite(Minimum) && float.IsFinite(Maximum);

    /// <summary>Whether this setting is chosen from a stated list rather than typed.</summary>
    /// <remarks>
    ///     ⚠ <b>A text kind <em>and</em> a non-empty list</b>, the same shape
    ///     <see cref="IsBounded" /> has. A list on a <see cref="SettingKind.Bool" /> is a declaration
    ///     that disagrees with itself — a checkbox already enumerates its two — and a dropdown drawn
    ///     over a numeric setting would write a label where a number is parsed.
    /// </remarks>
    public bool IsChoice => Kind == SettingKind.Text && Accepted.Length > 0;

    /// <summary>Whether a written value is one this setting accepts.</summary>
    /// <param name="value">What the author wrote.</param>
    /// <returns>
    ///     Whether <paramref name="value" /> is one of <see cref="Accepted" /> — and true for every
    ///     value when the setting states no list, because a setting that accepts anything accepts
    ///     this.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Ordinal, because these are stored names rather than words.</b> A setting's value
    ///         is what a saved graph holds and what a compiler matches, so a culture in which
    ///         <c>id</c> uppercases to something else must not decide whether a graph compiles.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And case-insensitively, which it was not — every refusal this mirrors ignores
    ///         case.</b> <c>TextureSettings.Enum&lt;T&gt;</c> parses with <c>ignoreCase: true</c>,
    ///         and both nodes that walk a list of their own compare with
    ///         <see cref="StringComparison.OrdinalIgnoreCase" />; a graph holding <c>multiply</c>
    ///         compiles today. So an exact-case predicate here was <em>stricter than the thing it
    ///         exists to agree with</em>, and anything that had wired it in as a refusal would have
    ///         rejected graphs that compile — which is the reason it could not be wired in, rather
    ///         than an argument that it should not be.
    ///         <see cref="StringComparison.OrdinalIgnoreCase" /> is culture-independent too, so the
    ///         paragraph above is untouched by this.
    ///     </para>
    /// </remarks>
    public bool Accepts(string value) => Accepted.Length == 0 || Match(value) >= 0;

    /// <summary>The spelling this setting states for a written value.</summary>
    /// <param name="value">What the author wrote.</param>
    /// <returns>
    ///     The entry of <see cref="Accepted" /> that matches ignoring case; <paramref name="value" />
    ///     itself when the setting states no list; and an empty string when it states one and this is
    ///     not in it.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="Accepts" /> answers the wrong question, and this is why it had no
    ///         caller</b> — <a href="https://github.com/Rikarin/Vixen/issues/1044">#1044</a>. The two
    ///         refusals in this repository that would adopt the predicate — <c>TextureMeshMaps</c>
    ///         and <c>TextureUsages</c> — each walked the same list themselves, and neither could
    ///         use a <c>bool</c>: a graph written <c>Multiply</c> and one written <c>multiply</c>
    ///         have to compile to one thing, so what the caller needs back is the <em>stored</em>
    ///         spelling. <see cref="Accepts" /> threw that away, which read as a seam nobody had got
    ///         round to wiring and was a seam of the wrong shape.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two share a walk rather than one calling the other, and the empty-string case
    ///         is why.</b> Writing <c>Accepts</c> as <c>Canonical(value).Length > 0</c> is right for
    ///         every list anybody would declare and wrong for one containing <c>""</c> — the two
    ///         would then disagree about a value the list plainly states. A shared index costs one
    ///         private member and cannot drift, which is the whole reason #1044 asked for one list.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A setting that states no list answers with the value itself</b>, symmetric with
    ///         <see cref="Accepts" /> answering true for everything. A caller for which that is
    ///         wrong — one whose whole purpose is a closed set — should refuse an empty
    ///         <see cref="Accepted" /> at the point it reads the declaration rather than here; both
    ///         adopters do, and say so.
    ///     </para>
    /// </remarks>
    public string Canonical(string value) {
        if (Accepted.Length == 0) {
            return value;
        }

        var found = Match(value);

        return found < 0 ? "" : Accepted[found];
    }

    /// <summary>Where a written value sits in <see cref="Accepted" />, or -1.</summary>
    /// <remarks>
    ///     Ordinal and case-insensitive, for the reasons <see cref="Accepts" /> states at length:
    ///     these are stored names rather than words, and every refusal this mirrors ignores case.
    /// </remarks>
    int Match(string value) {
        for (var index = 0; index < Accepted.Length; index++) {
            if (string.Equals(Accepted[index], value, StringComparison.OrdinalIgnoreCase)) {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>One node type: what a graph can contain an instance of.</summary>
/// <param name="Path">The menu path, and the key a saved graph stores.</param>
/// <param name="Ports">Its ports, inputs first, in declaration order.</param>
/// <param name="Create">Makes an instance. Generated, so there is no reflection anywhere in this.</param>
/// <param name="Summary">One line saying what the node is for.</param>
/// <param name="Preview">Whether a view should draw a preview thumbnail for it.</param>
/// <param name="Settings">
///     The names it holds, in declaration order. Empty for the many node types that hold none.
/// </param>
public sealed record NodeTypeDefinition(
    string Path,
    ImmutableArray<PortDefinition> Ports,
    Func<Node> Create,
    string Summary = "",
    bool Preview = false,
    ImmutableArray<SettingDefinition> Settings = default
) {
    /// <summary>The settings, or an empty array for a type with none.</summary>
    public ImmutableArray<SettingDefinition> Settings { get; } = Settings.IsDefault ? [] : Settings;

    /// <summary>One setting by name.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The setting, or null when the type has no such setting.</returns>
    public SettingDefinition? Setting(string name) {
        foreach (var setting in Settings) {
            if (string.Equals(setting.Name, name, StringComparison.Ordinal)) {
                return setting;
            }
        }

        return null;
    }

    /// <summary>The last segment of <see cref="Path" />: what the node is called.</summary>
    public string Title => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>Everything before that: where it sits in the create menu.</summary>
    public string Category => Path.LastIndexOf('/') is var slash && slash < 0 ? "" : Path[..slash];

    /// <summary>One port by name.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="direction">Which way it faces.</param>
    /// <returns>The port, or null when the type has no such port.</returns>
    public PortDefinition? Port(string name, PortDirection direction) {
        foreach (var port in Ports) {
            if (port.Direction == direction && string.Equals(port.Name, name, StringComparison.Ordinal)) {
                return port;
            }
        }

        return null;
    }

    /// <summary>Whether any of its ports is dynamically typed.</summary>
    public bool IsDynamic {
        get {
            foreach (var port in Ports) {
                if (port.Kind == PortKind.Dynamic) {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
///     The base every node type derives from, and the half of it the generator writes.
/// </summary>
/// <remarks>
///     A node's ports are its <i>fields</i>, which is what makes a node declaration readable — the
///     alternative is a dictionary the node looks itself up in, and an <c>Emit</c> full of string
///     keys. <see cref="Bind" /> is what fills them, and it is generated from the same marked fields
///     the port metadata comes from, so the two cannot disagree.
/// </remarks>
public abstract class Node {
    /// <summary>What each port carries this time round.</summary>
    /// <remarks>
    ///     Set by the compiler just before the node is asked to do anything, and available so a node
    ///     can ask the one question its fields cannot answer — whether a port is <i>connected</i>.
    ///     An unconnected input still carries an expression, because it carries its default, and a
    ///     texture node that wants the mesh's own coordinate when nobody wired one has to be able to
    ///     tell the two apart.
    /// </remarks>
    public NodeBinding Binding { get; internal set; } = NodeBinding.Empty;

    /// <summary>Fills the port fields from what the compiler resolved. Generated.</summary>
    /// <param name="binding">The expressions the ports carry this time round.</param>
    public abstract void Bind(NodeBinding binding);
}

/// <summary>What each of a node's ports carries, for one compilation of one instance.</summary>
/// <remarks>
///     Handed to <see cref="Node.Bind" /> rather than reached out for, so a node cannot read a port it
///     did not declare and cannot read the graph at all. A node is a function from its inputs to its
///     outputs, and this is the argument list.
/// </remarks>
public sealed class NodeBinding {
    readonly Dictionary<string, string> inputs;
    readonly Dictionary<string, string> outputs;
    readonly Dictionary<string, float[]> values;
    readonly Dictionary<string, string> texts;
    readonly HashSet<string> connected;

    internal NodeBinding(
        Dictionary<string, string> inputs,
        Dictionary<string, string> outputs,
        Dictionary<string, float[]> values,
        Dictionary<string, string> texts,
        HashSet<string> connected,
        PortKind resolved
    ) {
        this.inputs = inputs;
        this.outputs = outputs;
        this.values = values;
        this.texts = texts;
        this.connected = connected;
        Resolved = resolved;
    }

    /// <summary>A binding with nothing in it, for a node nobody has compiled yet.</summary>
    public static NodeBinding Empty { get; } = new([], [], [], [], [], PortKind.Float);

    /// <summary>The constant an unconnected input carries, as lanes.</summary>
    /// <param name="name">The port's name.</param>
    /// <returns>Its lanes, padded to the resolved width; empty when the port is connected.</returns>
    /// <remarks>
    ///     <b>For a target that is not source text.</b> A shader graph reads
    ///     <see cref="Input" /> and interpolates it into a line of Raven; a VFX graph is compiling to
    ///     an array of operations whose parameters are <i>numbers</i>, and parsing them back out of a
    ///     literal it just formatted would be absurd. So the resolution happens once and both forms
    ///     are handed over.
    /// </remarks>
    public ReadOnlySpan<float> Value(string name) => values.TryGetValue(name, out var lanes) ? lanes : [];

    /// <summary>The text a setting was given on this node, or its declared default.</summary>
    /// <param name="name">The setting's name.</param>
    /// <returns>What the author typed.</returns>
    /// <remarks>
    ///     <para>
    ///         The other half of <see cref="Value" />, for the things made of names — see
    ///         <see cref="SettingAttribute" />. It is taken straight off the node rather than resolved
    ///         through an edge: a name is authored on the node that uses it, and there is no
    ///         expression to interpolate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Also answers for a key that is not declared at all.</b> The graphics compositor
    ///         predates <c>[Setting]</c> and keys its settings by hand off <c>CompositorField</c>, so
    ///         whatever a node carries in <see cref="GraphNode.Texts" /> is readable here whether or
    ///         not its type declares it.
    ///     </para>
    /// </remarks>
    public string Text(string name) => texts.TryGetValue(name, out var value) ? value : string.Empty;

    /// <summary>Whether an input has an edge arriving at it.</summary>
    /// <param name="name">The port's name.</param>
    /// <returns><see langword="true" /> if something is wired to it.</returns>
    /// <remarks>
    ///     Not the same as "carries an expression": an unconnected input carries its default, which
    ///     is a perfectly good expression and the wrong thing for a node that wanted to substitute
    ///     something of its own.
    /// </remarks>
    public bool IsConnected(string name) => connected.Contains(name);

    /// <summary>What this instance's dynamic ports resolved to.</summary>
    /// <remarks>
    ///     <see cref="PortKind.Float" /> for a node that has none, which is harmless: a node with no
    ///     dynamic ports has nothing to ask this about.
    /// </remarks>
    public PortKind Resolved { get; }

    /// <summary>The expression arriving at one input.</summary>
    /// <param name="name">The port's name.</param>
    /// <returns>The expression.</returns>
    /// <exception cref="KeyNotFoundException">
    ///     The node has no such input — which, since the caller is generated from the same fields the
    ///     definition is, means the generated code and the definition have come apart.
    /// </exception>
    public string Input(string name) => inputs[name];

    /// <summary>The variable one output writes to.</summary>
    /// <param name="name">The port's name.</param>
    /// <returns>The variable's name.</returns>
    /// <exception cref="KeyNotFoundException">The node has no such output.</exception>
    public string Output(string name) => outputs[name];
}
