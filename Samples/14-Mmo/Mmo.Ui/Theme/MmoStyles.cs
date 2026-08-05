// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;
using Vixen.Ui.Styling.Utilities;

namespace Vixen.Samples.Mmo.Ui;

/// <summary>The game's stylesheet: its tokens, its own rules, and the utilities it actually uses.</summary>
/// <remarks>
///     <para>
///         <b>Three inputs and one output.</b> <c>Theme/vixen.ui.yaml</c> is the tokens,
///         <c>Theme/hud.vcss</c> is the handful of rules a utility cannot say, and the markup is
///         scanned for every class name that could be a utility. What comes out is one sheet, and
///         the only rules in it are the ones something in this game refers to.
///     </para>
///     <para>
///         ⚠ <b>The scan happens at startup here and belongs in a build step.</b>
///         <c>Vixen.Ui.Styling.Utilities</c>' README lists build-step integration as waiting on the
///         asset pipeline; until then somebody has to do it, and doing it over embedded copies of the
///         same files with the same scanner is the honest stand-in rather than a different mechanism.
///         It costs a few milliseconds once.
///     </para>
///     <para>
///         ⚠ <b>The utility layer loses every specificity argument on purpose.</b> Everything
///         generated lands in <c>@layer utilities</c>, so <c>hud.vcss</c>'s <c>.slot.on-cooldown</c>
///         beats <c>bg-ink-600</c> without either of them saying <c>!important</c> — which is the
///         whole reason the layer exists, and the reason the base sheet is loaded <em>first</em>
///         below rather than last.
///     </para>
/// </remarks>
public static class MmoStyles {
    /// <summary>Class names the scanner cannot see, because nothing writes them down whole.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one place the scanner's design has a cost, and it is worth understanding
    ///         rather than working around.</b> It is deliberately over-inclusive — it pulls every run
    ///         of characters that <em>could</em> be a class name out of the text and lets the
    ///         generator discard the rest — because a false positive is one unused rule and a false
    ///         negative is a style silently missing at run time. But it cannot see a name that is
    ///         never written: <c>$"text-{rarity}"</c> is <c>text-</c> and a variable, and
    ///         <c>text-storied</c> appears nowhere.
    ///     </para>
    ///     <para>
    ///         So the rarity colours are listed here, and the general rule is Tailwind's own: <b>a
    ///         class name assembled at run time must be safelisted, or written out in full in a
    ///         switch the scanner can read.</b> This sample does the first for the five rarities
    ///         because they are a closed set that four different panels colour by.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<string> Safelist { get; } = [
        "text-worn", "text-common", "text-fine", "text-rare", "text-storied",
        "border-worn", "border-common", "border-fine", "border-rare", "border-storied",
        "text-health", "text-mana", "text-rage", "text-focus", "text-cast"
    ];

    /// <summary>The design tokens, read from the game's <c>vixen.ui.yaml</c>.</summary>
    /// <returns>Them.</returns>
    public static ThemeTokens Tokens() => ThemeTokens.Parse(Text("Theme.vixen.ui.yaml"));

    /// <summary>Every class name this game's markup mentions, plus the ones it assembles.</summary>
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

    /// <summary>The whole sheet: this game's own rules, then the utilities it uses.</summary>
    /// <param name="unrecognised">Every candidate the utility system did not know. Empty is the healthy answer.</param>
    /// <returns>The CSS.</returns>
    /// <remarks>
    ///     ⚠ <b><paramref name="unrecognised" /> is not a diagnostic to log and forget.</b> The
    ///     scanner is over-inclusive, so most of what lands in it is prose — but a *misspelt* utility
    ///     lands in it too, and a misspelt utility is a style that silently does nothing. The test
    ///     suite filters the prose and asserts on the rest.
    /// </remarks>
    public static string Compile(out ImmutableArray<string> unrecognised) {
        var generator = new UtilityGenerator(Tokens());
        var utilities = generator.Generate(Candidates());

        unrecognised = [.. generator.Unrecognised];

        // Base first. It is unlayered, the utilities are in `@layer utilities`, and a layer loses to
        // an unlayered rule whatever the order — loading it second as well would make that true for
        // the wrong reason and hide a regression in the layering.
        return Text("Theme.hud.vcss") + "\n" + utilities;
    }

    /// <summary>The whole sheet, for a caller that does not want the diagnostics.</summary>
    /// <returns>The CSS.</returns>
    public static string Compile() => Compile(out _);

    /// <summary>The embedded markup files, which are what a build step would glob.</summary>
    /// <returns>Their resource names.</returns>
    internal static IEnumerable<string> Markup() =>
        typeof(MmoStyles).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".vxml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    const string Prefix = "Vixen.Samples.Mmo.Ui.";

    static string Text(string suffix) {
        var assembly = typeof(MmoStyles).Assembly;
        // ⚠ The root namespace and not the assembly name. This assembly is `Mmo.Ui` and its types
        // live in `Vixen.Samples.Mmo.Ui`, because CA1716 sent the namespace one way and doc 27's
        // reference graph sent the assembly the other — and a manifest resource is named after the
        // namespace.
        var name = suffix.StartsWith(Prefix, StringComparison.Ordinal) ? suffix : Prefix + suffix;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is not embedded. {Available(assembly)}");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    static string Available(Assembly assembly) =>
        "Embedded: " + string.Join(", ", assembly.GetManifestResourceNames());
}
