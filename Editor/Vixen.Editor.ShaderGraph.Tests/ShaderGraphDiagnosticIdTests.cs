// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Editor.ShaderGraph.Tests;

/// <summary>One id means one thing across the whole <c>SG</c> family, in both assemblies that report it.</summary>
/// <remarks>
///     <para>
///         <b>The shape <a href="https://github.com/Rikarin/Vixen/issues/804">#804</a>,
///         <a href="https://github.com/Rikarin/Vixen/issues/936">#936</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/963">#963</a> gave three assemblies, at
///         the scope <a href="https://github.com/Rikarin/Vixen/issues/982">#982</a> says it should
///         have had.</b> A diagnostic family is what an author filters, suppresses and links help on;
///         it is not a compilation unit. <c>SG</c> was declared in two places that could not
///         enumerate each other, so the collision each of those three gates exists to stop was
///         reachable in the one family that spanned a seam.
///     </para>
///     <para>
///         ⚠ <b>So the walk below reads two source trees and not one.</b> That is the whole
///         difference from its three siblings, and it is why this file is anchored at
///         <c>Vixen.Editor.ShaderGraph.Tests</c> rather than either project: the assemblies are
///         asymmetric — <c>Vixen.Editor.AssetEditors</c> references
///         <c>Vixen.Editor.ShaderGraph</c> and not the reverse — but their source directories are
///         siblings, so a walk anchored one level up sees both.
///     </para>
///     <para>
///         ⚠ <b>Anchored at this file's own compiled path.</b> A walk from the repository root reads
///         <c>.claude/worktrees</c>, which holds a whole checkout per agent, so the roll call would be
///         comparing other people's copies of these files with each other.
///     </para>
/// </remarks>
public class ShaderGraphDiagnosticIdTests {
    /// <summary>What an id of either family looks like written down as a string.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Quoted, and that is load-bearing.</b> Prose about a diagnostic spells it as
    ///         <c>&lt;c&gt;SG0003&lt;/c&gt;</c> in a doc comment, which is what the remarks in this
    ///         repository are for and not a second call site. Only a string literal reports anything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>SGP</c> is tried before <c>SG</c>, because the alternation is ordered and
    ///         <c>SG</c> would match <c>SGP0001</c>'s first two letters.</b> It still ends up
    ///         matching through the backtrack, but relying on that is one regex edit away from a
    ///         detector that quietly stops seeing the preview family.
    ///     </para>
    /// </remarks>
    static readonly Regex Literal = new("\"((?:SGP|SG)[0-9]{4})\"", RegexOptions.CultureInvariant);

    /// <summary>Where this file was compiled from, which is what the source walk is anchored to.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>The directory holding every editor project, which is this test project's parent.</summary>
    static string Editors() => Path.GetDirectoryName(Path.GetDirectoryName(Here())!)!;

    /// <summary>Every <c>.cs</c> file one production project owns, without what the build wrote.</summary>
    static (string Name, string Text)[] Production(string project) =>
        Directory.GetFiles(Path.Combine(Editors(), project), "*.cs", SearchOption.AllDirectories)
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
    public void No_two_shader_graph_diagnostics_share_an_id() {
        var ids = ShaderGraphDiagnostics.Ids;

        Assert.True(
            ids.Length >= 9,
            $"ShaderGraphDiagnostics declares {ids.Length} ids and there were nine when this was written — "
            + "six SG and three SGP — so the reflection walk is finding less than the file holds, which is "
            + "the silent-success failure this file is about. Check that the members are still "
            + "`const string`."
        );

        Assert.All(ids, id => Assert.Matches("^SGP?[0-9]{4}$", id));

        var repeated = ids.GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            repeated.Length == 0,
            $"{string.Join(", ", repeated)} is declared twice in ShaderGraphDiagnostics. An id is what a "
            + "host filters, suppresses and links help on, so two meanings under one id is a filter that "
            + "hides the wrong half of them — #804, #936, #963, #982."
        );
    }

    /// <summary>
    ///     ⚠ Neither assembly that reports an <c>SG</c> id writes one as a literal, so the one
    ///     declaration is the only place one can come from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the half that stops the next collision</b>, and the other one only finds it
    ///         once somebody has already declared it. A call site that types <c>"SG0005"</c> gets no
    ///         compiler complaint whatever, and what it means is invisible until an author filters on
    ///         the id and loses half of what they meant to see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two trees, which is the whole of #982.</b> Before it, this project's four ids and
    ///         that project's two were governed by two gates that each saw one project — so the
    ///         collision was not merely possible, the surviving gate <em>routed</em> a new id into the
    ///         half that could not see the other.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instrument is the detector applied to the one file that must match.</b> If
    ///         <see cref="Literal" /> stopped matching — a raw string literal, a typo in the pattern —
    ///         the walk below would find no strays anywhere and pass having checked nothing. So the
    ///         ids the regex finds in <c>ShaderGraphDiagnostics.cs</c> are required to be exactly
    ///         <c>ShaderGraphDiagnostics.Ids</c>: the detector is proved against the declarations it
    ///         was derived from, in the same run.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_source_in_either_assembly_writes_a_shader_graph_id_as_a_literal() {
        var directory = Editors();

        Assert.True(
            Directory.Exists(Path.Combine(directory, "Vixen.Editor.ShaderGraph")),
            $"'{directory}' does not hold Vixen.Editor.ShaderGraph, so this roll call read no files at all. "
            + "It is anchored at this file's compiled path; a run whose sources are not on the machine "
            + "cannot take it."
        );

        var mine = Production("Vixen.Editor.ShaderGraph");
        var theirs = Production("Vixen.Editor.AssetEditors");

        // Both halves of the family are really being read: the compiler raises SG0001…SG0004 and the
        // document raises SG0000 and SG0100, so a walk that lost either project would still find one
        // set of ids and look like a clean run.
        Assert.Contains(mine, source => source.Name == "ShaderGraphCompiler.cs");
        Assert.Contains(theirs, source => source.Name == "ShaderGraphDocument.cs");

        Assert.True(
            mine.Length >= 12 && theirs.Length >= 70,
            $"{mine.Length} sources in Vixen.Editor.ShaderGraph and {theirs.Length} in "
            + "Vixen.Editor.AssetEditors, against fourteen and ninety when this was written. The walk is "
            + "finding almost nothing, which is a pass over no work rather than two clean assemblies."
        );

        var declaring = Assert.Single(mine, source => source.Name == "ShaderGraphDiagnostics.cs");

        var found = Literal.Matches(declaring.Text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ShaderGraphDiagnostics.Ids, found);

        var strays = mine.Concat(theirs)
            .Where(source => source.Name != "ShaderGraphDiagnostics.cs")
            .SelectMany(source => Literal.Matches(source.Text)
                .Select(match => $"{source.Name}: {match.Groups[1].Value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            strays.Length == 0,
            $"{string.Join(", ", strays)} — a diagnostic id written as a literal rather than taken from "
            + "ShaderGraphDiagnostics. Nothing tells you what that id already means, which is how TG0012, "
            + "TG0017 and TG0018 each came to mean two things one assembly over — #804, #963. Declare it "
            + "in ShaderGraphDiagnostics, with the sentence it means, and report it by name: both "
            + "assemblies can spell it, which is what #982 is."
        );
    }
}
