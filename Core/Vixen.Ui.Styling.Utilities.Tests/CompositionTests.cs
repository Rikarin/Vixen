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
        Assert.StartsWith("background-image: linear-gradient(to right in oklab, ", fixture.Emits("bg-linear-to-r")[0], StringComparison.Ordinal);

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
            "linear-gradient(to right in oklab, #4f7cff 0%, #8a8a99 50%, #1f1f26 100%)",
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

        Assert.Equal("linear-gradient(to right in oklab, #4f7cff 0%, #1f1f26 100%)", rest);
        Assert.Equal("linear-gradient(to right in oklab, #6a91ff 0%, #1f1f26 100%)", hover);

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
        "linear-gradient(to right in oklab, #4f7cff 0%, #1f1f26 100%)")]
    // No `to` either. Still a gradient, and the missing end is the initial value rather than a hole.
    [InlineData(new[] { "from-accent", "bg-linear-to-r" },
        "linear-gradient(to right in oklab, #4f7cff 0%, transparent 100%)")]
    // The assembler entirely alone. A two-stop transparent gradient is a no-op that draws nothing,
    // which is the right answer; a dropped declaration would have been indistinguishable from a typo.
    [InlineData(new[] { "bg-linear-to-r" },
        "linear-gradient(to right in oklab, transparent 0%, transparent 100%)")]
    // A position with no colour, which is the other half of the same family missing.
    [InlineData(new[] { "from-40%", "to-surface-3", "bg-linear-to-r" },
        "linear-gradient(to right in oklab, transparent 40%, #1f1f26 100%)")]
    // Every fragment written out, so that the defaults above are visibly defaults and not the only
    // thing the assembler can produce.
    [InlineData(new[] { "from-accent", "from-10%", "via-muted", "via-60%", "to-surface-3", "to-90%", "bg-linear-to-r" },
        "linear-gradient(to right in oklab, #4f7cff 10%, #8a8a99 60%, #1f1f26 90%)")]
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

    /// <summary>The leak <c>@property</c>'s <c>inherits: false</c> would stop, pinned as a divergence.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A fragment set on a box is visible to every descendant, so an assembler with no
    ///         fragments of its own silently paints its ancestor's.</b> That is <i>correct CSS</i> for
    ///         an unregistered custom property and it is a divergence from Tailwind, which registers
    ///         every <c>--tw-*</c> with <c>inherits: false</c> precisely to stop it. Vixen has no
    ///         <c>@property</c> and every custom property inherits — see
    ///         <see cref="Vixen.Ui.Styling.InheritedProperties.IsCustomProperty" />, which is the one
    ///         line that decides it.
    ///     </para>
    ///     <para>
    ///         <b>The divergence stands (2026-09-05), and this test is the reason it will not be
    ///         rediscovered as a bug.</b> It is not a defect to fix by hand at the assembler: an
    ///         assembler cannot tell an inherited fragment from a set one, because the cascade hands
    ///         it a computed value either way. Closing it means registration, which is
    ///         <c>docs/plan/43</c> § <i>The composition mechanism</i> and #291 — and the day
    ///         registration lands, this test goes red, which is what makes it the note's expiry
    ///         rather than a restatement of it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_fragment_set_on_a_box_is_visible_to_its_descendants() {
        var fixture = new UtilityFixture();

        // The child carries the assembler and its own `to-*`, and no `from-*` at all. The parent
        // carries `from-accent` and no assembler — so on the web, where the fragment is registered,
        // the child's first stop is the initial value and the parent's colour is unreachable.
        var leaked = fixture.Computed(
            ["bg-linear-to-r", "to-surface-3"],
            "background-image",
            extraCss: fixture.Generate("from-accent"),
            ancestor: new Probe(["from-accent"])
        );

        // Here it reaches, and nothing on the child asked for it.
        Assert.Equal("linear-gradient(to right in oklab, #4f7cff 0%, #1f1f26 100%)", leaked);

        // The control, which is also what the registered form would produce in both cases: the same
        // child under a parent setting nothing falls back to the fragment's initial value. Without
        // this half the assertion above would pass just as well against an engine that had never
        // inherited anything and simply resolved `from-accent` on the child.
        var contained = fixture.Computed(
            ["bg-linear-to-r", "to-surface-3"],
            "background-image",
            ancestor: new Probe([])
        );

        Assert.Equal("linear-gradient(to right in oklab, transparent 0%, #1f1f26 100%)", contained);
        Assert.NotEqual(leaked, contained);
    }

    /// <summary>The second thing registration would buy: a type, without which a gradient cannot animate.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A transitioned gradient does not fail — it <i>snaps</i>, at the halfway mark, and
    ///         that is the shape a reader has to expect.</b> A registered custom property carries a
    ///         syntax, which is what lets a browser interpolate one; Vixen has none, so an assembled
    ///         <c>background-image</c> reaches <see cref="StyleValue.Lerp" /> as a value with no
    ///         magnitude and takes CSS's discrete path. It is the same shape as the three transition
    ///         residuals, one layer down.
    ///     </para>
    ///     <para>
    ///         <b>This divergence stands too (2026-09-05).</b> Interpolating the assembled text would
    ///         be the wrong fix at the wrong layer: what wants interpolating is the <i>stop colour</i>
    ///         a fragment holds, and only a registered type can say that a fragment is a colour.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_gradient_is_a_discrete_swap_rather_than_an_interpolation() {
        var values = new NameTable();
        var parser = new StyleValueParser(values, new NameTable());

        var from = parser.Parse("linear-gradient(to right in oklab, #4f7cff 0%, #1f1f26 100%)");
        var to = parser.Parse("linear-gradient(to right in oklab, #6a91ff 0%, #1f1f26 100%)");

        // Neither end has a magnitude to interpolate, so no midpoint exists…
        Assert.False(StyleValue.CanInterpolate(from, to));

        // …and the value jumps at 0.5 rather than passing through anything between the two.
        Assert.Equal(from, StyleValue.Lerp(from, to, 0.49f));
        Assert.Equal(to, StyleValue.Lerp(from, to, 0.51f));

        // The contrast that makes the point: the *colour* either gradient is built from does
        // interpolate, and lands between the two. What is missing is a type saying the fragment
        // holding it is a colour — not any ability to blend one.
        var start = parser.Parse("#4f7cff");
        var end = parser.Parse("#6a91ff");

        Assert.True(StyleValue.CanInterpolate(start, end));
        Assert.NotEqual(start, StyleValue.Lerp(start, end, 0.5f));
        Assert.NotEqual(end, StyleValue.Lerp(start, end, 0.5f));
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
            Assert.Equal($"var({fragment}, {UtilityComposition.InitialValueOf(fragment)})", UtilityComposition.Reference(fragment));

            // ⚠ <b>The invariant is the <i>comma</i> and never a non-empty initial, and this row
            // used to assert the second.</b> What makes a reference safe is that a fallback exists
            // at all: `var(--x)` unset is invalid at computed-value time and takes the whole
            // declaration with it, and `var(--x,)` unset is legally the empty token stream. The
            // five `font-variant-numeric` slots are worth exactly that empty stream, because the
            // assembled value is a list whose *length* varies — the first fragments in this table
            // with no identity to fall back to. Asserting non-empty would have refused them for
            // being correct.
            Assert.Contains(',', UtilityComposition.Reference(fragment));
        }

        // ⚠ <b>The unprefixed `--` placeholders, which are *not* fragments and must not be mistaken
        // for them.</b> This list used to name `--rotate` and `--translate-x` in the same breath, and
        // the two have parted company: `--translate-x` was a placeholder and is a fragment now, under
        // the prefix, assembled into `translate`. `--blur` is the shape all five used to have — a
        // value parked in a name no engine anywhere reads, which is strictly worse than an unread CSS
        // property because no reader could ever have closed it. #28 is where it gets an assembler.
        Assert.False(UtilityComposition.IsFragment("--translate-x"));
        Assert.False(UtilityComposition.IsFragment("--blur"));
        Assert.False(UtilityComposition.IsFragment("--tw-not-registered"));
        Assert.Throws<ArgumentException>(() => UtilityComposition.InitialValueOf("--tw-not-registered"));
    }

    /// <summary>
    ///     ⚠ <b>The translation is composed out of two fragments into <i>one</i> property, which the
    ///     gradient stops are not, and both families are assemblers.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>from-*</c> sets a fragment and emits no declaration; something else — <c>bg-linear-*</c>
    ///         — has to be on the element for anything to happen. That is right for a gradient, where
    ///         the assembler is the thing you are turning on, and wrong for a translation: Tailwind v3
    ///         needed a separate <c>transform</c> class exactly this way, and dropped it in v4 because
    ///         it was forgotten constantly and its absence was indistinguishable from the utility being
    ///         broken. So both axes emit the same assembly beside their own fragment, and
    ///         <c>translate-x-2</c> alone works.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The initial values are what make one axis alone legal.</b> A bare
    ///         <c>translate: var(--tw-translate-x) var(--tw-translate-y)</c> on an element carrying only
    ///         <c>translate-x-2</c> is invalid at computed-value time and the whole declaration is
    ///         dropped — the element would not move at all, in the case that is by far the commonest.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_two_translate_axes_assemble_into_one_property() {
        var fixture = new UtilityFixture();
        var assembly = $"translate: {UtilityComposition.Translation()}";

        Assert.True(UtilityComposition.IsFragment(UtilityComposition.TranslateX));
        Assert.True(UtilityComposition.IsFragment(UtilityComposition.TranslateY));

        // Each axis sets its own fragment and emits the same assembly beside it.
        Assert.Equal(["--tw-translate-x: 8px", assembly], fixture.Emits("translate-x-2"));
        Assert.Equal(["--tw-translate-y: 8px", assembly], fixture.Emits("translate-y-2"));

        // ⚠ The negation reaches the fragment and leaves the assembly alone, which is only true
        // because `TryResolve` appends `Alongside` *after* negating. Appended first, `TryNegate`
        // would meet a value beginning with `v`, refuse the whole utility, and `-translate-x-2` would
        // be reported as an unrecognised class rather than as minus eight pixels.
        Assert.Equal(["--tw-translate-x: -8px", assembly], fixture.Emits("-translate-x-2"));

        // A percentage, which is the form that needs the element's own box — `-translate-x-full` is
        // the drawer idiom, and `Size` rather than `Spacing` is what admits it.
        Assert.Equal(["--tw-translate-x: 100%", assembly], fixture.Emits("translate-x-full"));

        // ⚠ The ROOT writes both slots and assembles the same property, which is what makes
        // `translate-4 translate-x-8` a cascade question rather than a fight: the two spellings of
        // one movement write the same two slots and the later declaration wins each of them. A root
        // registered against a `translate` of its own would have had two assemblies overwriting each
        // other instead.
        Assert.Equal(["--tw-translate-x: 8px", "--tw-translate-y: 8px", assembly], fixture.Emits("translate-2"));
        Assert.Equal(["--tw-translate-x: 100%", "--tw-translate-y: 100%", assembly], fixture.Emits("translate-full"));
        Assert.Equal(["--tw-translate-x: -8px", "--tw-translate-y: -8px", assembly], fixture.Emits("-translate-2"));
    }

    /// <summary><c>translate-none</c> turns the movement off, and the assembly does not undo it.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the one value on the root that could not be a keyword on the root's family,
    ///     and the reason is <see cref="UtilityFamilies.TryResolve" />'s order.</b> <c>Alongside</c>
    ///     is appended on every resolution, so a <c>none</c> in the family's keyword table would emit
    ///     <c>translate: none</c> and then <c>translate: var(--tw-translate-x) var(--tw-translate-y)</c>
    ///     over the top of it — the later declaration wins, and a class spelling "do not move" would
    ///     move. It is registered under its own name instead, which <c>SplitName</c>'s longest-prefix
    ///     rule keeps apart from the root.
    ///     <para>
    ///         ⚠ And the value is read BY NAME rather than by falling out of a parse:
    ///         <c>TranslationReader.Of</c> compares the interned value against <c>none</c> before it
    ///         parses anything, which is what makes this a registration against a consumer rather
    ///         than a declaration nothing acts on.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Turning_the_translation_off_does_not_go_through_the_assembly() {
        var fixture = new UtilityFixture();

        Assert.Equal(["translate: none"], fixture.Emits("translate-none"));

        // The axes still assemble, so the two spellings have not been collapsed into one family.
        Assert.Equal(
            ["--tw-translate-x: 8px", $"translate: {UtilityComposition.Translation()}"],
            fixture.Emits("translate-x-2")
        );
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
                $"linear-gradient({expected} in oklab, ",
                fixture.Computed([$"bg-linear-{direction}"], "background-image"),
                StringComparison.Ordinal
            );
        }

        // ⚠ <b>And the two round assemblers, which take no geometry at all.</b> That is Tailwind's
        // own shape rather than a simplification: `bg-radial` is `radial-gradient(in oklab, …)`,
        // because CSS's defaults are a centred farthest-corner ellipse and a sweep from twelve
        // o'clock, and both are functions of the box the shader already has.
        foreach (var (name, function) in new[] { ("bg-radial", "radial"), ("bg-conic", "conic") }) {
            Assert.StartsWith(
                $"{function}-gradient(in oklab, ",
                fixture.Computed([name], "background-image"),
                StringComparison.Ordinal
            );
        }
    }

    /// <summary>The numeric form of the two assemblers that take a direction.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Zero is the value this needed a new kind for, and it is not a corner case — it is
    ///         the one an author writes first.</b> <c>bg-conic-0</c> is a sweep starting at twelve
    ///         o'clock and <c>bg-linear-0</c> is a ramp running upwards; both are real Tailwind
    ///         classes and both were reported as unrecognised typos while these families shared
    ///         <c>TryCount</c>, which floors at one because <c>grid-cols-0</c> is not a track list.
    ///         Asserting the zero row alone would have passed against the old code as an unresolved
    ///         class only if the assertion were `Null` — so the row that proves the kind is this one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The angle rides the axis lane the parser and the shader already had.</b>
    ///         <c>GradientReader.ReadPrelude</c> has read <c>from &lt;angle&gt;</c> since conic
    ///         gradients landed and the box shader recovers it with <c>atan2(x, -y)</c>, so nothing
    ///         under this table moved for these four rows — which is exactly what the issue said and
    ///         is worth having pinned, because the next reader will assume a shader change.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("bg-linear-0", "linear-gradient(0deg in oklab, ")]
    [InlineData("bg-linear-45", "linear-gradient(45deg in oklab, ")]
    [InlineData("bg-linear-180", "linear-gradient(180deg in oklab, ")]
    [InlineData("bg-conic-0", "conic-gradient(from 0deg in oklab, ")]
    [InlineData("bg-conic-45", "conic-gradient(from 45deg in oklab, ")]
    // ⚠ The hatch goes *through* the template rather than around it: an angle family's declaration is
    // a whole gradient function and the value is one word inside it, so the ordinary arbitrary path —
    // emit the value as the declaration — would produce `background-image: 3rad` and the cascade
    // would drop the element's whole stop list with it.
    [InlineData("bg-conic-[3rad]", "conic-gradient(from 3rad in oklab, ")]
    [InlineData("bg-linear-[0.25turn]", "linear-gradient(0.25turn in oklab, ")]
    public void An_angle_assembler_writes_the_angle_into_the_gradient(string utility, string expected) {
        var fixture = new UtilityFixture();

        Assert.StartsWith(expected, fixture.Computed([utility], "background-image"), StringComparison.Ordinal);
    }

    /// <summary>The bare <c>bg</c> root's two other readings: v3's assembler spelling, and the opt-out.</summary>
    /// <remarks>
    ///     ⚠ <b>Both hang off <c>bg</c> and not off <c>bg-linear</c>, which is Tailwind's own
    ///     arrangement</b> — v4 keeps <c>bg-gradient-to-*</c> in <c>compat/legacy-utilities.ts</c>,
    ///     aliased to what <c>bg-linear-to-*</c> emits. Registering them as a second keyword table on
    ///     an existing <see cref="ValueKind.Color" /> family is what <c>Register</c>'s merge is for,
    ///     and <c>bg-accent</c> still resolves as a colour because the keyword table is consulted
    ///     first and holds no colour name.
    /// </remarks>
    [Fact]
    public void The_bare_bg_root_carries_the_legacy_assemblers_and_the_opt_out() {
        var fixture = new UtilityFixture();

        Assert.Equal(["background-image: none"], fixture.Emits("bg-none"));

        foreach (var (suffix, expected) in new[] {
                     ("t", "to top"), ("tr", "to top right"), ("r", "to right"), ("br", "to bottom right"),
                     ("b", "to bottom"), ("bl", "to bottom left"), ("l", "to left"), ("tl", "to top left")
                 }) {
            Assert.Equal(
                fixture.Computed([$"bg-linear-to-{suffix}"], "background-image"),
                fixture.Computed([$"bg-gradient-to-{suffix}"], "background-image")
            );

            Assert.StartsWith(
                $"linear-gradient({expected} in oklab, ",
                fixture.Computed([$"bg-gradient-to-{suffix}"], "background-image"),
                StringComparison.Ordinal
            );
        }

        // The colour reading of the same prefix is untouched by the keyword table beside it.
        Assert.Equal(["background-color: #4f7cff"], fixture.Emits("bg-accent"));
    }

    /// <summary>Every gradient this generator emits names its interpolation space.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Tailwind v4's own behaviour, and the difference is not subtle.</b> CSS's default
    ///         for an unhinted gradient is sRGB; the engine's palette now ships as v4.3.3's, quoted in
    ///         <c>oklch</c> and chosen so that equal numeric steps look like equal perceptual steps.
    ///         Interpolating two of those swatches anywhere but a perceptual space throws that away at
    ///         the midpoint — which between complements is the difference between a colour and a grey
    ///         dead zone, and is the only pixel where the choice is visible.
    ///     </para>
    ///     <para>
    ///         ⚠ Asserted across every assembler rather than on one, because the hint lives in a
    ///         format string per family and an eleventh added without it would be the one gradient in
    ///         the interface that fades differently — which reads as a palette problem.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_assembler_asks_for_oklab() {
        var fixture = new UtilityFixture();

        string[] assemblers = [
            "bg-linear-to-t", "bg-linear-to-tr", "bg-linear-to-r", "bg-linear-to-br",
            "bg-linear-to-b", "bg-linear-to-bl", "bg-linear-to-l", "bg-linear-to-tl",
            "bg-radial", "bg-conic"
        ];

        foreach (var assembler in assemblers) {
            Assert.Contains(
                "in oklab,",
                fixture.Computed([assembler], "background-image"),
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
            "linear-gradient(to right in oklab, #4f7cff 0%, transparent 100%)",
            fixture.Computed(classes, "background-image", media: new MediaContext(320, 640))
        );

        Assert.Equal(
            "linear-gradient(to right in oklab, #8a8a99 0%, transparent 100%)",
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
