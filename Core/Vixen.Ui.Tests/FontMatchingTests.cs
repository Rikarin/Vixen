// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>CSS Fonts 4 § 5.2, which decides which face of a family a declaration gets.</summary>
/// <remarks>
///     ⚠ <b>Faces here are distinguished by identity, not by what they draw.</b> Every one is the same
///     loaded file registered under different metadata, because what is under test is the choosing
///     rather than the rendering — and a test that needed nine real weights of a real family could not
///     be committed, since the repository has no Latin UI font to commit at all.
/// </remarks>
public class FontMatchingTests {
    /// <summary>A fresh instance of the one font the repository can commit.</summary>
    /// <remarks>
    ///     A new object each time on purpose: the tests assert <i>which face came back</i> by
    ///     reference, so two candidates must not be the same object however identical their contents.
    /// </remarks>
    static FontFace Face() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    static (FontRegistry Registry, Dictionary<string, FontFace> Faces) Family(params (string Label, int Weight, FontStyle Style, FontStretch Stretch)[] faces) {
        var registry = new FontRegistry();
        var byLabel = new Dictionary<string, FontFace>(StringComparer.Ordinal);

        foreach (var (label, weight, style, stretch) in faces) {
            var face = Face();
            byLabel[label] = face;
            registry.Register("Family", face, weight, style, stretch);
        }

        return (registry, byLabel);
    }

    [Theory]
    // An exact match always wins.
    [InlineData(400, "regular")]
    [InlineData(700, "bold")]
    // ⚠ 400 tries 500 before it tries anything lighter, and 500 tries 400 — the two-way special case
    // in the specification, and the thing a plain "nearest weight" implementation gets wrong.
    [InlineData(500, "regular")]
    // Below 400 goes lighter first, so 300 takes the 200 rather than the nearer-by-distance 400.
    [InlineData(300, "light")]
    // Above 500 goes heavier first.
    [InlineData(600, "bold")]
    [InlineData(900, "bold")]
    public void The_weight_rule_prefers_a_direction_before_a_distance(int wanted, string expected) {
        var (registry, faces) = Family(
            ("light", 200, FontStyle.Normal, FontStretch.Normal),
            ("regular", 400, FontStyle.Normal, FontStretch.Normal),
            ("bold", 700, FontStyle.Normal, FontStretch.Normal)
        );

        Assert.Same(faces[expected], registry.Resolve("Family", new FontQuery(wanted)));
    }

    [Fact]
    public void The_direction_rule_beats_the_nearer_face() {
        // ⚠ **The case that separates the specification's rule from "pick the nearest", and without
        // it the whole rank function was untested.** Every family above happens to give the same
        // answer both ways, so a sabotage replacing the rule with plain distance passed — which is
        // how the 400–500 band bug below was found rather than reasoned about.
        var (heavier, heavierFaces) = Family(
            ("near", 550, FontStyle.Normal, FontStretch.Normal),
            ("far", 700, FontStyle.Normal, FontStretch.Normal)
        );

        // 600 is 50 from 550 and 100 from 700 — but above 500 the rule goes heavier first, so the
        // *farther* face wins.
        Assert.Same(heavierFaces["far"], heavier.Resolve("Family", new FontQuery(600)));

        var (lighter, lighterFaces) = Family(
            ("near", 350, FontStyle.Normal, FontStretch.Normal),
            ("far", 200, FontStyle.Normal, FontStretch.Normal)
        );

        // And the mirror below 400: 300 is 50 from 350 and 100 from 200, and takes the 200.
        Assert.Same(lighterFaces["far"], lighter.Resolve("Family", new FontQuery(300)));
    }

    [Fact]
    public void Four_hundred_searches_the_whole_band_up_to_five_hundred() {
        // ⚠ **The bug a sabotage found in this file's first version**, which had the rule as "400
        // checks exactly 500 first". CSS checks the whole band — "weights greater than or equal to
        // 400 and less than or equal to 500, in ascending order" — so a family with a 300 and a 450
        // gives 400 the 450, and the single-value reading gives it the 300. Both read plausibly and
        // only one is what a font vendor shipping a 450 expects.
        var (registry, faces) = Family(
            ("light", 300, FontStyle.Normal, FontStretch.Normal),
            ("book", 450, FontStyle.Normal, FontStretch.Normal)
        );

        Assert.Same(faces["book"], registry.Resolve("Family", new FontQuery(400)));
        Assert.Same(faces["book"], registry.Resolve("Family", new FontQuery(500)));
    }

    [Fact]
    public void Four_hundred_takes_a_lighter_face_over_a_heavier_one() {
        // The family a browser's own test suite uses for this: nothing at 400, one either side.
        var (registry, faces) = Family(
            ("light", 300, FontStyle.Normal, FontStretch.Normal),
            ("bold", 700, FontStyle.Normal, FontStretch.Normal)
        );

        // ⚠ 300 is 100 away and 700 is 300 away, so nearest agrees here — but 500 is the case that
        // separates them, and it goes the *other* way: 500 checks 400 (absent), then downward, and
        // 300 is downward. A "nearest" implementation gives 500 the 700.
        Assert.Same(faces["light"], registry.Resolve("Family", new FontQuery(400)));
        Assert.Same(faces["light"], registry.Resolve("Family", new FontQuery(500)));
    }

    [Fact]
    public void Style_is_settled_before_weight() {
        var (registry, faces) = Family(
            ("italic-light", 300, FontStyle.Italic, FontStretch.Normal),
            ("upright-bold", 700, FontStyle.Normal, FontStretch.Normal)
        );

        // ⚠ The order of the three axes is the algorithm. Asked for a bold italic, a family with a
        // light italic and an upright bold must give the *italic* — being the wrong weight in the
        // right style is a substitution, being upright when italic was asked for is a different
        // typeface. Swapping the two steps produces the opposite answer and looks equally plausible.
        Assert.Same(faces["italic-light"], registry.Resolve("Family", new FontQuery(700, FontStyle.Italic)));
    }

    [Fact]
    public void Stretch_is_settled_before_style() {
        var (registry, faces) = Family(
            ("condensed-upright", 400, FontStyle.Normal, FontStretch.Condensed),
            ("normal-italic", 400, FontStyle.Italic, FontStretch.Normal)
        );

        Assert.Same(
            faces["condensed-upright"],
            registry.Resolve("Family", new FontQuery(400, FontStyle.Italic, FontStretch.Condensed))
        );
    }

    [Fact]
    public void An_italic_takes_an_oblique_before_an_upright() {
        var (registry, faces) = Family(
            ("upright", 400, FontStyle.Normal, FontStretch.Normal),
            ("oblique", 400, FontStyle.Oblique, FontStretch.Normal)
        );

        Assert.Same(faces["oblique"], registry.Resolve("Family", new FontQuery(400, FontStyle.Italic)));

        // And the reverse chain is not the mirror image: an upright request prefers an oblique to an
        // italic, because a sheared roman is closer to a roman than a separately drawn cursive is.
        var (second, secondFaces) = Family(
            ("italic", 400, FontStyle.Italic, FontStretch.Normal),
            ("oblique", 400, FontStyle.Oblique, FontStretch.Normal)
        );

        Assert.Same(secondFaces["oblique"], second.Resolve("Family", new FontQuery(400)));
    }

    [Fact]
    public void A_condensed_request_never_widens_when_it_could_narrow() {
        // ⚠ **The wrong-way face is registered first, and that is not cosmetic.** With the tie-break
        // deleted both candidates score identically, and the winner then falls out of registration
        // order — so a test that registered the *right* answer first passed with the rule removed.
        // Cost a sabotage to find, twice: the first fix was an exact tie and it still was not enough.
        var (registry, faces) = Family(
            ("semi-expanded", 400, FontStyle.Normal, FontStretch.SemiExpanded),
            ("extra-condensed", 400, FontStyle.Normal, FontStretch.ExtraCondensed)
        );

        // ⚠ **An exact tie, because anything else leaves the rule unreachable.** The first version of
        // this test used candidates at different distances, so plain distance already decided and a
        // sabotage removing the direction preference passed. SemiCondensed is 87; ExtraCondensed (62)
        // and SemiExpanded (112) are both 25 away, and below normal the narrow side must win — being
        // handed an expanded face when you asked for condensed is the substitution nobody wants.
        Assert.Same(
            faces["extra-condensed"],
            registry.Resolve("Family", new FontQuery(400, FontStyle.Normal, FontStretch.SemiCondensed))
        );

        // And above normal it goes the other way, from the same distance.
        var (wide, wideFaces) = Family(
            ("condensed", 400, FontStyle.Normal, FontStretch.Condensed),
            ("expanded", 400, FontStyle.Normal, FontStretch.SemiExpanded)
        );

        Assert.Same(
            wideFaces["expanded"],
            wide.Resolve("Family", new FontQuery(400, FontStyle.Normal, FontStretch.SemiExpanded))
        );
    }

    [Fact]
    public void The_first_registered_family_wins_even_with_a_worse_weight() {
        var registry = new FontRegistry();
        var first = Face();
        var second = Face();

        registry.Register("First", first);
        registry.Register("Second", second, 300);

        // ⚠ The query is applied *within* a family, never across them. Preferring the exact 300 in
        // the second family would mean `font-family: Inter, Arial` with `font-weight: 300` coming
        // back in Arial — a different typeface, chosen because it happened to have a light.
        Assert.Same(first, registry.Resolve("First, Second", new FontQuery(300)));
    }

    [Fact]
    public void The_cascade_reaches_the_registry() {
        using var document = new UiDocument(200f, 100f);
        var regular = Face();
        var bold = Face();

        document.Fonts.Register("Test", regular);
        document.Fonts.Register("Test", bold, 700);

        document.Load("""
            root { font-family: Test; }
            .strong { font-weight: bold; }
        """);

        var plain = document.Root.Add("div");
        plain.Text = "x";
        var strong = document.Root.Add("div", classNames: "strong");
        strong.Text = "x";

        document.Update();

        // The end-to-end assertion, and the one the unit tests above cannot make: `font-weight: bold`
        // is a *keyword* in the cascade, not the number 700, so a reader that asked for a number
        // would find nothing and quietly give every element the regular face.
        Assert.Same(regular, plain.Run()!.Font);
        Assert.Same(bold, strong.Run()!.Font);
    }

    [Fact]
    public void A_numeric_weight_reaches_it_too() {
        using var document = new UiDocument(200f, 100f);
        var light = Face();
        var regular = Face();

        document.Fonts.Register("Test", regular);
        document.Fonts.Register("Test", light, 200);

        document.Load("""
            root { font-family: Test; }
            .light { font-weight: 200; }
        """);

        var element = document.Root.Add("div", classNames: "light");
        element.Text = "x";
        document.Update();

        Assert.Same(light, element.Run()!.Font);
    }
}
