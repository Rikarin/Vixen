// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>The <c>--tw-*</c> composition mechanism, and the two arguments that decided its shape.</summary>
/// <remarks>
///     <para>
///         <b>The requirement is <c>docs/plan/43</c>'s twelve <c>composed</c> roots.</b> A composed
///         utility sets a custom property and emits no declaration of its own; a different utility
///         assembles the fragments. <c>from-*</c>, <c>via-*</c> and <c>to-*</c> with
///         <c>bg-linear-*</c> are the worked example here, and the same pattern is v4's transforms,
///         box-shadow and filters — so what these tests pin is the mechanism, not gradients.
///     </para>
///     <para>
///         ⚠ <b>Two designs were on the table and the second one is cheaper.</b> (a) the utilities
///         really set custom properties and the cascade resolves the <c>var()</c> references at use
///         time; (b) the generator folds the fragments into one declaration as it emits. Every test
///         below that names a variant or an unset fragment exists because the decision between them
///         is not a matter of taste, and it is not settled by reading the generator either — it is
///         settled by two things the engine does, both measured here.
///     </para>
/// </remarks>
public class CompositionTests {
    /// <summary>The end-to-end proof: three composed utilities and one assembler make a gradient.</summary>
    [Fact]
    public void Three_composed_utilities_and_an_assembler_make_one_declaration() {
        var fixture = new UtilityFixture();

        // None of the three stop utilities emits `background-image`. That is what `composed` means,
        // and asserting it is what stops somebody "fixing" them into ordinary families later.
        Assert.Equal(["--tw-gradient-from: #4f7cff"], fixture.Emits("from-accent"));
        Assert.Equal(["--tw-gradient-to: #1f1f26"], fixture.Emits("to-surface-3"));

        // …and `via-*` is the one that also carries an alongside declaration, because a middle stop is
        // the one thing a `var()` fallback cannot conjure.
        var via = fixture.Emits("via-muted");
        Assert.Equal("--tw-gradient-via: #8a8a99", via[0]);
        Assert.StartsWith("--tw-gradient-stops: ", via[1], StringComparison.Ordinal);
        Assert.Equal(2, via.Length);

        // The assembler is the only one of the four that emits a property a consumer could read.
        Assert.Single(fixture.Emits("bg-linear-to-r"));
        Assert.StartsWith("background-image: linear-gradient(to right, ", fixture.Emits("bg-linear-to-r")[0], StringComparison.Ordinal);

        // And put together, through the real cascade, they are one gradient.
        //
        // ⚠ <b>The stops arrive as hex and not as `rgb(…)`, and whatever reads this has to expect
        // that.</b> `bg-accent` reaches the cascade as `background-color: #4f7cff` and comes back
        // normalised, because ExCSS parsed it; a value containing a `var()` is left verbatim by
        // design — `VarSubstitution`'s own remark says so — so the substitution happens *after* the
        // only thing that would have normalised it. Nothing composed can ever be normalised, so this
        // is a standing fact about the mechanism rather than a quirk of gradients, and it is a note
        // for whoever writes the renderer half in #43.
        Assert.Equal(
            "linear-gradient(to right, #4f7cff 0%, #8a8a99 50%, #1f1f26 100%)",
            fixture.Computed(["from-accent", "via-muted", "to-surface-3", "bg-linear-to-r"], "background-image")
        );
    }

    /// <summary>The decisive case: a variant changes which fragment wins, and only the cascade knows.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the whole argument for design (a), and the reason design (b) is not merely
    ///         worse but unable.</b> One element, one stylesheet, two pointer states, two different
    ///         gradients. A generator that composed the fragments when it emitted would have to write
    ///         a <i>constant</i> <c>background-image</c> into <c>.bg-linear-to-r</c>, and no constant
    ///         is equal to two different values — so the hover half would be dropped, silently,
    ///         which is precisely the failure <c>docs/plan/43</c> exists to eliminate.
    ///     </para>
    ///     <para>
    ///         The escape design (b) would have is to emit the cross-product instead, and
    ///         <see cref="What_generation_time_composition_would_have_had_to_emit" /> is what that
    ///         costs.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_variant_decides_which_fragment_wins_and_only_the_cascade_can() {
        var fixture = new UtilityFixture();
        string[] classes = ["from-accent", "hover:from-accent-hover", "to-surface-3", "bg-linear-to-r"];

        var rest = fixture.Computed(classes, "background-image");
        var hover = fixture.Computed(classes, "background-image", state: ElementState.Hover);

        Assert.Equal("linear-gradient(to right, #4f7cff 0%, #1f1f26 100%)", rest);
        Assert.Equal("linear-gradient(to right, #6a91ff 0%, #1f1f26 100%)", hover);

        // The proof by contradiction, stated as an assertion rather than left in a comment: the two
        // are different, so no single emitted declaration could have been both.
        Assert.NotEqual(rest, hover);
    }

    /// <summary>What design (b) would have had to emit, counted rather than argued.</summary>
    /// <remarks>
    ///     ⚠ <b>The generator resolves one candidate at a time and writes one rule per class name.</b>
    ///     So a generation-time composition does not merely have to pick a value — it has to invent a
    ///     selector that names <i>two</i> classes at once, because the fragment and the assembler are
    ///     different classes and the variant belongs to the fragment. The rule it would need for the
    ///     hovered case is <c>.bg-linear-to-r.hover\:from-accent-hover:hover</c>, a cross-product it
    ///     cannot even enumerate until it has seen the whole candidate set, and which grows as
    ///     assemblers × fragment-bearing classes × variants. This test pins that the faithful design
    ///     emits neither — one rule per class, and no selector mentions two.
    /// </remarks>
    [Fact]
    public void What_generation_time_composition_would_have_had_to_emit() {
        var fixture = new UtilityFixture();
        var sheet = fixture.Generate("from-accent", "hover:from-accent-hover", "to-surface-3", "bg-linear-to-r");

        // Four classes in, four rules out. Design (b) needs five at least: one more for the hovered
        // pairing, and one per further pairing after that.
        Assert.Equal(4, fixture.Generator.RuleCount);

        // Exactly one rule sets `background-image`, and it is the assembler's — not one per fragment,
        // and not one per fragment-and-variant pairing.
        Assert.Equal(1, Occurrences(sheet, "background-image"));

        // And no selector names two classes. `.a.b` is the shape a composed cross-product needs, and
        // its absence is the mechanical statement that nothing here composes at emit time.
        Assert.DoesNotContain("bg-linear-to-r.", sheet, StringComparison.Ordinal);

        // Both `from-*` rules survive, each under its own selector, which is what makes the cascade
        // able to choose between them later.
        Assert.Equal(2, Occurrences(sheet, "--tw-gradient-from:"));
    }

    /// <summary>The sabotage: a gradient with no <c>via-*</c> degrades to two stops, not to nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The trap this mechanism was most likely to fall into.</b> Per CSS a <c>var()</c>
    ///         that resolves to nothing and has no fallback makes the declaration <i>invalid at
    ///         computed-value time</i>, and the whole declaration is dropped —
    ///         <see cref="Vixen.Ui.Styling.VarSubstitution" /> implements exactly that. So the naive
    ///         <c>linear-gradient(var(--tw-gradient-from), var(--tw-gradient-via),
    ///         var(--tw-gradient-to))</c> makes <c>from-accent to-surface-3</c> paint <i>no
    ///         gradient</i> rather than a two-stop one, and it does it silently.
    ///     </para>
    ///     <para>
    ///         <b>The answer is the fallback chain, and it needed no <c>@property</c>.</b> Every
    ///         fragment carries an initial value in <see cref="UtilityComposition" /> and is only ever
    ///         mentioned through <see cref="UtilityComposition.Reference" />, so the two-stop list is
    ///         what <c>--tw-gradient-stops</c> is worth when nobody set it. Each assertion below is a
    ///         fragment left out on purpose.
    ///     </para>
    /// </remarks>
    [Theory]
    // No `via` at all — the case that would have vanished.
    [InlineData(new[] { "from-accent", "to-surface-3", "bg-linear-to-r" },
        "linear-gradient(to right, #4f7cff 0%, #1f1f26 100%)")]
    // No `to` either. Still a gradient, and the missing end is the initial value rather than a hole.
    [InlineData(new[] { "from-accent", "bg-linear-to-r" },
        "linear-gradient(to right, #4f7cff 0%, transparent 100%)")]
    // The assembler entirely alone. A two-stop transparent gradient is a no-op that draws nothing,
    // which is the right answer; a dropped declaration would have been indistinguishable from a typo.
    [InlineData(new[] { "bg-linear-to-r" },
        "linear-gradient(to right, transparent 0%, transparent 100%)")]
    // A position with no colour, which is the other half of the same family missing.
    [InlineData(new[] { "from-40%", "to-surface-3", "bg-linear-to-r" },
        "linear-gradient(to right, transparent 40%, #1f1f26 100%)")]
    // Every fragment written out, so that the defaults above are visibly defaults and not the only
    // thing the assembler can produce.
    [InlineData(new[] { "from-accent", "from-10%", "via-muted", "via-60%", "to-surface-3", "to-90%", "bg-linear-to-r" },
        "linear-gradient(to right, #4f7cff 10%, #8a8a99 60%, #1f1f26 90%)")]
    public void An_unset_fragment_falls_back_instead_of_dropping_the_declaration(string[] classes, string expected) {
        Assert.Equal(expected, new UtilityFixture().Computed(classes, "background-image"));
    }

    /// <summary>What the naive form would have done, so that the fallback is visibly load-bearing.</summary>
    /// <remarks>
    ///     ⚠ <b>The counter-example, run rather than asserted from memory.</b> Without this, the
    ///     theory above passes just as well against an engine that quietly tolerates an unset
    ///     <c>var()</c> — and then the fallbacks would be decoration, and the first assembler somebody
    ///     wrote by hand without them would work fine until the day a fragment was missing. This is
    ///     the same stylesheet with the fallbacks taken out, and it produces nothing at all.
    /// </remarks>
    [Fact]
    public void Without_the_fallback_an_unset_fragment_drops_the_whole_declaration() {
        var engine = new StyleEngine();
        engine.Load(
            """
            .from-accent    { --tw-gradient-from: #4f7cff; }
            .naive          { background-image: linear-gradient(to right, var(--tw-gradient-from), var(--tw-gradient-via), var(--tw-gradient-to)); }
            .careful        { background-image: linear-gradient(to right, var(--tw-gradient-from, transparent), var(--tw-gradient-to, transparent)); }
            """,
            StyleOrigin.Author
        );

        Assert.Null(Resolve(engine, ["from-accent", "naive"]));
        Assert.Equal("linear-gradient(to right, #4f7cff, transparent)", Resolve(engine, ["from-accent", "careful"]));

        static string? Resolve(StyleEngine engine, string[] classes) {
            var element = engine.Tree.CreateElement("div", classNames: classes);
            var style = engine.Resolver.Resolve(engine.Tree, element);
            var id = engine.Properties.Lookup("background-image");

            return id != NameTable.None && style.TryGet(id, out var value) ? engine.Values.NameOf(value) : null;
        }
    }

    /// <summary>Every registered fragment says what it is worth unset, and is only reachable with it.</summary>
    /// <remarks>
    ///     The invariant that makes the trap above unreachable by construction rather than by
    ///     everybody remembering. A fragment that cannot say what it is worth unset does not belong in
    ///     the table, and <see cref="UtilityComposition.Reference" /> is the only supported way to
    ///     mention one.
    /// </remarks>
    [Fact]
    public void Every_fragment_carries_an_initial_value() {
        Assert.NotEmpty(UtilityComposition.Fragments);

        foreach (var fragment in UtilityComposition.Fragments) {
            Assert.StartsWith(UtilityComposition.Prefix, fragment, StringComparison.Ordinal);
            Assert.True(UtilityComposition.IsFragment(fragment));
            Assert.NotEmpty(UtilityComposition.InitialValueOf(fragment));
            Assert.Equal($"var({fragment}, {UtilityComposition.InitialValueOf(fragment)})", UtilityComposition.Reference(fragment));
        }

        // The `--` placeholders that are *not* fragments, and must not be mistaken for them: nothing
        // assembles them, so they are inert and InertProperties.txt records the debt. #23 and #28 are
        // where they get assemblers, and this mechanism is the shape those tasks now have to fill.
        Assert.False(UtilityComposition.IsFragment("--rotate"));
        Assert.False(UtilityComposition.IsFragment("--blur"));
        Assert.False(UtilityComposition.IsFragment("--tw-not-registered"));
        Assert.Throws<ArgumentException>(() => UtilityComposition.InitialValueOf("--tw-not-registered"));
    }

    /// <summary>Composed families still behave like utilities everywhere else.</summary>
    [Fact]
    public void A_composed_family_is_still_an_ordinary_utility() {
        var fixture = new UtilityFixture();

        // The arbitrary escape hatch reaches the fragment, so a colour outside the palette works.
        Assert.Equal(["--tw-gradient-from: #123456"], fixture.Emits("from-[#123456]"));

        // A percentage is a position and a token is a colour, decided by the value's shape. No colour
        // token can be named `40%`, so the order shadows nothing reachable.
        Assert.Equal(["--tw-gradient-from-position: 40%"], fixture.Emits("from-40%"));

        // Nonsense is still nonsense rather than a rule that quietly changes the gradient.
        Assert.Null(fixture.Declarations("via-nonsense"));
        Assert.Null(fixture.Declarations("-via-muted"));
        Assert.Null(fixture.Declarations("bg-linear-sideways"));

        // And `bg-` is not shadowed by `bg-linear`: longest-prefix splitting keeps both reachable.
        Assert.Equal(["background-color: #4f7cff"], fixture.Emits("bg-accent"));

        // All eight directions resolve, and the direction really reaches the declaration.
        foreach (var (direction, expected) in new[] {
                     ("to-t", "to top"), ("to-tr", "to top right"), ("to-r", "to right"),
                     ("to-br", "to bottom right"), ("to-b", "to bottom"), ("to-bl", "to bottom left"),
                     ("to-l", "to left"), ("to-tl", "to top left")
                 }) {
            Assert.StartsWith(
                $"linear-gradient({expected}, ",
                fixture.Computed([$"bg-linear-{direction}"], "background-image"),
                StringComparison.Ordinal
            );
        }
    }

    /// <summary>A fragment set on a media or state variant reaches the assembler through the cascade.</summary>
    /// <remarks>
    ///     The variant argument once more with an at-rule instead of a pseudo-class, because the two
    ///     travel by different routes through <c>UtilityGenerator.BuildSelector</c> — one becomes a
    ///     selector suffix and the other an <c>@media</c> wrapper — and a mechanism that worked for
    ///     only one of them would look right in the test above.
    /// </remarks>
    [Fact]
    public void A_media_variant_reaches_the_assembler_too() {
        var fixture = new UtilityFixture();
        string[] classes = ["from-accent", "md:from-muted", "bg-linear-to-r"];

        Assert.Equal(
            "linear-gradient(to right, #4f7cff 0%, transparent 100%)",
            fixture.Computed(classes, "background-image", media: new MediaContext(320, 640))
        );

        Assert.Equal(
            "linear-gradient(to right, #8a8a99 0%, transparent 100%)",
            fixture.Computed(classes, "background-image", media: new MediaContext(1024, 768))
        );
    }

    static int Occurrences(string text, string needle) {
        var count = 0;

        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }
}
