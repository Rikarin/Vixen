// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Ui.Styling.Utilities;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Samples.Mmo.Ui.Tests;

/// <summary>The stylesheet, checked against the engine rather than against an expectation of text.</summary>
/// <remarks>
///     ⚠ <b>A misspelt utility is a style that silently does nothing</b>, and neither the compiler
///     nor the markup binder can see one: <c>class</c> is a string, and every string parses. So the
///     check has to be here, and it has to be about what the cascade computes rather than about what
///     the generator printed — a generator emitting valid CSS the engine then read differently would
///     pass every comparison of text.
/// </remarks>
public partial class StylesheetTests {
    /// <summary>Class names that are ours rather than the utility system's.</summary>
    /// <remarks>
    ///     Every one of them is a rule in <c>Theme/hud.vcss</c>, and the list being short is the
    ///     point: a component class is a thing somebody has to go and find before they can change it,
    ///     where a utility says what it does at the use site.
    /// </remarks>
    static readonly HashSet<string> Ours = new(StringComparer.Ordinal) {
        "slot", "on-cooldown", "unusable", "quest", "ready"
    };

    [GeneratedRegex("class=\"([^\"]*)\"")]
    private static partial Regex ClassAttribute { get; }

    /// <summary>Every literal class name written in the markup, with the interpolations dropped.</summary>
    public static TheoryData<string> Written {
        get {
            var data = new TheoryData<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var markup in Markup()) {
                foreach (Match match in ClassAttribute.Matches(markup)) {
                    foreach (var name in match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                        // `@Slot.RarityClass.Value` is a whole class name at run time and nothing at
                        // compile time. Those go through MmoStyles.Safelist instead.
                        if (!name.StartsWith('@') && seen.Add(name)) {
                            data.Add(name);
                        }
                    }
                }
            }

            return data;
        }
    }

    /// <summary>
    ///     ⚠ <b>The one that catches a typo.</b> Asserting on <c>UtilityGenerator.Unrecognised</c>
    ///     directly cannot work — the scanner is over-inclusive on purpose, so most of what lands
    ///     there is prose out of a comment. What *can* be asserted is the set actually written in a
    ///     <c>class</c> attribute: every one of those is either a utility the system knows or a rule
    ///     this game wrote, and anything else is a style that will silently do nothing.
    /// </summary>
    /// <param name="name">The class name.</param>
    [Theory]
    [MemberData(nameof(Written))]
    public void EveryClassNameInTheMarkupIsAUtilityOrOneOfOurs(string name) {
        if (Ours.Contains(name)) {
            return;
        }

        var generator = new UtilityGenerator(MmoStyles.Tokens());

        generator.Generate([name]);

        Assert.True(
            generator.Unrecognised.Count == 0,
            $"'{name}' is neither a utility this theme can emit nor a rule in hud.vcss."
        );
    }

    /// <summary>Every safelisted name is a real utility, or the safelist is protecting a typo.</summary>
    [Fact]
    public void TheSafelistIsAllUtilities() {
        var generator = new UtilityGenerator(MmoStyles.Tokens());

        generator.Generate(MmoStyles.Safelist);

        Assert.Empty(generator.Unrecognised);
    }

    /// <summary>
    ///     ⚠ <b>The safelist earns its place here.</b> Nothing in the markup writes
    ///     <c>border-storied</c> — the bag slot writes <c>@slot.RarityClass.Value</c> — so without the
    ///     safelist this rule is not emitted, every slot draws the default border, and it looks like
    ///     a theme decision rather than a missing rule.
    /// </summary>
    [Theory]
    [InlineData("border-worn")]
    [InlineData("border-common")]
    [InlineData("border-fine")]
    [InlineData("border-rare")]
    [InlineData("border-storied")]
    public void ARarityBorderResolvesToTheThemesColour(string rarity) {
        using var ui = UiTest.Create();

        ui.Load(MmoStyles.Compile());

        var slot = ui.Create("bag-slot", ui.Document.Root, "slot", rarity);

        ui.Frame();

        Assert.NotNull(ui.ColorOf(slot, "border-top-color"));
    }

    /// <summary>
    ///     ⚠ <b>The layer, checked by resolving rather than by reading the text.</b> The generated
    ///     utilities are in <c>@layer utilities</c> and <c>hud.vcss</c> is not, so a single-class rule
    ///     of ours beats a single-class utility whatever the source order — and nothing had to say
    ///     <c>!important</c> for that to be true.
    /// </summary>
    [Fact]
    public void OurOwnRulesBeatTheUtilityLayer() {
        using var ui = UiTest.Create();

        ui.Load(MmoStyles.Compile());

        var slot = ui.Create("slot-cell", ui.Document.Root, "cell", "slot", "on-cooldown", "opacity-100");

        ui.Frame();

        // hud.vcss says 0.45 and the utility says 1. The layer is what decides, not the order.
        Assert.Equal(0.45f, ui.NumberOf(slot, "opacity") ?? 1f, 3);
    }

    /// <summary>The scan sees the markup at all, which every test above quietly depends on.</summary>
    [Fact]
    public void TheMarkupIsEmbeddedAndScanned() {
        Assert.Equal(8, MmoStyles.Markup().Count());
        Assert.Contains("flex", MmoStyles.Candidates());
        Assert.Contains("shadow-panel", MmoStyles.Candidates());
    }

    static IEnumerable<string> Markup() {
        var assembly = typeof(MmoStyles).Assembly;

        foreach (var name in assembly.GetManifestResourceNames().Where(entry => entry.EndsWith(".vxml", StringComparison.Ordinal))) {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);

            yield return reader.ReadToEnd();
        }
    }
}
