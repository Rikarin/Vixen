// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>
///     Asks the engine's <c>transform</c> reader, rather than the resolver, whether a class that fills a
///     slot of the assembled <c>transform</c> list is one it accepts
///     (<a href="https://github.com/Rikarin/Vixen/issues/1348">#1348</a>).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Emission is not acceptance, and for an assembled list the difference is worse than a
///         class that paints nothing.</b> Every slot family's <c>Alongside</c> writes the one shared
///         <c>transform</c> of <see cref="UtilityComposition.Transform" />, and
///         <c>TransformReader.Functions</c> drops the <b>whole</b> list on a single function it
///         declines. So a slot value the reader refuses deletes every slot beside it —
///         <c>translate-z-full rotate-z-90</c> did not rotate (#1328) — while
///         <see cref="ParityLedger.Measure" /> scored the root <c>works</c>, because all it asked was
///         whether the family emitted a declaration.
///     </para>
///     <para>
///         <b>The observation is a neighbour, because the reader's two answers look the same alone.</b>
///         <c>TransformReader.Of</c> returns null for a refused list and null for an identity, so
///         <c>translate-z-0</c> accepted and <c>translate-z-full</c> refused are indistinguishable on
///         their own. Beside a witness in a <i>different</i> slot they are not: an accepted value
///         composes with the witness's quarter turn and leaves a transform, a refused one takes the
///         witness down with it and leaves none. That is the defect's own shape, and a witness in the
///         same slot would be no witness — the cascade keeps one of the two values and the other never
///         reaches the reader.
///     </para>
///     <para>
///         Through the generator, the loader and a real <see cref="UiDocument" />, for
///         <c>UtilityConsumptionProbe.Emissions</c>' reason: what a consumer reads is the escaped,
///         layered, substituted sheet, not the registry's intentions.
///     </para>
/// </remarks>
static class AssembledReaderProbe {
    /// <summary>The assembled property this probes, and the only one whose reader drops a whole list today.</summary>
    public const string Property = "transform";

    /// <summary>A class in the <c>rotate-z</c> slot, which alone leaves a non-identity transform.</summary>
    public const string Witness = "rotate-z-90";

    /// <summary>The witness for a class that itself fills <see cref="Witness" />'s slot.</summary>
    public const string OtherWitness = "skew-x-12";

    /// <summary>The theme every question here is asked against: the consumption probe's.</summary>
    public static readonly ThemeTokens Tokens = ThemeTokens.Parse(UtilityConsumptionProbe.ProbeTheme);

    static readonly Dictionary<string, bool> Cache = new(StringComparer.Ordinal);
    static readonly Lock Gate = new();

    /// <summary>Whether a class writes the shared <c>transform</c> list through a slot.</summary>
    /// <param name="utility">The class name.</param>
    /// <param name="tokens">The theme it resolves against.</param>
    /// <param name="slots">The fragments it fills, when it does.</param>
    public static bool FillsASlot(string utility, ThemeTokens tokens, out IReadOnlyList<string> slots) {
        List<UtilityDeclaration> declarations = [];
        slots = [];

        if (!UtilityParser.TryParse(utility, out var parsed) || !UtilityFamilies.TryResolve(parsed, tokens, declarations)) {
            return false;
        }

        // ⚠ By the `var(--tw-` reference and not by the property name: `transform-none` writes the
        // same property and is a keyword, not a slot, and a witness beside it is SUPPOSED to vanish.
        if (!declarations.Exists(d => d.Property == Property && d.Value.Contains($"var({UtilityComposition.Prefix}", StringComparison.Ordinal))) {
            return false;
        }

        slots = [.. declarations.Where(d => UtilityComposition.IsFragment(d.Property)).Select(d => d.Property)];

        return true;
    }

    /// <summary>
    ///     Whether the engine's reader declines the class: beside a witness in another slot, the
    ///     element ends up with no transform at all.
    /// </summary>
    /// <param name="utility">A class that <see cref="FillsASlot" />.</param>
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

            FillsASlot(utility, Tokens, out var slots);

            var witness = slots.Contains(UtilityComposition.RotateZ) ? OtherWitness : Witness;
            var declined = TransformOf(Tokens, utility, witness) is null;

            Cache[utility] = declined;

            return declined;
        }
    }

    /// <summary>Value spellings every scale kind answers some of, beyond what the surface happens to hold.</summary>
    /// <remarks>
    ///     ⚠ <b>The surface is one value per family, and one value is exactly what hid #1328.</b>
    ///     <c>UtilityFamilies.Surface</c> spells <c>translate-z-2</c> for a <c>Size</c> or a
    ///     <c>Depth</c> kind alike, and <c>2</c> is the one value both answer the same way — the defect
    ///     was in <c>full</c>, <c>screen</c>, <c>auto</c>, <c>min</c>, <c>max</c>, <c>fit</c>,
    ///     <c>lh</c> and <c>1/2</c>, none of which the surface ever asks. So this is the scale arms'
    ///     vocabulary, written out: the spacing numbers and <c>px</c>, the angle steps, the size
    ///     keywords and the six viewport units, a fraction. Whatever of it a slot family resolves is
    ///     probed; whatever it does not is not a class, and costs a parse.
    /// </remarks>
    static readonly string[] Vocabulary = [
        "0", "px", "0.5", "1", "2", "3", "4", "6", "12", "45", "90", "100", "150", "180",
        "auto", "full", "screen", "min", "max", "fit", "lh",
        "svw", "lvw", "dvw", "svh", "lvh", "dvh",
        "1/2", "2/3", "3/4"
    ];

    /// <summary>
    ///     Every class, positive and negative, that a slot family answers from <see cref="Vocabulary" />
    ///     and from the values the surface already spells for any family.
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

        foreach (var utility in surface) {
            if (!UtilityParser.TryParse(utility, out var parsed) || parsed.Arbitrary is not null || parsed.Variants.Count > 0) {
                continue;
            }

            if (parsed.Value.Length > 0) {
                values.Add(parsed.SlashSuffix is null ? parsed.Value : $"{parsed.Value}/{parsed.SlashSuffix}");
            }

            if (FillsASlot(utility, Tokens, out _)) {
                roots.Add(parsed.Name);
            }
        }

        var candidates = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var root in roots) {
            foreach (var value in values) {
                foreach (var candidate in (string[]) [$"{root}-{value}", $"-{root}-{value}"]) {
                    if (FillsASlot(candidate, Tokens, out _)) {
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
}
