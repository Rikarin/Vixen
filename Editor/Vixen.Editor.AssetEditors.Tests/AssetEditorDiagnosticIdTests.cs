// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Vixen.Core;
using Vixen.Editor.AssetEditors.Compositor;
using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.ShaderGraph;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>One id means one thing here too, and nothing in this assembly may invent one in passing.</summary>
/// <remarks>
///     <para>
///         <b>The shape <a href="https://github.com/Rikarin/Vixen/issues/804">#804</a> gave
///         <c>TextureDiagnosticIdTests</c> and #936 gave <c>TexturingDiagnosticIdTests</c>, on the
///         assembly <a href="https://github.com/Rikarin/Vixen/issues/963">#963</a> named third.</b>
///         Unlike those two this one is not built ahead of the damage: the assembly already reports
///         nine ids across three prefixes, so the second half below is a gate over eight existing
///         chances rather than a precaution about the next one.
///     </para>
///     <para>
///         ⚠ <b>Anchored at this file's own compiled path.</b> A walk from the repository root reads
///         <c>.claude/worktrees</c>, which holds a whole checkout per agent, so the roll call would
///         be comparing other people's copies of these files with each other.
///     </para>
///     <para>
///         ⚠ <b>The <c>SG</c> family is no longer half of it, and the walk still looks for
///         <c>SG</c></b> — <a href="https://github.com/Rikarin/Vixen/issues/982">#982</a>. Every
///         <c>SG</c> and <c>SGP</c> id is declared in
///         <c>Vixen.Editor.ShaderGraph.ShaderGraphDiagnostics</c>, which this assembly references and
///         <c>ShaderGraphDocument</c> reports two of; <c>ShaderGraphDiagnosticIdTests</c> walks both
///         source trees against that one list. What stays here is the <em>refusal</em>: an
///         <c>SG…</c> literal written in this project is a stray, because the declaration it belongs
///         to is one <c>using</c> away. ⚠ Before #982 this same refusal actively routed a new
///         <c>SG</c> id into the half of the family that could not see the other half.
///     </para>
/// </remarks>
public class AssetEditorDiagnosticIdTests {
    /// <summary>What an id looks like written down as a string.</summary>
    /// <remarks>
    ///     ⚠ <b>Quoted, and that is load-bearing.</b> Prose about a diagnostic spells it as
    ///     <c>&lt;c&gt;CO0003&lt;/c&gt;</c> in a doc comment, which is what the remarks in this
    ///     repository are for and not a second call site. Only a string literal reports anything.
    /// </remarks>
    static readonly Regex Literal = new("\"((?:SGP|SG|CO|VF)[0-9]{4})\"", RegexOptions.CultureInvariant);

    /// <summary>Where this file was compiled from, which is what the source walk is anchored to.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>The production project's directory, beside this test project's.</summary>
    static string Sources() =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Here())!)!, "Vixen.Editor.AssetEditors");

    /// <summary>Every source file the production project owns, without what the build wrote.</summary>
    /// <remarks>
    ///     ⚠ <b><c>.vxml</c> as well as <c>.cs</c>, and here that is a live blind spot rather than a
    ///     precaution</b> — <a href="https://github.com/Rikarin/Vixen/issues/1007">#1007</a>. This
    ///     project carries twenty-five views, and a view's <c>&lt;code&gt;</c> block is production
    ///     C#: it is where four of the five document kinds render their load diagnostics, and
    ///     therefore exactly where an id written as a literal would be. A <c>--include="*.cs"</c>
    ///     grep of this shape filed
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1002">#1002</a>, claiming all five
    ///     report a load failure to nobody; it was closed invalid.
    ///     <c>ShaderGraphDiagnosticIdTests.Production</c> reads both for the same reason.
    /// </remarks>
    static (string Name, string Text)[] Production() =>
        Directory.GetFiles(Sources(), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".vxml", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .ToArray();

    /// <summary>⚠ No id is declared twice, which a compiler cannot tell you.</summary>
    /// <remarks>
    ///     <b>The instrument first, because an empty reflection result is trivially distinct.</b> The
    ///     count is floored rather than fixed, so a slice that adds an id is covered by this without
    ///     coming here to say so — which is the difference between a derived roll call and a copy of
    ///     the list.
    /// </remarks>
    [Fact]
    public void No_two_diagnostics_here_share_an_id() {
        var ids = AssetEditorDiagnostics.Ids;

        Assert.True(
            ids.Length >= 7,
            $"AssetEditorDiagnostics declares {ids.Length} ids and there were seven after #982 moved the two "
            + "SG ones to ShaderGraphDiagnostics, so the reflection walk is finding less than the file holds "
            + "— which is the silent-success failure this file is about. Check that the members are still "
            + "`const string`."
        );

        // ⚠ No SG here any more — #982. An `SG…` id in this array would be a second declaration of a
        // family `ShaderGraphDiagnostics` declares whole, which is the collision one seam along.
        Assert.All(ids, id => Assert.Matches("^(CO|VF)[0-9]{4}$", id));

        var repeated = ids.GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            repeated.Length == 0,
            $"{string.Join(", ", repeated)} is declared twice in AssetEditorDiagnostics. An id is what a host "
            + "filters, suppresses and links help on, so two meanings under one id is a filter that hides the "
            + "wrong half of them — #804, #936, #963."
        );
    }

    /// <summary>
    ///     ⚠ Nothing in the production assembly writes an id as a literal, so the declarations are the
    ///     only place one can come from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the half that stops the next collision</b>, and the other one only finds it
    ///         once somebody has already declared it. A call site that types <c>"CO0007"</c> gets no
    ///         compiler complaint whatever, and what it means is invisible until an author filters on
    ///         the id and loses half of what they meant to see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instrument is the detector applied to the one file that must match.</b> If
    ///         <see cref="Literal" /> stopped matching — a raw string literal, a typo in the pattern —
    ///         the walk below would find no strays anywhere and pass having checked nothing. So the
    ///         ids the regex finds in <c>AssetEditorDiagnostics.cs</c> are required to be exactly
    ///         <c>AssetEditorDiagnostics.Ids</c>: the detector is proved against the declarations it
    ///         was derived from, in the same run.
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

        Assert.Contains(sources, source => source.Name == "CompositorGraphCompiler.cs");

        // ⚠ And it really read the views, or the widening is a glob term matching nothing and this
        // is the `--include="*.cs"` sweep again with a longer comment — #1007. Twenty-five of them,
        // and their code blocks are where four of the five document kinds render the diagnostics
        // this file is about.
        Assert.Contains(sources, source => source.Name.EndsWith(".vxml", StringComparison.Ordinal));

        Assert.True(
            sources.Length >= 70,
            $"Only {sources.Length} source files were read out of '{directory}', and there were ninety when "
            + "this was written. The walk is finding almost nothing, which is a pass over no work rather "
            + "than a clean assembly."
        );

        var declaring = Assert.Single(sources, source => source.Name == "AssetEditorDiagnostics.cs");

        var found = Literal.Matches(declaring.Text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AssetEditorDiagnostics.Ids, found);

        var strays = sources
            .Where(source => source.Name != "AssetEditorDiagnostics.cs")
            .SelectMany(source => Literal.Matches(source.Text)
                .Select(match => $"{source.Name}: {match.Groups[1].Value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            strays.Length == 0,
            $"{string.Join(", ", strays)} — a diagnostic id written as a literal rather than taken from a "
            + "declaration. Nothing tells you what that id already means, which is how TG0012, TG0017 and "
            + "TG0018 each came to mean two things one assembly over — #804, #963. CO and VF are declared "
            + "in AssetEditorDiagnostics; SG and SGP belong to Vixen.Editor.ShaderGraph's "
            + "ShaderGraphDiagnostics, which this assembly references, because that family is reported "
            + "from both projects — #982."
        );
    }

    /// <summary>⚠ And the id a corrupt file actually produces is the declared one.</summary>
    /// <remarks>
    ///     <b>Because the two halves above are both statements about source text.</b> A declaration
    ///     nothing reports is the defect this workstream produces more than any other, and a
    ///     production path that went on reporting some other spelling would satisfy the roll calls —
    ///     the literal walk cannot see a <c>nameof</c>, an interpolation or a constant folded
    ///     somewhere else. This opens a <c>.vxcompositor</c> that is not YAML and reads the id off
    ///     the document.
    /// </remarks>
    [Fact]
    public void A_compositor_that_does_not_parse_reports_the_declared_id() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/Unreadable.vxcomp", "nodes: [ this is not\n  yaml: {");
        var document = new CompositorDocument(fixture.Project, AssetId.New(), path);
        var diagnostic = Assert.Single(document.LoadDiagnostics);

        Assert.Equal(AssetEditorDiagnostics.CompositorFileDoesNotParse, diagnostic.Id);
    }

    /// <summary>⚠ And the one id this assembly reports out of another assembly's declaration.</summary>
    /// <remarks>
    ///     <b>The half of <a href="https://github.com/Rikarin/Vixen/issues/982">#982</a> a source walk
    ///     cannot take.</b> Both roll calls are statements about text: this project's refuses an
    ///     <c>SG</c> literal and <c>ShaderGraphDiagnosticIdTests</c> requires every one of them to be
    ///     declared in <c>ShaderGraphDiagnostics</c>. Neither can tell that the constant a document
    ///     actually reports is the constant it names — a <c>nameof</c>, an interpolation or a folded
    ///     constant would satisfy both — and the whole point of moving the declaration was that the
    ///     panel shows one family under one set of numbers.
    /// </remarks>
    [Fact]
    public void A_shader_graph_that_does_not_parse_reports_the_declared_id() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/Unreadable.vxshadergraph", "nodes: [ this is not\n  yaml: {");
        var document = new ShaderGraphDocument(fixture.Project, AssetId.New(), path);
        var diagnostic = Assert.Single(document.LoadDiagnostics);

        Assert.Equal(ShaderGraphDiagnostics.FileDoesNotParse, diagnostic.Id);
    }
}
