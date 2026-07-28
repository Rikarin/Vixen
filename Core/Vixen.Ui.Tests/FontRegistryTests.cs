// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Which face a family, a weight and a slant resolve to.</summary>
/// <remarks>
///     The faces here are two unrelated fonts registered as variants of one family, which is a lie
///     the registry cannot detect and does not need to: what is under test is the <i>choosing</i>,
///     and two faces that are trivially distinguishable by name make the choice visible. Loading four
///     real weights of one family would test the same branch with a much larger binary.
/// </remarks>
public class FontRegistryTests {
    static readonly FontFace Roman = Load("TestShapeLana.ttf", "roman");
    static readonly FontFace Other = Load("NotoSerifKannada-Regular.ttf", "other");

    static FontFace Load(string resource, string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"the test font '{resource}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    [Fact]
    public void An_exact_weight_wins() {
        var registry = new FontRegistry();
        registry.Register("Sans", Roman, 400);
        registry.Register("Sans", Other, 700);

        Assert.Same(Roman, registry.Resolve("Sans", 400));
        Assert.Same(Other, registry.Resolve("Sans", 700));
    }

    [Fact]
    public void Four_hundred_takes_five_hundred_before_it_looks_downwards() {
        // ⚠ The one asymmetry in CSS's algorithm, and the reason this is not nearest-neighbour: 400
        // is equidistant from 300 and 500, and the specification says the *heavier* one wins. The
        // obvious implementation picks whichever was registered first and is right half the time.
        var registry = new FontRegistry();
        registry.Register("Sans", Roman, 300);
        registry.Register("Sans", Other, 500);

        Assert.Same(Other, registry.Resolve("Sans", 400));
    }

    [Fact]
    public void Below_the_middle_the_search_runs_downwards_first() {
        // 300 is nearer to 400 than to 200, and 200 wins: below 500 the rule is "lighter first",
        // not "closest".
        var registry = new FontRegistry();
        registry.Register("Sans", Roman, 200);
        registry.Register("Sans", Other, 400);

        Assert.Same(Roman, registry.Resolve("Sans", 300));
    }

    [Fact]
    public void Above_the_middle_the_search_runs_upwards_first() {
        // And 600 takes the 900 over the 500, for the mirror of the same reason.
        var registry = new FontRegistry();
        registry.Register("Sans", Roman, 500);
        registry.Register("Sans", Other, 900);

        Assert.Same(Other, registry.Resolve("Sans", 600));
    }

    [Fact]
    public void A_side_with_nothing_on_it_falls_through_to_the_other() {
        var registry = new FontRegistry();
        registry.Register("Sans", Roman, 700);

        Assert.Same(Roman, registry.Resolve("Sans", 100));
        Assert.Same(Roman, registry.Resolve("Sans", 900));
    }

    [Fact]
    public void The_slant_is_settled_before_the_weight() {
        // An italic at the wrong weight answers `italic` better than an upright at the right one,
        // which is CSS's order and not an arbitrary one: the reader sees the slant first.
        var registry = new FontRegistry();
        registry.Register("Sans", Roman, 400);
        registry.Register("Sans", Other, 900, FontStyle.Italic);

        Assert.Same(Other, registry.Resolve("Sans", 400, FontStyle.Italic));
        Assert.Same(Roman, registry.Resolve("Sans", 900));
    }

    [Fact]
    public void An_italic_falls_back_to_an_upright_when_the_family_has_none() {
        // Rather than to nothing. Synthesising a slant is not on offer, so an upright is the honest
        // last resort — and text that is there in the wrong style beats text that is not there.
        var registry = new FontRegistry();
        registry.Register("Sans", Roman, 400);

        Assert.Same(Roman, registry.Resolve("Sans", 400, FontStyle.Italic));
    }

    [Fact]
    public void The_family_is_chosen_before_the_weight_is() {
        // ⚠ Worth pinning because the other reading looks more helpful. `Sans` has no bold and
        // `Fallback` does, and `Sans` still wins — a fallback family that could outrank the
        // designer's choice by happening to ship more weights would be a surprising rule.
        var registry = new FontRegistry();
        registry.Register("Sans", Roman, 400);
        registry.Register("Fallback", Other, 700);

        Assert.Same(Roman, registry.Resolve("Sans, Fallback", 700));
    }

    [Fact]
    public void Registering_the_same_variant_twice_replaces_it() {
        var registry = new FontRegistry();
        registry.Register("Sans", Roman, 400);
        registry.Register("Sans", Other, 400);

        Assert.Same(Other, registry.Resolve("Sans", 400));
        Assert.Single(registry.Variants("Sans"));
    }

    [Fact]
    public void A_family_nobody_registered_falls_back_to_the_default() {
        var registry = new FontRegistry();
        registry.Register("Sans", Roman, 400);

        // The first face registered, so a stylesheet with a typo in a family name draws in some font
        // rather than not at all — which is what makes the typo findable.
        Assert.Same(Roman, registry.Resolve("Nonexistent", 700));
        Assert.Same(Roman, registry.Resolve(null));
    }

    [Fact]
    public void Font_weight_reaches_the_run_through_the_cascade() {
        using var document = new UiDocument(400f, 200f);

        document.Fonts.Register("Sans", Roman, 400);
        document.Fonts.Register("Sans", Other, 700);
        document.Load("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            label { font-family: Sans; }
            .heavy { font-weight: 700; }
            .named { font-weight: bold; }
        """);

        var regular = document.Root.Add("label");
        var heavy = document.Root.Add("label", null, "heavy");
        var named = document.Root.Add("label", null, "named");

        foreach (var label in new[] { regular, heavy, named }) {
            label.Text = "AB";
        }

        document.Update();

        Assert.Same(Roman, regular.Run()!.Font);
        Assert.Same(Other, heavy.Run()!.Font);

        // `bold` is the keyword form of 700 and has to resolve to the same face as the number.
        Assert.Same(Other, named.Run()!.Font);
    }

    [Fact]
    public void Font_weight_inherits_so_a_heading_makes_its_children_heavy() {
        using var document = new UiDocument(400f, 200f);

        document.Fonts.Register("Sans", Roman, 400);
        document.Fonts.Register("Sans", Other, 700);
        document.Load("""
            root { width: 400px; height: 200px; align-items: flex-start; font-family: Sans; }
            .heading { font-weight: bold; }
        """);

        var heading = document.Root.Add("div", classNames: "heading");
        var label = heading.Add("label");
        label.Text = "AB";

        document.Update();

        Assert.Same(Other, label.Run()!.Font);
    }
}
