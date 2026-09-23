// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Whether anything outside a test project can reach <see cref="ScrollView" />'s touch gestures.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Issue #767's last open row, held by a measurement instead of by a sentence in a
///         comment thread.</b> Four passes over that issue recorded, in prose, that
///         <see cref="ScrollView.PulledToRefresh" /> has no production subscriber and that this is
///         correct rather than a defect — the gesture is a thumb's, and this repository ships a
///         desktop editor and desktop samples. The issue is kept open so the wiring is not lost the
///         day a touch shell exists, and a record that lives only in an issue comment is a record
///         nobody re-reads. Red here means exactly one thing: the day arrived.
///     </para>
///     <para>
///         ⚠ <b>Two zeros and not one, which is sharper than the issue states it.</b> The gesture
///         is reachable from a finger or a pen, <i>or</i> from a mouse on a view that set
///         <see cref="ScrollView.DragToScroll" /> — and nothing outside a test project sets that
///         either. So on this tree the pull is unreachable from both directions at once, and wiring
///         a handler alone would not make it fire on any machine anybody here develops on.
///     </para>
///     <para>
///         ⚠ <b>An "is still zero" theory is the only kind that passes when its own instrument
///         breaks</b>, which is why the sweep is asked for a needle it must find first. A needle
///         spelt wrong, a prune that swallowed the tree and a walk that threw all read as "still
///         zero" otherwise. <see cref="ScrollView.ScrollIntoView" /> is the control that proves the
///         walk: same type, same assembly, and production callers throughout the editor.
///     </para>
/// </remarks>
public class ScrollViewReachTests {
    /// <summary>
    ///     ⚠ <b>The pull is opt-in by subscription, and nothing opts in.</b>
    ///     <see cref="ScrollView.PulledToRefresh" /> says on itself that the gesture does not exist
    ///     until something subscribes, so zero subscribers is not a dormant handler — it is the
    ///     feature being off everywhere in the repository.
    /// </summary>
    [Fact]
    public void The_pull_to_refresh_gesture_is_still_unsubscribed_everywhere() {
        Assert.NotEmpty(ProductionCallers("ScrollIntoView("));

        var subscribers = ProductionCallers("PulledToRefresh +=");

        Assert.True(
            subscribers.Count == 0,
            $"""
             {subscribers.Count} production file(s) now subscribe to `ScrollView.PulledToRefresh`:

               {string.Join("\n  ", subscribers)}

             That is what issue #767's last open row is waiting for. Read what the new subscriber
             refreshes, say so in `docs/guide/ui/async-loading.md` beside the recipe that has had no
             caller since it was written, and make this theory the `Assert.NotEmpty` it should then
             be. Check `The_only_mouse_route_to_a_pull_is_still_unused` in the same breath: a
             subscriber on a view no finger can reach still never fires.
             """
        );
    }

    /// <summary>
    ///     ⚠ <b>The other half of the same zero, and the one the issue's comments never counted.</b>
    ///     <see cref="ScrollView.DragToScroll" /> is what a kiosk sets so that a <i>mouse</i> drags
    ///     the content, and it is the only way the pull can happen on a machine with no touchscreen.
    ///     Nothing outside a test project sets it, so every pull-to-refresh handler written today
    ///     would be dead code on every developer machine in this project.
    /// </summary>
    [Fact]
    public void The_only_mouse_route_to_a_pull_is_still_unused() {
        Assert.NotEmpty(ProductionCallers("ScrollIntoView("));

        var callers = ProductionCallers("DragToScroll = ");

        Assert.True(
            callers.Count == 0,
            $"""
             {callers.Count} production file(s) now set `ScrollView.DragToScroll`:

               {string.Join("\n  ", callers)}

             A mouse can now drag a view's content somewhere in this tree, which is the one route by
             which `PulledToRefresh` fires without a touchscreen. Say which view and why on the
             property, and turn this into the `Assert.NotEmpty` it should then be.
             """
        );
    }

    /// <summary>
    ///     The instrument, checked before the thing it measures: the sweep must reach both source
    ///     kinds and must be able to tell a production file from a test one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The markup half is the one that can silently find nothing.</b> A <c>.vxml</c>
    ///     <c>@code</c> block is production C# and compiles under <c>obj/</c>, which this walk
    ///     prunes — so a <c>*.cs</c>-only sweep would report zero subscribers for every interface in
    ///     the repository and look exactly like a sweep that read them and found none. Two issues
    ///     were filed and closed invalid that way.
    /// </remarks>
    [Fact]
    public void The_sweep_reads_markup_as_well_as_code_and_excludes_test_projects() {
        var code = SourceFiles("*.cs");
        var markup = SourceFiles("*.vxml");

        Assert.True(code.Count > 1000, $"the sweep found only {code.Count} C# files");
        Assert.True(markup.Count > 10, $"the sweep found only {markup.Count} .vxml files");

        Assert.Contains(code, path => path.Contains(".Tests", StringComparison.Ordinal));
        Assert.DoesNotContain(ProductionCallers("PulledToRefresh +="), IsTest);

        // The suites that do subscribe are real, so the exclusion is doing work rather than being
        // satisfied by an empty set on both sides.
        Assert.Contains(code, path => path.EndsWith("ScrollRubberBandTests.cs", StringComparison.Ordinal));
    }

    /// <summary>The production files with at least one live occurrence of something.</summary>
    /// <remarks>
    ///     ⚠ <b>Line by line and past the comments.</b> A whole-file <c>Contains</c> counts the
    ///     prose explaining why nothing calls an API as a caller, which is the instrument failure
    ///     <c>ResponderReachTests</c> records having made — and this file is mostly prose about an
    ///     API nothing calls, so it would count itself.
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

                // A declaration is not a use: `ScrollView.cs` owns both of these members and cannot
                // stop naming them, so counting it would make either theory unfalsifiable.
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
    ///     code that ships and a fixture in <c>Vixen.Ui.Controls.Tests</c> is not.
    /// </remarks>
    static bool IsTest(string path) =>
        path.Contains(".Tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Directories a source sweep must not descend into, matched by name at any depth.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a full checkout of this repository per agent, so a walk
    ///     that does not prune it answers a question about somebody else's tree — and would find a
    ///     subscriber another agent had written and not yet merged.
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
