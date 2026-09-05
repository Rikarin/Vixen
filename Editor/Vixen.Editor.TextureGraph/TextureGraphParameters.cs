// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph;

/// <summary>What one exposed parameter holds.</summary>
/// <remarks>
///     ⚠ <b>Three, and not one per <see cref="PortKind" />.</b> A parameter is a number an author
///     types and an expression reads, and Raven has exactly three spellings of that —
///     <c>float</c>, <c>int</c>, <c>bool</c>. A colour parameter would be four numbers under one
///     name and there is no <c>const val</c> of a vector that
///     <see cref="TextureGraphExpressions" /> could fold, so it is left out rather than declared and
///     refused at the point of use.
/// </remarks>
enum TextureGraphParameterKind {
    /// <summary>A <c>float</c>.</summary>
    Scalar,

    /// <summary>An <c>int</c>.</summary>
    Integer,

    /// <summary>A <c>bool</c>, carried as zero or one.</summary>
    Boolean
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
/// <remarks>
///     <para>
///         <b>A name, a type, a default, a range and a group — doc 48 § D9's list exactly.</b> The
///         range is not decoration: it is what an inspector draws a slider between, and it is what
///         <see cref="TextureGraphParameters.Read" /> refuses an override outside, because a
///         parameter whose declared range says <c>0…1</c> and whose value is <c>40</c> is a graph
///         whose author has been lied to about what it does.
///     </para>
///     <para>
///         ⚠ <b>The range is carried here and not on <see cref="SettingDefinition" />, which has
///         nowhere to put one.</b> A framework setting is a name, a default and a summary; a texture
///         graph's parameter is all of that plus two numbers and a group. So
///         <see cref="TextureGraphParameters.Definition" /> writes the range into the setting's
///         <em>summary</em> for the framework's inspector to show, and anything that wants to
///         <em>enforce</em> it reads this record instead —
///         <a href="https://github.com/Rikarin/Vixen/issues/730">#730</a>.
///     </para>
/// </remarks>
sealed record TextureGraphParameter(
    string Name,
    TextureGraphParameterKind Kind = TextureGraphParameterKind.Scalar,
    float Default = 0f,
    float Minimum = float.NegativeInfinity,
    float Maximum = float.PositiveInfinity,
    string Group = "",
    string Summary = ""
) {
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
///         ⚠ <b>Where the values live, and what that costs.</b> A parameter <em>list</em> belongs to
///         the graph and <c>NodeGraphModel</c> has nowhere to put one — it carries a name, a node
///         list and an interface — so the list is a property of the compiler, exactly as
///         <c>BaseWidth</c> and <c>Seed</c> are, and a <c>.vxtexgraph</c> cannot round-trip it until
///         the model can hold it (<a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>).
///         The <em>overrides</em> have a home already: a sub-graph node's
///         <see cref="GraphNode.Texts" />, under the parameter's own name, which is what
///         <see cref="Read" /> reads.
///     </para>
/// </remarks>
static class TextureGraphParameters {
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
    public static ImmutableArray<SettingDefinition> Settings(IReadOnlyList<TextureGraphParameter> parameters) {
        ArgumentNullException.ThrowIfNull(parameters);

        var settings = ImmutableArray.CreateBuilder<SettingDefinition>(parameters.Count);

        foreach (var parameter in parameters) {
            settings.Add(new(parameter.Name, parameter.Text(parameter.Default), Describe(parameter)));
        }

        return settings.ToImmutable();
    }

    /// <summary>What one parameter's summary says, with its group and range folded in.</summary>
    /// <param name="parameter">The parameter.</param>
    /// <returns>The line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameter" /> is null.</exception>
    public static string Describe(TextureGraphParameter parameter) {
        ArgumentNullException.ThrowIfNull(parameter);

        var range = float.IsInfinity(parameter.Minimum) && float.IsInfinity(parameter.Maximum)
            ? ""
            : $"{parameter.Minimum}…{parameter.Maximum}";

        var parts = new[] { parameter.Summary, parameter.Group, range }.Where(part => part.Length > 0);

        return string.Join(" · ", parts);
    }

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
