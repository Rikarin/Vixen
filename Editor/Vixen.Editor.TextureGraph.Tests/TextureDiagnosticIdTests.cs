// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/804">#804</a>: one id means one thing, and
///     nothing in this assembly may invent one in passing.
/// </summary>
/// <remarks>
///     <para>
///         <b>The defect this replaces is a hand renumbering.</b> Batch 6 gave the Pixel Processor
///         <c>TG0017</c> and <c>TG0018</c>, which two other sites already used for entirely
///         different complaints — one of each pair a warning and the other an error. Batch 7 moved
///         the Pixel Processor to <c>TG0020</c>/<c>TG0021</c>, which fixed the instance and left the
///         cause: nine files reporting string literals and nothing anywhere listing them. ⚠ It also
///         left a third collision that predated the batch — <c>TG0012</c> meant both "this
///         expression is one this compiler refuses" and "this node's iteration count is out of
///         range", and a graph can hold both at once.
///     </para>
///     <para>
///         <b>Two mechanisms, because they cover different holes.</b>
///         <see cref="No_two_diagnostics_here_share_an_id" /> reads the declarations off
///         <c>TextureDiagnostics</c> and refuses a repeat, which is what makes a collision findable
///         at all — two members holding one string compile perfectly.
///         <see cref="No_source_in_this_assembly_writes_a_diagnostic_id_as_a_literal" /> walks the
///         production sources and refuses a <c>"TG…"</c> anywhere but that one file, which is what
///         keeps the declarations the only place an id can come from — a list nobody is obliged to
///         use is a list the tenth call site walks past.
///     </para>
///     <para>
///         ⚠ <b>Anchored at this file's own compiled path.</b> A walk from the repository root reads
///         <c>.claude/worktrees</c>, which holds a whole checkout per agent, so the roll call would
///         be reading other people's copies of these files.
///         <c>TextureAdapterRollCallTests</c> made the same choice for the same reason, and this
///         one goes one directory sideways rather than up.
///     </para>
/// </remarks>
public class TextureDiagnosticIdTests {
    /// <summary>Where this file was compiled from, which is what the source walk is anchored to.</summary>
    static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    /// <summary>What an id looks like written down as a string.</summary>
    /// <remarks>
    ///     ⚠ <b>Quoted, and that is load-bearing.</b> Several files spell an id inside a doc comment
    ///     as <c>&lt;c&gt;TG0005&lt;/c&gt;</c> — prose about a diagnostic, which is exactly what the
    ///     remarks in this repository are for and not a second call site. Only a string literal
    ///     reports anything.
    /// </remarks>
    static readonly Regex Literal = new("\"(TG[0-9]{4})\"", RegexOptions.CultureInvariant);

    /// <summary>The production project's directory, beside this test project's.</summary>
    static string Sources() => Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(Here())!)!,
        "Vixen.Editor.TextureGraph"
    );

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
    ///     <para>
    ///         <b>Derived, not listed.</b> <c>TextureDiagnostics.Ids</c> is a reflection walk over
    ///         the class's own literals, so this is a statement about the declarations rather than
    ///         about a copy of them, and a slice that adds a twenty-second id is covered by it
    ///         without editing this file. That is the difference between this and the five exact
    ///         equalities in this workstream that went red on a merge.
    ///     </para>
    ///     <para>
    ///         <b>The instruments first, because each is a way for this to be green over nothing.</b>
    ///         An empty reflection result is trivially distinct and trivially well-formed, so the
    ///         count is floored; and the floor is a floor rather than an equality, because a slice
    ///         that adds a diagnostic should not have to come here to say so.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_two_diagnostics_here_share_an_id() {
        var ids = TextureDiagnostics.Ids;

        Assert.True(
            ids.Length >= 19,
            $"TextureDiagnostics declares {ids.Length} ids and there were nineteen when this was written. "
            + "A reflection walk that came back short is the silent-success failure this file is about: "
            + "check that the members are still `const string`."
        );

        Assert.All(ids, id => Assert.Matches("^TG[0-9]{4}$", id));

        var repeated = ids.GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            repeated.Length == 0,
            $"{string.Join(", ", repeated)} is declared twice in TextureDiagnostics. An id is what a host "
            + "filters, suppresses and links help on, so two meanings under one id is a filter that hides the "
            + "wrong half of them — #804. Give the newer complaint an id of its own, or report it under the "
            + "one whose sentence already covers it."
        );
    }

    /// <summary>
    ///     ⚠ Nothing in the production assembly writes an id as a literal, so the declarations are
    ///     the only place one can come from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the half that stops the next collision</b>, and the other one only finds it
    ///         after somebody has already declared it. A tenth call site that types <c>"TG0012"</c>
    ///         gets no compiler complaint whatever, and the collision it makes is invisible until an
    ///         author filters on the id and loses half of what they meant to see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instrument is the detector applied to the one file that must match.</b> If
    ///         <see cref="Literal" /> stopped matching — a raw string literal, a
    ///         <c>nameof</c>-shaped spelling, a typo in the pattern — the walk below would find no
    ///         strays anywhere and pass having checked nothing. So the ids the regex finds in
    ///         <c>TextureDiagnostics.cs</c> are required to be exactly <c>TextureDiagnostics.Ids</c>:
    ///         the detector is proved against the declarations it was derived from, in the same run.
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

        Assert.Contains(sources, source => source.Name == "TextureGraphCompiler.cs");

        Assert.True(
            sources.Length >= 30,
            $"Only {sources.Length} source files were read out of '{directory}', and there were about forty "
            + "when this was written. The walk is finding almost nothing, which is a pass over no work rather "
            + "than a clean assembly."
        );

        // The detector, proved against the declarations: every id declared is one the regex finds in
        // the declaring file, and the regex finds nothing there that is not declared.
        var declaring = Assert.Single(sources, source => source.Name == "TextureDiagnostics.cs");

        var found = Literal.Matches(declaring.Text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(TextureDiagnostics.Ids, found);

        var strays = sources
            .Where(source => source.Name != "TextureDiagnostics.cs")
            .SelectMany(source => Literal.Matches(source.Text)
                .Select(match => $"{source.Name}: {match.Groups[1].Value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            strays.Length == 0,
            $"{string.Join(", ", strays)} — a diagnostic id written as a literal rather than taken from "
            + "TextureDiagnostics. Nothing tells you that id already means something else, which is how "
            + "TG0012, TG0017 and TG0018 each came to mean two things — #804. Declare it there, with the "
            + "sentence it means, and report it by name."
        );
    }

    /// <summary>
    ///     ⚠ A refused expression and a refused setting are two diagnostics with two ids, in one
    ///     compilation of one graph.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#804's third collision, as behaviour rather than as an inventory.</b> Both halves
    ///         of this graph were <c>TG0012</c> before this batch: <c>TextureGraphExpressions</c>
    ///         reports it for an expression it will not put through Raven, and
    ///         <c>Analysis/Flood Fill</c> reported it for an iteration count outside the range it
    ///         runs over. Two errors, two unrelated sentences, one id — and this is the graph that
    ///         holds both at once, which is what makes it a filter that hides the wrong half rather
    ///         than a tidiness complaint.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The assertion is that the ids differ, not what either of them is</b>, so it
    ///         survives the day one of these complaints is renumbered again — and it goes red the
    ///         day anything merges the two back together. The ports are asserted too, because two
    ///         diagnostics that happened to be about the same port would make "they differ" a
    ///         weaker claim than it reads as.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_refused_expression_and_a_refused_setting_are_told_apart() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var flood = graph.Add("Analysis/Flood Fill");
        var blur = graph.Add("Filters/Blur");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(flood.Id, "Mask"));
        graph.Connect(new(flood.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));

        // Out of the range the node runs over, which is what one half used to report as TG0012.
        flood.SetValue("Iterations", 0);

        // And an expression the compiler refuses before Raven sees it: a newline ends a statement in
        // Raven, so this is two of them and the second is discarded.
        blur.SetValue("Radius", 3f);
        blur.SetText(TextureGraphExpressions.KeyOf("Radius"), "amount\n * 2f");

        TextureGraphCompiler compiler = new(registry) { BaseWidth = 128, BaseHeight = 128, Seed = 3 };

        compiler.Parameters.Add(new("amount", Default: 0.5f, Minimum: 0f, Maximum: 4f));

        var diagnostics = compiler.Compile(graph).Diagnostics;

        var setting = Assert.Single(diagnostics, one => string.Equals(one.Port, "Iterations", StringComparison.Ordinal));
        var expression = Assert.Single(diagnostics, one => string.Equals(one.Port, "Radius", StringComparison.Ordinal));

        Assert.NotEqual(setting.Id, expression.Id);

        // And both are still errors an author has to act on, so this is not "they differ because one
        // of them quietly became a warning".
        Assert.Equal(NodeSeverity.Error, setting.Severity);
        Assert.Equal(NodeSeverity.Error, expression.Severity);
    }
}
