// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vixen.Ui.Markup.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     Every English word production C# hands a control to <i>say</i> — <c>add.Label = "Add"</c> — held
///     against a committed census.
/// </summary>
/// <remarks>
///     <para>
///         <b>The C# half of <c>Rikarin/Vixen#1338</c>.</b> <see cref="MarkupAccessibleNameTests" />
///         counts <c>Label="…"</c> in markup; the same literal written as <c>button.Label = "Add"</c>
///         in a <c>.cs</c> file or a <c>@code</c> body is the same word no translator can reach, and
///         nothing saw it. <see cref="Vixen.Ui.Testing.AccessibilitySnapshot.Untranslated" /> cannot:
///         it compares against the declaration table, and a word no class declares is not in it.
///     </para>
///     <para>
///         ⚠ <b>The property name is not the question, and a scan that thought it was would be a
///         census of log messages.</b> <c>Message = "…"</c> is every <c>[LoggerMessage]</c> in the
///         tree, <c>Description = "…"</c> every command-line option, <c>Title = "…"</c> every window
///         the samples open — measured, a bare name match over production C# is overwhelmingly those.
///         What makes a literal a spoken name is the <i>receiver</i>: a <c>Button</c>'s <c>Label</c> is
///         what it answers <c>NativeAccessibleName</c> with and a window's <c>Title</c> is not. So each
///         assignment's receiver is resolved to a type from the syntax — a local's initializer
///         (<c>var add = row.Add&lt;Button&gt;()</c>), a field's or property's declared type, a
///         parameter's, the <c>T</c> of <c>new T { … }</c> — and the row is written only when that
///         type is one <see cref="Spoken" /> <i>proved</i> speaks that property.
///     </para>
///     <para>
///         ⚠ <b>Proved, not listed.</b> <see cref="Spoken" /> builds every public element type in the
///         three UI assemblies this suite loads, sets each watched string property to a sentinel, and
///         reads the accessibility tree back the way <see cref="MarkupAccessibleNameTests.Announced" />
///         does. A table written by hand would be a claim about a hundred overrides that nothing
///         checks; this one moves the day an override does.
///     </para>
///     <para>
///         ⚠ <b>What this cannot judge, stated as a number rather than hidden.</b> A receiver the
///         syntax does not type — <c>row.Cells[0].Label</c>, a method's return value, an element
///         bound by <c>ref="@X"</c> in markup — and a type this assembly cannot load, which is every
///         control the editor declares for itself, are <i>unjudged</i>. They are counted, and the
///         instrument check below names the count, but they are neither rows nor passes. A literal
///         spoken by <c>PaletteRow</c> is invisible here for the same reason it is invisible to a
///         catalogue: nothing that runs in this assembly can say whether that property speaks.
///     </para>
///     <para>
///         ⚠ <b>A census and not a refusal, for the markup census's reason</b> — there are dozens, the
///         editor's words are moving into <c>EditorStrings</c> a panel at a time, and a gate with that
///         many failures on its first run is the wall nobody keeps green. The set is committed and
///         compared exactly in both directions, so a new literal cannot arrive without a line and a
///         localised one must leave.
///     </para>
/// </remarks>
public class CodeAccessibleNameTests {
    /// <summary>Where the census lives, relative to the repository root.</summary>
    const string CensusFile = "Core/Vixen.Ui.Controls.Advanced.Tests/LiteralAccessibleNamesInCode.txt";

    /// <summary>Set <c>VIXEN_REGENERATE=1</c> to write the census back instead of asserting it.</summary>
    static bool Regenerating => Environment.GetEnvironmentVariable("VIXEN_REGENERATE") is "1";

    /// <summary>
    ///     The two direct spellings, which every element answers with once it is in the accessibility
    ///     tree — so a literal assigned to either is a spoken name whatever the receiver.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not probed, because the probe would say "no" for a <c>Panel</c>, which has no role and so
    ///     is not in the tree, and markup sets the role the same way it sets the name.
    ///     <see cref="MarkupAccessibleNameTests" /> watches them on anything for the same reason.
    /// </remarks>
    static readonly string[] Direct = ["AccessibleName", "AccessibleDescription"];

    /// <summary>A line worth parsing: a watched name assigned something that starts as a string.</summary>
    /// <remarks>
    ///     A pre-filter and nothing more — every candidate it passes is decided by the syntax tree.
    ///     It exists because parsing every production file in the repository to find the few hundred
    ///     that could matter is most of the cost of this test.
    /// </remarks>
    static readonly Regex Candidate = new(
        @"\b(?:" + string.Join('|', MarkupAccessibleNameTests.Watched) + @")\s*=\s*""",
        RegexOptions.Compiled
    );

    /// <summary>What the C# writes, sorted into the kinds this file tells apart.</summary>
    /// <param name="Literal">A row per literal spoken name, as <c>path TAB Type.Property TAB value</c>.</param>
    /// <param name="Sites">Every literal assigned to a watched property name, spoken or not.</param>
    /// <param name="Untyped">Each site whose receiver the syntax does not type, as <c>path:line</c>.</param>
    /// <param name="Foreign">Each receiver type named that is not an element this assembly loads, to its site count.</param>
    /// <param name="Files">How many source files the walk read.</param>
    public sealed record Code(
        IReadOnlyList<string> Literal,
        int Sites,
        IReadOnlyList<string> Untyped,
        IReadOnlyDictionary<string, int> Foreign,
        int Files
    );

    /// <summary>The scan, done once.</summary>
    public static Code Scanned => scanned ??= Scan();

    static Code? scanned;

    /// <summary>
    ///     Every element type this assembly can build, to the watched properties it was seen to speak.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Spoken => spoken ??= Probe();

    static IReadOnlyDictionary<string, IReadOnlySet<string>>? spoken;

    /// <summary>
    ///     Builds every public element type in the three UI assemblies and asks which of its watched
    ///     string properties a screen reader is handed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A fixture per probe.</b> A sentinel left in one control's tree would be heard by the
    ///     next probe's walk, and the answer would be about the wrong type.
    ///     <para>
    ///         Keyed by the simple name, because that is all the syntax has: <c>row.Add&lt;Button&gt;()</c>
    ///         says <c>Button</c> and nothing about its namespace. A name two loaded types share would
    ///         make the key ambiguous, so that is asserted not to happen rather than resolved by order.
    ///     </para>
    /// </remarks>
    static Dictionary<string, IReadOnlySet<string>> Probe() {
        var make = typeof(CodeAccessibleNameTests).GetMethod(nameof(Make), BindingFlags.NonPublic | BindingFlags.Static)!;
        var table = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        var types = new[] { typeof(UiElement).Assembly, typeof(Button).Assembly, typeof(DataGrid).Assembly }
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type.IsPublic && !type.IsAbstract && typeof(UiElement).IsAssignableFrom(type))
            .Where(static type => type.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(static type => type.FullName, StringComparer.Ordinal);

        foreach (var type in types) {
            Assert.False(table.ContainsKey(type.Name), $"two element types are called {type.Name}, so a name in the source no longer picks one.");

            var speaks = new HashSet<string>(Direct, StringComparer.Ordinal);

            foreach (var property in MarkupAccessibleNameTests.Watched.Except(Direct)) {
                if (type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance) is not { CanWrite: true } setter
                    || setter.PropertyType != typeof(string)
                    || setter.SetMethod is not { IsPublic: true }) {
                    continue;
                }

                using var fixture = new AdvancedFixture();

                var element = (UiElement)make.MakeGenericMethod(type).Invoke(null, [fixture.Document.Root])!;
                var sentinel = $"probe {type.Name}.{property}";

                setter.SetValue(element, sentinel);
                fixture.Update();

                if (MarkupAccessibleNameTests.Announced(fixture.Document.Root).Contains(sentinel, StringComparer.Ordinal)) {
                    speaks.Add(property);
                }
            }

            table[type.Name] = speaks;
        }

        return table;
    }

    static UiElement Make<T>(UiElement parent) where T : UiElement, new() => parent.Add<T>();

    static Code Scan() {
        var root = Root();
        var rows = new SortedSet<string>(StringComparer.Ordinal);
        var sites = 0;
        var untyped = new List<string>();
        var foreign = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var files = 0;

        foreach (var path in Sources(root)) {
            files++;

            var name = Path.GetRelativePath(root, path).Replace('\\', '/');
            var source = path.EndsWith(".vxml", StringComparison.Ordinal)
                ? CodeOf(File.ReadAllLines(path))
                : File.ReadAllText(path);

            if (!Candidate.IsMatch(source)) {
                continue;
            }

            var found = ScanFile(name, source, Spoken);

            rows.UnionWith(found.Literal);
            sites += found.Sites;
            untyped.AddRange(found.Untyped);

            foreach (var (type, count) in found.Foreign) {
                foreign[type] = foreign.GetValueOrDefault(type) + count;
            }
        }

        return new Code([.. rows], sites, untyped, foreign, files);
    }

    /// <summary>A <c>.vxml</c>'s <c>@code</c> body as a compilation unit, or nothing when it has none.</summary>
    /// <param name="lines">The file.</param>
    /// <returns>The body with its directive turned into a class, so it parses as the members it is.</returns>
    /// <remarks>
    ///     Read through <see cref="VxmlLines" />, the reader every other <c>.vxml</c> sweep here uses: a
    ///     header comment demonstrating <c>Label = "…"</c> is prose, and the markup's own attributes
    ///     are <see cref="MarkupAccessibleNameTests" />' question, not this one's.
    /// </remarks>
    internal static string CodeOf(IReadOnlyList<string> lines) {
        var code = new StringBuilder();

        foreach (var line in VxmlLines.Read(lines)) {
            if (line.Region != VxmlRegion.Code) {
                continue;
            }

            code.AppendLine(code.Length == 0 ? Directive.Replace(line.Text, "class Code", 1) : line.Text);
        }

        return code.ToString();
    }

    static readonly Regex Directive = new(@"@code\b", RegexOptions.Compiled);

    /// <summary>What one file's C# says.</summary>
    /// <param name="name">How a row spells the file.</param>
    /// <param name="source">The C#.</param>
    /// <param name="spoken">Which element types speak which properties.</param>
    /// <returns>Its literal rows, and how many sites it had and could not judge.</returns>
    internal static Code ScanFile(string name, string source, IReadOnlyDictionary<string, IReadOnlySet<string>> spoken) {
        var watched = MarkupAccessibleNameTests.Watched.ToHashSet(StringComparer.Ordinal);
        var tree = CSharpSyntaxTree.ParseText(source);
        var rows = new SortedSet<string>(StringComparer.Ordinal);
        var sites = 0;
        var untyped = new List<string>();
        var foreign = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()) {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                || assignment.Right is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal) {
                continue;
            }

            var (property, receiver) = assignment.Left switch {
                IdentifierNameSyntax bare when assignment.Parent is InitializerExpressionSyntax initializer
                    => (bare.Identifier.ValueText, Created(initializer)),
                MemberAccessExpressionSyntax access
                    => (access.Name.Identifier.ValueText, TypeOf(access.Expression, assignment)),
                _ => (null, null)
            };

            if (property is null || !watched.Contains(property)) {
                continue;
            }

            var value = literal.Token.ValueText;

            // A value with nothing in it is not a word anybody says.
            if (value.Trim().Length == 0) {
                continue;
            }

            sites++;

            if (receiver is null) {
                untyped.Add($"{name}:{assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                continue;
            }

            if (!spoken.TryGetValue(receiver, out var speaks)) {
                foreign[receiver] = foreign.GetValueOrDefault(receiver) + 1;
                continue;
            }

            if (!speaks.Contains(property)) {
                continue;
            }

            var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);

            rows.Add($"{name}\t{receiver}.{property}\t{escaped}");
        }

        return new Code([.. rows], sites, untyped, foreign, 1);
    }

    /// <summary>The type an object initializer's <c>new</c> names, if it names one.</summary>
    static string? Created(InitializerExpressionSyntax initializer) =>
        initializer.Parent switch {
            ObjectCreationExpressionSyntax creation => Simple(creation.Type),
            ImplicitObjectCreationExpressionSyntax implicitly => Target(implicitly),
            _ => null
        };

    /// <summary>What a target-typed <c>new()</c> is declared as, when it is declared at all.</summary>
    static string? Target(ExpressionSyntax expression) =>
        expression.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }
        && !declaration.Type.IsVar
            ? Simple(declaration.Type)
            : null;

    /// <summary>The type the syntax gives a receiver, or nothing when it does not say.</summary>
    /// <param name="receiver">What is left of the dot.</param>
    /// <param name="at">The assignment, which bounds which declarations are visible.</param>
    static string? TypeOf(ExpressionSyntax receiver, SyntaxNode at) =>
        receiver switch {
            IdentifierNameSyntax identifier => Declared(identifier.Identifier.ValueText, at),
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => Member(member.Name.Identifier.ValueText, at),
            ParenthesizedExpressionSyntax parenthesized => TypeOf(parenthesized.Expression, at),
            _ => Produced(receiver)
        };

    /// <summary>The type an expression evidently produces, from its own shape.</summary>
    /// <remarks>
    ///     ⚠ <b>A generic call with one type argument is taken to return it</b> —
    ///     <c>Add&lt;Button&gt;()</c>, <c>Part&lt;IconButton&gt;()</c>. That is this repository's shape
    ///     for building an element, and a call of that shape that returns something else (a list of
    ///     them) has no watched string property to assign, so it cannot produce a row.
    /// </remarks>
    static string? Produced(ExpressionSyntax expression) =>
        expression switch {
            ObjectCreationExpressionSyntax creation => Simple(creation.Type),
            CastExpressionSyntax cast => Simple(cast.Type),
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression, Right: TypeSyntax type } => Simple(type),
            InvocationExpressionSyntax {
                Expression: GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic
            } => Simple(generic.TypeArgumentList.Arguments[0]),
            InvocationExpressionSyntax {
                Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic }
            } => Simple(generic.TypeArgumentList.Arguments[0]),
            ParenthesizedExpressionSyntax parenthesized => Produced(parenthesized.Expression),
            _ => null
        };

    /// <summary>A name's type: the nearest local or parameter declared before the use, then a member.</summary>
    static string? Declared(string name, SyntaxNode at) {
        var scope = at.Ancestors().FirstOrDefault(static node =>
            node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax
                or PropertyDeclarationSyntax or CompilationUnitSyntax
        );

        if (scope is not null) {
            var local = scope.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .Where(declarator => declarator.Identifier.ValueText == name && declarator.SpanStart < at.SpanStart)
                .MaxBy(static declarator => declarator.SpanStart);

            if (local is { Parent: VariableDeclarationSyntax declaration }) {
                return declaration.Type.IsVar
                    ? local.Initializer is { } initializer ? Produced(initializer.Value) : null
                    : Simple(declaration.Type);
            }

            var parameter = at.Ancestors()
                .SelectMany(static node => node.ChildNodes().OfType<ParameterListSyntax>())
                .SelectMany(static list => list.Parameters)
                .FirstOrDefault(parameter => parameter.Identifier.ValueText == name);

            if (parameter?.Type is { } type) {
                return Simple(type);
            }
        }

        return Member(name, at);
    }

    /// <summary>A field's or property's declared type, in the type that encloses the use.</summary>
    static string? Member(string name, SyntaxNode at) {
        foreach (var type in at.Ancestors().OfType<TypeDeclarationSyntax>()) {
            foreach (var member in type.Members) {
                switch (member) {
                    case PropertyDeclarationSyntax property when property.Identifier.ValueText == name:
                        return Simple(property.Type);

                    case FieldDeclarationSyntax field when field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == name):
                        return Simple(field.Declaration.Type);
                }
            }
        }

        return null;
    }

    /// <summary>The last identifier of a type's name, which is all a simple-name lookup can use.</summary>
    static string? Simple(TypeSyntax type) =>
        type switch {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            // ⚠ With its arity, the way reflection spells it, because the simple name alone collides:
            // `new Option<bool>("--verbose") { Description = "…" }` is a command-line option and
            // `Option` is also the select control's item. Measured — every CLI flag in the tree
            // resolved to the control until the arity was part of the name.
            GenericNameSyntax generic => $"{generic.Identifier.ValueText}`{generic.TypeArgumentList.Arguments.Count}",
            QualifiedNameSyntax qualified => Simple(qualified.Right),
            AliasQualifiedNameSyntax alias => Simple(alias.Name),
            NullableTypeSyntax nullable => Simple(nullable.ElementType),
            _ => null
        };

    /// <summary>
    ///     Every production C# source: <c>.cs</c> files and the <c>@code</c> bodies of <c>.vxml</c>
    ///     ones, test assemblies and benchmarks left out.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>.vxml</c> as well as <c>.cs</c>, which is the point.</b> The markup census skips
    ///     <c>@code</c> bodies on the stated ground that reading them without the <c>.cs</c> files
    ///     would be a census whose domain is an accident of where somebody put a line; this file
    ///     reads both, so neither census has a hole the other one assumes is covered.
    ///     <para>
    ///         A test assembly sets labels on everything it builds, and a benchmark's
    ///         <c>button.Label = "Go"</c> is a fixture nobody is shown — neither is an interface
    ///         anybody hears. Samples and templates are kept: both are what somebody runs.
    ///     </para>
    /// </remarks>
    static List<string> Sources(string root) {
        var found = new List<string>();
        Walk(root, found);

        found.RemoveAll(static path =>
            path.Contains(".Tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || path.Contains(Path.DirectorySeparatorChar + "Benchmarks" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        );

        found.Sort(StringComparer.Ordinal);

        return found;
    }

    static readonly string[] Unwalked = [".git", ".claude", "bin", "obj", "artifacts", "node_modules"];

    static void Walk(string directory, List<string> into) {
        into.AddRange(Directory.EnumerateFiles(directory, "*.cs"));
        into.AddRange(Directory.EnumerateFiles(directory, "*.vxml"));

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (!Unwalked.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                Walk(child, into);
            }
        }
    }

    /// <summary>
    ///     The probe proves the properties the census rests on, and does not credit one no control
    ///     speaks.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because a probe that said "yes" to everything would make every
    ///     <c>Title</c> on a window a row and one that said "no" would empty the census.</b>
    ///     <c>Button</c>'s <c>Label</c> is the issue's own example, and <c>IconButton</c> inherits it;
    ///     <c>Alert</c>'s <c>Title</c> and <c>Toast</c>'s <c>Message</c> are two other overrides the
    ///     markup census names. And a <c>Panel</c> speaks nothing beyond the two direct spellings —
    ///     <c>Containers.cs</c> gives it no role — so the probe is shown able to say no.
    /// </remarks>
    [Fact]
    public void The_probe_proves_what_the_census_rests_on() {
        var table = Spoken;

        Assert.True(table.Count >= 80, $"only {table.Count} element types were probed, against 125 measured.");

        Assert.Contains("Label", table["Button"]);
        Assert.Contains("Label", table["IconButton"]);
        Assert.Contains("Title", table["Alert"]);
        Assert.Contains("Message", table["Toast"]);

        Assert.Equal(Direct.Order(StringComparer.Ordinal), table["Panel"].Order(StringComparer.Ordinal));
    }

    /// <summary>The receiver decides, and each way the syntax names one is read.</summary>
    /// <remarks>
    ///     ⚠ <b>Synthetic, because the question is the resolver and not the tree.</b> Every shape the
    ///     editor uses is here — a local from <c>Add&lt;T&gt;</c>, a property declared as a control,
    ///     an initializer — and so is every shape that must <i>not</i> be a row: a window's
    ///     <c>Title</c>, an attribute argument, a label read from a declaration, an interpolated
    ///     string, a receiver the syntax does not type. A resolver that stopped reading any one of
    ///     them either drops a row or credits a word nobody says.
    /// </remarks>
    [Fact]
    public void The_receiver_decides_whether_a_literal_is_spoken() {
        const string source = """
            using Vixen.Ui.Controls;

            class Panelish : UiElement {
                public IconButton Reset { get; private set; } = null!;

                [LoggerMessage(Message = "not a control")]
                partial void Log();

                void Build(UiElement row, Alert warning) {
                    Reset = Part<IconButton>();
                    Reset.Label = "From A Property";

                    var add = row.Add<Button>();
                    add.Label = "From A Local";
                    add.Label = EditorStrings.Add.Text;
                    add.Label = $"Interpolated {row}";

                    warning.Title = "From A Parameter";

                    var made = new Button { Label = "From An Initializer" };
                    Button typed = new() { Label = "From A Typed New" };

                    var window = new WindowOptions { Title = "Not A Control" };
                    window.Title = "Still Not A Control";

                    row.Children[0].Label = "Unjudged";
                    Lookup().Label = "Also Unjudged";
                    var flag = new Option<bool>("--verbose") { Description = "A Flag, Not An Option" };
                }
            }
            """;

        var table = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal) {
            ["Button"] = new HashSet<string>(["Label", .. Direct]),
            ["IconButton"] = new HashSet<string>(["Label", .. Direct]),
            ["Alert"] = new HashSet<string>(["Title", .. Direct]),
            ["Option"] = new HashSet<string>(["Label", "Description", .. Direct]),
        };

        var found = CodeAccessibleNameTests.ScanFile("Synthetic.cs", source, table);

        Assert.Equal(
            [
                "Synthetic.cs\tAlert.Title\tFrom A Parameter",
                "Synthetic.cs\tButton.Label\tFrom A Local",
                "Synthetic.cs\tButton.Label\tFrom A Typed New",
                "Synthetic.cs\tButton.Label\tFrom An Initializer",
                "Synthetic.cs\tIconButton.Label\tFrom A Property"
            ],
            found.Literal
        );

        // Ten literals assigned to a watched name — the attribute argument is not an assignment.
        // Two have a receiver the syntax does not type, and three are a type that is no element
        // here: a window's options twice, and a generic `Option<bool>` that shares its simple name
        // with the select control's item and must not be read as one.
        Assert.Equal(10, found.Sites);
        Assert.Equal(["Synthetic.cs:26", "Synthetic.cs:27"], found.Untyped);
        Assert.Equal(new Dictionary<string, int> { ["Option`1"] = 1, ["WindowOptions"] = 2 }, found.Foreign);
    }

    /// <summary>A <c>@code</c> body is read, and the markup and prose around it are not.</summary>
    [Fact]
    public void A_code_body_is_read_and_its_markup_is_not() {
        var code = CodeOf([
            "<!-- Written as `add.Label = \"From A Comment\";` in a panel's body. -->",
            "<Button Label=\"From Markup\" />",
            "@code {",
            "    void Build(UiElement row) {",
            "        var add = row.Add<Button>();",
            "        add.Label = \"From Code\";",
            "    }",
            "}"
        ]);

        var table = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal) {
            ["Button"] = new HashSet<string>(["Label", .. Direct])
        };

        Assert.Equal(["View.vxml\tButton.Label\tFrom Code"], ScanFile("View.vxml", code, table).Literal);
    }

    /// <summary>The walk found the repository, and the census is not a record of its own blindness.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the census passes loudest on the day it stops running.</b> A moved root,
    ///     a pre-filter that stopped matching and a resolver that typed nothing all produce no rows,
    ///     and no rows against a census somebody emptied is a pass. So the corpus is asserted to be a
    ///     corpus, one row the editor really writes is named, and the unjudged count is held under
    ///     the judged one — a resolver that gave up on everything would otherwise agree with an empty
    ///     census perfectly.
    /// </remarks>
    [Fact]
    public void The_code_scan_actually_ran() {
        var code = Scanned;

        Assert.True(code.Files >= 3000, $"only {code.Files} source files were found, against 6 000 measured.");
        Assert.True(code.Sites >= 50, $"only {code.Sites} literals were assigned to a watched name, which is not this repository.");

        // `ListDrawer.cs` builds the list's add button with `editor.Fold.Content.Add<Button>()` and
        // labels it in English — the shape the issue names, in C#.
        Assert.Contains(
            "Editor/Vixen.Editor.Inspector/Drawers/ListDrawer.cs\tButton.Label\tAdd Element",
            code.Literal,
            StringComparer.Ordinal
        );

        Assert.True(
            code.Untyped.Count < code.Sites - code.Untyped.Count,
            $"{code.Untyped.Count} of {code.Sites} sites have a receiver the syntax does not type, so the census "
            + "below is mostly a record of what the resolver could not see."
        );
    }

    /// <summary>
    ///     The literal spoken names production C# writes are exactly the committed census, in both
    ///     directions.
    /// </summary>
    /// <remarks>
    ///     Exact for <see cref="MarkupAccessibleNameTests" />' reason: a ceiling cannot say that a word
    ///     was localised, so its row would outlive it and the next literal would take the seat. Re-run
    ///     with <c>VIXEN_REGENERATE=1</c> to write it back, and read the diff.
    /// </remarks>
    [Fact]
    public void Every_literal_spoken_name_in_code_is_in_the_committed_census() {
        var path = Path.Combine(Root(), CensusFile);
        var literal = Scanned.Literal;

        if (Regenerating) {
            Write(path, literal);
        }

        var census = Census(path);

        var arrived = literal.Where(row => !census.Contains(row)).ToList();
        var departed = census.Where(row => !literal.Contains(row)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             The census of literal accessible names in C# is out of date.

             Assigned in C# and not in {CensusFile}:
             {Lines(arrived)}

             In {CensusFile} and no longer assigned — delete the row:
             {Lines(departed)}

             The receiver answers `AccessibleName` with this property — the probe in this file built
             the control and heard it — so a literal here is a word a screen reader says and no
             translator can reach. Move it into a `*Strings` class and assign `Whatever.Text`, or add
             the row.
             """
        );
    }

    static HashSet<string> Census(string path) {
        var lines = File.ReadAllLines(path);

        Assert.True(
            lines.Count(static line => line.StartsWith('#')) >= 5,
            $"{CensusFile} has lost its header, so it was emptied rather than answered."
        );

        var rows = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines) {
            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            rows.Add(line.TrimEnd());
        }

        return rows;
    }

    /// <summary>Writes the census back under its own header, with the repository's line ending.</summary>
    static void Write(string path, IEnumerable<string> rows) {
        var text = new StringBuilder();

        foreach (var line in File.ReadLines(path)) {
            if (!line.StartsWith('#') && line.Trim().Length != 0) {
                break;
            }

            text.Append(line).Append('\n');
        }

        foreach (var row in rows) {
            text.Append(row).Append('\n');
        }

        File.WriteAllText(path, text.ToString());
    }

    static string Lines(IEnumerable<string> rows) {
        var joined = new StringBuilder();

        foreach (var row in rows) {
            joined.Append("  ").AppendLine(row.Replace('\t', ' '));
        }

        return joined.Length == 0 ? "  (none)" : joined.ToString().TrimEnd('\n');
    }

    static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
