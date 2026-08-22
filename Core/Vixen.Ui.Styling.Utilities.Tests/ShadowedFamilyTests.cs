// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>What a shadowed family costs, measured rather than argued about.</summary>
/// <remarks>
///     <para>
///         <b><c>docs/plan/43</c> F8, and the reason it is a measurement.</b> F8 has been called the
///         highest-leverage item in the survey, then refuted, then partly rehabilitated, and the
///         thing it kept turning on is a claim nobody had run: that <see cref="UtilityFamilies.SplitName" />
///         takes the longest registered prefix and never retries a shorter one, so the roots in the
///         ledger's <c>shadowed_by</c> column would resolve if it did. Three revisions of that claim
///         disagreed with each other because it was a claim about the registry and the registry was
///         never asked. This file asks it, on every build.
///     </para>
///     <para>
///         ⚠ <b>The answer is that a retry rescues nothing, and it is not a fact about today's
///         registry that anybody should re-derive by reading it.</b>
///         <see cref="A_shorter_prefix_would_rescue_nothing" /> sweeps every nesting pair the
///         registry contains against every token key both shipped themes contain, and asserts that no
///         class exists which the longest-first rule refuses and a shorter prefix would answer. The
///         shadowed roots — <c>rounded-ss-*</c>, <c>scale-x-*</c>, <c>border-spacing-*</c> and the
///         rest — are shadowed by a family that is the <i>only</i> registered prefix they have, so
///         there is nothing shorter to retry: whatever closes them, it is not the retry, and the
///         retry is a separate question with the answer "no".
///     </para>
///     <para>
///         ⚠ <b>Which leaves the diagnostic, and that is what F8 was actually worth.</b>
///         <see cref="UtilityFamilies.TryResolve" /> says <c>false</c> for "no such family" and for
///         "that family has no such value" alike, so <c>bg-clip-text</c> — a real Tailwind class
///         against a root Vixen registers — used to arrive in <see cref="UtilityGenerator.Unrecognised" />
///         beside every English word the over-inclusive scanner picked up. Indistinguishable from a
///         typo is the failure mode; <see cref="UtilityGenerator.Unresolved" /> is the fix, and the
///         rest of this file pins it.
///     </para>
///     <para>
///         <b>What this file does not do is register anything.</b> Closing the shadowed column was
///         never one fallback, and it was not thirty-five registrations either: six of the
///         thirty-five are registered — <see cref="A_registered_logical_root_resolves_to_what_the_engine_reads" />
///         — and twenty-nine are refusals with a measurement, written into the `note` cell of their
///         own row and summarised at the foot of <see cref="UtilityFamilies" />' constructor. None of
///         it was blocked or unblocked by the split above.
///     </para>
/// </remarks>
public class ShadowedFamilyTests {
    static readonly ThemeTokens Probe = ThemeTokens.Parse(UtilityConsumptionProbe.ProbeTheme);
    static readonly ThemeTokens Shipped = ThemeTokens.CreateDefault();

    /// <summary>Every name the registry holds, asked of the registry rather than counted by hand.</summary>
    static SortedSet<string> Registered(ThemeTokens tokens) {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var utility in UtilityFamilies.Surface(tokens)) {
            names.Add(UtilityFamilies.SplitName(utility).Name);
        }

        return names;
    }

    public static TheoryData<string> Themes => ["probe", "shipped"];

    static ThemeTokens Theme(string name) => name == "probe" ? Probe : Shipped;

    /// <summary>No class exists that longest-first refuses and a shorter registered prefix answers.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole of F8's resolution half, and it fails the day it stops being true.</b>
    ///         A shadow only costs anything when the shorter family <i>can</i> answer what the longer
    ///         one was handed — <c>bg</c> given <c>linear-500</c> in a theme with a colour called
    ///         <c>linear</c>, say — and the sweep below is that question asked of every nesting pair
    ///         in the registry against every colour, radius, shadow, size, weight, family and screen
    ///         key the theme holds. It is empty today. A future family whose name is a prefix of a
    ///         registered one, or a theme token that collides with a suffix, makes it non-empty, and
    ///         at that point a retrying <see cref="UtilityFamilies.SplitName" /> is worth writing —
    ///         with this test naming the classes it would rescue.
    ///     </para>
    ///     <para>
    ///         <b>Forced through the public constructor rather than through the parser</b>, because
    ///         asking "what would the shorter family have said" is by construction a question the
    ///         parser cannot be made to ask: it is the parser's choice that is under test.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Themes))]
    public void A_shorter_prefix_would_rescue_nothing(string themeName) {
        var tokens = Theme(themeName);
        var registered = Registered(tokens);
        var declarations = new List<UtilityDeclaration>();

        var suffixes = new List<string> { string.Empty };
        suffixes.AddRange(tokens.Colors.Keys);
        suffixes.AddRange(tokens.Radius.Keys);
        suffixes.AddRange(tokens.Shadow.Keys);
        suffixes.AddRange(tokens.FontSize.Keys);
        suffixes.AddRange(tokens.FontWeight.Keys);
        suffixes.AddRange(tokens.FontFamily.Keys);
        suffixes.AddRange(tokens.Screens.Keys);
        suffixes.AddRange(["0", "1", "2", "4", "px", "full", "auto", "none", "reverse"]);

        var rescued = new List<string>();

        foreach (var longer in registered) {
            foreach (var shorter in registered) {
                if (string.Equals(shorter, longer, StringComparison.Ordinal)
                    || !longer.StartsWith(shorter + "-", StringComparison.Ordinal)) {
                    continue;
                }

                var stolen = longer[(shorter.Length + 1)..];

                foreach (var suffix in suffixes) {
                    var value = suffix.Length == 0 ? stolen : stolen + "-" + suffix;
                    var whole = shorter + "-" + value;

                    // Only interesting where longest-first does not already pick the shorter family.
                    if (string.Equals(
                            UtilityFamilies.SplitName(whole).Name,
                            shorter,
                            StringComparison.Ordinal
                        )) {
                        continue;
                    }

                    if (UtilityParser.TryParse(whole, out var parsed)
                        && UtilityFamilies.TryResolve(parsed, tokens, declarations)) {
                        continue;
                    }

                    var forced = new UtilityCandidate(whole, [], shorter, value, false, null, null, null, false);

                    if (UtilityFamilies.TryResolve(forced, tokens, declarations)) {
                        rescued.Add($"{whole}: refused by '{longer}', answered by '{shorter}'");
                    }
                }
            }
        }

        Assert.True(
            rescued.Count == 0,
            "A retrying SplitName would now rescue classes, which it could not when doc 43 F8 was"
            + " settled. Update F8 and consider the retry:\n  " + string.Join("\n  ", rescued)
        );
    }

    /// <summary>Every shadowed root the ledger names has exactly one registered prefix.</summary>
    /// <remarks>
    ///     ⚠ <b>The reason the retry is not the fix, stated as the shape of the data rather than as
    ///     an opinion.</b> <c>rounded-ss-2xl</c> is taken by <c>rounded</c> because <c>rounded</c> is
    ///     the only registered prefix it has; there is no second candidate for a retry to fall back
    ///     to. Whatever closes these rows, it is a <c>rounded-ss</c> registration.
    /// </remarks>
    [Theory]
    [InlineData("inset-ring-0", "inset")]
    [InlineData("inset-shadow-2xs", "inset")]
    [InlineData("border-spacing-0", "border")]
    [InlineData("rounded-ss-2xl", "rounded")]
    [InlineData("rounded-es-2xl", "rounded")]
    [InlineData("scale-x-0", "scale")]
    [InlineData("rotate-z-0", "rotate")]
    [InlineData("flex-shrink-0", "flex")]
    [InlineData("flex-grow-0", "flex")]
    [InlineData("max-w-screen-md", "max-w")]
    [InlineData("ring-offset-0", "ring")]
    [InlineData("text-shadow-2xs", "text")]
    [InlineData("font-stretch-50%", "font")]
    [InlineData("bg-clip-text", "bg")]
    [InlineData("bg-blend-normal", "bg")]
    [InlineData("stroke-none", "stroke")]
    [InlineData("content-none", "content")]
    public void A_shadowed_root_has_one_registered_prefix_and_no_shorter_one(string whole, string only) {
        var registered = Registered(Probe);

        var prefixes = registered
            .Where(name => whole.Length > name.Length
                && whole.StartsWith(name, StringComparison.Ordinal)
                && whole[name.Length] == '-')
            .ToList();

        Assert.Equal([only], prefixes);
    }

    /// <summary>The six that left the column: each reaches its own family and emits a read property.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two claims, and the second is the one a registration can quietly fail.</b> That
    ///         the class now splits to its own family rather than to the one that used to swallow it
    ///         is the cheap half — <c>SplitName</c> sorts longest-first, so it follows from the
    ///         entry existing. That the declaration it emits names a property
    ///         <see cref="UtilityConsumptionProbe" /> has measured a consumer for is the half worth
    ///         asserting: this repository's commonest defect is a family that resolves, cascades and
    ///         moves nothing, and <c>docs/plan/43</c>'s <c>shadowed_by</c> column is thirty-five
    ///         invitations to add one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The inline pair emits a logical longhand and the block pair a physical one, and
    ///         the rows say so rather than leaving it to the remark in
    ///         <see cref="UtilityFamilies" />.</b> A later hand "correcting" <c>inset-bs-*</c> to
    ///         <c>inset-block-start</c> for Tailwind fidelity would be reverting it to something
    ///         nothing interns — a green cascade and a dead class — and the expected value here is
    ///         what fails on that. The reverse edit is just as wrong: <c>inset-s-*</c> mapped to
    ///         <c>left</c> would stop mirroring under <c>direction: rtl</c>, which is the whole
    ///         reason the logical spelling exists.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("inset-s-2", "inset-s", "inset-inline-start", "8px")]
    [InlineData("inset-e-2", "inset-e", "inset-inline-end", "8px")]
    [InlineData("inset-bs-2", "inset-bs", "top", "8px")]
    [InlineData("inset-be-2", "inset-be", "bottom", "8px")]
    [InlineData("inset-s-full", "inset-s", "inset-inline-start", "100%")]
    [InlineData("inset-bs-full", "inset-bs", "top", "100%")]
    [InlineData("border-bs-2", "border-bs", "border-top-width", "2px")]
    [InlineData("border-be-2", "border-be", "border-bottom-width", "2px")]
    [InlineData("border-bs-paint", "border-bs", "border-top-color", "#3366cc")]
    [InlineData("border-be-paint", "border-be", "border-bottom-color", "#3366cc")]
    public void A_registered_logical_root_resolves_to_what_the_engine_reads(
        string whole,
        string family,
        string property,
        string value
    ) {
        Assert.Equal(family, UtilityFamilies.SplitName(whole).Name);

        var declarations = new List<UtilityDeclaration>();

        Assert.True(UtilityParser.TryParse(whole, out var parsed));
        Assert.True(UtilityFamilies.TryResolve(parsed, Probe, declarations));
        Assert.Equal([new UtilityDeclaration(property, value)], declarations);

        // And the property is one something in the engine acts on, measured rather than asserted.
        Assert.NotEmpty(UtilityConsumptionProbe.Channels(property, value));
    }

    /// <summary>The three the ledger calls shadowed that are not: the bracket comes before the split.</summary>
    /// <remarks>
    ///     ⚠ <b>An arbitrary value is decided in <c>UtilityParser</c> before
    ///     <see cref="UtilityFamilies.SplitName" /> is consulted at all</b> — see the parser's remark
    ///     on the three escape hatches — so <c>bg-size-[auto]</c> parses to the name
    ///     <c>bg-size</c> and not to <c>bg</c> with a value of <c>size-[auto]</c>. It is an unknown
    ///     family rather than a shadowed one, which is the opposite diagnostic and therefore the
    ///     opposite fix. The ledger's note said "swallowed by the family <c>bg</c>" for all three.
    /// </remarks>
    [Theory]
    [InlineData("bg-size-[auto]", "bg-size")]
    [InlineData("bg-position-[center]", "bg-position")]
    [InlineData("font-features-[normal]", "font-features")]
    public void An_arbitrary_value_is_not_shadowed_it_is_unknown(string whole, string name) {
        Assert.True(UtilityParser.TryParse(whole, out var parsed));
        Assert.Equal(name, parsed.Name);
        Assert.False(UtilityFamilies.IsRegistered(parsed.Name));

        // And the shorter name it is *not* split to is registered, which is why it looked shadowed.
        Assert.True(UtilityFamilies.IsRegistered(UtilityFamilies.SplitName(whole).Name));
    }

    /// <summary>A registered family given a value it has not got is not reported as a typo.</summary>
    /// <remarks>
    ///     ⚠ <b>The deliverable F8 was actually worth.</b> Both of these emit no rule and used to be
    ///     one undifferentiated list; the scanner puts several hundred English words in that list per
    ///     project, so the one line worth acting on was unfindable. The refusal names the family that
    ///     was consulted and the value it had nothing for, which is the sentence somebody debugging
    ///     "why does <c>bg-clip-text</c> do nothing" needs to read.
    /// </remarks>
    [Fact]
    public void A_shadowed_class_is_refused_distinctly_from_an_unknown_one() {
        var generator = new UtilityGenerator(Probe);
        generator.Generate(["bg-clip-text", "rounded-ss-lg", "flexx-4", "however", "p-4"]);

        Assert.Equal(1, generator.RuleCount);

        Assert.Equal(
            [
                new UtilityRefusal("bg-clip-text", "bg", "clip-text", UtilityRefusalKind.Value),
                new UtilityRefusal("rounded-ss-lg", "rounded", "ss-lg", UtilityRefusalKind.Value)
            ],
            generator.Unresolved
        );

        // The prose keeps its own channel, and the two classes above have left it.
        Assert.Equal(["flexx-4", "however"], generator.Unrecognised);
    }

    /// <summary>A utility that resolves and whose variant does not is news, not prose.</summary>
    /// <remarks>
    ///     The same defect one field over: <c>BuildSelector</c> returning <c>null</c> put a fully
    ///     resolved utility into the unrecognised list, where it read as a word. Nothing that has
    ///     survived <see cref="UtilityFamilies.TryResolve" /> is prose.
    /// </remarks>
    [Fact]
    public void An_unknown_variant_on_a_real_utility_is_a_refusal() {
        var generator = new UtilityGenerator(Probe);
        generator.Generate(["hover:p-4", "wednesday:p-4"]);

        Assert.Equal(1, generator.RuleCount);
        Assert.Empty(generator.Unrecognised);

        Assert.Equal(
            [new UtilityRefusal("wednesday:p-4", "p", "wednesday", UtilityRefusalKind.Variant)],
            generator.Unresolved
        );
    }

    /// <summary>Nothing the generator used to emit stopped being emitted.</summary>
    /// <remarks>
    ///     ⚠ <b>The one way this change could have cost anything.</b> The refusal split moves
    ///     candidates between two report lists and must not move a rule out of the sheet — the
    ///     scanner is over-inclusive precisely because a false positive costs one unused rule and a
    ///     false negative is a style that silently does not exist. Measured against the surface,
    ///     which is every class the registry can answer.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Themes))]
    public void The_whole_surface_still_emits(string themeName) {
        var tokens = Theme(themeName);
        var surface = UtilityFamilies.Surface(tokens);
        var generator = new UtilityGenerator(tokens);
        generator.Generate(surface);

        Assert.Equal(surface.Count, generator.RuleCount);
        Assert.Empty(generator.Unrecognised);
        Assert.Empty(generator.Unresolved);
    }
}
