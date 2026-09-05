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
    public sealed record Scene(string Variant, ElementState State, int Before, int After, bool Matches);

    /// <summary>The scenes, as data the completeness gate can also read.</summary>
    /// <remarks>
    ///     Kept beside <see cref="StateScenes" /> rather than inside it because
    ///     <c>TheoryDataRow</c> does not hand its values back — and the gate's whole job is to read
    ///     which variants appear here.
    /// </remarks>
    static readonly Scene[] Scenes = BuildScenes();

    public static TheoryData<string, ElementState, int, int, bool> StateScenes {
        get {
            // Primitives only. xunit serialises theory rows so it can run one of them on its own, and
            // a `Probe[]` in the row would collapse the whole theory into a single opaque case.
            var data = new TheoryData<string, ElementState, int, int, bool>();

            foreach (var scene in Scenes) {
                data.Add(scene.Variant, scene.State, scene.Before, scene.After, scene.Matches);
            }

            return data;
        }
    }

    static Scene[] BuildScenes() {
        var scenes = new List<Scene>();

        // variant, own state, siblings before, siblings after, should match
        void Row(string variant, ElementState state, int before, int after, bool matches) =>
            scenes.Add(new Scene(variant, state, before, after, matches));

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

        return [.. scenes];
    }

    [Theory]
    [MemberData(nameof(StateScenes))]
    public void A_state_variant_changes_what_the_element_computes(
        string variant,
        ElementState state,
        int before,
        int after,
        bool matches
    ) {
        var fixture = new UtilityFixture();

        // A parent is forced for every row, not only the structural ones, so that the positive and
        // the negative scene differ in the one thing the row is about.
        var value = fixture.Computed(
            [$"{variant}:p-4"],
            "padding-left",
            state: state,
            ancestor: new Probe([]),
            before: Filler(before),
            after: Filler(after)
        );

        Assert.Equal(matches ? "16px" : null, value);
    }

    static Probe[] Filler(int count) => [.. Enumerable.Range(0, count).Select(_ => new Probe([]))];

    [Fact]
    public void The_state_variant_table_has_no_untested_entry() {
        // The gate. A thirteenth entry added to `Variants.States` without a scene above lands here
        // rather than in the silent majority — which is where all eleven of them were.
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
