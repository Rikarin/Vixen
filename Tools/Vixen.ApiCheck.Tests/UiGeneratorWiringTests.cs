// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ That a project with an interface in it actually gets both UI generators — asked of the
///     shapes the wiring has, and of every project in the tree that reaches it.
/// </summary>
/// <remarks>
///     <para>
///         <a href="https://github.com/Rikarin/Vixen/issues/1113">#1113</a>, and the reason it is a
///         separate issue from the fix it gates is that
///         <a href="https://github.com/Rikarin/Vixen/issues/1102">#1102</a> changed a rule with five
///         shapes and left one of them covered. The uncovered ones are not hypothetical: two real
///         projects self-import <c>Vixen.Ui.targets</c> and name one generator, one names both, and
///         the rest name neither.
///     </para>
///     <para>
///         <b>An evaluation, not a build, which is what makes this affordable.</b>
///         <c>dotnet msbuild &lt;project&gt; -getItem:ProjectReference</c> runs the property and item
///         passes and stops — no restore, no compile, no analyzer — so the whole file costs seconds
///         where <c>BuildIntegrationTests</c>' equivalent costs about twenty per case. The question
///         is entirely an item-list question, so nothing is lost by not compiling.
///     </para>
///     <para>
///         ⚠ <b>The instrument first: a walk that resolves no projects reports every project
///         compliant.</b> So the subject set is asserted before it is used, and not merely by count:
///         every wiring <em>shape</em> has to still be present in the tree, because a classifier that
///         silently stopped recognising a self-import would leave a walk that passes over nothing
///         while printing success. An evaluation that fails, or that returns no references at all, is
///         a failure here rather than a project with no duplicates.
///     </para>
///     <para>
///         ⚠ <b>What this deliberately does not check: that a project declaring
///         <c>[UiProperty]</c> has the property generator.</b> Telling a declaration from a mention
///         needs a parse — <c>Vixen.Ui.Markup</c>, <c>Vixen.Ui.Generators</c> and two test
///         assemblies all contain the text and none of them declares one — and a rule with an
///         exemption list per false positive is a worse instrument than none. VX4003 asks that
///         question at build time for any project that also owns a <c>.vxml</c>; the gap is a project
///         with a <c>[UiProperty]</c> and no markup, which nothing sees.
///     </para>
/// </remarks>
public sealed class UiGeneratorWiringTests : IDisposable {
    const string PropertyGenerator = "Vixen.Ui.Generators.csproj";
    const string MarkupGenerator = "Vixen.Ui.Markup.Generators.csproj";

    /// <summary>
    ///     ⚠ Inside the repository, under the one directory that is both git-ignored and skipped by
    ///     every gate that globs the tree.
    /// </summary>
    /// <remarks>
    ///     <b>Not the system temp directory, and that is a correction rather than a preference.</b>
    ///     A fixture has to spell its own references the way a real project does — relative — because
    ///     the rule under test compares what the project named with what the targets file wants, and
    ///     comparing two notations for one path is the failure it is written to avoid. From
    ///     <c>$TMPDIR</c> on macOS the relative walk climbs out through <c>/var</c>'s symlink and
    ///     <c>%(FullPath)</c> resolves to <c>/private/Users/…</c>, a path that does not exist: the
    ///     comparison then misses and the fixture reports a duplicate the tree does not have. Which
    ///     is the whole hazard in one line — a fixture that builds its own inputs can fail in ways
    ///     the subject cannot.
    /// </remarks>
    readonly string fixtures = Path.Combine(RepositoryRoot(), "artifacts", "ui-wiring", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose() {
        try {
            if (Directory.Exists(fixtures)) {
                Directory.Delete(fixtures, recursive: true);
            }
        } catch (IOException) {
            // A fixture directory that would not go is not a test failure.
        }
    }

    /// <summary>
    ///     ⚠ All five shapes of the wiring, one evaluation each — the four the tree could not cover
    ///     and the one it does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The table is #1113's. <c>VixenUi=true</c> crossed with "does the project import
    ///         <c>Vixen.Ui.targets</c> itself" and "which generators has it already named", plus the
    ///         project that imports and does not set the property, which must come out unchanged.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves are falsifiable and by different sabotage.</b> Restoring the
    ///         pre-#1102 guard — <c>_VixenUiWiredByProject</c> on the generator item group — turns
    ///         the two self-importing rows that name nothing or one into 0/0 and 1/0. Removing the
    ///         <c>Exclude</c> turns the rows that name a generator by hand into 2/2 and 2/1. Neither
    ///         sabotage moves the other rows, which is what says the cases are not one case written
    ///         five times.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryShapeOfTheWiringGetsEachGeneratorExactlyOnce() {
        var root = RepositoryRoot();
        var property = Path.Combine(root, "Core", "Vixen.Ui.Generators", PropertyGenerator);
        var markup = Path.Combine(root, "Core", "Vixen.Ui.Markup.Generators", MarkupGenerator);

        (string Name, bool VixenUi, bool SelfImport, string[] Named, int Property, int Markup)[] shapes = [
            ("named-neither-no-import", true, false, [], 1, 1),
            ("named-neither-self-import", true, true, [], 1, 1),
            ("named-one-self-import", true, true, [property], 1, 1),
            ("named-both-self-import", true, true, [property, markup], 1, 1),
            ("named-both-no-import", true, false, [property, markup], 1, 1),

            // ⚠ The row that must NOT move: no property, so this file contributes nothing and the
            // project keeps exactly what it wrote. Without it every assertion above is also
            // satisfied by a targets file that adds both generators to every project in existence.
            ("no-property-self-import", false, true, [], 0, 0)
        ];

        var evaluated = Evaluate(
            shapes.Select(shape => Fixture(shape.Name, shape.VixenUi, shape.SelfImport, shape.Named)).ToList()
        );

        var wrong = new List<string>();

        foreach (var shape in shapes) {
            var references = evaluated[Path.Combine(fixtures, shape.Name, "Demo.csproj")];
            var property_ = Count(references, PropertyGenerator);
            var markup_ = Count(references, MarkupGenerator);

            if (property_ != shape.Property || markup_ != shape.Markup) {
                wrong.Add(
                    $"{shape.Name}: expected {shape.Property} property generator and {shape.Markup} markup "
                    + $"compiler, evaluated to {property_} and {markup_}."
                );
            }
        }

        Assert.True(
            wrong.Count == 0,
            "Directory.Build.targets contributes the two UI generators to a VixenUi=true project, "
            + "deduplicated against whatever the project already named. A count of 0 is a generator "
            + "that will not run — silently, for the property one. A count of 2 is a duplicate "
            + "ProjectReference, which the SDK reports as an error.\n  "
            + string.Join("\n  ", wrong)
        );
    }

    /// <summary>
    ///     And the same question of the tree itself, which is where a shape nobody wrote a case for
    ///     turns up.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The four measurements #1102's commit message carries were taken by hand with
    ///         <c>-getItem:ProjectReference</c> and re-run by nothing. This is that, over every
    ///         project in the solution that reaches the wiring at all, which is two dozen rather than
    ///         four.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The subject is <c>Vixen.slnx</c> for <c>ApiCoverageTests</c>' reason</b>: a walk
    ///         from the repository root descends into <c>.claude/worktrees</c>, where there is a
    ///         whole checkout per agent, and would evaluate one agent's copy of this repository
    ///         against another's.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryProjectThatReachesTheWiringGetsEachGeneratorAtMostOnce() {
        var subjects = Subjects();

        // ⚠ The instrument, and it is not a count of one thing. A classifier that stopped
        // recognising a self-import, a solution file that stopped parsing, or a `.vxml` scan that
        // walked the wrong directory each leave a subject set that is smaller and a walk that passes
        // over nothing — and every one of them prints success.
        // Seventeen today. The floor is a little under it because a project legitimately leaving
        // the set is an ordinary commit and a floor that tracked the count exactly would be a
        // second ledger to update; two below is close enough that a classifier which had stopped
        // working could not sit under it.
        Assert.True(
            subjects.Count >= 15,
            $"Only {subjects.Count} projects reach the UI wiring, which is too few to be this tree — "
            + "the classifier below has stopped recognising something, and a walk over nothing "
            + "reports every project compliant."
        );
        Assert.Contains(subjects, subject => subject is { VixenUi: true, SelfImport: false });
        Assert.Contains(subjects, subject => subject is { VixenUi: false, SelfImport: true });
        Assert.Contains(subjects, subject => subject.Markup > 0);
        Assert.Contains(subjects, subject => subject.MarkupCheck is false);

        // ⚠ And the shape #1113's table says is here is NOT here, which is why the case above is
        // synthetic and has to be: no project in this tree both sets VixenUi and imports
        // Vixen.Ui.targets itself. The issue names Editor/Vixen.Editor.Texturing for that row, and
        // the `<VixenUi>true</VixenUi>` in that file is inside a comment explaining #1102 — a text
        // search finds it and an XML reader does not. Asserted rather than written down, so that a
        // project arriving in the shape sends somebody back to this comment.
        Assert.DoesNotContain(subjects, subject => subject is { VixenUi: true, SelfImport: true });

        var evaluated = Evaluate(subjects.Select(subject => subject.Path).ToList());

        // ⚠ The other half of the instrument, and the one an empty item list would slip past: a
        // reader that stopped matching returns nothing for every project, and "nothing" satisfies
        // every rule below. Two dozen generator references is what this tree carries; a lone
        // project may legitimately have none, and all of them may not.
        var total = evaluated.Values.Sum(references => Count(references, PropertyGenerator) + Count(references, MarkupGenerator));

        Assert.True(
            total >= 20,
            $"The whole tree evaluated to {total} generator references, which is too few to be this "
            + "tree — the evaluation or the reader has stopped working, and a rule over an empty "
            + "list passes."
        );

        var wrong = new List<string>();

        foreach (var subject in subjects) {
            var references = evaluated[subject.Path];
            var property = Count(references, PropertyGenerator);
            var markup = Count(references, MarkupGenerator);
            var name = Path.GetRelativePath(RepositoryRoot(), subject.Path).Replace('\\', '/');

            if (property > 1 || markup > 1) {
                wrong.Add($"{name}: names a generator twice ({property} property, {markup} markup) — the SDK refuses a duplicate ProjectReference.");
            } else if (subject.VixenUi && (property != 1 || markup != 1)) {
                wrong.Add($"{name}: sets VixenUi=true and evaluates to {property} property generator and {markup} markup compiler, not one of each.");
            } else if (subject is { Markup: > 0, MarkupCheck: not false } && markup != 1) {
                wrong.Add($"{name}: owns {subject.Markup} .vxml and has no markup compiler, so they are compiler input nothing reads (VX4002).");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "A project with an interface in it gets both generators exactly once — from its own "
            + "ProjectReference, from Directory.Build.targets' contribution under VixenUi=true, or "
            + "one of each. Analyzers do not travel through a ProjectReference, so the assembly that "
            + "owns the .vxml or the [UiProperty] is the one that has to end up naming them.\n  "
            + string.Join("\n  ", wrong)
        );
    }

    // ================================================================== Plumbing

    /// <summary>Writes one synthetic project and returns its path.</summary>
    /// <param name="name">The shape's name, which is also its directory.</param>
    /// <param name="vixenUi">Whether it sets the property.</param>
    /// <param name="selfImport">Whether it imports <c>Vixen.Ui.targets</c> itself.</param>
    /// <param name="named">The generators it names by hand.</param>
    /// <remarks>
    ///     ⚠ Every path it writes is relative, because that is how a project in this tree spells its
    ///     own references and the rule under test is a comparison between two spellings. It reaches
    ///     <c>Directory.Build.targets</c> through <c>DirectoryBuildTargetsPath</c> — the property
    ///     MSBuild's own auto-import reads — so the file lands where it lands for every project here
    ///     rather than at a hand-picked point that happens to work.
    /// </remarks>
    string Fixture(string name, bool vixenUi, bool selfImport, string[] named) {
        var root = RepositoryRoot();
        var directory = Path.Combine(fixtures, name);
        Directory.CreateDirectory(directory);

        var references = new StringBuilder();

        foreach (var project in named) {
            references
                .Append("    <ProjectReference Include=\"")
                .Append(Path.GetRelativePath(directory, project))
                .AppendLine("\" OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" />");
        }

        var import = selfImport
            ? $"""  <Import Project="{Path.GetRelativePath(directory, Path.Combine(root, "Core", "Vixen.Ui", "build", "Vixen.Ui.targets"))}" />"""
            : string.Empty;

        var path = Path.Combine(directory, "Demo.csproj");

        File.WriteAllText(
            path,
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <PropertyGroup>
                 <TargetFramework>net10.0</TargetFramework>
                 <DirectoryBuildTargetsPath>{Path.Combine(root, "Directory.Build.targets")}</DirectoryBuildTargetsPath>
                 {(vixenUi ? "<VixenUi>true</VixenUi>" : string.Empty)}
               </PropertyGroup>
               <ItemGroup>
                 <ProjectReference Include="{Path.GetRelativePath(directory, Path.Combine(root, "Core", "Vixen.Ui", "Vixen.Ui.csproj"))}" />
             {references}  </ItemGroup>
             {import}
             </Project>
             """
        );

        return path;
    }

    /// <summary>One project this file has an opinion about.</summary>
    /// <param name="Path">Its absolute path.</param>
    /// <param name="VixenUi">Whether it sets <c>VixenUi</c> to true.</param>
    /// <param name="SelfImport">Whether it imports <c>Vixen.Ui.targets</c> itself.</param>
    /// <param name="Markup">How many <c>.vxml</c> files sit under it, outside <c>obj</c> and <c>bin</c>.</param>
    /// <param name="MarkupCheck">
    ///     What it says about <c>VixenUiMarkupCheck</c>, or null for the ordinary case of saying
    ///     nothing. ⚠ Read rather than assumed: <c>Tools/Vixen.Templates</c> sets it false because
    ///     its <c>.vxml</c> belongs to a project it copies rather than builds, and a rule that did
    ///     not know about the escape hatch the build's own diagnostics offer would be a rule that
    ///     disagrees with the gate it is standing in for.
    /// </param>
    record Subject(string Path, bool VixenUi, bool SelfImport, int Markup, bool? MarkupCheck);

    /// <summary>Every project in the solution that reaches the UI wiring in any way.</summary>
    static List<Subject> Subjects() {
        var root = RepositoryRoot();
        var subjects = new List<Subject>();

        foreach (var relative in SolutionProjects()) {
            var path = Path.Combine(root, relative);

            if (!File.Exists(path)) {
                continue;
            }

            var document = XDocument.Load(path);

            // ⚠ The Import *element*, not the text. Directory.Build.targets' own remarks say what
            // goes wrong with a grep here, having watched four recounts do it: a comment naming the
            // file matches, and Core/Vixen.Ui.Styling.Utilities carries exactly such a comment.
            var selfImport = document.Descendants("Import")
                .Any(element => (element.Attribute("Project")?.Value ?? string.Empty)
                    .Replace('\\', '/')
                    .EndsWith("Vixen.Ui.targets", StringComparison.Ordinal));

            var vixenUi = string.Equals(Property(document, "VixenUi"), "true", StringComparison.OrdinalIgnoreCase);
            var markupCheck = Property(document, "VixenUiMarkupCheck") is { } stated
                ? string.Equals(stated, "true", StringComparison.OrdinalIgnoreCase)
                : (bool?)null;

            var markup = Markup(Path.GetDirectoryName(path)!);

            if (selfImport || vixenUi || markup > 0) {
                subjects.Add(new(path, vixenUi, selfImport, markup, markupCheck));
            }
        }

        return subjects;
    }

    static string? Property(XDocument project, string name) =>
        project.Descendants(name).FirstOrDefault()?.Value.Trim();

    /// <summary>How many committed-looking <c>.vxml</c> files a project owns.</summary>
    /// <remarks>
    ///     <c>obj</c> and <c>bin</c> are skipped because a generated tree under them holds copies of
    ///     the very files being counted, and counting a build output as a source would make the
    ///     answer depend on whether anybody had built.
    /// </remarks>
    static int Markup(string directory) {
        var count = 0;

        foreach (var path in Directory.EnumerateFiles(directory, "*.vxml", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');

            if (!relative.StartsWith("obj/", StringComparison.Ordinal) && !relative.StartsWith("bin/", StringComparison.Ordinal)) {
                count++;
            }
        }

        return count;
    }

    /// <summary>Evaluates each project's <c>ProjectReference</c> item list, four at a time.</summary>
    /// <param name="projects">The projects to evaluate.</param>
    /// <returns>The file names of each project's references, by project path.</returns>
    /// <remarks>
    ///     ⚠ Node reuse off, for the reason <c>BuildIntegrationTests.Run</c> records: a reusable
    ///     MSBuild worker outlives the invocation that spawned it and inherits the redirected pipes,
    ///     so the child can exit while the read handle stays open and the wait never returns.
    /// </remarks>
    static Dictionary<string, List<string>> Evaluate(List<string> projects) {
        var results = new ConcurrentDictionary<string, List<string>>(StringComparer.Ordinal);

        Parallel.ForEach(
            projects,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            project => results[project] = ReferencesOf(project)
        );

        return new(results, StringComparer.Ordinal);
    }

    static List<string> ReferencesOf(string project) {
        var process = new Process {
            StartInfo = new ProcessStartInfo("dotnet") {
                WorkingDirectory = Path.GetDirectoryName(project)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                Environment = { ["MSBUILDDISABLENODEREUSE"] = "1" }
            }
        };

        process.StartInfo.ArgumentList.Add("msbuild");
        process.StartInfo.ArgumentList.Add(project);
        process.StartInfo.ArgumentList.Add("-getItem:ProjectReference");
        process.StartInfo.ArgumentList.Add("-nologo");
        process.StartInfo.ArgumentList.Add("-nodeReuse:false");

        var output = new StringBuilder();
        process.OutputDataReceived += (_, line) => output.AppendLine(line.Data);
        process.ErrorDataReceived += (_, line) => output.AppendLine(line.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        // ⚠ A failed evaluation is a failure and not an empty list. The whole family of defects this
        // file is about is a check that reports success on the day it does not run, and "the
        // evaluation did not happen" is indistinguishable from "no duplicates" to every assertion
        // above. ⚠ An empty item list, though, is a real answer — Tools/Vixen.Templates references
        // no project at all — so the aggregate is what the caller checks instead.
        Assert.True(process.ExitCode == 0, $"Evaluating {project} failed:\n{output}");

        using var json = JsonDocument.Parse(output.ToString());

        if (!json.RootElement.TryGetProperty("Items", out var items)
            || !items.TryGetProperty("ProjectReference", out var element)) {
            return [];
        }

        return [
            .. element.EnumerateArray()
                .Select(item => Path.GetFileName(item.GetProperty("FullPath").GetString() ?? string.Empty))
        ];
    }

    static int Count(List<string> references, string name) =>
        references.Count(reference => string.Equals(reference, name, StringComparison.Ordinal));

    static IEnumerable<string> SolutionProjects() =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "Vixen.slnx"))
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!.Replace('\\', '/'));

    /// <summary>Walks up from the test assembly until the repository root is recognisable.</summary>
    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
