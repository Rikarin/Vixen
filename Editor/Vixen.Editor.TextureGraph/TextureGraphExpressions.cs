// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Vixen.Editor.NodeGraph;
using Vixen.Raven;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Editor.TextureGraph;

/// <summary>One scalar port whose value an author wrote as an expression.</summary>
/// <param name="Node">The node it is on, in the graph the compiler is walking.</param>
/// <param name="Port">Which of its ports.</param>
/// <param name="Text">What the author typed, exactly.</param>
readonly record struct TextureExpression(NodeId Node, string Port, string Text);

/// <summary>What one expression turned out to be worth.</summary>
/// <param name="Node">The node.</param>
/// <param name="Port">The port.</param>
/// <param name="Value">The number, or zero when it did not fold.</param>
/// <param name="Folded">Whether it folded at all.</param>
readonly record struct TextureExpressionValue(NodeId Node, string Port, float Value, bool Folded);

/// <summary>
///     Doc 48 § D6's other half: a scalar parameter is a Raven expression over the graph's exposed
///     parameters, folded by the real Raven compiler.
/// </summary>
/// <remarks>
///     <para>
///         <b>The real compiler, and this is the whole point of the section.</b> Doc 48 § D6 refuses
///         a hand-rolled expression evaluator by name — Designer's function graph is a visual
///         programming language, and Vixen already has a typed, diagnosed, tested language for this
///         arithmetic. So an expression is emitted as a <c>const val</c> in a generated Raven source
///         alongside one <c>const val</c> per exposed parameter, the source is bound by
///         <see cref="Compilation" />, and the value read back is the one
///         <c>SourceFieldSymbol.ConstantValue</c> folded. Nothing here evaluates anything: every
///         operator, every literal suffix and every conversion rule is Raven's, and an expression
///         that means something different in Raven than an author expected is a question with one
///         answer rather than two.
///     </para>
///     <para>
///         ⚠ <b>Only what Raven folds at compile time folds here, and a call does not.</b>
///         <c>ConstantEvaluator</c> folds literals, <c>const</c> references, unary and binary
///         operators and conversions — so <c>amount * 0.5f + rust</c> is a number and
///         <c>sin(amount)</c> is not, and the second is reported as "not a compile-time constant"
///         rather than silently taken as zero. That is a real limit of doing it this way and it is
///         the price of not writing a second evaluator; widening it is a change to Raven's folder,
///         which every shader in the repository would also get.
///     </para>
///     <para>
///         ⚠ <b>One compilation for the whole graph, not one per expression.</b> Every parameter and
///         every expression goes into a single source, so a graph with forty expression fields costs
///         one parse and one bind — and, more importantly, the parameters are declared once, in one
///         place, in an order a diagnostic's line number can be mapped back through.
///     </para>
///     <para>
///         ⚠ <b>An expression is one line because a newline ends a statement in Raven.</b> Text with
///         a line break in it would not be one expression; it would be a <c>const val</c> whose
///         initializer stopped early and a second statement in the middle of a declaration list, and
///         every line number after it would name the wrong node. So a break is refused where it is
///         typed rather than mis-attributed three diagnostics later, and so are the braces that would
///         let a field close the declaration it is inside.
///     </para>
/// </remarks>
static class TextureGraphExpressions {
    /// <summary>What marks a key in <see cref="GraphNode.Texts" /> as a port's expression.</summary>
    /// <remarks>
    ///     ⚠ <b>A prefix, so a setting and an expression cannot collide.</b> Settings are stored in
    ///     the same dictionary under their own names, and a node with a setting called <c>Radius</c>
    ///     and a port called <c>Radius</c> is a node whose two values would be one. A character no
    ///     identifier may start with keeps them apart, and it is the character a spreadsheet uses for
    ///     the same distinction.
    /// </remarks>
    public const string Marker = "=";

    /// <summary>The name a port's expression is stored under.</summary>
    /// <param name="port">The port's name.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string port) => Marker + port;

    /// <summary>Whether a key is a port's expression, and which port's.</summary>
    /// <param name="key">The key.</param>
    /// <param name="port">The port, when it is one.</param>
    /// <returns><see langword="true" /> if the key names a port's expression.</returns>
    public static bool IsExpression(string key, out string port) {
        if (key is { Length: > 1 } && key.StartsWith(Marker, StringComparison.Ordinal)) {
            port = key[Marker.Length..];

            return true;
        }

        port = "";

        return false;
    }

    /// <summary>Folds every expression of one graph, against one set of parameter values.</summary>
    /// <param name="parameters">The graph's exposed parameters, already checked.</param>
    /// <param name="values">What each is worth — <see cref="TextureGraphParameters.Read" />'s answer.</param>
    /// <param name="expressions">Every port whose value was written as an expression.</param>
    /// <param name="diagnostics">One per expression that did not fold, against its node and port.</param>
    /// <returns>One value per expression, in the order given.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>An expression that did not fold answers <see cref="TextureExpressionValue.Folded" />
    ///     false rather than zero, and the caller keeps the port's own number.</b> Zero is a
    ///     valid-looking radius, a valid-looking amount and a valid-looking gamma; a graph that
    ///     silently lost one is a graph whose picture changed for a reason nothing anywhere states.
    /// </remarks>
    public static ImmutableArray<TextureExpressionValue> Fold(
        IReadOnlyList<TextureGraphParameter> parameters,
        IReadOnlyDictionary<string, float> values,
        IReadOnlyList<TextureExpression> expressions,
        out ImmutableArray<NodeDiagnostic> diagnostics
    ) {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(expressions);

        var problems = ImmutableArray.CreateBuilder<NodeDiagnostic>();
        var folded = ImmutableArray.CreateBuilder<TextureExpressionValue>(expressions.Count);

        if (expressions.Count == 0) {
            diagnostics = [];

            return folded.ToImmutable();
        }

        var source = new StringBuilder();
        var line = 0;

        void Write(string text) {
            source.Append(text).Append('\n');
            line++;
        }

        Write("package Vixen.Editor.TextureGraph.Expressions");
        Write("");
        Write($"shader {TypeName} {{");

        foreach (var parameter in parameters) {
            Write($"    const val {parameter.Name}: {Spelling(parameter.Kind)} = "
                  + $"{parameter.RavenLiteral(values.GetValueOrDefault(parameter.Name, parameter.Default))}");
        }

        // Which line each expression's declaration is on, so a complaint about a line names the port
        // that wrote it. It is the same join `ShaderGraphSource.Spans` makes one graph over, and it
        // works for the same reason: one statement per line, counted while writing rather than
        // recomputed after.
        var lineOf = new int[expressions.Count];
        var refused = new bool[expressions.Count];

        for (var index = 0; index < expressions.Count; index++) {
            var expression = expressions[index];
            var text = expression.Text.Trim();

            if (Refuse(text) is { } reason) {
                problems.Add(new(
                    TextureDiagnostics.ExpressionRefused,
                    $"'{expression.Port}' is an expression that {reason}",
                    expression.Node,
                    expression.Port
                ));

                refused[index] = true;
                lineOf[index] = -1;

                continue;
            }

            lineOf[index] = line;
            Write($"    const val {NameOf(index)}: float = {text}");
        }

        Write("}");

        var text0 = source.ToString();
        var tree = SyntaxTree.ParseText(text0, path: FileName);
        var compilation = Compilation.Create("TextureGraphExpressions", tree);

        foreach (var diagnostic in compilation.GetDiagnostics()) {
            if (!diagnostic.IsError) {
                continue;
            }

            var at = diagnostic.Location.IsNone ? -1 : diagnostic.Location.GetLineSpan().Start.Line;
            var owner = Owner(lineOf, at);

            problems.Add(new(
                TextureDiagnostics.ExpressionDoesNotCompile,
                owner < 0
                    ? $"The graph's parameters do not compile: {diagnostic.Id}: {diagnostic.GetMessage()}"
                    : $"'{expressions[owner].Port}' does not compile: {diagnostic.Id}: {diagnostic.GetMessage()}",
                owner < 0 ? NodeId.None : expressions[owner].Node,
                owner < 0 ? "" : expressions[owner].Port,
                NodeSeverity.Error,

                // The line the complaint is on rather than the whole declaration list, because what a
                // pane showing the generated text would squiggle is one line — `ShaderGraphDocument`
                // makes the same choice for the same reason.
                at < 0 ? NodeSpan.None : new NodeSpan(at, 1)
            ));

            if (owner >= 0) {
                refused[owner] = true;
            }
        }

        var members = Members(compilation);

        for (var index = 0; index < expressions.Count; index++) {
            var expression = expressions[index];

            if (refused[index]) {
                folded.Add(new(expression.Node, expression.Port, 0f, false));

                continue;
            }

            if (Value(members, NameOf(index)) is not { } value) {
                problems.Add(new(
                    TextureDiagnostics.ExpressionDoesNotFold,
                    $"'{expression.Port}' is '{expression.Text.Trim()}', which Raven binds and cannot fold to a "
                    + "number at compile time. A plan's parameter is one float, so an expression here is "
                    + "literals, parameters, and arithmetic over them — a function call is not folded.",
                    expression.Node,
                    expression.Port
                ));

                folded.Add(new(expression.Node, expression.Port, 0f, false));

                continue;
            }

            folded.Add(new(expression.Node, expression.Port, value, true));
        }

        diagnostics = problems.ToImmutable();

        return folded.ToImmutable();
    }

    /// <summary>What the generated source is called, so a diagnostic names something recognisable.</summary>
    internal const string FileName = "TextureGraphParameters.rvn";

    /// <summary>The shader the parameters and the expressions are declared in.</summary>
    internal const string TypeName = "Parameters";

    /// <summary>The name one expression's constant is declared under.</summary>
    /// <param name="index">Its index in the list.</param>
    /// <returns>The name.</returns>
    internal static string NameOf(int index) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"expression{index}");

    /// <summary>Why an expression cannot be emitted at all, or null when it can.</summary>
    /// <param name="text">The trimmed text.</param>
    /// <returns>The tail of a sentence beginning "'Radius' is an expression that ".</returns>
    internal static string? Refuse(string text) {
        if (text.Length == 0) {
            // ⚠ Unreachable from `TextureGraphCompiler.Collect`, which drops an empty field before it
            // gets here — clearing the box is how an author goes back to the port's own number. It
            // stays because this method is the contract for anything else that folds an expression,
            // and an empty `const val` initializer is a parse error blamed on the next line's node.
            return "is empty, so there is nothing to fold. An empty field means the port keeps the "
                   + "number typed on it.";
        }

        if (text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal)) {
            return "runs over more than one line. A newline ends a statement in Raven, so the second line "
                   + "would not be part of it — write it as one line.";
        }

        if (text.Contains('{', StringComparison.Ordinal) || text.Contains('}', StringComparison.Ordinal)) {
            return "contains a brace. An expression here is arithmetic over the graph's parameters, and a "
                   + "block would close the declaration it is written into.";
        }

        return null;
    }

    static string Spelling(TextureGraphParameterKind kind) =>
        kind switch {
            TextureGraphParameterKind.Integer => "int",
            TextureGraphParameterKind.Boolean => "bool",
            _ => "float"
        };

    /// <summary>Which expression owns a line of the generated source, or −1 when none does.</summary>
    static int Owner(int[] lineOf, int line) {
        if (line < 0) {
            return -1;
        }

        for (var index = 0; index < lineOf.Length; index++) {
            if (lineOf[index] == line) {
                return index;
            }
        }

        return -1;
    }

    static IReadOnlyList<Symbol> Members(Compilation compilation) {
        foreach (var type in compilation.GetAllTypes()) {
            if (string.Equals(type.Name, TypeName, StringComparison.Ordinal)) {
                return type.GetMembers();
            }
        }

        return [];
    }

    /// <summary>One folded constant as a float, or null when it did not fold.</summary>
    static float? Value(IReadOnlyList<Symbol> members, string name) {
        foreach (var member in members) {
            if (member is not FieldSymbol field || !string.Equals(field.Name, name, StringComparison.Ordinal)) {
                continue;
            }

            return field.ConstantValue switch {
                float value => value,
                double value => (float)value,
                int value => value,
                uint value => value,
                bool value => value ? 1f : 0f,
                _ => null
            };
        }

        return null;
    }
}
