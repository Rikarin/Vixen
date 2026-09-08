// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph;

/// <summary>What one exposed parameter holds.</summary>
/// <remarks>
///     <para>
///         <b>Three numbers and one name, and the line between them is folding versus
///         substitution.</b> The first three are numbers an author types and an <em>expression</em>
///         reads, and Raven has exactly three spellings of that — <c>float</c>, <c>int</c>,
///         <c>bool</c>. <see cref="Name" /> is none of them and is never folded: it is copied into an
///         inner node's setting before the walk begins, which is what lets it exist with no
///         <c>const val</c> behind it.
///     </para>
///     <para>
///         ⚠ <b>A colour is still not a fourth</b>, for the reason the first paragraph gives: it would
///         be four numbers under one name and there is no <c>const val</c> of a vector that
///         <see cref="TextureGraphExpressions" /> could fold. It is left out rather than declared and
///         refused at the point of use, and
///         <a href="https://github.com/Rikarin/Vixen/issues/1060">#1060</a> is where that half stays
///         open.
///     </para>
/// </remarks>
public enum TextureGraphParameterKind {
    /// <summary>A <c>float</c>.</summary>
    Scalar,

    /// <summary>An <c>int</c>.</summary>
    Integer,

    /// <summary>A <c>bool</c>, carried as zero or one.</summary>
    Boolean,

    /// <summary>A name, substituted into an inner node's setting rather than folded into arithmetic.</summary>
    /// <remarks>
    ///     ⚠ <b>The one kind an expression cannot read.</b> Every other member reaches
    ///     <see cref="TextureGraphExpressions" /> as a <c>const val</c> an expression may spell; this
    ///     one never does, because a name is not a number and a source that declared one would not
    ///     parse. What reads it is <see cref="TextureGraphParameters.IsReference" />'s convention, one
    ///     level down, on a <c>[Setting]</c> of a node <em>inside</em> the published graph.
    /// </remarks>
    Name
}

/// <summary>One knob a published graph has: doc 48 § D9's <c>[Setting]</c>-shaped parameter.</summary>
/// <param name="Name">
///     Its name, which is what an expression spells and what a containing graph stores. It has to be
///     a Raven identifier — see <see cref="TextureGraphParameters.Check" />.
/// </param>
/// <param name="Kind">Its type.</param>
/// <param name="Default">What it is worth when nobody overrides it.</param>
/// <param name="Minimum">The bottom of its range.</param>
/// <param name="Maximum">The top of it.</param>
/// <param name="Group">Which group of the inspector it belongs to, or empty for the ungrouped ones.</param>
/// <param name="Summary">One line saying what it means.</param>
/// <param name="Choice">
///     What a <see cref="TextureGraphParameterKind.Name" /> parameter is worth when nobody overrides
///     it. Ignored by every other kind, which defaults through <paramref name="Default" />.
/// </param>
/// <param name="Accepted">
///     Every name a <see cref="TextureGraphParameterKind.Name" /> parameter may hold, or empty for
///     any. It is what draws a picker and what <see cref="TextureGraphParameters.TryChoose" />
///     refuses an override against.
/// </param>
/// <remarks>
///     <para>
///         <b>A name, a type, a default, a range and a group — doc 48 § D9's list exactly.</b> The
///         range is not decoration: it is what an inspector draws a slider between, and it is what
///         <see cref="TextureGraphParameters.Read" /> refuses an override outside, because a
///         parameter whose declared range says <c>0…1</c> and whose value is <c>40</c> is a graph
///         whose author has been lied to about what it does.
///     </para>
///     <para>
///         ⚠ <b>The range crosses to the framework now rather than being described to it —
///         <a href="https://github.com/Rikarin/Vixen/issues/730">#730</a>, closed.</b> A
///         <see cref="SettingDefinition" /> was a name, a default and a summary, so
///         <see cref="TextureGraphParameters.Settings" /> folded the group and the two numbers into
///         the <em>summary</em> and an inspector drew a parameter declared <c>0…1</c> as a text box
///         with a helpful tooltip. It carries a kind, a range and a group of its own now. This record
///         is still what <em>enforces</em> the range, because refusing a value is a compiler's job
///         and a slider's bounds are a courtesy.
///     </para>
///     <para>
///         ⚠ <b>Two defaults, one per half of <see cref="TextureGraphParameterKind" />, and that is
///         a shape worth being uncomfortable about.</b> <see cref="Default" /> is a number and
///         <see cref="Choice" /> is a name; a <see cref="TextureGraphParameterKind.Name" /> parameter
///         reads the second and every other kind reads the first. Folding them into one member would
///         mean parsing a name out of a float or a float out of a name at every use, and
///         <see cref="TextureGraphParameters.Check" /> is what stops the unread one from being set to
///         something that reads as a disagreement.
///     </para>
/// </remarks>
public sealed record TextureGraphParameter(
    string Name,
    TextureGraphParameterKind Kind = TextureGraphParameterKind.Scalar,
    float Default = 0f,
    float Minimum = float.NegativeInfinity,
    float Maximum = float.PositiveInfinity,
    string Group = "",
    string Summary = "",
    string Choice = "",
    ImmutableArray<string> Accepted = default
) {
    /// <summary>Every name this parameter may hold, in declaration order, or empty for any.</summary>
    /// <remarks>
    ///     ⚠ <b>Normalised out of <c>default</c> <em>and</em> out of empty, both for
    ///     <see cref="SettingDefinition.Accepted" />'s reasons.</b> An
    ///     <see cref="ImmutableArray{T}" /> parameter with no argument is an uninitialised array
    ///     rather than an empty one and every member on it throws; and two empty ones built
    ///     differently are unequal, because the type compares by the identity of the array it wraps —
    ///     which makes two records that state the same thing compare unequal.
    /// </remarks>
    public ImmutableArray<string> Accepted { get; } =
        Accepted.IsDefaultOrEmpty ? ImmutableArray<string>.Empty : Accepted;

    /// <summary>Whether this parameter is a name chosen from a stated list rather than typed.</summary>
    /// <remarks>
    ///     ⚠ <b>A name kind <em>and</em> a non-empty list</b>, which is
    ///     <see cref="SettingDefinition.IsChoice" />'s shape and reaches it unchanged: a list on a
    ///     numeric parameter is a declaration that disagrees with itself, and a dropdown drawn over
    ///     one would write a label where a number is parsed.
    /// </remarks>
    public bool IsChoice => Kind == TextureGraphParameterKind.Name && Accepted.Length > 0;

    /// <summary>The spelling this parameter states for a written name.</summary>
    /// <param name="value">What the author wrote.</param>
    /// <returns>
    ///     The entry of <see cref="Accepted" /> that matches ignoring case; <paramref name="value" />
    ///     itself when the parameter states no list; and an empty string when it states one and this
    ///     is not in it.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>The stored spelling and not a <see cref="bool" />, which is
    ///     <see cref="SettingDefinition.Canonical" />'s shape and is taken for its reason</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1044">#1044</a>. What this answer is
    ///     written into is an inner node's setting, read by <c>TextureSettings.Enum</c> with
    ///     <c>ignoreCase: true</c>: a predicate would let <c>blend</c> through and leave <c>blend</c>
    ///     in the file, so the graph and the picker would then disagree about a value both accept.
    /// </remarks>
    public string Canonical(string value) {
        if (Accepted.Length == 0) {
            return value;
        }

        foreach (var accepted in Accepted) {
            if (string.Equals(accepted, value, StringComparison.OrdinalIgnoreCase)) {
                return accepted;
            }
        }

        return "";
    }
    /// <summary>Whether a value is inside the declared range.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    public bool Holds(float value) => value >= Minimum && value <= Maximum;

    /// <summary>How this parameter's value is spelled in Raven.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The literal.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <c>f</c> suffix is load-bearing and its absence is silent.</b>
    ///         <c>const val amount: float = 0.5</c> declares a <c>float</c> whose folded constant is
    ///         a <b>double</b> — Raven converts on the way into the field and the folder keeps the
    ///         literal's own type — so <c>amount * 8f</c> is then a binary operator over a double and
    ///         a float, which <c>ConstantEvaluator</c> does not fold. The result is not an error and
    ///         not a wrong number: it is <em>no</em> number, reported as "cannot fold", on an
    ///         expression that is perfectly good Raven. Measured, not reasoned: the probe that found
    ///         it printed <c>const=True value=0.5 type=float</c> beside <c>value=&lt;null&gt;</c> for
    ///         the expression that read it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why this is not <see cref="Literal.Of" />.</b> That one deliberately
    ///         writes no suffix, because a shader graph interpolates it into a context that types it;
    ///         here the literal <em>is</em> the type.
    ///     </para>
    /// </remarks>
    public string RavenLiteral(float value) =>
        Kind switch {
            TextureGraphParameterKind.Integer =>
                ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture),
            TextureGraphParameterKind.Boolean => value != 0f ? "true" : "false",
            _ => Literal.Of(value) + "f"
        };

    /// <summary>How an author's override of it is spelled in a saved graph.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public string Text(float value) =>
        Kind switch {
            TextureGraphParameterKind.Integer =>
                ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture),
            TextureGraphParameterKind.Boolean => value != 0f ? "true" : "false",
            _ => value.ToString("R", CultureInfo.InvariantCulture)
        };
}

/// <summary>
///     A graph's exposed parameters: what makes a published <c>.vxtexgraph</c> a node with knobs.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D9 in one sentence: a published graph is a node, and its parameters are its
///         settings.</b> <see cref="SubGraphs.Definition" /> already turns a graph's
///         <see cref="NodeGraphModel.Interface" /> into the node's <em>ports</em>;
///         <see cref="Definition" /> is the other half, and it is here rather than in the framework
///         because a parameter's type, range and group are a texture graph's vocabulary and no
///         other graph in this repository has asked for one.
///     </para>
///     <para>
///         ⚠ <b>Where the declarations live, and what that used to cost.</b> A parameter
///         <em>list</em> belongs to the graph and <c>NodeGraphModel</c> had nowhere to put one, so it
///         was a property of the compiler — exactly as <c>BaseWidth</c> and <c>Seed</c> were — and a
///         <c>.vxtexgraph</c> reopened with whatever knobs its host invented
///         (<a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>).
///         <see cref="NodeGraphModel.Parameters" /> is the home, <see cref="Declared" /> reads it and
///         <see cref="Settings" /> writes it. The <em>overrides</em> always had one: a sub-graph
///         node's <see cref="GraphNode.Texts" />, under the parameter's own name, which is what
///         <see cref="Read" /> reads.
///     </para>
/// </remarks>
public static class TextureGraphParameters {
    /// <summary>Checks a parameter list, and says what is wrong with it.</summary>
    /// <param name="parameters">The list.</param>
    /// <returns>One message per problem, in the order the list holds them, or empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The name is checked against Raven's identifier rules and not against C#'s.</b> A
    ///     parameter's name is emitted into a Raven source as a <c>const val</c>, so a name with a
    ///     hyphen in it produces a complaint about a file the author never wrote — and that
    ///     complaint would be mapped back to whichever node's expression happened to be on the line
    ///     after it.
    /// </remarks>
    public static ImmutableArray<string> Check(IReadOnlyList<TextureGraphParameter> parameters) {
        ArgumentNullException.ThrowIfNull(parameters);

        var problems = ImmutableArray.CreateBuilder<string>();
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var parameter in parameters) {
            if (!IsIdentifier(parameter.Name)) {
                problems.Add(
                    $"'{parameter.Name}' is not a name an expression could spell. A parameter's name is a Raven "
                    + "identifier: a letter or an underscore, then letters, digits and underscores."
                );

                continue;
            }

            if (!seen.Add(parameter.Name)) {
                problems.Add(
                    $"Two parameters are called '{parameter.Name}'. An expression naming it could mean either, "
                    + "and a setting is stored under its name."
                );

                continue;
            }

            if (parameter.Kind == TextureGraphParameterKind.Name) {
                // ⚠ A name parameter is checked against its own list and against nothing numeric.
                // `Default`, `Minimum` and `Maximum` are members it does not read, and running the
                // range checks below over one would refuse every name knob whose author left the
                // numbers alone — which is all of them.
                if (parameter.Accepted.Length == 0) {
                    // ⚠ Refused rather than allowed as a free-text knob, and `Declared`'s note is
                    // why: an empty list is what every parameter written before this kind existed
                    // has, so a name knob without one would be indistinguishable from a scalar knob
                    // saved by an older build.
                    problems.Add(
                        $"'{parameter.Name}' is a name and states no list of names. A name knob is forwarded "
                        + "into a setting inside the graph, and the list is both what draws its picker and what "
                        + "tells a scalar knob saved by an older build apart from this."
                    );

                    continue;
                }

                if (parameter.Choice.Length == 0 || parameter.Canonical(parameter.Choice).Length == 0) {
                    problems.Add(
                        $"'{parameter.Name}' defaults to '{parameter.Choice}', which is not one of the names it "
                        + $"accepts ({string.Join(", ", parameter.Accepted)})."
                    );
                }

                continue;
            }

            if (!float.IsFinite(parameter.Default)) {
                // ⚠ A parameter's default is written into a generated Raven source as a literal, and
                // there is no literal for a NaN. Left alone it would be an unparseable file and a
                // complaint about a line, blamed on whichever expression happened to sit under it.
                problems.Add(
                    $"'{parameter.Name}' defaults to {parameter.Default}, which is not a number an expression "
                    + "could be written against."
                );

                continue;
            }

            if (parameter.Minimum > parameter.Maximum) {
                problems.Add(
                    $"'{parameter.Name}' has a range of {parameter.Minimum}…{parameter.Maximum}, which holds "
                    + "nothing. Every value an author could type would be refused."
                );

                continue;
            }

            if (!parameter.Holds(parameter.Default)) {
                // ⚠ Said rather than clamped. A default outside its own range is a declaration that
                // disagrees with itself, and clamping it silently makes the node behave as though the
                // author had typed the edge of the slider — which is a plausible picture.
                problems.Add(
                    $"'{parameter.Name}' defaults to {parameter.Default}, which is outside its own range of "
                    + $"{parameter.Minimum}…{parameter.Maximum}."
                );
            }
        }

        return problems.ToImmutable();
    }

    /// <summary>The node type a published graph stands for: its interface, and its parameters.</summary>
    /// <param name="graph">The published graph.</param>
    /// <param name="parameters">Its exposed parameters.</param>
    /// <param name="path">The menu path, and the key a containing graph stores.</param>
    /// <returns>The type.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public static NodeTypeDefinition Definition(
        NodeGraphModel graph,
        IReadOnlyList<TextureGraphParameter> parameters,
        string path
    ) {
        ArgumentNullException.ThrowIfNull(parameters);

        var ports = SubGraphs.Definition(graph, path);

        // ⚠ Rebuilt rather than `with`-ed. `NodeTypeDefinition.Settings` is redeclared as a get-only
        // property over its own positional parameter — that is what normalises a default
        // `ImmutableArray` to empty — and a redeclared positional member has no setter, so a `with`
        // on it does not compile.
        return new(ports.Path, ports.Ports, ports.Create, ports.Summary, ports.Preview, Settings(parameters));
    }

    /// <summary>The parameters as the framework's settings.</summary>
    /// <param name="parameters">The parameters.</param>
    /// <returns>One setting each, in the order given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>All five of doc 48 § D9's fields now, which
    ///     <a href="https://github.com/Rikarin/Vixen/issues/730">#730</a> is the finding that they
    ///     were not.</b> A <see cref="SettingDefinition" /> was a name, a default and a sentence, so a
    ///     parameter declared <c>0…1</c> reached the inspector as a text box and its range reached it
    ///     as prose in a tooltip. The kind and the range are carried rather than described now, so a
    ///     summary is a summary again — and <c>Describe</c>, which was the fold, is deleted rather
    ///     than left behind. See the note beside <see cref="Kind" /> for why.
    /// </remarks>
    public static ImmutableArray<SettingDefinition> Settings(IReadOnlyList<TextureGraphParameter> parameters) {
        ArgumentNullException.ThrowIfNull(parameters);

        var settings = ImmutableArray.CreateBuilder<SettingDefinition>(parameters.Count);

        foreach (var parameter in parameters) {
            settings.Add(
                new(
                    parameter.Name,
                    parameter.Kind == TextureGraphParameterKind.Name
                        ? parameter.Choice
                        : parameter.Text(parameter.Default),
                    parameter.Summary,
                    Kind(parameter.Kind),
                    parameter.Minimum,
                    parameter.Maximum,
                    parameter.Group,
                    parameter.Accepted
                )
            );
        }

        return settings.ToImmutable();
    }

    /// <summary>The parameters a graph declared, read back off its own settings.</summary>
    /// <param name="settings">What <see cref="NodeGraphModel.Parameters" /> holds.</param>
    /// <returns>One parameter each, in the order given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="Settings" /> backwards, and the pair is what makes a published graph's
    ///         knobs survive a save — <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>.</b>
    ///         A parameter list was a property of the compiler, so a <c>.vxtexgraph</c> reopened with
    ///         whatever knobs its host invented.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A default that does not parse becomes zero, and that is not this method's
    ///         decision to make loudly.</b> The declaration is checked as a whole by
    ///         <see cref="Check" /> — which is what the compiler runs, and which reports a default
    ///         that is not a number against the graph. Refusing here would mean a file with one bad
    ///         line could not be opened at all, which is the editor a person cannot use to fix it.
    ///     </para>
    /// </remarks>
    public static List<TextureGraphParameter> Declared(IReadOnlyList<SettingDefinition> settings) {
        ArgumentNullException.ThrowIfNull(settings);

        List<TextureGraphParameter> parameters = [];

        foreach (var setting in settings) {
            // ⚠ `SettingKind.Text` used to arrive here as a `Scalar` whose default was parsed out of
            // a name — which is where #1060's cheap half was thrown away, at exactly this line.
            //
            // ⚠ And it is the *list* that says so rather than the kind alone, which is a
            // compatibility statement and not a heuristic. `SettingKind`'s zero is `Text`, and the
            // shipped compounds declare `default: '0.5'` with no `kind:` key at all — so reading
            // every text-kind parameter as a name would turn `Generators/Dust`'s `facing` into a
            // choice whose default is the string "0.5", in eleven files that already exist. Every
            // one of them predates `Accepted`, so an empty list is exactly the set of declarations
            // written before this kind existed.
            var kind = setting.Kind switch {
                SettingKind.Int => TextureGraphParameterKind.Integer,
                SettingKind.Bool => TextureGraphParameterKind.Boolean,
                SettingKind.Text when setting.Accepted.Length > 0 => TextureGraphParameterKind.Name,
                _ => TextureGraphParameterKind.Scalar
            };

            parameters.Add(
                new(
                    setting.Name,
                    kind,
                    kind == TextureGraphParameterKind.Name ? 0f : Number(setting.Default),
                    setting.Minimum,
                    setting.Maximum,
                    setting.Group,
                    setting.Summary,
                    kind == TextureGraphParameterKind.Name ? setting.Default : "",
                    setting.Accepted
                )
            );
        }

        return parameters;
    }

    /// <summary>One default as a number: a literal, or a flag as one and zero.</summary>
    static float Number(string text) {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
            return value;
        }

        return bool.TryParse(text, out var flag) && flag ? 1f : 0f;
    }

    /// <summary>One parameter's type, as the framework spells it.</summary>
    /// <remarks>
    ///     Two enums rather than one, because they answer different questions: this one says how a
    ///     <em>row</em> edits the text, and <see cref="TextureGraphParameterKind" /> says which of
    ///     Raven's three <c>const val</c> spellings the parameter is folded as. The mapping is total
    ///     and the default arm is the safe one — a text box refuses nothing.
    /// </remarks>
    public static SettingKind Kind(TextureGraphParameterKind kind) =>
        kind switch {
            TextureGraphParameterKind.Integer => SettingKind.Int,
            TextureGraphParameterKind.Boolean => SettingKind.Bool,
            TextureGraphParameterKind.Name => SettingKind.Text,
            _ => SettingKind.Float
        };

    /// <summary>What a setting written as one of the graph's own name knobs is spelled with.</summary>
    /// <remarks>
    ///     ⚠ <b>A prefix on the <em>value</em>, where the expression convention is a prefix on the
    ///     <em>key</em>, and the two could not have shared one.</b> An expression is stored under
    ///     <c>=Port</c> beside the number it overrides, because a port has a number as well; a
    ///     setting has only its text, so a reference has to be written where the name would be. It
    ///     also could not have been spelled <c>=</c>: <c>TextureGraphCompiler.Collect</c> reports
    ///     every <c>=X</c> key whose <c>X</c> is not an input port, so a setting written that way
    ///     would draw a diagnostic before anything read it.
    /// </remarks>
    public const string ReferencePrefix = "$";

    /// <summary>Whether a setting's text names one of the containing graph's name parameters.</summary>
    /// <param name="text">What the setting holds.</param>
    /// <param name="name">The parameter named, when it does.</param>
    /// <returns><see langword="true" /> if it is a reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The tail has to be a Raven identifier, and that is what keeps an ordinary value that
    ///     happens to begin with a dollar readable as itself.</b> A setting is a name, a path or an
    ///     enum member; <c>$</c> followed by anything that is not an identifier — a separator, a
    ///     digit, a second <c>$</c> — is left exactly alone rather than turned into a complaint about
    ///     a parameter nobody declared.
    /// </remarks>
    public static bool IsReference(string text, out string name) {
        ArgumentNullException.ThrowIfNull(text);

        if (text.StartsWith(ReferencePrefix, StringComparison.Ordinal) && IsIdentifier(text[1..])) {
            name = text[1..];

            return true;
        }

        name = "";

        return false;
    }

    /// <summary>What one name parameter is worth, given a set of overrides.</summary>
    /// <param name="parameters">The declared parameters of the graph the reference was written in.</param>
    /// <param name="overrides">
    ///     What the node standing for that graph was given, by parameter name — a sub-graph node's
    ///     <see cref="GraphNode.Texts" />. Null for the author's own graph with nothing over it.
    /// </param>
    /// <param name="name">The parameter named.</param>
    /// <param name="value">
    ///     The name to substitute. ⚠ It may itself be a reference — a published graph's knob set to
    ///     one of its <em>container's</em> knobs — so a caller resolves it again one scope out.
    /// </param>
    /// <param name="problem">What to say when something was refused, or empty.</param>
    /// <returns>
    ///     <see langword="false" /> when no name parameter of that name is declared, which is the one
    ///     case with no value at all.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters" /> or <paramref name="name" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A refused override keeps the declared choice and says so</b>, which is
    ///     <see cref="Read" />'s bargain one kind over. Substituting the refused name would put a
    ///     value the inner node does not accept into its setting, where it becomes that node's
    ///     <c>TG0010</c> — a second complaint, about a node the author never wrote, naming a name
    ///     they never typed.
    /// </remarks>
    public static bool TryChoose(
        IReadOnlyList<TextureGraphParameter> parameters,
        IReadOnlyDictionary<string, string>? overrides,
        string name,
        out string value,
        out string problem
    ) {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(name);

        problem = "";
        value = "";

        TextureGraphParameter? declared = null;

        foreach (var parameter in parameters) {
            if (parameter.Kind == TextureGraphParameterKind.Name
                && string.Equals(parameter.Name, name, StringComparison.Ordinal)) {
                declared = parameter;

                break;
            }
        }

        if (declared is null) {
            return false;
        }

        value = declared.Choice;

        if (overrides is null
            || !overrides.TryGetValue(name, out var written)
            || string.IsNullOrWhiteSpace(written)) {
            return true;
        }

        var trimmed = written.Trim();

        // A knob set to one of the container's knobs, which this call cannot resolve: the parameters
        // it would be read against are one scope further out and only the walk knows which.
        if (IsReference(trimmed, out _)) {
            value = trimmed;

            return true;
        }

        if (declared.Canonical(trimmed) is not { Length: > 0 } canonical) {
            problem =
                $"'{name}' was given '{trimmed}', which is not one of {string.Join(", ", declared.Accepted)}. "
                + $"It keeps its default of '{declared.Choice}'.";

            return true;
        }

        value = canonical;

        return true;
    }

    // ⚠ `Describe` is deleted rather than left behind. It folded a parameter's group and range into
    // its summary because a `SettingDefinition` had nowhere else to put them — a workaround that read
    // as one, said so in its own remarks, and had exactly one caller. Now that both have a home
    // (#730), keeping it would mean a second, prose copy of two numbers a row already draws, and the
    // day the two disagreed the tooltip would be the convincing one.

    /// <summary>What each parameter is worth, given a set of overrides.</summary>
    /// <param name="parameters">The declared parameters.</param>
    /// <param name="overrides">
    ///     What an author typed, by parameter name — a sub-graph node's <see cref="GraphNode.Texts" />,
    ///     or a <c>.vxsmartmat</c>'s. Keys naming no parameter are ignored.
    /// </param>
    /// <param name="problems">One message per override that could not be used.</param>
    /// <returns>Every declared parameter's value, by name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>An unparseable or out-of-range override keeps the default and says so.</b> Answering
    ///     with the parsed-as-zero value is the failure this method exists to prevent: zero is a
    ///     valid-looking number for almost every parameter a texture graph has, and a graph that
    ///     silently lost its amount is a graph nobody can debug.
    /// </remarks>
    public static Dictionary<string, float> Read(
        IReadOnlyList<TextureGraphParameter> parameters,
        IReadOnlyDictionary<string, string>? overrides,
        out ImmutableArray<string> problems
    ) {
        ArgumentNullException.ThrowIfNull(parameters);

        Dictionary<string, float> values = new(StringComparer.Ordinal);
        var refused = ImmutableArray.CreateBuilder<string>();

        foreach (var parameter in parameters) {
            if (parameter.Kind == TextureGraphParameterKind.Name) {
                // ⚠ Skipped rather than parsed as zero, and left out of the dictionary rather than
                // entered as zero. A name is not a number, so the first thing the loop below would do
                // with `Metal` set to `Gold` is refuse it — one warning per compile, about the knob
                // working exactly as declared. What reads a name is `TryChoose`.
                continue;
            }

            values[parameter.Name] = parameter.Default;

            if (overrides is null
                || !overrides.TryGetValue(parameter.Name, out var text)
                || string.IsNullOrWhiteSpace(text)) {
                continue;
            }

            if (!TryParse(parameter.Kind, text.Trim(), out var value)) {
                refused.Add(
                    $"'{parameter.Name}' was given '{text.Trim()}', which is not a {Spelling(parameter.Kind)}. "
                    + $"It keeps its default of {parameter.Text(parameter.Default)}."
                );

                continue;
            }

            if (!parameter.Holds(value)) {
                refused.Add(
                    $"'{parameter.Name}' was given {parameter.Text(value)}, which is outside its range of "
                    + $"{parameter.Minimum}…{parameter.Maximum}. It keeps its default of "
                    + $"{parameter.Text(parameter.Default)}."
                );

                continue;
            }

            values[parameter.Name] = value;
        }

        problems = refused.ToImmutable();

        return values;
    }

    /// <summary>Whether a name is one Raven would accept.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    public static bool IsIdentifier(string name) {
        if (string.IsNullOrEmpty(name) || (!char.IsLetter(name[0]) && name[0] != '_')) {
            return false;
        }

        foreach (var character in name) {
            if (!char.IsLetterOrDigit(character) && character != '_') {
                return false;
            }
        }

        return true;
    }

    static string Spelling(TextureGraphParameterKind kind) =>
        kind switch {
            TextureGraphParameterKind.Integer => "whole number",
            TextureGraphParameterKind.Boolean => "true or false",
            _ => "number"
        };

    static bool TryParse(TextureGraphParameterKind kind, string text, out float value) {
        switch (kind) {
            case TextureGraphParameterKind.Boolean:
                if (bool.TryParse(text, out var flag)) {
                    value = flag ? 1f : 0f;

                    return true;
                }

                value = 0f;

                return false;

            case TextureGraphParameterKind.Integer:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) {
                    value = whole;

                    return true;
                }

                value = 0f;

                return false;

            default:
                // ⚠ `float.TryParse` accepts "NaN" and "Infinity", and neither is a value an
                // expression could be written against — see `Check`.
                return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                       && float.IsFinite(value);
        }
    }
}
