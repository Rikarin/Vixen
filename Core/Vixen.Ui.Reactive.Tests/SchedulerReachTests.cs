// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Reactive.Tests;

/// <summary>Every production effect names the scheduler it belongs to, rather than taking the thread's.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1129">#1129</a>, and it is an
///         instrument question before it is a rule.</b> <c>UiDiagnostics.BrokenBindings</c> counts
///         the effects <em>a document's</em> scheduler suspends, which is the only reading under
///         which a nought means something. An <see cref="Effect" /> constructed with no scheduler
///         queues on <see cref="EffectScheduler.Default" /> instead — <c>[ThreadStatic]</c>, owned by
///         no document, with a <c>NullLogger</c> and no <c>Suspended</c> sink — so a suspension there
///         is silent in every direction at once: no count, no log, and an interface that keeps the
///         frame it had.
///     </para>
///     <para>
///         ⚠ <b>The three doors are one door.</b> <c>Effect(action, scheduler = null)</c> is where
///         the fallback lives; <c>AsyncComputed(request, load, scheduler = null)</c> and
///         <c>UiWindowTitle.Bind(window, document, scheduler = null)</c> funnel through it. The issue
///         proposed making <c>Bind</c>'s parameter required, which would have closed one of the three
///         and read as though the class were shut.
///     </para>
///     <para>
///         ⚠ <b>Measured rather than assumed, and it refutes the size the fix was thought to
///         be.</b> "~58 construction sites" counts the test assemblies, where a thread default is
///         exactly right — a test host is one thread with one graph. In <em>production</em> source,
///         across <c>.cs</c> and <c>.vxml</c> alike, every one of these calls already names a
///         scheduler. So the exposure this issue is about is nought today and the whole of what is
///         owed is keeping it there, which a census can do without the public-API break that removing
///         the defaults would be.
///     </para>
///     <para>
///         ⚠ <b>A census and not a compiler.</b> The argument count comes from a paren walk over the
///         text, so a shape it cannot parse fails loudly with a file and a line rather than passing
///         quietly — which is the direction a gate is allowed to be wrong in. What it must never do
///         is find nothing and call that success, so each door asserts it located calls at all before
///         anything is concluded from their being well-formed.
///     </para>
/// </remarks>
public class SchedulerReachTests {
    /// <summary>Directories a source sweep must not descend into, matched by name at any depth.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a whole checkout of this repository per parallel agent,
    ///     so a walk that descends into it compares old copies of these same files with each other
    ///     and reports a defect somebody else has already fixed — or misses this tree's entirely.
    ///     Pruned during the walk rather than filtered after it, because the filter still visits
    ///     every file in every copy.
    /// </remarks>
    static readonly string[] Unwalked = [".git", ".claude", "bin", "obj", "artifacts", "node_modules"];

    /// <summary>The calls whose scheduler is optional, and how many arguments naming one takes.</summary>
    static readonly (string Token, int Least, string What)[] Doors = [
        ("new Effect", 2, "Effect(action, scheduler)"),
        ("new AsyncComputed", 3, "AsyncComputed(request, load, scheduler)"),
        ("UiWindowTitle.Bind", 3, "UiWindowTitle.Bind(window, document, scheduler)")
    ];

    /// <summary>No production effect is queued on the thread's scheduler.</summary>
    /// <remarks>
    ///     ⚠ <b>What a failure here looks like at runtime is nothing at all.</b> The effect keeps its
    ///     dependencies and its place in the graph, the panel keeps the frame it last drew, the
    ///     document's broken-binding count stays at nought because the suspension happened on another
    ///     scheduler, and the log line goes to the <c>NullLogger</c> the thread default was built
    ///     with. That is #1109's freeze with the one instrument that could have named it looking the
    ///     other way.
    /// </remarks>
    [Fact]
    public void NoProductionEffectTakesTheThreadsScheduler() {
        List<string> unscheduled = [];
        Dictionary<string, int> seen = Doors.ToDictionary(door => door.Token, _ => 0, StringComparer.Ordinal);

        foreach (var file in Production()) {
            var text = File.ReadAllText(file);

            foreach (var (token, least, what) in Doors) {
                for (var at = text.IndexOf(token, StringComparison.Ordinal);
                     at >= 0;
                     at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal)) {
                    // `new EffectScheduler` and `new AsyncComputedFoo` start with the same letters,
                    // and a `<see cref="…" />` naming one of these is prose rather than a call.
                    if (Opening(text, at + token.Length) is not { } open) {
                        continue;
                    }

                    seen[token]++;

                    if (Arguments(text, open) < least) {
                        unscheduled.Add($"{Path.GetRelativePath(Root(), file)}:{Line(text, at)} — {what}");
                    }
                }
            }
        }

        // The instrument, before anything is concluded from the emptiness below: a walk that found no
        // calls — a pruned directory too many, a pattern that stopped matching — would satisfy the
        // rule by having measured nothing, which is this repository's commonest shape of false green.
        foreach (var (token, _, what) in Doors) {
            Assert.True(seen[token] > 0, $"the census found no production call to {what}, so it measured nothing.");
        }

        Assert.True(
            unscheduled.Count == 0,
            "these production calls take EffectScheduler.Default, which is [ThreadStatic] and belongs to no "
            + "document — so a suspension there is counted by no UiDiagnostics.BrokenBindings and logged by no "
            + "logger, and the interface simply keeps the frame it had (#1129): "
            + string.Join("; ", unscheduled)
        );
    }

    /// <summary>Every source file in the working tree that is not a test.</summary>
    /// <remarks>
    ///     ⚠ <b><c>.vxml</c> as well as <c>.cs</c>, because a view's <c>code</c> block is production
    ///     C#</b> — and both production <c>UiWindowTitle.Bind</c> calls in this repository are in one.
    ///     A sweep reading only <c>*.cs</c> would have reported this door as having no callers, which
    ///     is a clean grep that means nothing.
    ///     <para>
    ///         A test assembly is excluded rather than swept: a test host is one thread with one
    ///         graph, so the thread default is the right scheduler there and is what most of these
    ///         fixtures deliberately use.
    ///     </para>
    /// </remarks>
    static IEnumerable<string> Production() {
        List<string> found = [];

        Walk(Root(), found);
        found.Sort(StringComparer.Ordinal);

        return found.Where(file => !file.Contains(".Tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    static void Walk(string directory, List<string> into) {
        into.AddRange(Directory.EnumerateFiles(directory, "*.cs"));
        into.AddRange(Directory.EnumerateFiles(directory, "*.vxml"));

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (!Unwalked.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                Walk(child, into);
            }
        }
    }

    /// <summary>The working tree's root, found by a directory only it has.</summary>
    /// <remarks>
    ///     ⚠ Walked up from <see cref="AppContext.BaseDirectory" /> and never from a
    ///     <c>[CallerFilePath]</c>: CI sets <c>ContinuousIntegrationBuild</c>, which rewrites every
    ///     compiled path to <c>/_/…</c>, so a test anchored on its own source location fails on all
    ///     three runners at once and passes on every developer machine.
    /// </remarks>
    static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "Vixen.slnx"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"no Vixen.slnx above '{AppContext.BaseDirectory}', so no repository.");
    }

    /// <summary>The call's opening parenthesis, or null when this occurrence is not a call.</summary>
    /// <param name="text">The file.</param>
    /// <param name="after">Just past the matched name.</param>
    /// <returns>Where the argument list opens.</returns>
    /// <remarks>
    ///     Type arguments are stepped over so that <c>new AsyncComputed&lt;string, int&gt;(…)</c> is
    ///     the same call as <c>new AsyncComputed(…)</c>; a name followed by anything else — an
    ///     identifier character, as in <c>new EffectScheduler</c>, or a <c>"</c> closing a
    ///     <c>cref</c> — is not one.
    /// </remarks>
    static int? Opening(string text, int after) {
        var index = after;

        if (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) {
            return null;
        }

        while (index < text.Length && char.IsWhiteSpace(text[index])) {
            index++;
        }

        if (index < text.Length && text[index] == '<') {
            var depth = 0;

            for (; index < text.Length; index++) {
                if (text[index] == '<') {
                    depth++;
                } else if (text[index] == '>' && --depth == 0) {
                    index++;

                    break;
                }
            }

            while (index < text.Length && char.IsWhiteSpace(text[index])) {
                index++;
            }
        }

        return index < text.Length && text[index] == '(' ? index : null;
    }

    /// <summary>How many arguments the call opening at <paramref name="open" /> passes.</summary>
    /// <param name="text">The file.</param>
    /// <param name="open">The opening parenthesis.</param>
    /// <returns>The count, which is one more than the commas at the list's own depth.</returns>
    /// <remarks>
    ///     ⚠ A lambda body's commas are inside braces and a collection expression's are inside
    ///     brackets, so all three bracket kinds move the depth and only a comma at the list's own
    ///     level separates arguments. Strings are stepped over whole — a comma inside one is text.
    /// </remarks>
    static int Arguments(string text, int open) {
        var depth = 0;
        var arguments = 1;
        var anything = false;

        for (var index = open; index < text.Length; index++) {
            var character = text[index];

            if (character is '"' or '\'') {
                index = Literal(text, index);
                anything = true;

                continue;
            }

            if (character == '/' && index + 1 < text.Length && text[index + 1] is '/' or '*') {
                index = Comment(text, index);

                continue;
            }

            switch (character) {
                case '(' or '[' or '{':
                    depth++;

                    break;

                case ')' or ']' or '}':
                    if (--depth == 0) {
                        return anything ? arguments : 0;
                    }

                    break;

                case ',' when depth == 1:
                    arguments++;

                    break;

                default:
                    anything |= depth > 0 && !char.IsWhiteSpace(character);

                    break;
            }
        }

        throw new InvalidOperationException("an argument list that never closes, so this file did not compile.");
    }

    /// <summary>The last index of the string or character literal starting at <paramref name="at" />.</summary>
    /// <param name="text">The file.</param>
    /// <param name="at">The opening quote.</param>
    /// <returns>The closing quote's index.</returns>
    static int Literal(string text, int at) {
        var quote = text[at];

        // A raw string ends at its own fence and holds no escapes at all.
        if (quote == '"' && text.AsSpan(at).StartsWith("\"\"\"", StringComparison.Ordinal)) {
            var close = text.IndexOf("\"\"\"", at + 3, StringComparison.Ordinal);

            return close < 0 ? text.Length - 1 : close + 2;
        }

        var verbatim = at > 0 && text[at - 1] == '@';

        for (var index = at + 1; index < text.Length; index++) {
            if (!verbatim && text[index] == '\\') {
                index++;

                continue;
            }

            if (text[index] != quote) {
                continue;
            }

            // In a verbatim string a doubled quote is a quote rather than the end of it.
            if (verbatim && index + 1 < text.Length && text[index + 1] == quote) {
                index++;

                continue;
            }

            return index;
        }

        return text.Length - 1;
    }

    /// <summary>The last index of the comment starting at <paramref name="at" />.</summary>
    /// <param name="text">The file.</param>
    /// <param name="at">The slash.</param>
    /// <returns>Where the comment ends.</returns>
    static int Comment(string text, int at) {
        if (text[at + 1] == '/') {
            var end = text.IndexOf('\n', at);

            return end < 0 ? text.Length - 1 : end;
        }

        var close = text.IndexOf("*/", at + 2, StringComparison.Ordinal);

        return close < 0 ? text.Length - 1 : close + 1;
    }

    /// <summary>Which line an index is on, counted from one.</summary>
    /// <param name="text">The file.</param>
    /// <param name="at">The index.</param>
    /// <returns>The line number.</returns>
    static int Line(string text, int at) => text.AsSpan(0, at).Count('\n') + 1;
}
