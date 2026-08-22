// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Samples.Mmo.Ui.Tests;

/// <summary>The stylesheet, checked against the engine rather than against an expectation of text.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A misspelt utility is a style that silently does nothing</b>, and neither the compiler
///         nor the markup binder can see one: <c>class</c> is a string, and every string parses. So the
///         check has to be here, and it has to be about what the cascade computes rather than about what
///         the generator printed — a generator emitting valid CSS the engine then read differently would
///         pass every comparison of text.
///     </para>
///     <para>
///         ⚠ <b>Every test here now reads what the <em>build</em> produced.</b>
///         <c>Theme/MmoStyles.cs</c> used to run the scanner and the generator at startup and these
///         tests drove that code; the build step does it now, <c>MmoStyles</c> is the constant it
///         emits, and asserting against it means a broken import or a safelist that never reached the
///         tool fails here rather than looking like a theme decision.
///     </para>
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

    /// <summary>The names assembled at run time, which the build safelists on this project's behalf.</summary>
    /// <remarks>
    ///     ⚠ <b>The list lives in <c>Mmo.Ui.csproj</c> now, as <c>VixenStyleSafelist</c> items.</b>
    ///     It is repeated here because a safelist protecting a typo is the failure worth catching and
    ///     the two copies disagreeing is how that happens — so the assertion below is really "every
    ///     name the project safelisted came out of the build as a rule", and a name added there and
    ///     not here is a name nothing checks.
    /// </remarks>
    public static TheoryData<string> Safelisted => [
        "text-worn", "text-common", "text-fine", "text-rare", "text-storied",
        "border-worn", "border-common", "border-fine", "border-rare", "border-storied",
        "text-health", "text-mana", "text-rage", "text-focus", "text-cast"
    ];

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
                        // compile time. Those go through the project's safelist instead.
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
    ///     ⚠ <b>The one that catches a typo.</b> Every class name written in a <c>class</c> attribute
    ///     is either a utility the theme can emit or a rule this game wrote, and anything else is a
    ///     style that will silently do nothing.
    /// </summary>
    /// <param name="name">The class name.</param>
    /// <remarks>
    ///     Asserted against the sheet the build produced rather than by re-running the generator, which
    ///     is what the startup bootstrap had to do. The stronger claim is the same one plus "and the
    ///     build step saw this file": a `.vxml` that fell out of the scan takes every class in it with
    ///     it, and that is a whole panel unstyled rather than one property.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Written))]
    public void EveryClassNameInTheMarkupIsAUtilityOrOneOfOurs(string name) =>
        Assert.True(
            HasRule(name),
            $"'{name}' is neither a utility this theme can emit nor a rule in hud.vcss: the build "
            + $"produced no '.{name}' selector."
        );

    /// <summary>Every safelisted name is a real utility, or the safelist is protecting a typo.</summary>
    /// <param name="name">The safelisted class name.</param>
    /// <remarks>
    ///     ⚠ <b>The safelist earns its place twice over.</b> Nothing in the markup writes
    ///     <c>border-storied</c> — the bag slot writes <c>@slot.RarityClass.Value</c> — so without it
    ///     the rule is not emitted, every slot draws the default border, and it looks like a theme
    ///     decision rather than a missing rule. And a safelisted name that is *not* a utility emits
    ///     nothing at all, which is the same silence with an extra line of MSBuild in front of it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Safelisted))]
    public void TheSafelistIsAllUtilities(string name) =>
        Assert.True(HasRule(name), $"'{name}' is safelisted in Mmo.Ui.csproj and the build emitted no rule for it.");

    /// <summary>The rarity borders resolve to a colour on a real element in a real document.</summary>
    [Theory]
    [InlineData("border-worn")]
    [InlineData("border-common")]
    [InlineData("border-fine")]
    [InlineData("border-rare")]
    [InlineData("border-storied")]
    public void ARarityBorderResolvesToTheThemesColour(string rarity) {
        using var ui = UiTest.Create();

        ui.Load(MmoStyles.Css);

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
    /// <remarks>
    ///     ⚠ It is also the check that the base sheet went in <em>first</em>. <c>hud.vcss</c> opens
    ///     with <c>@layer base, components, utilities;</c>, and that statement is what fixes the
    ///     ladder's order; emitted after the utilities it would put <c>utilities</c> at layer zero and
    ///     invert the whole thing. The build hands it to StyleGen as <c>VixenStyleBase</c>, which is
    ///     ordered ahead of what it generates for exactly this reason.
    /// </remarks>
    [Fact]
    public void OurOwnRulesBeatTheUtilityLayer() {
        using var ui = UiTest.Create();

        ui.Load(MmoStyles.Css);

        var slot = ui.Create("slot-cell", ui.Document.Root, "cell", "slot", "on-cooldown", "opacity-100");

        ui.Frame();

        // hud.vcss says 0.45 and the utility says 1. The layer is what decides, not the order.
        Assert.Equal(0.45f, ui.NumberOf(slot, "opacity") ?? 1f, 3);
    }

    /// <summary>
    ///     The build step ran and saw the markup, which every test above quietly depends on.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The cheapest possible check that the wiring is there at all.</b> A project whose
    ///     <c>VixenUi</c> line went missing compiles perfectly and produces an empty sheet, and every
    ///     style in every panel then quietly does nothing — so the count is asserted, and so is a
    ///     class that appears in the markup and nowhere else.
    /// </remarks>
    [Fact]
    public void TheMarkupIsScannedByTheBuild() {
        Assert.Equal(8, MarkupFiles().Count);
        Assert.True(MmoStyles.RuleCount > 0, "the build emitted no utility rules at all.");
        Assert.True(HasRule("flex"));
        Assert.True(HasRule("shadow-panel"));
    }

    /// <summary>Whether the built sheet carries a rule for a class.</summary>
    /// <remarks>
    ///     The negative lookahead is what keeps <c>text-rare</c> from being satisfied by
    ///     <c>.text-rare-thing</c>, and matching a leading <c>.</c> is what keeps a class name
    ///     mentioned in a comment or in a custom property's value from counting.
    /// </remarks>
    static bool HasRule(string name) =>
        Regex.IsMatch(MmoStyles.Css, @"\." + Regex.Escape(name) + @"(?![-\w])", RegexOptions.None, TimeSpan.FromSeconds(5));

    static IEnumerable<string> Markup() => MarkupFiles().Select(File.ReadAllText);

    /// <summary>
    ///     The markup as authored, read off disk.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Off disk rather than out of the assembly, because it is no longer in the assembly.</b>
    ///     The `.vxml` used to be embedded so that a startup bootstrap could scan it; the build step
    ///     scans the files, so embedding a second copy of every panel into the shipped binary would be
    ///     paying for a mechanism that is gone. The same walk `SharedUiShaderTests` uses, for the same
    ///     reason: the subject of the test is a file in the repository.
    /// </remarks>
    static IReadOnlyList<string> MarkupFiles() {
        var project = Path.Combine(RepositoryRoot(), "Samples", "14-Mmo", "Mmo.Ui");

        // ⚠ `bin` and `obj` are excluded for the reason `Vixen.Ui.targets` gives about its own glob:
        // a copy of a `.vxml` under either is a build output, and counting one would make the
        // assertion below depend on what the last build left behind.
        return [
            .. Directory.EnumerateFiles(project, "*.vxml", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
    }

    static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
