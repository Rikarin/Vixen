// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Ui;
using Vixen.Ui.Styling;
using Vixen.Ui.Styling.Utilities;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The editor's utility sheet, checked against the engine rather than against its own text.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A misspelt utility is a style that silently does nothing</b>, and neither the compiler
///         nor the markup binder can see one: <c>class</c> is a string, and every string parses. So
///         the check has to be here, and it has to be about what the cascade computes rather than
///         about what the generator printed — a generator emitting valid CSS the engine then read
///         differently would pass every comparison of text.
///     </para>
///     <para>
///         The sheet itself arrives through <c>EditorStyles</c>, which is a name for a constant the
///         build step wrote. Nothing in this file scans anything at run time; the two fixtures it
///         reads are the build step's <i>inputs</i>, and they are here because the question "is every
///         class name in the markup a real utility" is one only the inputs can answer.
///     </para>
/// </remarks>
public partial class StylesheetTests {
    /// <summary>Class names in the editor's markup that are <c>EditorTheme</c>'s rather than utilities.</summary>
    /// <remarks>
    ///     <para>
    ///         Nearly empty, and worth keeping nearly empty: <c>EditorTheme</c> styles the editor's
    ///         chrome by <i>tag</i> almost throughout — <c>task-row</c>, <c>status-cell</c>,
    ///         <c>console-message</c> — so a class name in a <c>.vxml</c> is a utility unless somebody
    ///         deliberately made it otherwise.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It was empty until <c>SettingsView</c> was ported, and that is the premise being
    ///         corrected rather than an exception being carved out.</b> "Almost throughout" was doing
    ///         real work in that sentence: <c>settings-rail &gt; button.settings-tab</c> has been an
    ///         <c>EditorTheme</c> rule since the window was written, and it needs a class because the
    ///         thing it styles is a <i>control</i> — a tag selector cannot tell one <c>button</c> from
    ///         another. Every such rule in the sheet was previously reached from C# with
    ///         <c>AddClass</c>, which this gate does not read, so the first panel to write one in
    ///         markup is the first to be accused of a typo.
    ///     </para>
    /// </remarks>
    static readonly HashSet<string> Ours = new(StringComparer.Ordinal) { "settings-tab" };

    [GeneratedRegex("class=\"([^\"]*)\"")]
    private static partial Regex ClassAttribute { get; }

    /// <summary>Every literal class name written in the editor's markup, with the bindings dropped.</summary>
    public static TheoryData<string> Written {
        get {
            var data = new TheoryData<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var markup in Directory.EnumerateFiles(Fixtures("markup"), "*.vxml", SearchOption.AllDirectories)) {
                foreach (Match match in ClassAttribute.Matches(File.ReadAllText(markup))) {
                    foreach (var name in match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                        // `@Mark(entry.Slot)` is a whole class name at run time and nothing at compile
                        // time. Those go through the `VixenStyleSafelist` item instead.
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
    ///     directly cannot work — the scanner is over-inclusive on purpose, and now that it reads the
    ///     C# as well, the report the build step writes is sixty kilobytes of ordinary English. What
    ///     <i>can</i> be asserted is the set actually written in a <c>class</c> attribute: every one
    ///     of those is either a utility the theme can emit or a rule <c>EditorTheme</c> wrote, and
    ///     anything else is a style that will silently do nothing.
    /// </summary>
    /// <param name="name">The class name.</param>
    [Theory]
    [MemberData(nameof(Written))]
    public void Every_class_name_in_the_markup_is_a_utility_or_one_of_ours(string name) {
        if (Ours.Contains(name)) {
            return;
        }

        var generator = new UtilityGenerator(Tokens());

        generator.Generate([name]);

        // ⚠ Both channels, and checking only the first would have quietly halved this gate. A refused
        // candidate leaves `Unrecognised` for `Unresolved` the moment its family is registered, so
        // `bg-surfaces` — the misspelling the theory below is built around — reports through the
        // second. What this asserts is that the class emitted a rule, which is what its name claims.
        Assert.True(
            generator.Unrecognised.Count == 0 && generator.Unresolved.Count == 0,
            $"'{name}' is neither a utility this theme can emit nor a rule in EditorTheme."
            + (generator.Unresolved.Count > 0
                ? $" The family '{generator.Unresolved[0].Family}' has no value '{generator.Unresolved[0].Detail}'."
                : string.Empty)
        );
    }

    /// <summary>
    ///     ⚠ <b>The gate above, sabotaged.</b> A test that only ever sees correct input cannot say
    ///     whether it would notice wrong input, and this is the wrong input: five class names a
    ///     reviewer's eye slides straight over. Every one has to be reported, because a report is the
    ///     only place a misspelt utility ever shows up.
    ///     <para>
    ///         <b>Which of the two channels is a fact about the typo, not about the gate.</b>
    ///         <c>flexx</c> mistypes the family and lands in <c>Unrecognised</c>; <c>p-4x</c> and
    ///         <c>bg-surfaces</c> mistype the <i>value</i> of a family that exists, so they are
    ///         refusals — which is the more useful diagnostic of the two and the reason the channels
    ///         were split. See <c>docs/plan/43</c> § F8.
    ///     </para>
    /// </summary>
    /// <param name="misspelt">The class name somebody nearly got right.</param>
    [Theory]
    [InlineData("flexx")]
    [InlineData("p-4x")]
    [InlineData("truncat")]
    [InlineData("min-width-0")]
    [InlineData("bg-surfaces")]
    public void A_misspelt_utility_is_reported(string misspelt) {
        var generator = new UtilityGenerator(Tokens());

        generator.Generate([misspelt]);

        Assert.Equal(0, generator.RuleCount);

        Assert.True(
            generator.Unrecognised.Contains(misspelt, StringComparer.Ordinal)
            ^ generator.Unresolved.Any(r => string.Equals(r.Candidate, misspelt, StringComparison.Ordinal)),
            $"'{misspelt}' was reported in neither channel, or in both"
        );
    }

    /// <summary>The tokens read cleanly, and every colour in them is the editor's own.</summary>
    /// <remarks>
    ///     ⚠ <b>The second assertion is the one that matters and it is not about YAML.</b> A theme
    ///     file full of hex would parse perfectly and be a <i>second palette</i>: correct on the day
    ///     it was written, silently disagreeing with <c>EditorTheme</c> the first time somebody
    ///     changed one of them, and immune to both the light/dark toggle and a user's theme file.
    ///     Every colour being a <c>var(--…)</c> reference is what makes "one palette" a fact rather
    ///     than an intention.
    /// </remarks>
    [Fact]
    public void The_tokens_are_the_editors_own_and_not_a_copy_of_them() {
        var tokens = Tokens();

        Assert.Empty(tokens.Diagnostics);
        Assert.NotEmpty(tokens.Colors);

        foreach (var (name, value) in tokens.Colors) {
            Assert.True(
                value.StartsWith("var(--", StringComparison.Ordinal),
                $"colour '{name}' is '{value}' rather than a reference to one of EditorTheme's tokens."
            );

            Assert.Contains(value[6..^1], EditorTheme.Css, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     ⚠ <b>The cheapest possible check that the build step ran at all, and it earns its place.</b>
    ///     A project whose <c>.targets</c> import went missing compiles perfectly and produces a sheet
    ///     with nothing in it — every utility in every panel then quietly does nothing, and no test
    ///     that asserts about a specific rule can tell that case from a rule that was renamed.
    /// </summary>
    [Fact]
    public void The_build_step_produced_a_sheet() {
        Assert.Contains("@layer utilities", EditorStyles.Utilities, StringComparison.Ordinal);
        Assert.True(EditorStyles.RuleCount > 0, "the generated sheet has no rules in it at all");
    }

    /// <summary>A utility written in the editor's markup reaches the element the markup put it on.</summary>
    /// <remarks>
    ///     End to end, and every link in it is real: the class name is in <c>TaskCenter.vxml</c>, the
    ///     build step scanned it, the generator emitted a rule for it, the compiler put the rule in
    ///     this assembly as a constant, and <c>EditorTheme.Install</c> loaded it into the document.
    ///     Nothing here is a fixture.
    /// </remarks>
    [Fact]
    public void A_utility_in_a_panels_markup_resolves_on_the_element_it_was_written_on() {
        using var ui = Editor();

        var title = ui.Create("task-title", ui.Document.Root, null, "truncate", "min-w-0");

        ui.Frame();

        // ⚠ All three of `truncate`'s declarations, not just the one it used to emit. Doc 43's F5 was
        // that this class named an ellipsis the engine could not draw and suppressed no wrapping;
        // asserting only `overflow` here is what let that stand for as long as it did.
        Assert.Equal("hidden", ui.StyleOf(title, "overflow"));
        Assert.Equal("ellipsis", ui.StyleOf(title, "text-overflow"));
        Assert.Equal("nowrap", ui.StyleOf(title, "white-space"));
        Assert.Equal("0", ui.StyleOf(title, "min-width"));
    }

    /// <summary>
    ///     ⚠ <b>The ladder, and <em>only</em> the ladder, can produce this answer — and it is the
    ///     opposite of the answer this test asserted before the theme sheets were layered.</b>
    ///     <c>task-center { min-width: 280px }</c> used to win because it was unlayered and unlayered
    ///     beats every layer, so every chrome rule beat every utility silently and always. It is now
    ///     a <c>components</c> rule, <c>utilities</c> comes after <c>components</c>, and
    ///     <c>class="min-w-0"</c> does what somebody writing it plainly meant.
    ///     <para>
    ///         Specificity and source order are both still pointing the other way from what makes
    ///         this interesting — the utility is the more specific of the two and is loaded second, so
    ///         a careless reading of a pass here would be measuring either of those. What rules them
    ///         out is <see cref="With_the_components_layer_gone_the_hand_written_rule_wins_again" />,
    ///         which changes nothing but the layer and gets the old answer back.
    ///     </para>
    /// </summary>
    [Fact]
    public void A_utility_beats_a_chrome_rule_because_the_ladder_says_so() {
        using var ui = Editor();

        var panel = ui.Create("task-center", ui.Document.Root, null, "min-w-0");

        ui.Frame();

        Assert.Equal("0", ui.StyleOf(panel, "min-width"));
    }

    /// <summary>
    ///     ⚠ <b>The sabotage, kept in the suite rather than run once and thrown away — and pointed at
    ///     the other sheet now.</b> The utilities README records a layer test that passed with the
    ///     <c>@layer</c> wrapper replaced by <c>@media all</c>, because it was accidentally asserting
    ///     document order. This is that replacement, made permanent, and the sheet it is applied to
    ///     had to move: unlayering the <i>utility</i> sheet no longer changes the answer, because a
    ///     utility that beats a <c>components</c> rule by layer also beats it unlayered, by
    ///     specificity. Unlayering the <i>chrome</i> sheet does change it, and getting <c>280px</c>
    ///     back is what proves its twin above is measuring the ladder and not the load order.
    /// </summary>
    [Fact]
    public void With_the_components_layer_gone_the_hand_written_rule_wins_again() {
        using var ui = UiTest.Create();

        ui.Document.Load(Unlayered(EditorTheme.Css), StyleOrigin.UserAgent);
        ui.Document.Load(EditorStyles.Utilities, StyleOrigin.UserAgent);

        var panel = ui.Create("task-center", ui.Document.Root, null, "min-w-0");

        ui.Frame();

        Assert.Equal("280px", ui.StyleOf(panel, "min-width"));
    }

    /// <summary>
    ///     ⚠ <b>Why the whole ladder has to live inside one origin, shown rather than asserted in a
    ///     comment.</b> <c>CascadePrecedence</c> compares <c>OriginRank</c> <i>before</i>
    ///     <c>LayerRank</c>, so no arrangement of layers settles anything across an origin boundary.
    ///     Here the chrome sheet is <c>Author</c> and the utility sheet is <c>UserAgent</c>: the
    ///     chrome rule is in the lower layer and wins anyway, and it would win if it were in the
    ///     lowest layer there is. A design that reached for origins to express the base → components
    ///     → utilities ordering would look right and do nothing, which is the mistake this keeps
    ///     measured rather than remembered.
    /// </summary>
    [Fact]
    public void An_origin_outranks_the_whole_ladder() {
        using var ui = UiTest.Create();

        ui.Document.Load(EditorTheme.Css, StyleOrigin.Author);
        ui.Document.Load(EditorStyles.Utilities, StyleOrigin.UserAgent);

        var panel = ui.Create("task-center", ui.Document.Root, null, "min-w-0");

        ui.Frame();

        Assert.Equal("280px", ui.StyleOf(panel, "min-width"));
    }

    /// <summary>
    ///     A panel's own scoped <c>&lt;style&gt;</c> beats a utility, which is the other half of the
    ///     same fact: a component's block loads as <c>Author</c> through <c>Component</c>, and an
    ///     author rule beats a user-agent one whatever either of them says about layers.
    /// </summary>
    [Fact]
    public void A_panels_own_scoped_style_beats_a_utility() {
        using var ui = Editor();

        ui.Document.Load("task-title { min-width: 44px; }", StyleOrigin.Author);

        var title = ui.Create("task-title", ui.Document.Root, null, "min-w-0");

        ui.Frame();

        Assert.Equal("44px", ui.StyleOf(title, "min-width"));
    }

    /// <summary>An editor document with the whole sheet stack in it, as <c>EditorShell</c> builds one.</summary>
    static UiTest Editor() {
        var ui = UiTest.Create();

        EditorTheme.Install(ui.Document);

        return ui;
    }

    /// <summary>The same tokens the build step read, from the copy beside the test assembly.</summary>
    static ThemeTokens Tokens() => ThemeTokens.Parse(File.ReadAllText(Fixtures("vixen.ui.vcss")));

    static string Fixtures(string name) => Path.Combine(AppContext.BaseDirectory, "__fixtures__", name);

    /// <summary>A sheet with its block layer taken away, for the sabotage test.</summary>
    /// <remarks>
    ///     <c>@media all</c> rather than deleting the wrapper, because the braces have to stay
    ///     balanced and an at-rule that matches everything is the smallest change that keeps the file
    ///     parseable while removing the one thing under test. The <c>@layer base, components,
    ///     utilities;</c> statement at the top is left alone deliberately: it declares three empty
    ///     layers and contributes no rules, so what the replacement removes is the sheet's
    ///     <i>membership</i> of a layer and nothing else.
    /// </remarks>
    static string Unlayered(string css) =>
        css.Replace("@layer utilities {", "@media all {", StringComparison.Ordinal)
            .Replace("@layer components {", "@media all {", StringComparison.Ordinal);
}
