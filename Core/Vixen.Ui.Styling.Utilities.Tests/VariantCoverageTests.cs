// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>Every variant, proved against the cascade rather than against the text it emits.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This file exists because a whole variant family was inert and the suite was green.</b>
///         Nothing in the engine ever constructed a <see cref="MediaContext" />, so every stylesheet
///         was judged against a nought-by-nought surface and every <c>@media (min-width: …)</c> was
///         false at every window size — <c>sm:</c>, <c>md:</c>, <c>lg:</c>, <c>xl:</c>, <c>2xl:</c>
///         and <c>dark:</c> under the media strategy, all of them, always. The tests that existed
///         asserted on the <i>generated text</i>, and generated text is exactly what a dead variant
///         still produces correctly.
///     </para>
///     <para>
///         So the rule here is one assertion shape and no other: build a document, put the element in
///         the condition the variant names, resolve, and read a computed property back — and assert
///         the <i>negative</i> case too, because a rule that applies unconditionally passes every
///         positive assertion in this file. <see cref="UtilityGenerationTests" /> keeps the text-level
///         checks that are about spelling; nothing here is allowed to be one.
///     </para>
///     <para>
///         ⚠ <b>And it is enumerated, not listed.</b>
///         <see cref="The_state_variant_table_has_no_untested_entry" /> and
///         <see cref="The_themes_breakpoints_have_no_untested_entry" /> walk the engine's own tables,
///         so a variant added without a row here fails the build rather than joining the silent ones.
///         That gate is what would have caught <c>2xl:</c>, which emitted an invalid selector into
///         every project using the shipped theme.
///     </para>
/// </remarks>
public class VariantCoverageTests {
    /// <summary>The scene each state variant needs in order to match, and one in which it must not.</summary>
    /// <remarks>
    ///     The negative column is the half that catches a variant compiling to something broader than
    ///     it should. <c>:nth-child(2n)</c> silently compiled to <c>:first-child</c> would pass every
    ///     positive row and fail <c>even</c>'s negative one.
    /// </remarks>
    /// <summary>One row: a variant, a scene, and whether the variant should reach the element in it.</summary>
    /// <param name="Variant">The variant, without its colon.</param>
    /// <param name="State">The element's own pseudo state.</param>
    /// <param name="Before">How many siblings precede it.</param>
    /// <param name="After">How many follow it.</param>
    /// <param name="Matches">Whether the utility should apply.</param>
    /// <param name="Tag">The element's own tag.</param>
    /// <param name="FillerTag">The tag its filler siblings carry.</param>
    /// <param name="Children">How many children it has.</param>
    /// <remarks>
    ///     ⚠ <b>The last three exist because a scene made of identical siblings cannot fail an
    ///     of-type test.</b> <c>:nth-of-type(2)</c> and <c>:nth-child(2)</c> pick the same element
    ///     out of any run of <c>div</c>s, so an of-type row whose fillers were <c>div</c>s like the
    ///     element would pass whichever of the two the compiler had actually produced — the exact
    ///     shape of green this file was written to stop. Every of-type row below therefore differs
    ///     from its child-test twin in the filler tag and in nothing else.
    /// </remarks>
    public sealed record Scene(
        string Variant,
        ElementState State,
        int Before,
        int After,
        bool Matches,
        string Tag = "div",
        string FillerTag = "div",
        int Children = 0
    );

    /// <summary>The scenes, as data the completeness gate can also read.</summary>
    /// <remarks>
    ///     Kept beside <see cref="StateScenes" /> rather than inside it because
    ///     <c>TheoryDataRow</c> does not hand its values back — and the gate's whole job is to read
    ///     which variants appear here.
    /// </remarks>
    static readonly Scene[] Scenes = BuildScenes();

    public static TheoryData<string, ElementState, int, int, bool, string, string, int> StateScenes {
        get {
            // Primitives only. xunit serialises theory rows so it can run one of them on its own, and
            // a `Probe[]` in the row would collapse the whole theory into a single opaque case.
            var data = new TheoryData<string, ElementState, int, int, bool, string, string, int>();

            foreach (var scene in Scenes) {
                data.Add(
                    scene.Variant,
                    scene.State,
                    scene.Before,
                    scene.After,
                    scene.Matches,
                    scene.Tag,
                    scene.FillerTag,
                    scene.Children
                );
            }

            return data;
        }
    }

    static Scene[] BuildScenes() {
        var scenes = new List<Scene>();

        // variant, own state, siblings before, siblings after, should match
        void Row(
            string variant,
            ElementState state,
            int before,
            int after,
            bool matches,
            string tag = "div",
            string fillerTag = "div",
            int children = 0
        ) =>
            scenes.Add(new Scene(variant, state, before, after, matches, tag, fillerTag, children));

        foreach (var (variant, on) in new[] {
                     ("hover", ElementState.Hover),
                     ("focus", ElementState.Focus),
                     ("focus-visible", ElementState.FocusVisible),
                     ("focus-within", ElementState.FocusWithin),
                     ("active", ElementState.Active),
                     ("disabled", ElementState.Disabled),
                     ("checked", ElementState.Checked)
                 }) {
            Row(variant, on, 0, 0, true);
            Row(variant, ElementState.None, 0, 0, false);
        }

        // ⚠ `:enabled` is the *absence* of `:disabled`, which is what CSS means by it — so its two
        // rows are the other way round from every row above, and a variant that compiled to a state
        // of its own rather than a negation would fail exactly this pair.
        Row("enabled", ElementState.None, 0, 0, true);
        Row("enabled", ElementState.Disabled, 0, 0, false);

        Row("first", ElementState.None, 0, 1, true);
        Row("first", ElementState.None, 1, 0, false);
        Row("last", ElementState.None, 1, 0, true);
        Row("last", ElementState.None, 0, 1, false);
        Row("only", ElementState.None, 0, 0, true);
        Row("only", ElementState.None, 0, 1, false);

        // Position is one-based, so an element with no preceding sibling is child 1 — odd.
        Row("odd", ElementState.None, 0, 1, true);
        Row("odd", ElementState.None, 1, 0, false);
        Row("even", ElementState.None, 1, 0, true);
        Row("even", ElementState.None, 0, 1, false);

        // ⚠ `:empty` counts text as content, so its negative has to be a child rather than a word —
        // the fixture has no way to hang text on a probe, and a scene that put one there would be
        // testing `StyleTree.SetHasText` instead. `SelectorMatchingTests` owns the text half.
        Row("empty", ElementState.None, 0, 0, true);
        Row("empty", ElementState.None, 0, 0, false, children: 1);

        // The of-type family. Every row is its child-test twin with the filler tags changed, so a
        // compiler that resolved `:first-of-type` to `:first-child` fails the positive rows here
        // while passing every row above.
        Row("first-of-type", ElementState.None, 1, 0, true, tag: "p", fillerTag: "div");
        Row("first-of-type", ElementState.None, 1, 0, false, tag: "p", fillerTag: "p");
        Row("last-of-type", ElementState.None, 0, 1, true, tag: "p", fillerTag: "div");
        Row("last-of-type", ElementState.None, 0, 1, false, tag: "p", fillerTag: "p");
        Row("only-of-type", ElementState.None, 1, 1, true, tag: "p", fillerTag: "div");
        Row("only-of-type", ElementState.None, 1, 0, false, tag: "p", fillerTag: "p");

        return [.. scenes];
    }

    [Theory]
    [MemberData(nameof(StateScenes))]
    public void A_state_variant_changes_what_the_element_computes(
        string variant,
        ElementState state,
        int before,
        int after,
        bool matches,
        string tag,
        string fillerTag,
        int children
    ) {
        var fixture = new UtilityFixture();

        // A parent is forced for every row, not only the structural ones, so that the positive and
        // the negative scene differ in the one thing the row is about.
        var value = fixture.Computed(
            [$"{variant}:p-4"],
            "padding-left",
            state: state,
            ancestor: new Probe([]),
            before: Filler(before, fillerTag),
            after: Filler(after, fillerTag),
            tag: tag,
            children: Filler(children, "div")
        );

        Assert.Equal(matches ? "16px" : null, value);
    }

    static Probe[] Filler(int count, string tag = "div") =>
        [.. Enumerable.Range(0, count).Select(_ => new Probe([], Tag: tag))];

    [Fact]
    public void The_state_variant_table_has_no_untested_entry() {
        // The gate. An entry added to `Variants.States` without a scene above lands here rather than
        // in the silent majority — which is where eleven of the first thirteen were. No count is
        // named: the table grows, and a number in this comment would be the copy nothing checks.
        var tested = Scenes.Select(scene => scene.Variant).ToHashSet(StringComparer.Ordinal);
        var untested = Variants.StateVariants.Where(variant => !tested.Contains(variant)).ToArray();

        Assert.True(
            untested.Length == 0,
            $"these state variants have no end-to-end scene in {nameof(Scenes)}: {string.Join(", ", untested)}"
        );

        // ⚠ Both ways round. A scene for a variant the table no longer has would otherwise sit here
        // passing for ever, which is how a coverage gate rots into decoration.
        var stale = tested.Where(variant => !Variants.StateVariants.Contains(variant)).ToArray();

        Assert.True(stale.Length == 0, $"these scenes name a variant that no longer exists: {string.Join(", ", stale)}");
    }

    /// <summary>The surfaces the media variants are judged against, by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Named rather than inlined, because a <see cref="MediaContext" /> in a theory row is
    ///     not a primitive and xunit would collapse the whole theory into one opaque case</b> — the
    ///     same constraint <see cref="StateScenes" /> is built around. A name also makes the failure
    ///     readable: <c>("motion-reduce", "reduced-motion", True)</c> says what was asked and of
    ///     what.
    /// </remarks>
    static readonly Dictionary<string, MediaContext> Surfaces = new(StringComparer.Ordinal) {
        // Landscape, a mouse, and every preference where a platform that has said nothing leaves it.
        ["desktop"] = new(1280, 720),
        ["portrait"] = new(720, 1280),
        ["reduced-motion"] = new(1280, 720) { Preferences = new(Motion: MotionPreference.Reduce) },
        ["more-contrast"] = new(1280, 720) { Preferences = new(Contrast: ContrastPreference.More) },
        ["less-contrast"] = new(1280, 720) { Preferences = new(Contrast: ContrastPreference.Less) },

        // ⚠ `custom` is neither more nor less, and it is here to prove that `contrast-more:` does not
        // read as "any stated contrast preference".
        ["custom-contrast"] = new(1280, 720) { Preferences = new(Contrast: ContrastPreference.Custom) },
        ["forced-colors"] = new(1280, 720) { Preferences = new(ForcedColors: true) },
        ["inverted"] = new(1280, 720) { Preferences = new(InvertedColors: true) },
        ["touch"] = new(1280, 720) {
            Preferences = new(Pointer: PointerCapability.Coarse, AnyPointer: PointerCapability.Coarse)
        },

        // ⚠ The row that tells `pointer-*` from `any-pointer-*`. A tablet with a stylus is coarse
        // *primarily* and fine as well, so `pointer-fine:` must be off here and `any-pointer-fine:`
        // must be on — and a table that resolved both families to the same feature passes every
        // other scene in this file.
        ["touch-and-stylus"] = new(1280, 720) {
            Preferences = new(
                Pointer: PointerCapability.Coarse,
                AnyPointer: PointerCapability.Coarse | PointerCapability.Fine
            )
        },
        ["no-pointer"] = new(1280, 720) {
            Preferences = new(Pointer: PointerCapability.NoDevice, AnyPointer: PointerCapability.NoDevice)
        }
    };

    static readonly (string Variant, string Surface, bool Matches)[] MediaRows = [
        ("motion-safe", "desktop", true),
        ("motion-safe", "reduced-motion", false),
        ("motion-reduce", "reduced-motion", true),
        ("motion-reduce", "desktop", false),

        ("contrast-more", "more-contrast", true),
        ("contrast-more", "desktop", false),
        ("contrast-more", "custom-contrast", false),
        ("contrast-less", "less-contrast", true),
        ("contrast-less", "desktop", false),
        ("contrast-less", "more-contrast", false),

        ("forced-colors", "forced-colors", true),
        ("forced-colors", "desktop", false),
        ("inverted-colors", "inverted", true),
        ("inverted-colors", "desktop", false),

        ("portrait", "portrait", true),
        ("portrait", "desktop", false),
        ("landscape", "desktop", true),
        ("landscape", "portrait", false),

        // ⚠ Two rows and both negative, which is the whole of what these two variants can be asked.
        // Paged media is out of scope for good and a Vixen document always scripts, so each is a
        // condition that resolves and never holds — and the assertion that matters is the one in
        // `Every_media_variant_generates_a_rule` below: the class is a class, so it is not a typo,
        // and it applies nowhere.
        ("print", "desktop", false),
        ("print", "portrait", false),
        ("noscript", "desktop", false),
        ("noscript", "touch", false),

        ("pointer-fine", "desktop", true),
        ("pointer-fine", "touch", false),
        ("pointer-fine", "touch-and-stylus", false),
        ("pointer-coarse", "touch", true),
        ("pointer-coarse", "desktop", false),
        ("pointer-coarse", "no-pointer", false),
        ("pointer-none", "no-pointer", true),
        ("pointer-none", "desktop", false),
        ("pointer-none", "touch", false),

        ("any-pointer-fine", "desktop", true),
        ("any-pointer-fine", "touch-and-stylus", true),
        ("any-pointer-fine", "touch", false),
        ("any-pointer-coarse", "touch", true),
        ("any-pointer-coarse", "touch-and-stylus", true),
        ("any-pointer-coarse", "desktop", false),
        ("any-pointer-none", "no-pointer", true),
        ("any-pointer-none", "desktop", false),
        ("any-pointer-none", "touch-and-stylus", false)
    ];

    public static TheoryData<string, string, bool> MediaScenes {
        get {
            var data = new TheoryData<string, string, bool>();

            foreach (var (variant, surface, matches) in MediaRows) {
                data.Add(variant, surface, matches);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(MediaScenes))]
    public void A_media_variant_changes_what_the_element_computes(string variant, string surface, bool matches) {
        var fixture = new UtilityFixture();
        var value = fixture.Computed([$"{variant}:p-4"], "padding-left", media: Surfaces[surface]);

        Assert.Equal(matches ? "16px" : null, value);
    }

    [Fact]
    public void The_media_variant_table_has_no_untested_entry() {
        // The same gate `The_state_variant_table_has_no_untested_entry` is, over the other table.
        var tested = MediaRows.Select(row => row.Variant).ToHashSet(StringComparer.Ordinal);
        var untested = Variants.MediaVariants.Where(variant => !tested.Contains(variant)).ToArray();

        Assert.True(
            untested.Length == 0,
            $"these media variants have no end-to-end scene: {string.Join(", ", untested)}"
        );

        var stale = tested.Where(variant => !Variants.MediaVariants.Contains(variant)).ToArray();

        Assert.True(stale.Length == 0, $"these scenes name a variant that no longer exists: {string.Join(", ", stale)}");

        // ⚠ And a scene of each sign, which is the assertion that stops a variant from passing on a
        // negative row alone — a table entry that emitted an at-rule nothing can satisfy would do
        // exactly that. The two exceptions are named rather than inferred: `print` and `noscript`
        // *cannot* have a positive scene, and a gate that worked that out for itself would stop
        // noticing the day a third one arrived by accident.
        string[] neverMatch = ["print", "noscript"];

        foreach (var variant in Variants.MediaVariants) {
            var signs = MediaRows.Where(row => row.Variant == variant).Select(row => row.Matches).ToArray();

            Assert.Contains(false, signs);

            if (!neverMatch.Contains(variant, StringComparer.Ordinal)) {
                Assert.Contains(true, signs);
            } else {
                Assert.DoesNotContain(true, signs);
            }
        }
    }

    [Fact]
    public void Every_media_variant_generates_a_rule_even_when_it_can_never_match() {
        // ⚠ What tells "a variant that is always false" from "not a variant at all", which is
        // exactly the pair `print:` and `noscript:` sit between. Their whole justification is that a
        // stylesheet shared with a web codebase loads unchanged; a class that silently vanished
        // would be that stylesheet failing quietly instead of loudly.
        var fixture = new UtilityFixture();

        foreach (var variant in Variants.MediaVariants) {
            Assert.Contains("padding", fixture.Generate($"{variant}:p-4"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_nth_variants_count_children_and_their_of_type_pair_counts_a_tag() {
        // ⚠ Four families that differ only in which sequence they index, so every row here is
        // matched by a row that must NOT apply — and the pairs are chosen so that resolving any one
        // family to any other fails at least one of them. `nth-2` and `nth-of-type-2` pick different
        // elements only once the siblings carry different tags, which is why the of-type rows mix
        // `p` and `div` and the child rows do not.
        var fixture = new UtilityFixture();

        string? Padding(string variant, string tag, string[] before, string[] after) =>
            fixture.Computed(
                [$"{variant}:p-4"],
                "padding-left",
                ancestor: new Probe([]),
                before: [.. before.Select(t => new Probe([], Tag: t))],
                after: [.. after.Select(t => new Probe([], Tag: t))],
                tag: tag
            );

        // Third child of five.
        Assert.Equal("16px", Padding("nth-3", "div", ["div", "div"], ["div", "div"]));
        Assert.Null(Padding("nth-4", "div", ["div", "div"], ["div", "div"]));

        // ⚠ Counted from the end, which is the whole of `nth-last-*`. The same element is child 3
        // and last-child 3 here on purpose — five siblings — so the pair below is what tells the two
        // families apart rather than the pair above.
        Assert.Equal("16px", Padding("nth-last-2", "div", ["div", "div", "div"], ["div"]));
        Assert.Null(Padding("nth-2", "div", ["div", "div", "div"], ["div"]));

        // Second `p` among `div p div p div`: child 4, of-type 2.
        Assert.Equal("16px", Padding("nth-of-type-2", "p", ["div", "p", "div"], ["div"]));
        Assert.Null(Padding("nth-2", "p", ["div", "p", "div"], ["div"]));
        Assert.Equal("16px", Padding("nth-4", "p", ["div", "p", "div"], ["div"]));

        // The same element counted from the end: of-type 1, child 2.
        Assert.Equal("16px", Padding("nth-last-of-type-1", "p", ["div", "p", "div"], ["div"]));
        Assert.Null(Padding("nth-last-of-type-2", "p", ["div", "p", "div"], ["div"]));
        Assert.Equal("16px", Padding("nth-last-2", "p", ["div", "p", "div"], ["div"]));

        // The arbitrary form carries a whole `an+b`, and the underscore is a space as it is
        // everywhere else a variant takes one.
        Assert.Equal("16px", Padding("nth-[2n+1]", "div", ["div", "div"], ["div"]));
        Assert.Null(Padding("nth-[2n]", "div", ["div", "div"], ["div"]));

        // ⚠ An argument that is not a positive integer is not a variant at all, so the class is
        // never generated — rather than being generated into a selector the compiler then refuses.
        Assert.Null(Padding("nth-two", "div", [], []));
        Assert.Null(Padding("nth-2n", "div", ["div"], []));
    }

    [Fact]
    public void The_has_variant_asks_about_the_subtree_and_refuses_a_relative_argument() {
        var fixture = new UtilityFixture();

        // Composed over the state table, so `has-checked:` is `:has(:checked)` and reads the same
        // entries `group-*` and `peer-*` do.
        Assert.Equal(
            "16px",
            fixture.Computed(["has-checked:p-4"], "padding-left", children: [new Probe([], ElementState.Checked)])
        );

        Assert.Null(fixture.Computed(["has-checked:p-4"], "padding-left", children: [new Probe([])]));

        // ⚠ The subtree and not the element. A `has-*` that dropped its `:has()` would style the
        // element from its own state and pass the positive row above, so the row that matters is
        // this one: the element is checked and has no checked descendant.
        Assert.Null(fixture.Computed(["has-checked:p-4"], "padding-left", state: ElementState.Checked));

        // The arbitrary form, which is what carries a class rather than a state.
        Assert.Equal(
            "16px",
            fixture.Computed(["has-[.error]:p-4"], "padding-left", children: [new Probe(["error"])])
        );

        Assert.Null(fixture.Computed(["has-[.error]:p-4"], "padding-left", children: [new Probe(["fine"])]));

        // ⚠ And the refusal that has to happen here rather than in the compiler. `has-[>_.error]` is
        // v4's child form, and ExCSS 4.3.2 parses `:has(> .error)` into the same node it parses
        // `:has(.error)` into — the combinator is gone before any Vixen code sees it, so a rule that
        // reached the compiler would silently mean "any descendant". This is the last place the text
        // is intact, so this is where it is refused.
        Assert.DoesNotContain("padding", fixture.Generate("has-[>_.error]:p-4"), StringComparison.Ordinal);
        Assert.DoesNotContain("padding", fixture.Generate("has-sm:p-4"), StringComparison.Ordinal);
        Assert.DoesNotContain("padding", fixture.Generate("has-nothing:p-4"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_not_variant_negates_the_variant_it_wraps_and_refuses_the_ones_it_cannot() {
        var fixture = new UtilityFixture();

        // The state table read through a negation. Both halves, because a `not-*` that dropped the
        // `:not()` and emitted the bare state would pass neither — and one that emitted nothing at
        // all would pass the first and fail the second.
        Assert.Equal("16px", fixture.Computed(["not-hover:p-4"], "padding-left"));
        Assert.Null(fixture.Computed(["not-hover:p-4"], "padding-left", state: ElementState.Hover));

        // Over a structural entry too, since `not-*` reads the same table `group-*` and `peer-*` do.
        Assert.Equal(
            "16px",
            fixture.Computed(["not-first:p-4"], "padding-left", ancestor: new Probe([]), before: Filler(1))
        );

        Assert.Null(
            fixture.Computed(["not-first:p-4"], "padding-left", ancestor: new Probe([]), after: Filler(1))
        );

        // ⚠ And the refusals, which are the half that says `not-*` is not a blanket prefix.
        // `not-sm:` is an at-rule in v4, `not-group-hover:` an ancestor, `not-[&>*]:` an arbitrary
        // selector with a `&` that has nowhere to land — all three are *not variants*, so the class
        // never reaches the stylesheet. A `not-` that wrapped them anyway would emit
        // `:not(@media …)`, and CSS has no way to say that is wrong.
        foreach (var candidate in new[] { "not-sm:p-4", "not-group-hover:p-4", "not-[&>*]:p-4", "not-nothing:p-4" }) {
            Assert.DoesNotContain("padding", fixture.Generate(candidate), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Group_and_peer_compose_over_the_same_table() {
        // `group-*` and `peer-*` are the state table read through an ancestor and through a preceding
        // sibling. Neither had a test of any kind that put a second element in the document, and
        // `peer-*` had no test at all — its `~` combinator had never been matched through a utility.
        var fixture = new UtilityFixture();

        Assert.Equal(
            "16px",
            fixture.Computed(
                ["group-hover:p-4"],
                "padding-left",
                ancestor: new Probe(["group"], ElementState.Hover)
            )
        );

        Assert.Null(
            fixture.Computed(["group-hover:p-4"], "padding-left", ancestor: new Probe(["group"]))
        );

        // ⚠ And the ancestor must be the one carrying `.group`. A selector that had lost its
        // descendant combinator would style the element from its *own* hover and pass the row above.
        Assert.Null(
            fixture.Computed(["group-hover:p-4"], "padding-left", state: ElementState.Hover, ancestor: new Probe([]))
        );

        // ⚠ <b>A sibling between the peer and the element, deliberately.</b> `peer-*` compiles to
        // `~`, the subsequent-sibling combinator, and with the peer *adjacent* this assertion passes
        // just as well against `+` — which is how the first version of this test let a `~` → `+`
        // sabotage through. The filler is what tells the two combinators apart.
        Assert.Equal(
            "16px",
            fixture.Computed(
                ["peer-checked:p-4"],
                "padding-left",
                ancestor: new Probe([]),
                before: [new Probe(["peer"], ElementState.Checked), new Probe([])]
            )
        );

        Assert.Null(
            fixture.Computed(
                ["peer-checked:p-4"],
                "padding-left",
                ancestor: new Probe([]),
                before: [new Probe(["peer"]), new Probe([])]
            )
        );

        // ⚠ A *following* sibling must not count. `~` is the subsequent-sibling combinator, so a
        // peer after the element is not a peer of it, and this is the row that tells `~` from a
        // sibling test that ignores order.
        Assert.Null(
            fixture.Computed(
                ["peer-checked:p-4"],
                "padding-left",
                ancestor: new Probe([]),
                after: [new Probe(["peer"], ElementState.Checked)]
            )
        );
    }

    [Fact]
    public void A_data_variant_reads_the_attribute_it_names() {
        var fixture = new UtilityFixture();

        // The presence form.
        Assert.Equal(
            "16px",
            fixture.Computed(["data-open:p-4"], "padding-left", attributes: [("data-open", "")])
        );

        Assert.Null(fixture.Computed(["data-open:p-4"], "padding-left"));

        // The equality form, and a value that is not the one asked for.
        Assert.Equal(
            "16px",
            fixture.Computed(["data-[state=open]:p-4"], "padding-left", attributes: [("data-state", "open")])
        );

        Assert.Null(
            fixture.Computed(["data-[state=open]:p-4"], "padding-left", attributes: [("data-state", "shut")])
        );
    }

    [Fact]
    public void An_aria_variant_reads_the_attribute_it_names() {
        // `aria-*` had no test of any kind — the string appeared once in `Variants` and nowhere else.
        var fixture = new UtilityFixture();

        Assert.Equal(
            "16px",
            fixture.Computed(["aria-expanded:p-4"], "padding-left", attributes: [("aria-expanded", "true")])
        );

        Assert.Null(fixture.Computed(["aria-expanded:p-4"], "padding-left"));

        // ⚠ <b>The row that found the bug, and the reason the two above could not.</b> An ARIA state
        // spells its false out — a collapsed disclosure carries `aria-expanded="false"` rather than
        // no attribute — so the *absent* negative above is not the negative that matters, and the
        // shorthand's original `[aria-expanded]` passed both assertions above while styling the
        // collapsed element exactly like the expanded one.
        Assert.Null(
            fixture.Computed(["aria-expanded:p-4"], "padding-left", attributes: [("aria-expanded", "false")])
        );

        Assert.Equal(
            "16px",
            fixture.Computed(["aria-[sort=ascending]:p-4"], "padding-left", attributes: [("aria-sort", "ascending")])
        );

        // The arbitrary form stays verbatim rather than picking up the shorthand's `="true"`, and a
        // value that is not the one asked for must not match — `data-`'s equality form has had this
        // row since it was written and `aria-`'s had not.
        Assert.Null(
            fixture.Computed(["aria-[sort=ascending]:p-4"], "padding-left", attributes: [("aria-sort", "descending")])
        );
    }

    [Fact]
    public void A_direction_variant_reads_dir_off_an_ancestor() {
        // ⚠ Nothing in the repository had ever set a `dir` attribute — not in a test, not in product
        // code — so `[dir=ltr]`/`[dir=rtl]` was a selector that had never been matched against
        // anything. The consequence the comment in `Variants` names is real and is asserted here: the
        // element cannot select on its *own* direction, only on an ancestor's.
        var fixture = new UtilityFixture();

        Assert.Equal(
            "16px",
            fixture.Computed(["rtl:p-4"], "padding-left", ancestor: new Probe([], Attributes: [("dir", "rtl")]))
        );

        Assert.Null(
            fixture.Computed(["rtl:p-4"], "padding-left", ancestor: new Probe([], Attributes: [("dir", "ltr")]))
        );

        Assert.Equal(
            "16px",
            fixture.Computed(["ltr:p-4"], "padding-left", ancestor: new Probe([], Attributes: [("dir", "ltr")]))
        );

        Assert.Null(fixture.Computed(["ltr:p-4"], "padding-left"));
    }

    [Fact]
    public void Dark_resolves_under_both_strategies() {
        // The media strategy is the one that was dead for the whole life of the feature, and the
        // class strategy is the one the editor actually uses. Both had only an `Assert.Contains`.
        var media = new UtilityFixture();

        Assert.Equal(
            "16px",
            media.Computed(
                ["dark:p-4"],
                "padding-left",
                media: new MediaContext(1024, 768, ColorScheme: ColorSchemePreference.Dark)
            )
        );

        Assert.Null(
            media.Computed(
                ["dark:p-4"],
                "padding-left",
                media: new MediaContext(1024, 768, ColorScheme: ColorSchemePreference.Light)
            )
        );

        var byClass = new UtilityFixture(
            UtilityFixture.Theme.Replace("--dark-mode: media", "--dark-mode: class", StringComparison.Ordinal)
        );

        Assert.Equal(
            "16px",
            byClass.Computed(["dark:p-4"], "padding-left", ancestor: new Probe(["dark"]))
        );

        Assert.Null(byClass.Computed(["dark:p-4"], "padding-left", ancestor: new Probe([])));

        // ⚠ And the class strategy must not also answer to the media query, or a theme that chose
        // `class` would still flip with the operating system. This is the assertion the text-level
        // `Assert.DoesNotContain("prefers-color-scheme")` was standing in for.
        Assert.Null(
            byClass.Computed(
                ["dark:p-4"],
                "padding-left",
                media: new MediaContext(1024, 768, ColorScheme: ColorSchemePreference.Dark),
                ancestor: new Probe([])
            )
        );
    }

    [Fact]
    public void The_themes_breakpoints_have_no_untested_entry() {
        // ⚠ Enumerated off the *shipped* theme rather than the fixture's, because the fixture is doc
        // 09's worked example and stops at `xl` — and `2xl` is the one that was broken. Its class name
        // starts with a digit, which CSS Syntax 3 § 4.3.8 says must be escaped as a code point; the
        // generator was backslash-escaping it instead, so `.2xl\:p-4` reached ExCSS, was refused, and
        // every `2xl:` utility in every project silently produced no rule at all.
        var fixture = new UtilityFixture("");
        var screens = fixture.Tokens.Screens;

        Assert.True(screens.ContainsKey("2xl"), "the shipped theme is expected to declare a 2xl breakpoint");

        foreach (var (name, width) in screens) {
            var candidate = $"{name}:p-4";

            Assert.Equal(
                "16px",
                fixture.Computed([candidate], "padding-left", media: new MediaContext(width + 1f, 800f))
            );

            Assert.Null(
                fixture.Computed([candidate], "padding-left", media: new MediaContext(width - 1f, 800f))
            );
        }
    }

    [Fact]
    public void The_themes_container_sizes_have_no_untested_entry() {
        // ⚠ The gate that says the scale is a *container's* and not a window's. The two namespaces
        // spell `sm` alike and mean numbers two-thirds apart, so the one assertion that catches a
        // `@sm:` resolved against `Screens` is that the same name is a smaller number here — and
        // the enumeration is what stops a fourteenth step from joining the untested.
        var fixture = new UtilityFixture("");
        var sizes = fixture.Tokens.Containers;

        Assert.True(sizes.ContainsKey("sm"), "the shipped theme is expected to declare a container sm");

        Assert.True(
            sizes["sm"] < fixture.Tokens.Screens["sm"],
            "the container scale's sm is not smaller than the breakpoint's, so it is a window's number"
        );

        foreach (var (name, width) in sizes) {
            var candidate = $"@{name}:p-4";

            Assert.Equal(
                "16px",
                fixture.Computed(
                    [candidate],
                    "padding-left",
                    container: new ContainerBox(width + 1f, 0f, ContainerKind.InlineSize)
                )
            );

            Assert.Null(
                fixture.Computed(
                    [candidate],
                    "padding-left",
                    container: new ContainerBox(width - 1f, 0f, ContainerKind.InlineSize)
                )
            );
        }
    }

    [Fact]
    public void A_container_variant_reads_the_box_it_is_inside_and_not_the_window() {
        // ⚠ The row `@media` structurally cannot pass: the surface is enormous and the box is small,
        // so a `@sm:` that had been wired to the breakpoints — or to nothing — would apply here.
        var fixture = new UtilityFixture("");

        Assert.Null(
            fixture.Computed(
                ["@sm:p-4"],
                "padding-left",
                media: new MediaContext(4000f, 3000f),
                container: new ContainerBox(200f, 0f, ContainerKind.InlineSize)
            )
        );

        // And with no container above it at all there is no eligible container, which CSS says
        // resolves false rather than to the viewport.
        Assert.Null(fixture.Computed(["@sm:p-4"], "padding-left", media: new MediaContext(4000f, 3000f)));
    }

    [Fact]
    public void The_container_range_forms_bracket_the_threshold_from_both_sides() {
        var fixture = new UtilityFixture("");

        // `@max-*` is the mirror of `@sm:`, so the pair has to disagree about the same box or one of
        // them is emitting the other's feature.
        Assert.Equal(
            "16px",
            fixture.Computed(["@max-md:p-4"], "padding-left", container: new ContainerBox(300f, 0f, ContainerKind.InlineSize))
        );

        Assert.Null(
            fixture.Computed(["@max-md:p-4"], "padding-left", container: new ContainerBox(900f, 0f, ContainerKind.InlineSize))
        );

        // The arbitrary form, which is the only one that can name a width the scale has no step for.
        Assert.Equal(
            "16px",
            fixture.Computed(["@min-[500px]:p-4"], "padding-left", container: new ContainerBox(600f, 0f, ContainerKind.InlineSize))
        );

        Assert.Null(
            fixture.Computed(["@min-[500px]:p-4"], "padding-left", container: new ContainerBox(400f, 0f, ContainerKind.InlineSize))
        );
    }

    [Fact]
    public void The_two_container_variants_meet_at_the_threshold_without_overlapping_on_it() {
        // ⚠ **The one box width at which `@sm:` and `@max-sm:` could both apply, and in v4 neither
        // pair does.** `--container-sm` is 24rem, so a container measured at exactly 384px is the
        // only probe that can tell `(width < 384px)` from `(max-width: 384px)`; every other width
        // answers the same under both, which is why this was silent for as long as it was and why
        // the rows in the test above cannot see it.
        var fixture = new UtilityFixture("");

        var atThreshold = new ContainerBox(384f, 0f, ContainerKind.InlineSize);

        // v4's `@max-sm` is `(width < 24rem)`, so the threshold belongs to `@sm:` alone.
        Assert.Null(fixture.Computed(["@max-sm:p-4"], "padding-left", container: atThreshold));
        Assert.Equal("16px", fixture.Computed(["@sm:p-4"], "padding-left", container: atThreshold));

        // One texel narrower and they swap, which proves the null above is the threshold and not a
        // variant that stopped resolving.
        var below = new ContainerBox(383f, 0f, ContainerKind.InlineSize);

        Assert.Equal("16px", fixture.Computed(["@max-sm:p-4"], "padding-left", container: below));
        Assert.Null(fixture.Computed(["@sm:p-4"], "padding-left", container: below));

        // ⚠ And `@min-*` stays inclusive — v4 spells that one `(width >= 24rem)` — so the exclusive
        // reading must not have been applied to the whole family.
        Assert.Equal("16px", fixture.Computed(["@min-[384px]:p-4"], "padding-left", container: atThreshold));
    }

    [Fact]
    public void A_named_container_variant_asks_the_container_with_that_name() {
        // ⚠ The name is not part of the condition — it chooses *which* box the condition is asked of
        // — so the discriminating scene is a box that satisfies the size and carries another name.
        // A variant that dropped its name would pass both positive rows and fail this one.
        var fixture = new UtilityFixture("");

        Assert.Equal(
            "16px",
            fixture.Computed(
                ["@sm/main:p-4"],
                "padding-left",
                container: new ContainerBox(900f, 0f, ContainerKind.InlineSize),
                containerName: "main"
            )
        );

        Assert.Null(
            fixture.Computed(
                ["@sm/main:p-4"],
                "padding-left",
                container: new ContainerBox(900f, 0f, ContainerKind.InlineSize),
                containerName: "aside"
            )
        );

        // And the unnamed form must still take the nearest container whatever it is called, or the
        // name would be silently required.
        Assert.Equal(
            "16px",
            fixture.Computed(
                ["@sm:p-4"],
                "padding-left",
                container: new ContainerBox(900f, 0f, ContainerKind.InlineSize),
                containerName: "aside"
            )
        );
    }

    [Fact]
    public void Two_container_variants_on_one_class_conjoin() {
        // A stacked range — wider than `@sm` and narrower than `@lg` — which is two nested
        // `@container` wrappers and the reason `BuildSelector` carries a list rather than a string.
        var fixture = new UtilityFixture("");

        Assert.Equal(
            "16px",
            fixture.Computed(["@sm:@max-lg:p-4"], "padding-left", container: new ContainerBox(450f, 0f, ContainerKind.InlineSize))
        );

        // Too narrow for the first half.
        Assert.Null(
            fixture.Computed(["@sm:@max-lg:p-4"], "padding-left", container: new ContainerBox(300f, 0f, ContainerKind.InlineSize))
        );

        // Too wide for the second.
        Assert.Null(
            fixture.Computed(["@sm:@max-lg:p-4"], "padding-left", container: new ContainerBox(900f, 0f, ContainerKind.InlineSize))
        );
    }

    [Fact]
    public void An_arbitrary_variant_reaches_a_child_and_not_the_element_carrying_it() {
        // Already covered in `UtilityGenerationTests`; restated through this file's one assertion
        // shape so that the enumerations above and this test together account for every shape in
        // `Variants`, rather than for all but one.
        var fixture = new UtilityFixture();

        // The element under test carries no utility of its own — the rule it must pick up belongs to
        // its parent's class, so the sheet has to be generated from that class and handed over as
        // extra CSS.
        var css = fixture.Generate("[&>*]:p-4");

        Assert.Equal(
            "16px",
            fixture.Computed([], "padding-left", extraCss: css, ancestor: new Probe(["[&>*]:p-4"]))
        );

        Assert.Null(fixture.Computed([], "padding-left", extraCss: css, ancestor: new Probe([])));
    }
}
