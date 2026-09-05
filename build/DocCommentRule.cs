// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vixen.Build;

/// <summary>
///     The defect a doc comment stapled above the wrong member is, expressed as a function of one
///     file's text.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two of these landed in one batch and every instrument this repository owns was green
///         over them</b> (<a href="https://github.com/Rikarin/Vixen/issues/866">#866</a>). An agent
///         inserted a new member between an existing comment and the member it documented, twice: in
///         <c>LayerStackPreview.cs</c> a <c>Refused</c> block ended up heading <c>Resolve</c>, giving
///         it two <c>&lt;summary&gt;</c>, two <c>&lt;returns&gt;</c> and a <c>&lt;param&gt;</c> naming
///         a parameter it does not have, and in <c>LayerStackPanelDeviceTests.cs</c> a helper's block
///         ended up heading a <c>[Fact]</c> with no parameters at all. A green build, a green
///         1 333-test suite, a clean <c>dotnet format whitespace</c> and a clean
///         <c>dotnet format style --severity warn</c> all passed over both.
///     </para>
///     <para>
///         ⚠ <b>And that is not a gap somebody forgot to close — nothing on the shelf closes it.</b> A
///         duplicated <c>&lt;summary&gt;</c> is not a Roslyn diagnostic at any severity. The one
///         diagnostic that names the second half, CS1572, needs <c>GenerateDocumentationFile</c>, and
///         <c>Directory.Build.props</c> turns that off for the whole tooling profile — so on the day
///         it would have fired it was not running. The reader was the only instrument, and a stapled
///         comment is precisely the defect that misleads a reader.
///     </para>
///     <para>
///         <b>The shape is <c>CheckWhitespace</c>'s: a folder walk with no MSBuild workspace.</b>
///         Nothing here needs a compilation — "this block has two summaries" and "this
///         <c>&lt;param&gt;</c> names a parameter the following member does not have" are both
///         answerable from the syntax of one file. Parsing is Roslyn's rather than a regular
///         expression's because the alternative measured 544 findings on this tree of which every one
///         sampled was the parser failing to see an <c>operator ==</c>, a tuple return type or an
///         indexer. A rule with that false-positive rate is not a gate, it is a list somebody learns
///         to ignore.
///     </para>
///     <para>
///         ⚠ <b>Syntax also gets the fixtures right for free.</b> The generator suites hold C# inside
///         raw string literals — declaration classes written to be reported on — and a textual sweep
///         reads their doc comments as this tree's. To a parser they are a string literal and carry no
///         trivia at all, so the exclusion <c>CheckStrings</c> needs by name is not needed here.
///     </para>
/// </remarks>
static class DocCommentRule {
    /// <summary>One thing wrong with one doc comment block.</summary>
    /// <param name="File">The file it is in, as given to <see cref="Check" />.</param>
    /// <param name="Line">The one-based line the block starts on.</param>
    /// <param name="Message">What is wrong, in the words the gate fails with.</param>
    public sealed record Finding(string File, int Line, string Message) {
        /// <summary>The finding as one line, the way a compiler reports one.</summary>
        public override string ToString() => $"{File}({Line}): {Message}";
    }

    /// <summary>
    ///     The tags a member may carry at most one of.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>summary</c> is the one that caught the batch-9 stapleing and <c>returns</c> is the
    ///     one that would have caught it a second way.</b> They are singletons because a member has
    ///     one description and one return value; a block with two of either has been given a second
    ///     member's documentation, which is what a staple <em>is</em>. <c>value</c> joins them for the
    ///     same reason and costs nothing.
    /// </remarks>
    static readonly string[] Singletons = ["summary", "returns", "value"];

    /// <summary>Directory fragments no walk of this repository should read.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees</c> holds a whole checkout per agent, so a walk that does not stop
    ///     at the repository's edge reports another session's files by their worktree path — the trap
    ///     <c>CheckStrings</c> and the golden walk each hit once. <c>Tools/Vixen.Templates/templates</c>
    ///     is not this repository's code either: it is what <c>dotnet new</c> writes into somebody
    ///     else's directory.
    /// </remarks>
    static readonly string[] SkippedFragments = [
        "/bin/",
        "/obj/",
        "/artifacts/",
        "/Vixen.Templates/templates/"
    ];

    /// <summary>The other checkouts of this repository, and only the other ones.</summary>
    /// <remarks>
    ///     ⚠ <b>Anchored at the root and never matched as a substring, and that is the whole
    ///     difference between a walk that skips the sibling checkouts and one that cannot run inside a
    ///     worktree at all.</b> An agent's own <c>RootDirectory</c> <em>is</em>
    ///     <c>…/.claude/worktrees/&lt;name&gt;</c>, so every path under it contains <c>/.claude/</c> —
    ///     a substring test excludes the entire tree and the rule then reports a clean repository
    ///     having read nothing. Measured here rather than reasoned about: the first run of this walk
    ///     returned 0 files for exactly that reason, which is the same mistake <c>CheckStrings</c>
    ///     records making a day apart from its opposite.
    /// </remarks>
    static readonly string[] SkippedRoots = [".claude/", ".git/"];

    /// <summary>Every C# file under a root that this rule is asked about.</summary>
    /// <param name="root">The repository root to walk.</param>
    /// <returns>Absolute paths with forward slashes, ordered.</returns>
    public static List<string> Sources(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var normalised = root.Replace('\\', '/').TrimEnd('/');

        return Directory
            .EnumerateFiles(normalised, "*.cs", SearchOption.AllDirectories)
            .Select(path => path.Replace('\\', '/'))
            .Where(path => !SkippedFragments.Any(fragment => path.Contains(fragment, StringComparison.Ordinal)))
            .Where(path => !SkippedRoots.Any(directory => path.StartsWith($"{normalised}/{directory}", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Where the files this rule is not asked about today are listed.</summary>
    /// <remarks>
    ///     ⚠ <b>Sixty-four blocks in forty-five files were already wrong when this rule was written,
    ///     and one of them is a live production staple</b> — <c>KeyChord.MacGlyphs</c>'s whole block
    ///     heads <c>MacWords</c>, so the glyph formatter is undocumented and the words formatter is
    ///     described twice. None is in doc 48's own files: batch 9's two were the only ones there and
    ///     the merge fixed them.
    ///     <para>
    ///         The exemption is per file and committed, on <c>docs/WhitespaceExempt.txt</c>'s terms —
    ///         the list may only shrink, a file on it that has become clean is an error rather than a
    ///         line that rots, and rewriting it is a command somebody runs rather than something the
    ///         gate does for itself. A gate that wrote its own exemptions would fail on nothing,
    ///         forever.
    ///     </para>
    /// </remarks>
    public const string ExemptionsPath = "docs/DocCommentExempt.txt";

    /// <summary>The files this rule is not asked about today.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Repository-relative paths with forward slashes.</returns>
    public static HashSet<string> Exemptions(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var file = Path.Combine(root, ExemptionsPath.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(file)
            ? File
                .ReadAllLines(file)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>What the exemption list and the findings say about each other.</summary>
    /// <param name="findings">Every finding, whose <see cref="Finding.File" /> is repository-relative.</param>
    /// <param name="exempt">The committed list, from <see cref="Exemptions" />.</param>
    /// <returns>
    ///     The files that are wrong and not listed, and the files that are listed and no longer wrong.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Both halves, because a list that may only grow is a number nothing measures.</b> A
    ///     file that has become clean has to leave the list in the same commit that cleaned it — the
    ///     rule <c>docs/WhitespaceExempt.txt</c> established after a count in
    ///     <c>Directory.Build.targets</c> managed to be wrong four times running.
    /// </remarks>
    public static (List<string> Unexpected, List<string> Stale) Review(
        IEnumerable<Finding> findings,
        HashSet<string> exempt
    ) {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(exempt);

        var offending = findings.Select(finding => finding.File).ToHashSet(StringComparer.Ordinal);

        return (
            offending.Except(exempt, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            exempt.Except(offending, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
        );
    }

    /// <summary>Everything wrong with the doc comments in one file.</summary>
    /// <param name="file">The name to report findings against.</param>
    /// <param name="text">The file's C#.</param>
    /// <returns>One finding per problem, in source order; empty when the file is clean.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Three questions, all of them syntactic.</b> Does a block carry two of a tag a member
    ///         has one of; does it name one parameter twice; does it name a parameter the member the
    ///         block is attached to does not have. Each is a property of the block and the following
    ///         declaration alone, which is why no compilation and no workspace is needed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A block attached to nothing is left alone deliberately.</b> A doc comment before a
    ///         closing brace, or at the end of a file, has no member to check against — reporting it
    ///         would be reporting the absence of the thing the rule reads rather than a defect in it,
    ///         and the two batch-9 stapleings were both attached to a member.
    ///     </para>
    /// </remarks>
    public static List<Finding> Check(string file, string text) {
        ArgumentNullException.ThrowIfNull(text);

        // ⚠ Both options are load-bearing and neither is the default that matters. Without
        // `DocumentationMode.Parse` a `///` block is unstructured trivia — `GetStructure()` returns
        // null for every one of them and this rule reports a clean tree having read nothing, which
        // is the exact failure mode #866 is about. `Preview` is so that syntax newer than the pinned
        // parser is parsed rather than recovered from: an error region can swallow a parameter list,
        // and a member that appears to take no parameters is something this rule reports.
        var tree = CSharpSyntaxTree.ParseText(
            text,
            CSharpParseOptions.Default
                .WithLanguageVersion(LanguageVersion.Preview)
                .WithDocumentationMode(DocumentationMode.Parse)
        );

        var root = tree.GetRoot();
        List<Finding> findings = [];

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true)) {
            if (trivia.GetStructure() is not DocumentationCommentTriviaSyntax block) {
                continue;
            }

            var line = tree.GetLineSpan(trivia.Span).StartLinePosition.Line + 1;
            var elements = TopLevelElements(block).ToList();

            foreach (var tag in Singletons) {
                var count = elements.Count(element => string.Equals(element.Name, tag, StringComparison.Ordinal));

                if (count > 1) {
                    findings.Add(
                        new(
                            file,
                            line,
                            $"this doc comment block has {count} <{tag}> elements. A member has one; a block with "
                            + "two has been given a second member's documentation — which is what happens when a "
                            + "new member is inserted between an existing comment and the member it documented."
                        )
                    );
                }
            }

            var documented = elements
                .Where(element => string.Equals(element.Name, "param", StringComparison.Ordinal))
                .Select(element => element.Attribute)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToList();

            foreach (var duplicate in documented.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1)) {
                findings.Add(new(file, line, $"this doc comment block documents the parameter `{duplicate.Key}` {duplicate.Count()} times."));
            }

            if (Owner(trivia) is not { } owner) {
                continue;
            }

            var declared = Parameters(owner);

            // ⚠ No parameter list and an empty one read the same to the person holding the comment,
            // and the second is the batch-9 case: a `[Fact]` takes nothing, so the staple's
            // `<param name="side">` is on a member whose parameter list exists and is empty. Saying
            // "its parameters are: " with nothing after the colon is how a message stops being read.
            if (declared is null or { Count: 0 }) {
                foreach (var name in documented.Distinct(StringComparer.Ordinal)) {
                    findings.Add(
                        new(
                            file,
                            line,
                            $"this doc comment block documents a parameter `{name}`, but the member it is attached "
                            + $"to — {Describe(owner)} — takes no parameters at all. The comment belongs to some "
                            + "other member."
                        )
                    );
                }

                continue;
            }

            foreach (var name in documented.Distinct(StringComparer.Ordinal).Where(name => !declared.Contains(name))) {
                findings.Add(
                    new(
                        file,
                        line,
                        $"this doc comment block documents a parameter `{name}`, which {Describe(owner)} does not "
                        + $"have. Its parameters are: {string.Join(", ", declared.Order(StringComparer.Ordinal))}."
                    )
                );
            }
        }

        return findings.OrderBy(finding => finding.Line).ToList();
    }

    /// <summary>The XML elements directly inside a doc comment block, with their <c>name</c>.</summary>
    /// <param name="block">The parsed block.</param>
    /// <returns>One entry per element, in order.</returns>
    /// <remarks>
    ///     ⚠ <b>Directly inside, which is the difference between a rule and a nuisance.</b> A
    ///     <c>&lt;summary&gt;</c> written inside a <c>&lt;code&gt;</c> sample is an example of one and
    ///     not a second description, and a textual count cannot tell those apart. This walks the
    ///     block's own content, so nesting is not counted.
    /// </remarks>
    static IEnumerable<(string Name, string? Attribute)> TopLevelElements(DocumentationCommentTriviaSyntax block) {
        foreach (var node in block.Content) {
            var (name, attributes) = node switch {
                XmlElementSyntax element => (element.StartTag.Name.ToString(), (IEnumerable<XmlAttributeSyntax>)element.StartTag.Attributes),
                XmlEmptyElementSyntax empty => (empty.Name.ToString(), empty.Attributes),
                _ => (null, null)
            };

            if (name is null) {
                continue;
            }

            var value = attributes!
                .OfType<XmlNameAttributeSyntax>()
                .Select(attribute => attribute.Identifier.Identifier.ValueText)
                .FirstOrDefault();

            yield return (name, value);
        }
    }

    /// <summary>The declaration a doc comment block is attached to, or <c>null</c>.</summary>
    /// <param name="trivia">The trivia the block was found as.</param>
    /// <returns>The smallest declaration the block heads.</returns>
    /// <remarks>
    ///     The block is leading trivia of some token, and the declaration it documents is the
    ///     innermost node that token opens. A local function is included because it is a member in
    ///     every sense this rule cares about — it has parameters and it can be documented.
    /// </remarks>
    static SyntaxNode? Owner(SyntaxTrivia trivia) {
        var token = trivia.Token;

        return token.Parent?
            .AncestorsAndSelf()
            .FirstOrDefault(node =>
                node is MemberDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax
                && node.GetFirstToken() == token
            );
    }

    /// <summary>
    ///     The parameter names a declaration has, or <c>null</c> when it has no parameter list at all.
    /// </summary>
    /// <param name="owner">The declaration, from <see cref="Owner" />.</param>
    /// <returns>The names, or <c>null</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>No parameter list and an empty parameter list are different answers and the rule needs
    ///     both.</b> A <c>[Fact]</c> that documents a parameter is the second batch-9 staple, and a
    ///     field or an enum member that documents one is the same defect — but a class with a primary
    ///     constructor <em>does</em> have parameters, and a record's positional members are documented
    ///     with <c>&lt;param&gt;</c> by convention. Returning the empty set for the first and
    ///     <c>null</c> for neither would fail every record in the tree.
    /// </remarks>
    static HashSet<string>? Parameters(SyntaxNode owner) {
        var list = owner switch {
            BaseMethodDeclarationSyntax method => method.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText),
            DelegateDeclarationSyntax method => method.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText),
            LocalFunctionStatementSyntax method => method.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText),
            IndexerDeclarationSyntax indexer => indexer.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText),
            TypeDeclarationSyntax type when type.ParameterList is { } parameters => parameters.Parameters.Select(parameter => parameter.Identifier.ValueText),
            _ => null
        };

        return list is null ? null : new HashSet<string>(list, StringComparer.Ordinal);
    }

    /// <summary>What to call a declaration in a message.</summary>
    /// <param name="owner">The declaration.</param>
    /// <returns>Its kind and its name, for a reader who has to find it.</returns>
    static string Describe(SyntaxNode owner) {
        var name = owner switch {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            LocalFunctionStatementSyntax method => method.Identifier.ValueText,
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            DelegateDeclarationSyntax method => method.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            EventDeclarationSyntax @event => @event.Identifier.ValueText,
            EnumMemberDeclarationSyntax member => member.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            IndexerDeclarationSyntax => "this[]",
            BaseFieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
            AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
            _ => "it"
        };

        return $"`{name}`";
    }
}
