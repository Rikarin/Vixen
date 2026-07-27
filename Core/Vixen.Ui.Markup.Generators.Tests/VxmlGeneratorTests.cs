// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.CodeAnalysis;
using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.Markup.Generators.Tests;

/// <summary>
///     The build half of the markup channel: a <c>.vxml</c> in the project, a class in the
///     compilation, and an error that lands on the markup rather than on generated code.
/// </summary>
public class VxmlGeneratorTests {
    const string Counter = """
                           @component Counter
                           @using Vixen.Ui.Reactive

                           @code {
                               public Signal<int> Count { get; } = new(0);
                           }

                           <div class="root">
                               <span>Count: @Count.Value</span>
                           </div>
                           """;

    // ------------------------------------------------------------ Generating

    [Fact]
    public void A_vxml_file_becomes_a_class_that_compiles() {
        var run = Harness.Once(Counter);

        Assert.Empty(run.Errors);
        Assert.Single(run.Generated);
        Assert.Contains("partial class Counter", run.Source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The namespace comes from the project and the folders, the way a hand-written C# file in
    ///     the same directory is named. Without one every component in every project would land in
    ///     the global namespace together.
    /// </summary>
    [Fact]
    public void The_class_lands_in_the_root_namespace_plus_the_file_s_folders() {
        var run = Harness.Once(Counter, Harness.ProjectDirectory + "Ui/Widgets/Counter.vxml");

        Assert.Empty(run.Errors);
        Assert.Contains("namespace Game.Ui.Widgets;", run.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_at_the_project_root_gets_the_root_namespace_alone() {
        Assert.Contains("namespace Game;", Harness.Once(Counter).Source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Roslyn throws when two generated files share a hint name, and naming them after the
    ///     component collides between two folders. The path encoding is one-to-one instead: an
    ///     underscore in a name doubles before the separators fold into single ones, so these two
    ///     files cannot meet.
    /// </summary>
    [Fact]
    public void Two_files_whose_paths_differ_only_in_a_separator_do_not_collide() {
        var driver = Harness.Driver(
            new MarkupFile(Harness.ProjectDirectory + "Ui/Counter.vxml", "@component A\n<div />"),
            new MarkupFile(Harness.ProjectDirectory + "Ui_Counter.vxml", "@component B\n<div />")
        );

        var result = driver.RunGenerators(Harness.Compilation(), Token).GetRunResult();

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.GeneratedTrees.Length);
        Assert.Equal(2, result.Results[0].GeneratedSources.Select(source => source.HintName).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_file_that_is_not_vxml_is_ignored() {
        var driver = Harness.Driver(new MarkupFile(Harness.ProjectDirectory + "notes.txt", "@component A\n<div />"));

        Assert.Empty(driver.RunGenerators(Harness.Compilation(), Token).GetRunResult().GeneratedTrees);
    }

    // ------------------------------------------------------------ Diagnostics

    /// <summary>
    ///     The point of the generator being a compiler rather than a script: what the author wrote
    ///     wrong is reported against the file they wrote it in, at the characters they wrote.
    /// </summary>
    [Fact]
    public void A_syntax_error_is_reported_on_the_vxml_at_the_span_that_caused_it() {
        var run = Harness.Once("@component Counter\n<div>\n");
        var diagnostic = Assert.Single(run.Diagnostics);
        var span = diagnostic.Location.GetLineSpan();

        Assert.Equal("VXML1002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        // Line 1, character 1: the `div`, not the `<` and not the top of the file. The whole reason
        // a diagnostic carries a span is that it can be pointed at.
        Assert.Equal(Harness.ProjectDirectory + "Counter.vxml", span.Path);
        Assert.Equal(1, span.StartLinePosition.Line);
        Assert.Equal(1, span.StartLinePosition.Character);
    }

    /// <summary>
    ///     ⚠ A <c>VXML1xxx</c> means the tree came out of error recovery, so it is a guess — and C#
    ///     emitted from a guess may not parse, which buries the one diagnostic the author needs
    ///     under a page about generated code they cannot see.
    /// </summary>
    [Fact]
    public void A_syntax_error_stops_the_emit_rather_than_generating_from_a_guess() {
        var run = Harness.Once("@component Counter\n<div>\n");

        Assert.Empty(run.Generated);
        Assert.Equal("VXML1002", Assert.Single(run.Diagnostics).Id);
    }

    /// <summary>
    ///     ⚠ And a <c>VXML2xxx</c> does not, because the tree is right and only its meaning is
    ///     wrong. Withholding the class would replace one real error with one at every use site in
    ///     the project — all of them about a type that is missing for a reason none of them names.
    /// </summary>
    [Fact]
    public void A_binding_error_still_generates_the_class_so_the_type_keeps_existing() {
        var run = Harness.Once("@component Counter\n<div class=\"a\" class=\"b\" />");

        Assert.Equal("VXML2002", Assert.Single(run.Diagnostics).Id);
        Assert.Contains("partial class Counter", run.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_is_reported_as_a_warning_and_stops_nothing() {
        var run = Harness.Once("@component Counter\n@for (var i in xs) { <p>@i</p> }");
        var diagnostic = Assert.Single(run.Diagnostics);

        Assert.Equal("VXML2004", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Single(run.Generated);
    }

    /// <summary>
    ///     A VXML message quotes the author's own source, and VXML1005's quotes a brace. What this
    ///     asserts is that the brace reaches the reader.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It does not gate the choice that was made for it.</b> The generator passes the
    ///     message as an argument under a <c>{0}</c> template rather than as the template itself,
    ///     and swapping the two leaves this green: Roslyn catches the <see cref="FormatException" />
    ///     and falls back to the unformatted template, which here is already the finished message.
    ///     Recorded rather than papered over — the claim this test can make is about the output, and
    ///     the reason for the indirection is in <c>VxmlGenerator.Rebuild</c>, labelled as insurance.
    /// </remarks>
    [Fact]
    public void A_message_that_quotes_a_brace_survives_becoming_a_diagnostic() {
        var run = Harness.Once("@component Counter\n@code {\n<div />");
        var diagnostic = Assert.Single(run.Diagnostics, candidate => candidate.Id == "VXML1005");

        Assert.Contains("'{'", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("CS8785", string.Join(" ", run.Diagnostics.Select(d => d.Id)), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A folder is not an identifier.</b> <c>Ui/2d/Hud.vxml</c> would name the namespace
    ///     <c>Game.Ui.2d</c>, which is a syntax error in the generated file rather than a message
    ///     about the folder — and the author is looking at markup that is perfectly correct. Found
    ///     by a sabotage that deleted the guard and broke nothing: every fixture here happened to
    ///     use folders that were already identifiers.
    /// </summary>
    [Fact]
    public void A_folder_that_is_not_a_csharp_identifier_still_produces_a_namespace_that_is() {
        var run = Harness.Once(Counter, Harness.ProjectDirectory + "Ui/2d-hud/Counter.vxml");

        Assert.Empty(run.Errors);
        Assert.Contains("namespace Game.Ui._2d_hud;", run.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_component_directive_generates_nothing_and_says_why() {
        var run = Harness.Once("<div />");

        Assert.Empty(run.Generated);
        Assert.Equal("VXML2001", Assert.Single(run.Diagnostics).Id);
    }

    // ------------------------------------------------------------ Incrementality

    /// <summary>
    ///     ⚠ <b>The claim a generator is judged by.</b> A C# edit re-runs the pipeline's cheap head
    ///     and must not reach the parser: an incremental generator that re-parses every
    ///     <c>.vxml</c> on every keystroke is correct and useless, and nothing about its output
    ///     says so.
    /// </summary>
    [Fact]
    public void Editing_a_csharp_file_does_not_re_run_the_markup_compiler() {
        var driver = Harness.Driver(new MarkupFile(Harness.ProjectDirectory + "Counter.vxml", Counter));

        driver = driver.RunGenerators(Harness.Compilation(("a.cs", "class A;")), Token);
        driver = driver.RunGenerators(Harness.Compilation(("a.cs", "class A { int b; }")), Token);

        Assert.All(
            Reasons(driver, VxmlGenerator.CompileStep),
            reason => Assert.True(reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged)
        );
    }

    /// <summary>
    ///     And editing one file re-compiles that file. The mirror of the test above, and the reason
    ///     it is not vacuous: a pipeline that never re-runs anything would pass the first on its own.
    /// </summary>
    [Fact]
    public void Editing_one_vxml_re_runs_that_file_and_leaves_the_others_cached() {
        var edited = new MarkupFile(Harness.ProjectDirectory + "Counter.vxml", Counter);
        var untouched = new MarkupFile(Harness.ProjectDirectory + "Other.vxml", "@component Other\n<div />");
        var compilation = Harness.Compilation();

        var driver = Harness.Driver(edited, untouched).RunGenerators(compilation, Token);

        driver = driver
            .ReplaceAdditionalText(
                edited,
                new MarkupFile(edited.Path, Counter.Replace("root", "changed", StringComparison.Ordinal))
            )
            .RunGenerators(compilation, Token);

        var reasons = Reasons(driver, VxmlGenerator.CompileStep).ToList();

        Assert.Contains(IncrementalStepRunReason.Modified, reasons);
        Assert.Contains(IncrementalStepRunReason.Cached, reasons);
    }

    /// <summary>
    ///     ⚠ <b>An edit that changes nothing must produce nothing.</b> The compile step re-runs
    ///     whenever the file's text differs, and Roslyn only downgrades that to
    ///     <see cref="IncrementalStepRunReason.Unchanged" /> if the result compares equal to the
    ///     one it cached — which for a file with diagnostics means comparing the diagnostic array
    ///     by its contents. <see cref="ImmutableArray{T}" /> compares by reference and would say
    ///     "different" every time, re-adding the source and re-reporting the diagnostic on every
    ///     keystroke, silently and correctly.
    /// </summary>
    /// <remarks>
    ///     The file is edited <i>after</i> the error, so the text differs and the diagnostic does
    ///     not. Found by a sabotage that broke the array's equality and failed to fail: nothing
    ///     else here re-runs the step and then agrees with itself.
    /// </remarks>
    [Fact]
    public void An_edit_that_changes_neither_the_code_nor_the_diagnostics_is_seen_as_unchanged() {
        var original = new MarkupFile(Harness.ProjectDirectory + "Counter.vxml", "@component Counter\n<div>\n");
        var compilation = Harness.Compilation();

        var driver = Harness.Driver(original).RunGenerators(compilation, Token);

        driver = driver
            .ReplaceAdditionalText(original, new MarkupFile(original.Path, "@component Counter\n<div>\n\n\n"))
            .RunGenerators(compilation, Token);

        Assert.Contains(IncrementalStepRunReason.Unchanged, Reasons(driver, VxmlGenerator.CompileStep));
    }

    /// <summary>
    ///     And the other direction: two files that emit nothing at all still differ if they are
    ///     wrong in different ways, so fixing one mistake and making another has to change what is
    ///     on screen.
    /// </summary>
    /// <remarks>
    ///     ⚠ The pair matters because each half alone is satisfied by a mistake. An equality that
    ///     says "always different" passes this and re-runs everything; one that says "always the
    ///     same" passes its neighbour and leaves the previous error reported over corrected source.
    /// </remarks>
    [Fact]
    public void An_error_that_becomes_a_different_error_is_reported_as_the_new_one() {
        var original = new MarkupFile(Harness.ProjectDirectory + "Counter.vxml", "@component Counter\n<div>\n");
        var compilation = Harness.Compilation();

        var driver = Harness.Driver(original).RunGenerators(compilation, Token);

        driver = driver
            .ReplaceAdditionalText(original, new MarkupFile(original.Path, "@component Counter\n<span>\n"))
            .RunGenerators(compilation, Token);

        var diagnostic = Assert.Single(driver.GetRunResult().Diagnostics);
        Assert.Contains("<span>", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static IEnumerable<IncrementalStepRunReason> Reasons(GeneratorDriver driver, string step) =>
        driver.GetRunResult().Results[0].TrackedSteps.TryGetValue(step, out var steps)
            ? steps.SelectMany(one => one.Outputs).Select(output => output.Reason)
            : [];

    // ------------------------------------------------------------ End to end

    /// <summary>
    ///     The whole chain, driven the way a build drives it: a file in a project becomes a class in
    ///     an assembly that builds elements and follows a signal. Everything above proves a stage.
    /// </summary>
    [Fact]
    public void A_generated_component_builds_a_tree_and_follows_its_signals() {
        var run = Harness.Once(Counter);
        Assert.Empty(run.Errors);

        using var image = new MemoryStream();
        Assert.True(run.Compilation.Emit(image, cancellationToken: Token).Success);

        var type = Assembly.Load(image.ToArray()).GetType("Game.Counter")!;
        using var document = new UiDocument(400f, 400f);

        var instance = typeof(BuildContext)
            .GetMethod(nameof(BuildContext.Build))!
            .MakeGenericMethod(type)
            .Invoke(null, [document, document.Root])!;

        var root = ((Component)instance).Root.Children.Single();
        var span = root.Children[0];

        EffectScheduler.Default.Flush();
        Assert.Equal("div", root.Tag);
        Assert.Equal(["Count: ", "0"], span.Children.Select(child => child.Text));

        ((Signal<int>)type.GetProperty("Count")!.GetValue(instance)!).Value = 7;
        EffectScheduler.Default.Flush();
        Assert.Equal(["Count: ", "7"], span.Children.Select(child => child.Text));
    }
}
