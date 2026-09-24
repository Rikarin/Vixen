// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Testing;
using Vixen.Ui.Markup.Testing;
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

        // ⚠ The exclusion is asserted against the UNFILTERED sweep, because the obvious spelling of
        // it cannot fail: `DoesNotContain(ProductionCallers(…), IsTest)` examines a list `IsTest`
        // has already emptied of test paths, so it is green against any filter at all — including
        // one that dropped the repository. What has weight is that the same needle finds test
        // subscribers when the filter is off: `ScrollRubberBandTests` is the one that subscribes,
        // so the exclusion is removing something that is really there.
        Assert.Contains(Callers("PulledToRefresh +=", productionOnly: false), IsTest);
        Assert.DoesNotContain(Callers("PulledToRefresh +=", productionOnly: true), IsTest);

        Assert.Contains(code, path => path.EndsWith("ScrollRubberBandTests.cs", StringComparison.Ordinal));
    }

    /// <summary>A <c>.vxml</c> header that demonstrates a subscription is not a subscriber.</summary>
    /// <remarks>
    ///     ⚠ <b>Synthetic, because no committed <c>.vxml</c> writes this line today</b> — measured
    ///     across all 96 — so a test reading the repository would be green against a sweep that read
    ///     every comment as code. The control is the same statement in a <c>@code</c> body, beside a
    ///     comment on the markup line above it, which must still be found: a sweep that refused every
    ///     <c>.vxml</c> would pass the first half alone. <c>Rikarin/Vixen#1341</c>.
    /// </remarks>
    [Fact]
    public void A_markup_comment_is_not_a_subscriber() {
        string[] prose = [
            "<!--",
            "    A panel that wants the gesture writes it the obvious way:",
            "        view.PulledToRefresh += Reload;",
            "-->",
            "<scroll-view />"
        ];

        Assert.False(Occurs("Demo.vxml", prose, "PulledToRefresh +="));

        string[] code = [
            "<scroll-view /> <!-- the list -->",
            "@code {",
            "    void Wire(ScrollView view) => view.PulledToRefresh += Reload;",
            "}"
        ];

        Assert.True(Occurs("Demo.vxml", code, "PulledToRefresh +="));
    }

    /// <summary>The production files with at least one live occurrence of something.</summary>
    /// <remarks>
    ///     ⚠ <b>Line by line and past the comments.</b> A whole-file <c>Contains</c> counts the
    ///     prose explaining why nothing calls an API as a caller, which is the instrument failure
    ///     <c>ResponderReachTests</c> records having made — and this file is mostly prose about an
    ///     API nothing calls, so it would count itself.
    ///     <para>
    ///         ⚠ <b>And past the whitespace, which matters here more than in a theory that asserts a
    ///         floor.</b> Both needles below carry spaces — <c>PulledToRefresh +=</c>,
    ///         <c>DragToScroll = </c> — and an exact match answers "still zero" for
    ///         <c>DragToScroll=true</c>, which is a subscriber this file would then have missed
    ///         rather than found. <c>CheckFormat</c> makes the spaced form overwhelmingly likely, so
    ///         this is belt and braces on the one kind of theory that passes when its instrument
    ///         misses.
    ///     </para>
    /// </remarks>
    static List<string> ProductionCallers(string call) => Callers(call, productionOnly: true);

    /// <summary>The files with at least one live occurrence of something, test projects or not.</summary>
    /// <param name="call">The needle, compared with the spaces removed from both sides.</param>
    /// <param name="productionOnly">Whether to drop the test assemblies, which is what both zeros ask.</param>
    static List<string> Callers(string call, bool productionOnly) {
        List<string> found = [];

        foreach (var path in SourceFiles("*.cs").Concat(SourceFiles("*.vxml"))) {
            if (productionOnly && IsTest(path)) {
                continue;
            }

            if (Occurs(path, File.ReadAllLines(path), call)) {
                found.Add(path);
            }
        }

        return found;
    }

    /// <summary>Whether one file has a live occurrence of the needle.</summary>
    /// <param name="path">The file's path, which decides whether it is markup.</param>
    /// <param name="lines">Its lines — handed in so a test can give it lines no committed file holds.</param>
    /// <param name="call">The needle, compared with the spaces removed from both sides.</param>
    /// <remarks>
    ///     ⚠ <b>A <c>.vxml</c> goes through <see cref="VxmlLines" /> first, because the three
    ///     line-start tests below are the C# comment forms and a <c>.vxml</c>'s prose is
    ///     <c>&lt;!-- … --&gt;</c>.</b> The shared parts' header comments demonstrate their own
    ///     element as indented markup, so a header saying how a panel would subscribe
    ///     <i>is</i> a subscriber to a line-start test — and on the day this theory is about, it would
    ///     go on saying "still zero" about the prose rather than "now one" about the code, or the other
    ///     way about. <c>Rikarin/Vixen#1341</c>.
    /// </remarks>
    static bool Occurs(string path, IReadOnlyList<string> lines, string call) {
        var needle = Squeezed(call);

        foreach (var line in VxmlLines.Source(path, lines)) {
            var code = line.TrimStart();

            if (code.StartsWith("//", StringComparison.Ordinal)
                || code.StartsWith('*')
                || code.StartsWith("/*", StringComparison.Ordinal)
                || !Squeezed(code).Contains(needle, StringComparison.Ordinal)) {
                continue;
            }

            // A declaration is not a use: `ScrollView.cs` owns both of these members and cannot
            // stop naming them, so counting it would make either theory unfalsifiable.
            if (code.StartsWith("public ", StringComparison.Ordinal)) {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>A line with its whitespace removed, so a needle's spaces are not part of the question.</summary>
    static string Squeezed(string line) =>
        string.Concat(line.Where(character => !char.IsWhiteSpace(character)));

    /// <summary>Whether a path belongs to a test assembly.</summary>
    /// <remarks>
    ///     By directory rather than by file name: a helper in <c>Vixen.Ui.Testing</c> is production
    ///     code that ships and a fixture in <c>Vixen.Ui.Controls.Tests</c> is not.
    /// </remarks>
    static bool IsTest(string path) =>
        path.Contains(".Tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The repository's files matching a pattern, as git defines the repository.</summary>
    /// <remarks>
    ///     ⚠ Not a directory walk (#1424): <c>.claude/worktrees/</c> holds a full checkout per agent,
    ///     and would find a subscriber another agent had written and not yet merged, and the ignored
    ///     <c>references/</c> holds cloned C# engines no census here is about.
    /// </remarks>
    static List<string> SourceFiles(string pattern) {
        var found = RepositoryFiles.Files(RepositoryRoot(), pattern);
        found.Sort(StringComparer.Ordinal);

        return found;
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
