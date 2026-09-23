// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>
///     Asks the engine's readers, rather than the resolver, whether a class that fills a slot of an
///     assembled list — <c>transform</c>, <c>filter</c> or <c>backdrop-filter</c> — is one they accept
///     (<a href="https://github.com/Rikarin/Vixen/issues/1348">#1348</a>).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Emission is not acceptance, and for an assembled list the difference is worse than a
///         class that paints nothing.</b> Every slot family's <c>Alongside</c> writes one shared
///         declaration — <see cref="UtilityComposition.Transform" />,
///         <see cref="UtilityComposition.Filter" /> or <see cref="UtilityComposition.BackdropFilter" />
///         — and both readers drop the <b>whole</b> list on a single function they decline. So a slot
///         value a reader refuses deletes every slot beside it — <c>translate-z-full rotate-z-90</c> did
///         not rotate (#1328) — while <see cref="ParityLedger.Measure" /> scored the root
///         <c>works</c>, because all it asked was whether the family emitted a declaration.
///     </para>
///     <para>
///         ⚠ <b>Three lists and not the one the issue named.</b> #1348 scoped this to "the transform
///         slots today"; <c>DrawListBuilder.Refused</c> says in as many words that a filter list is one
///         declaration and one function it cannot execute takes the eight beside it, and the backdrop
///         list is the same code. Probing only <c>transform</c> would have left two thirds of the
///         shape measured by emission.
///     </para>
///     <para>
///         <b>Two ways of observing a refusal, because the two readers report differently.</b>
///         <c>TransformReader.Of</c> returns null for a refused list and null for an identity and says
///         nothing, so <c>translate-z-0</c> accepted and <c>translate-z-full</c> refused are
///         indistinguishable alone. Beside a witness in a <i>different</i> slot they are not: an
///         accepted value composes with the witness's quarter turn and leaves a transform, a refused
///         one takes the witness down with it. The filter executor does say — it files the declaration
///         among <see cref="UiDocument.Refusals" /> — so a filter class is drawn and its document's
///         refusals read, which needs no witness.
///     </para>
///     <para>
///         Through the generator, the loader and a real <see cref="UiDocument" />, for
///         <c>UtilityConsumptionProbe.Emissions</c>' reason: what a consumer reads is the escaped,
///         layered, substituted sheet, not the registry's intentions.
///     </para>
/// </remarks>
static class AssembledReaderProbe {
    /// <summary>The assembled properties whose readers drop a whole list on one declined function.</summary>
    public static readonly string[] Properties = ["transform", "filter", "backdrop-filter"];

    /// <summary>A class in the <c>rotate-z</c> slot, which alone leaves a non-identity transform.</summary>
    public const string Witness = "rotate-z-90";

    /// <summary>The witness for a class that itself fills <see cref="Witness" />'s slot.</summary>
    public const string OtherWitness = "skew-x-12";

    /// <summary>The theme every question here is asked against: the consumption probe's.</summary>
    public static readonly ThemeTokens Tokens = ThemeTokens.Parse(UtilityConsumptionProbe.ProbeTheme);

    static readonly Dictionary<string, bool> Cache = new(StringComparer.Ordinal);
    static readonly Lock Gate = new();

    /// <summary>
    ///     Something to paint, so that a filter has a picture to act on and the executor is reached at
    ///     all — an empty box is skipped before its filter is read.
    /// </summary>
    const string Painted = "#probe { width: 20px; height: 20px; background-color: #4f7cff; }";

    /// <summary>Whether a class writes one of <see cref="Properties" /> through a slot.</summary>
    /// <param name="utility">The class name.</param>
    /// <param name="tokens">The theme it resolves against.</param>
    /// <param name="property">The assembled property it writes, when it does.</param>
    /// <param name="slots">The fragments it fills, when it does.</param>
    public static bool FillsASlot(string utility, ThemeTokens tokens, out string property, out IReadOnlyList<string> slots) {
        List<UtilityDeclaration> declarations = [];
        property = string.Empty;
        slots = [];

        if (!UtilityParser.TryParse(utility, out var parsed) || !UtilityFamilies.TryResolve(parsed, tokens, declarations)) {
            return false;
        }

        // ⚠ By the `var(--tw-` reference and not by the property name: `transform-none` and
        // `filter-none` write the same properties and are keywords, not slots, and a witness beside one
        // is SUPPOSED to vanish.
        var assembled = declarations.Find(d => Array.IndexOf(Properties, d.Property) >= 0
                                               && d.Value.Contains($"var({UtilityComposition.Prefix}", StringComparison.Ordinal));

        if (assembled.Property is null) {
            return false;
        }

        property = assembled.Property;
        slots = [.. declarations.Where(d => UtilityComposition.IsFragment(d.Property)).Select(d => d.Property)];

        return true;
    }

    /// <summary>Whether a class writes one of <see cref="Properties" /> through a slot.</summary>
    /// <param name="utility">The class name.</param>
    /// <param name="tokens">The theme it resolves against.</param>
    public static bool FillsASlot(string utility, ThemeTokens tokens) => FillsASlot(utility, tokens, out _, out _);

    /// <summary>Whether the engine's reader for the list the class fills declines it.</summary>
    /// <param name="utility">A class that <see cref="FillsASlot(string, ThemeTokens)" />.</param>
    /// <remarks>
    ///     Against the consumption probe's theme and no other, which is what lets the answer be cached
    ///     by name: <see cref="ParityLedger.Measure" /> runs several times a test run and asks the
    ///     same questions each time.
    /// </remarks>
    public static bool Declines(string utility) {
        lock (Gate) {
            if (Cache.TryGetValue(utility, out var known)) {
                return known;
            }

            FillsASlot(utility, Tokens, out var property, out var slots);

            var declined = property == "transform"
                ? TransformOf(Tokens, utility, slots.Contains(UtilityComposition.RotateZ) ? OtherWitness : Witness) is null
                : Refuses(RefusalsOf(Tokens, utility), property);

            Cache[utility] = declined;

            return declined;
        }
    }

    /// <summary>Value spellings every scale kind answers some of, beyond what the surface happens to hold.</summary>
    /// <remarks>
    ///     ⚠ <b>The surface is one value per family, and one value is exactly what hid #1328.</b>
    ///     <c>UtilityFamilies.Surface</c> spells <c>translate-z-2</c> for a <c>Size</c> or a
    ///     <c>Depth</c> kind alike, and <c>2</c> is the one value both answer the same way — the defect
    ///     was in <c>full</c>, <c>screen</c>, <c>auto</c>, <c>min</c>, <c>max</c>, <c>fit</c> and
    ///     <c>1/2</c>, none of which the surface ever asks. So this is the scale arms' vocabulary,
    ///     written out: the spacing numbers and <c>px</c>, the angle and percentage steps, the size
    ///     keywords and the six viewport units, fractions — and, in <see cref="Candidates" />, every
    ///     blur and drop-shadow token the theme names. Whatever of it a slot family resolves is probed;
    ///     whatever it does not is not a class, and costs a parse.
    /// </remarks>
    static readonly string[] Vocabulary = [
        "0", "px", "0.5", "1", "2", "3", "4", "6", "12", "45", "50", "75", "90", "100", "125", "150", "180", "200",
        "auto", "full", "screen", "min", "max", "fit", "lh", "none",
        "svw", "lvw", "dvw", "svh", "lvh", "dvh",
        "1/2", "2/3", "3/4"
    ];

    /// <summary>
    ///     Every class, positive and negative, that a slot family answers from <see cref="Vocabulary" />,
    ///     from the theme's blur and drop-shadow names, and from the values the surface already spells
    ///     for any family.
    /// </summary>
    /// <param name="surface">The registry's surface, which names the slot families and adds its values.</param>
    /// <remarks>
    ///     ⚠ <b>Arbitrary values are not probed.</b> <c>translate-z-[50%]</c> is refused by the reader
    ///     exactly as a browser refuses <c>translateZ(50%)</c>: the author asked for it by name. A
    ///     family's own scale is what the ledger vouches for, and a named value it offers that the
    ///     reader then throws away is the defect.
    /// </remarks>
    public static IReadOnlyList<string> Candidates(IReadOnlyList<string> surface) {
        var roots = new SortedSet<string>(StringComparer.Ordinal);
        var values = new SortedSet<string>(Vocabulary, StringComparer.Ordinal);

        values.UnionWith(Tokens.Blur.Keys);
        values.UnionWith(Tokens.DropShadow.Keys);

        foreach (var utility in surface) {
            if (!UtilityParser.TryParse(utility, out var parsed) || parsed.Arbitrary is not null || parsed.Variants.Count > 0) {
                continue;
            }

            if (parsed.Value.Length > 0) {
                values.Add(parsed.SlashSuffix is null ? parsed.Value : $"{parsed.Value}/{parsed.SlashSuffix}");
            }

            if (FillsASlot(utility, Tokens)) {
                roots.Add(parsed.Name);
            }
        }

        var candidates = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var root in roots) {
            // The bare name too: `blur` and `drop-shadow` are classes on their own.
            if (FillsASlot(root, Tokens)) {
                candidates.Add(root);
            }

            foreach (var value in values) {
                foreach (var candidate in (string[])[$"{root}-{value}", $"-{root}-{value}"]) {
                    if (FillsASlot(candidate, Tokens)) {
                        candidates.Add(candidate);
                    }
                }
            }
        }

        return [.. candidates];
    }

    /// <summary>The transform an element carrying exactly these classes ends up under.</summary>
    /// <param name="tokens">The theme the classes resolve against.</param>
    /// <param name="classes">The classes, all of which are generated into the sheet.</param>
    public static UiTransform? TransformOf(ThemeTokens tokens, params string[] classes) {
        using var document = new UiDocument(200f, 100f);
        document.Load(new UtilityGenerator(tokens).Generate(classes), StyleOrigin.Author);

        var probe = document.Create("div", document.Root, null, classes);
        document.Update();

        return probe.Transform;
    }

    /// <summary>Whether a document's refusals name a declaration of this property.</summary>
    /// <param name="refusals">What <see cref="RefusalsOf" /> returned.</param>
    /// <param name="property">The assembled property.</param>
    /// <remarks>
    ///     A refusal is a diagnostic record printed whole — <c>SelectorDiagnostic { Text = filter: … }</c>
    ///     — so the property is matched as the start of its text. <c>Text = filter:</c> is not a
    ///     substring of <c>Text = backdrop-filter:</c>, which is what keeps the two lists apart.
    /// </remarks>
    public static bool Refuses(IReadOnlyList<string> refusals, string property) =>
        refusals.Any(refusal => refusal.Contains($"Text = {property}: ", StringComparison.Ordinal));

    /// <summary>What a document refused, having painted one element carrying exactly these classes.</summary>
    /// <param name="tokens">The theme the classes resolve against.</param>
    /// <param name="classes">The classes, all of which are generated into the sheet.</param>
    public static IReadOnlyList<string> RefusalsOf(ThemeTokens tokens, params string[] classes) {
        using var document = new UiDocument(200f, 100f);
        document.Load(new UtilityGenerator(tokens).Generate(classes), StyleOrigin.Author);
        document.Load(Painted, StyleOrigin.Author);

        document.Create("div", document.Root, "probe", classes);
        document.Update();
        document.Draw();

        return document.Refusals();
    }

    /// <summary>
    ///     The colour matrix the draw list carries for one painted element with exactly these classes,
    ///     or null where the element opened no filtered group.
    /// </summary>
    /// <param name="tokens">The theme the classes resolve against.</param>
    /// <param name="classes">The classes, all of which are generated into the sheet.</param>
    /// <remarks>
    ///     The draw list rather than <see cref="RefusalsOf" />, because "nothing was refused" is also
    ///     what a filter that never reached the executor leaves behind. A
    ///     <see cref="DrawCommandKind.LayerPush" /> carrying a <see cref="DrawCommand.Filter" /> is the
    ///     list reaching the frame. Compositing is on, as it is in a host, because without it no
    ///     filter opens a group at all.
    /// </remarks>
    public static UiColorMatrix? FilterOf(ThemeTokens tokens, params string[] classes) {
        using var document = new UiDocument(200f, 100f) { Compositing = true };
        document.Load(new UtilityGenerator(tokens).Generate(classes), StyleOrigin.Author);
        document.Load(Painted, StyleOrigin.Author);

        document.Create("div", document.Root, "probe", classes);
        document.Update();
        document.Draw();

        return document.Drawing.Commands.FirstOrDefault(c => c is { Kind: DrawCommandKind.LayerPush, Filter: not null }).Filter;
    }
}
