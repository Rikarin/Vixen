// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/936">#936</a>: one id means one thing here
///     too, and nothing in this assembly may invent one in passing.
/// </summary>
/// <remarks>
///     <para>
///         <b>The shape <a href="https://github.com/Rikarin/Vixen/issues/804">#804</a> gave
///         <c>TextureDiagnosticIdTests</c>, copied before there is damage to undo.</b> That gate was
///         built after <c>TG0012</c>, <c>TG0017</c> and <c>TG0018</c> had each come to mean two
///         things; this one is built while <c>Vixen.Editor.Texturing</c> reports exactly one id, so
///         its whole value is in the second half — the walk that stops the next call site typing four
///         characters nothing lists.
///     </para>
///     <para>
///         ⚠ <b>Anchored at this file's own compiled path.</b> A walk from the repository root reads
///         <c>.claude/worktrees</c>, which holds a whole checkout per agent, so the roll call would be
///         comparing other people's copies of these files with each other.
///         <c>TextureDiagnosticIdTests</c> and <c>TexturingAdapterRollCallTests</c> both made this
///         choice, for the same reason.
///     </para>
/// </remarks>
public class TexturingDiagnosticIdTests {
    /// <summary>What an id looks like written down as a string.</summary>
    /// <remarks>
    ///     ⚠ <b>Quoted, and that is load-bearing.</b> Prose about a diagnostic spells it as
    ///     <c>&lt;c&gt;TX0000&lt;/c&gt;</c> in a doc comment, which is what the remarks in this
    ///     repository are for and not a second call site. Only a string literal reports anything.
    /// </remarks>
    static readonly Regex Literal = new("\"(TX[0-9]{4})\"", RegexOptions.CultureInvariant);

    /// <summary>Where this file was compiled from, which is what the source walk is anchored to.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>The production project's directory, beside this test project's.</summary>
    static string Sources() =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Here())!)!, "Vixen.Editor.Texturing");

    /// <summary>Every <c>.cs</c> file the production project owns, without what the build wrote.</summary>
    static (string Name, string Text)[] Production() =>
        Directory.GetFiles(Sources(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .ToArray();

    /// <summary>⚠ No id is declared twice, which a compiler cannot tell you.</summary>
    /// <remarks>
    ///     <b>The instrument first, because an empty reflection result is trivially distinct.</b> The
    ///     count is floored rather than fixed, so a slice that adds a second id is covered by this
    ///     without coming here to say so — which is the difference between a derived roll call and a
    ///     copy of the list.
    /// </remarks>
    [Fact]
    public void No_two_diagnostics_here_share_an_id() {
        var ids = TexturingDiagnostics.Ids;

        Assert.True(
            ids.Length >= 1,
            "TexturingDiagnostics declares no ids at all, so the reflection walk came back empty — which is "
            + "the silent-success failure this file is about. Check that the members are still `const string`."
        );

        Assert.All(ids, id => Assert.Matches("^TX[0-9]{4}$", id));

        var repeated = ids.GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            repeated.Length == 0,
            $"{string.Join(", ", repeated)} is declared twice in TexturingDiagnostics. An id is what a host "
            + "filters, suppresses and links help on, so two meanings under one id is a filter that hides the "
            + "wrong half of them — #804, #936."
        );
    }

    /// <summary>
    ///     ⚠ Nothing in the production assembly writes an id as a literal, so the declarations are the
    ///     only place one can come from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the half that stops the next collision</b>, and the other one only finds it
    ///         once somebody has already declared it. A call site that types <c>"TX0001"</c> gets no
    ///         compiler complaint whatever, and what it means is invisible until an author filters on
    ///         the id and loses half of what they meant to see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instrument is the detector applied to the one file that must match.</b> If
    ///         <see cref="Literal" /> stopped matching — a raw string literal, a typo in the pattern —
    ///         the walk below would find no strays anywhere and pass having checked nothing. So the
    ///         ids the regex finds in <c>TexturingDiagnostics.cs</c> are required to be exactly
    ///         <c>TexturingDiagnostics.Ids</c>: the detector is proved against the declarations it was
    ///         derived from, in the same run.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_source_in_this_assembly_writes_a_diagnostic_id_as_a_literal() {
        var directory = Sources();

        Assert.True(
            Directory.Exists(directory),
            $"'{directory}' does not exist, so this roll call read no files at all. It is anchored at this "
            + "file's compiled path; a run whose sources are not on the machine cannot take it."
        );

        var sources = Production();

        Assert.Contains(sources, source => source.Name == "TextureGraphDocument.cs");

        Assert.True(
            sources.Length >= 25,
            $"Only {sources.Length} source files were read out of '{directory}', and there were about thirty "
            + "when this was written. The walk is finding almost nothing, which is a pass over no work rather "
            + "than a clean assembly."
        );

        var declaring = Assert.Single(sources, source => source.Name == "TexturingDiagnostics.cs");

        var found = Literal.Matches(declaring.Text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(TexturingDiagnostics.Ids, found);

        var strays = sources
            .Where(source => source.Name != "TexturingDiagnostics.cs")
            .SelectMany(source => Literal.Matches(source.Text)
                .Select(match => $"{source.Name}: {match.Groups[1].Value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            strays.Length == 0,
            $"{string.Join(", ", strays)} — a diagnostic id written as a literal rather than taken from "
            + "TexturingDiagnostics. Nothing tells you what that id already means, which is how TG0012, "
            + "TG0017 and TG0018 each came to mean two things one assembly over — #804, #936. Declare it "
            + "there, with the sentence it means, and report it by name."
        );
    }

    /// <summary>⚠ And the id a corrupt file actually produces is the declared one.</summary>
    /// <remarks>
    ///     <b>Because the two halves above are both statements about source text.</b> A declaration
    ///     nothing reports is the defect this workstream produces more than any other, and a
    ///     production path that went on reporting some other spelling would satisfy the roll calls
    ///     — the literal walk cannot see a <c>nameof</c>, an interpolation or a constant folded
    ///     somewhere else. This opens a <c>.vxtexgraph</c> that is not YAML and reads the id off the
    ///     document.
    /// </remarks>
    [Fact]
    public void A_graph_that_does_not_parse_reports_the_declared_id() {
        using var fixture = new TexturingFixture();

        var asset = fixture.AddGraph("Unreadable", "nodes: [ this is not\n  yaml: {");

        var document = new TextureGraphDocument(
            fixture.Project,
            asset,
            fixture.Paths.Absolute("Assets/Unreadable" + TextureGraphDocument.Extension)
        );

        var diagnostic = Assert.Single(document.LoadDiagnostics);

        Assert.Equal(TexturingDiagnostics.GraphFileDoesNotParse, diagnostic.Id);
    }
}
