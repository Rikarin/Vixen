// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;
using Vixen.Ui.Styling.Utilities;

namespace Vixen.Editor.Ui;

/// <summary>The utility half of the editor's stylesheet: its tokens, and the utilities its markup uses.</summary>
/// <remarks>
///     <para>
///         <b>Two inputs and one output.</b> <c>Theming/vixen.ui.yaml</c> is the tokens and the
///         editor's <c>.vxml</c> is scanned for every class name that could be a utility. What comes
///         out is a sheet in <c>@layer utilities</c> whose only rules are the ones something in the
///         editor refers to. <see cref="EditorTheme.Install" /> loads it, immediately after the
///         hand-written sheet, so one call installs the whole stack.
///     </para>
///     <para>
///         ⚠ <b>The tokens are not a second palette, and the yaml goes to some trouble to stay that
///         way.</b> Every colour in it is a <c>var(--…)</c> reference to a custom property
///         <see cref="EditorTheme" /> already declares on the root — so <c>bg-surface</c> and a
///         hand-written <c>background: var(--surface)</c> are the same declaration, the light/dark
///         toggle moves both, and a user theme loaded through <see cref="ThemeService" /> moves both
///         again. Copying the hex across would have produced two palettes that agreed until the day
///         one of them was edited.
///     </para>
///     <para>
///         ⚠ <b>The scan happens at startup here and belongs in a build step.</b>
///         <c>Vixen.Ui.Styling.Utilities</c>' README lists build-step integration as waiting on the
///         asset pipeline; until then somebody has to do it, and doing it over embedded copies of the
///         same files with the same scanner is the honest stand-in rather than a different mechanism.
///         It costs a few milliseconds once, and <see cref="Utilities" /> caches the answer because
///         the editor opens more than one document.
///     </para>
///     <para>
///         ⚠ <b>The utility layer loses every argument it has with <see cref="EditorTheme" />, and
///         that is deliberate — but it is a sharper trade here than in a game.</b> Everything
///         generated lands in <c>@layer utilities</c> and the hand-written sheet is unlayered, so
///         <c>task-row { padding: 6px }</c> beats <c>p-3</c> without either of them saying
///         <c>!important</c>. The consequence is worth stating plainly: a utility only takes effect on
///         a property no rule in <see cref="EditorTheme" /> already sets for that element. New panels
///         get the whole vocabulary; retro-fitting one onto chrome the sheet already styles means
///         deleting the hand-written rule first.
///     </para>
/// </remarks>
public static class EditorStyles {
    /// <summary>Class names the scanner cannot see, because nothing writes them down whole.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Empty, and checked rather than assumed.</b> The scanner is deliberately
    ///         over-inclusive — it takes every run of characters that could be a class name and lets
    ///         the generator discard the rest — but it cannot see a name that is never written:
    ///         <c>$"level-{severity}"</c> is <c>level-</c> and a variable. The editor does build class
    ///         names at run time in four places (<c>ThemeService</c>'s <c>dark</c>,
    ///         <c>ConsoleView</c>'s and <c>MessageLogView</c>'s <c>level-*</c>, and whatever a plugin
    ///         puts in <c>EditorCommand.ClassName</c>), and not one of them names a <i>utility</i> —
    ///         they all name rules <see cref="EditorTheme" /> writes by hand. So there is nothing to
    ///         safelist today.
    ///     </para>
    ///     <para>
    ///         The rule for when that changes is Tailwind's own: <b>a utility class name assembled at
    ///         run time must be listed here, or written out in full in a switch the scanner can
    ///         read.</b> A name that is neither is a style that silently does nothing, and no compiler
    ///         and no binder can see one — <c>class</c> is a string, and every string parses.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<string> Safelist { get; } = [];

    /// <summary>The generated utility sheet, compiled once.</summary>
    /// <remarks>
    ///     Cached because the scan is a startup cost rather than a per-document one, and the inputs
    ///     are embedded resources that cannot change while the process runs.
    /// </remarks>
    public static string Utilities => Cached.Value;

    /// <summary>The design tokens, read from the editor's <c>vixen.ui.yaml</c>.</summary>
    /// <returns>Them.</returns>
    public static ThemeTokens Tokens() => ThemeTokens.Parse(Text("Theming.vixen.ui.yaml"));

    /// <summary>Every class name the editor's markup mentions, plus the ones it assembles.</summary>
    /// <returns>The candidates, in no particular order.</returns>
    public static IReadOnlySet<string> Candidates() {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in Markup()) {
            CandidateScanner.Scan(Text(name), found);
        }

        foreach (var safe in Safelist) {
            found.Add(safe);
        }

        return found;
    }

    /// <summary>The utility sheet, with the candidates the generator did not know.</summary>
    /// <param name="unrecognised">Every candidate the utility system did not know. Prose, mostly.</param>
    /// <returns>VCSS text, entirely inside <c>@layer utilities</c>.</returns>
    /// <remarks>
    ///     ⚠ <b><paramref name="unrecognised" /> is not a diagnostic to log and forget.</b> The
    ///     scanner is over-inclusive, so most of what lands in it is prose out of a comment — but a
    ///     <i>misspelt</i> utility lands in it too, and a misspelt utility is a style that silently
    ///     does nothing. <c>StylesheetTests</c> filters the prose by asking the narrower question the
    ///     MMO sample's suite asks: every name actually written in a <c>class</c> attribute is either
    ///     a utility or a rule <see cref="EditorTheme" /> wrote, and anything else is a typo.
    /// </remarks>
    public static string Compile(out ImmutableArray<string> unrecognised) {
        var generator = new UtilityGenerator(Tokens());
        var utilities = generator.Generate(Candidates());

        unrecognised = [.. generator.Unrecognised];

        return utilities;
    }

    /// <summary>The utility sheet, for a caller that does not want the diagnostics.</summary>
    /// <returns>The CSS.</returns>
    public static string Compile() => Compile(out _);

    /// <summary>The embedded markup files, which are what a build step would glob.</summary>
    /// <returns>Their resource names.</returns>
    /// <remarks>
    ///     ⚠ <b>Only <c>.vxml</c>, which is the honest limit of this stand-in and is worth knowing
    ///     about.</b> Most of the editor's chrome is still built in C# with <c>AddClass("…")</c>, and
    ///     those literals are invisible here — a build step scanning the source tree would see them,
    ///     and until there is one, a panel that wants utilities should be written in markup. The
    ///     assembly has one <c>.vxml</c> today and is acquiring more.
    /// </remarks>
    internal static IEnumerable<string> Markup() =>
        typeof(EditorStyles).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".vxml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    const string Prefix = "Vixen.Editor.Ui.";

    static readonly Lazy<string> Cached = new(Compile, isThreadSafe: true);

    static string Text(string suffix) {
        var assembly = typeof(EditorStyles).Assembly;
        var name = suffix.StartsWith(Prefix, StringComparison.Ordinal) ? suffix : Prefix + suffix;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is not embedded. {Available(assembly)}");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    static string Available(Assembly assembly) =>
        "Embedded: " + string.Join(", ", assembly.GetManifestResourceNames());
}
