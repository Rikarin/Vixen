// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Whether anything outside a test project has ever registered a responder.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every other test in this assembly registers its own handler first.</b> That makes
///         <c>CommandRoute</c>'s defining rule — the nearest responder that answers wins, all the way
///         out — a claim only its own tests could make: eight hundred lines of careful
///         <c>NSResponder</c>-shaped routing whose element leg was, in production, a parent loop that
///         always found nothing followed by one dictionary lookup. No test in the repository could
///         fail because of that, which is exactly the shape of this codebase's commonest defect and
///         the reason issue #642 was filed.
///     </para>
///     <para>
///         ⚠ <b>So the assertion is about callers and not about types.</b> A test that constructed a
///         handler and resolved it would be green on the day every production registration was
///         deleted. What cannot be faked is a count of the files outside <c>*.Tests</c> that call
///         <see cref="UiElement.AddCommandHandler" />.
///     </para>
///     <para>
///         ⚠ <b>The mirror half of #642's gate is here now, and it is not the editor that closed
///         it.</b> <see cref="UiElement.CommandScope" /> had <b>zero</b> production callers for the
///         whole life of the API, and the change the issue expected to close that — replacing the
///         editor's <c>EditorShell.Context</c>, a mutable string pushed from ten pointer handlers —
///         is still not made, because the editor's contexts are a mix of panel ids and mode names
///         and a panel's scope would silently outrank the mode the user is in. What a scope needed
///         was one honest user, and <c>Samples/02-HelloUi</c> is one: two panels declare a scope on
///         their own roots and the shell reads it back with
///         <see cref="CommandRoute.ScopeOf" /> from wherever the focus happens to be.
///     </para>
///     <para>
///         ⚠ <b>So the sweep reads <c>.vxml</c> as well as <c>.cs</c>, and it must.</b> A Vixen
///         interface is markup with a <c>@code</c> block in it, and the C# that block becomes lives
///         under <c>obj/</c> — which this walk prunes, for the reason it prunes
///         <c>.claude/worktrees/</c>. A <c>*.cs</c>-only sweep would have reported zero production
///         callers on the day two samples had them, which is the same instrument failure in the
///         other direction from the one the helper below records.
///     </para>
/// </remarks>
public class ResponderReachTests {
    /// <summary>
    ///     ⚠ <b>Two, and it was zero for the whole life of the API.</b>
    ///     <c>Vixen.Ui.Controls/TextField.cs</c> and
    ///     <c>Vixen.Ui.Controls.Advanced/CodeEditor.cs</c> register the editing verbs, which is what
    ///     makes a menu item mean "this field's text" while the caret is in it. The floor is one
    ///     rather than two so that merging the two controls, or moving the registration to a shared
    ///     base, is not a failure — what must not happen is the count going back to nothing.
    /// </summary>
    [Fact]
    public void Something_outside_a_test_project_registers_a_command_handler() {
        var callers = ProductionCallers("AddCommandHandler(");

        Assert.NotEmpty(callers);

        // Named rather than only counted: a failure that says which files used to answer is the
        // difference between "somebody deleted the responders" and "somebody renamed the method".
        Assert.Contains(callers, path => path.EndsWith("TextField.cs", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ <b>Two, and it was zero for the whole life of the API.</b>
    ///     <c>Samples/02-HelloUi</c>'s Hierarchy and Inspector panels each declare a scope on their
    ///     own root, which is what makes "which panel am I in" a thing derived from the focus
    ///     rather than a string somebody remembered to push.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A scope with no reader would be half a claim</b>, so this asserts the other end as
    ///     well: something outside a test project asks <see cref="CommandRoute.ScopeOf" /> what the
    ///     answer is. Without that, every scope in the repository could be write-only and this file
    ///     would still be green.
    /// </remarks>
    [Fact]
    public void Something_outside_a_test_project_declares_and_reads_a_command_scope() {
        var declared = ProductionCallers("CommandScope = ");

        Assert.NotEmpty(declared);
        Assert.Contains(declared, path => path.EndsWith("Hierarchy.vxml", StringComparison.Ordinal));

        // ⚠ Qualified, because `ScopeOf` is three different methods in this repository —
        // `ScopedStyles.ScopeOf`, `UtilityFamilies.ScopeOf` and this one — and a bare `ScopeOf(`
        // would have been satisfied by a stylesheet scope with nothing command-shaped anywhere.
        Assert.NotEmpty(ProductionCallers("CommandRoute.ScopeOf("));
    }

    /// <summary>
    ///     ⚠ <b>One, and it was zero for the whole life of the API.</b> The chain has two slots past
    ///     the last element and only the second of them — the application's, which the editor's shell
    ///     fills — had ever been written to. The first,
    ///     <see cref="UiDocument.CommandResponder" />, is what an object that owns <i>what a window
    ///     is showing</i> installs, and <c>Samples/02-HelloUi</c>'s shell is the first thing in the
    ///     repository to be one: its Copy fallback moved off <c>Root</c>, where it had to hold a
    ///     piece of the element tree in order to answer a verb.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The leading dot is load-bearing.</b> The bare name is satisfied by the editor shell's
    ///     assignment to the <i>other</i> slot — the one that never lacked a caller — so a sweep
    ///     without it would have been green on the day this slot had none.
    /// </remarks>
    [Fact]
    public void Something_outside_a_test_project_installs_a_document_responder() {
        var callers = ProductionCallers(".CommandResponder = ");

        Assert.NotEmpty(callers);
        Assert.DoesNotContain(callers, path => path.EndsWith("EditorShell.cs", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The instrument, checked before the thing it measures: the sweep must be able to tell a
    ///     production file from a test one, or the theory above is green on the test projects alone.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that would have caught the sweep being wrong.</b>
    ///     <c>AddCommandHandler</c> has dozens of callers inside <c>*.Tests</c> assemblies and had
    ///     none outside them; a filter that let one test file through would have made the theory
    ///     above pass on the day it was written against the tree it was written to fail on.
    /// </remarks>
    [Fact]
    public void The_sweep_excludes_test_projects_and_still_finds_the_repository() {
        var everywhere = SourceFiles("*.cs");

        Assert.True(everywhere.Count > 1000, $"the sweep found only {everywhere.Count} C# files");
        Assert.Contains(everywhere, path => path.Contains(".Tests", StringComparison.Ordinal));
        Assert.DoesNotContain(ProductionCallers("AddCommandHandler("), path => IsTest(path));

        // ⚠ The markup half, asserted separately because it is the half that can silently find
        // nothing: a `.vxml` `@code` block compiles to C# under `obj/`, which this walk prunes, so a
        // sweep that read only `*.cs` would report zero callers for every interface in the
        // repository and look exactly like a sweep that read them and found none.
        var markup = SourceFiles("*.vxml");

        Assert.True(markup.Count > 10, $"the sweep found only {markup.Count} .vxml files");
    }

    /// <summary>The production files with at least one live call to something.</summary>
    /// <remarks>
    ///     ⚠ <b>Line by line and past the comments, and the first version of this was not — it read
    ///     whole files, and the sabotage came back green.</b> Commenting both controls' registrations
    ///     out left the text <c>AddCommandHandler(</c> sitting in the file, so a
    ///     <c>File.ReadAllText().Contains()</c> sweep still counted two callers where there were now
    ///     none. A gate that cannot tell a call from a call somebody disabled is a gate that reports
    ///     success on the day it should not: exactly the instrument failure this repository keeps
    ///     rediscovering, in the test written to catch a different one.
    /// </remarks>
    static List<string> ProductionCallers(string call) {
        List<string> found = [];

        foreach (var path in SourceFiles("*.cs").Concat(SourceFiles("*.vxml"))) {
            if (IsTest(path)) {
                continue;
            }

            foreach (var line in File.ReadLines(path)) {
                var code = line.TrimStart();

                if (code.StartsWith("//", StringComparison.Ordinal)
                    || code.StartsWith('*')
                    || code.StartsWith("/*", StringComparison.Ordinal)
                    || !code.Contains(call, StringComparison.Ordinal)) {
                    continue;
                }

                // ⚠ A definition is not a caller. Counting the file that owns the API would make the
                // theory unfalsifiable, because the API cannot be deleted without deleting its own
                // declaration — and `Commands.cs` would then satisfy, by itself, a gate that exists
                // to say something else uses it.
                if (code.StartsWith("public ", StringComparison.Ordinal)) {
                    continue;
                }

                found.Add(path);
                break;
            }
        }

        return found;
    }

    /// <summary>Whether a path belongs to a test assembly.</summary>
    /// <remarks>
    ///     By directory rather than by file name: a helper in <c>Vixen.Ui.Testing</c> is production
    ///     code that ships, and a fixture in <c>Vixen.Ui.Tests</c> is not, and only the directory
    ///     tells them apart.
    /// </remarks>
    static bool IsTest(string path) =>
        path.Contains(".Tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Directories a source sweep must not descend into, matched by name at any depth.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a full checkout of this repository per agent — dozens of
    ///     them — so a walk that does not prune it is both minutes slower and answering a question
    ///     about somebody else's tree. A sweep in another suite failed a gate that way, by finding in
    ///     a neighbouring worktree the very defect it exists to prevent.
    /// </remarks>
    static readonly string[] Unwalked = [".git", ".claude", "bin", "obj", "artifacts", "node_modules"];

    static List<string> SourceFiles(string pattern) {
        List<string> found = [];
        Walk(RepositoryRoot(), pattern, found);
        found.Sort(StringComparer.Ordinal);

        return found;
    }

    static void Walk(string directory, string pattern, List<string> into) {
        into.AddRange(Directory.EnumerateFiles(directory, pattern));

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (!Unwalked.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                Walk(child, pattern, into);
            }
        }
    }

    static string RepositoryRoot() {
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
