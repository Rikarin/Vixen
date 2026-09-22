// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/89">#89</a>: the editor views still built in
///     C# rather than in markup are exactly the ones <c>docs/MarkupPending.txt</c> names, each with a
///     reason — so the ratio the issue says is owed is gated, and can only move one way.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two directions, and the second is what makes the list a ledger rather than a mute
///         button.</b> A view-shaped class with no <c>.vxml</c> beside it and no line in the ledger
///         is a new hand-built view that has not said why; a line whose file has grown a
///         <c>.vxml</c>, or has gone, is a port nobody recorded — delete the line in the same commit.
///         <c>NotRooted.txt</c> and <c>WhitespaceExempt.txt</c> are the same shape for the same
///         reason.
///     </para>
///     <para>
///         ⚠ <b>By declared class and not by file name</b>, which is where the issue's own audit went
///         wrong: it counted <c>InputDebugPanel.cs</c> and <c>AgentDebuggerPanel.cs</c> as unported,
///         and both are partials of <c>AssetEditorsModule</c> that <em>register</em> a view whose
///         <c>.vxml</c> exists. A name-shaped count reported two ports that were already done.
///     </para>
/// </remarks>
public class MarkupRatioTests {
    const string Ledger = "docs/MarkupPending.txt";

    /// <summary>A top-level class or record whose name ends the way a view's does.</summary>
    /// <remarks>
    ///     ⚠ Anchored at column zero, so a nested helper is not a view. <c>ImportPipeline</c>
    ///     declares a <c>SequentialView</c> and <c>EditorDiagnostics</c> a <c>ShownView</c>; both are
    ///     implementation details of the type around them, and a sweep that counted them would ask
    ///     for a <c>.vxml</c> beside a file that draws nothing.
    /// </remarks>
    static readonly Regex ViewShaped = new(
        @"^(?:public\s+|internal\s+)?(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(?:class|record)\s+(?<name>\w+(?:View|Panel|Inspector|Popup))\b",
        RegexOptions.Compiled | RegexOptions.Multiline
    );

    /// <summary>Directories a source sweep must not descend into, matched by name at any depth.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a full checkout per agent, so a walk that does not prune
    ///     it is minutes slower and answering a question about somebody else's tree.
    /// </remarks>
    static readonly string[] Unwalked = [".git", ".claude", "bin", "obj", "artifacts", "node_modules"];

    /// <summary>The hand-built views are the ledger, and the ledger is the hand-built views.</summary>
    [Fact]
    public void The_views_without_markup_are_exactly_the_ones_the_ledger_names() {
        var root = RepositoryRoot();
        var (withMarkup, without) = Measure(root);
        var ledger = ReadLedger(root);

        var unlisted = without.Except(ledger.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var stale = ledger.Keys.Except(without, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unlisted.Count == 0,
            "View-shaped classes with no .vxml beside them and no line in " + Ledger + ": "
            + string.Join(", ", unlisted)
            + ". Port it, or add a line saying why it stays C# (#89)."
        );

        Assert.True(
            stale.Count == 0,
            "Lines in " + Ledger + " naming a file that now has a .vxml beside it, or is gone: "
            + string.Join(", ", stale)
            + ". Delete the line — the list can only shrink."
        );

        // ⚠ The instrument. A sweep that found no markup views at all would call an empty ledger
        // correct, and the tree has dozens.
        Assert.True(withMarkup.Count > 30, $"the sweep found only {withMarkup.Count} views with a .vxml beside them");
    }

    /// <summary>Every line says why, and starts with one of the four words the header defines.</summary>
    [Fact]
    public void Every_ledger_line_gives_one_of_the_four_reasons() {
        var ledger = ReadLedger(RepositoryRoot());

        Assert.NotEmpty(ledger);

        foreach (var (path, reason) in ledger) {
            Assert.True(
                Regex.IsMatch(reason, @"^(canvas|machinery|not-a-view|pending)\b"),
                $"{Ledger}: '{path}' has the reason '{reason}', which does not start with canvas, machinery, not-a-view or pending"
            );
        }
    }

    /// <summary>The two module partials the issue's audit miscounted are not views, and their views are markup.</summary>
    [Fact]
    public void A_module_partial_that_registers_a_markup_view_is_not_a_hand_built_view() {
        var root = RepositoryRoot();
        var (withMarkup, without) = Measure(root);

        Assert.DoesNotContain("Editor/Vixen.Editor.AssetEditors/Input/InputDebugPanel.cs", without);
        Assert.DoesNotContain("Editor/Vixen.Editor.AssetEditors/Ai/AgentDebuggerPanel.cs", without);
        Assert.Contains("Editor/Vixen.Editor.AssetEditors/Input/InputDebugView.cs", withMarkup);
        Assert.Contains("Editor/Vixen.Editor.AssetEditors/Ai/AgentDebuggerView.cs", withMarkup);
    }

    static (HashSet<string> WithMarkup, HashSet<string> Without) Measure(string root) {
        HashSet<string> withMarkup = new(StringComparer.Ordinal);
        HashSet<string> without = new(StringComparer.Ordinal);

        foreach (var path in SourceFiles(Path.Combine(root, "Editor"), "*.cs")) {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');

            if (relative.Contains(".Tests/", StringComparison.Ordinal)
                || relative.Contains("Generator", StringComparison.Ordinal)) {
                continue;
            }

            if (!ViewShaped.IsMatch(File.ReadAllText(path))) {
                continue;
            }

            var markup = Path.ChangeExtension(path, ".vxml");

            (File.Exists(markup) ? withMarkup : without).Add(relative);
        }

        return (withMarkup, without);
    }

    static Dictionary<string, string> ReadLedger(string root) {
        Dictionary<string, string> entries = new(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(Path.Combine(root, Ledger))) {
            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);

            Assert.True(space > 0, $"{Ledger}: '{line}' has no reason after the path");

            entries[line[..space]] = line[(space + 1)..].Trim();
        }

        return entries;
    }

    static List<string> SourceFiles(string directory, string pattern) {
        List<string> found = [];
        Walk(directory, pattern, found);
        found.Sort(StringComparer.Ordinal);

        return found;
    }

    static void Walk(string directory, string pattern, List<string> into) {
        into.AddRange(Directory.EnumerateFiles(directory, pattern));

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (Array.IndexOf(Unwalked, Path.GetFileName(child)) < 0) {
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
