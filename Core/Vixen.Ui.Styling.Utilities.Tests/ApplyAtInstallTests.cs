// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary><c>@apply</c> in a sheet a document installs, which is where every real sheet arrives.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gap these close is that <c>@apply</c> was inert everywhere it could actually be
///         written.</b> <see cref="ApplyExpander" /> was reachable only from
///         <c>Tools/Vixen.StyleGen</c>, over files named by <c>@(VixenStyleBase)</c>, and no project
///         in the tree set that item — so the tests beside this file exercised the expander as a
///         function and nothing exercised it as a feature. A sheet with an <c>@apply</c> in it
///         reached ExCSS verbatim and the at-rule was dropped without a word.
///     </para>
///     <para>
///         ⚠ <b>The case worth the most is the last one, and it is the one that fails silently.</b>
///         At build time StyleGen holds every <c>@theme</c> before it expands anything. At install
///         time the sheets arrive one at a time, so an <c>@apply p-4</c> can be expanded before the
///         sheet that sets <c>--spacing</c> has been loaded — and because
///         <see cref="ThemeTokens.CreateDefault" /> answers every namespace, the result is not a
///         diagnostic but a wrong number. Nothing about the rendered UI says which.
///     </para>
/// </remarks>
public class ApplyAtInstallTests {
    /// <summary>The theme the ordering cases turn on: a spacing unit that is not the shipped one.</summary>
    /// <remarks>
    ///     Ten rather than a near-miss, so the two candidate answers — 40px with the theme, 16px
    ///     without it — cannot be confused with a rounding difference in a failure message.
    /// </remarks>
    const string TenPixelSpacing = "@theme { --spacing: 10px; }";

    /// <summary>What one element's <c>padding-left</c> came out as, through the whole pass.</summary>
    static float? Padding(UiDocument document, string className) {
        var element = document.Root.Add("div", classNames: className);
        document.Update();

        return document.LengthOf(element.Style, document.PropertyId("padding-left"));
    }

    /// <summary>The base fact: an installed <c>@apply</c> produces declarations.</summary>
    /// <remarks>
    ///     Sabotage is one line — remove <c>Styles.Preprocessor = ExpandApply</c> from
    ///     <c>UiDocument</c>'s constructor — and this returns null, which is precisely the state the
    ///     whole repository was in.
    /// </remarks>
    [Fact]
    public void An_apply_in_an_installed_sheet_is_expanded_rather_than_dropped() {
        using var document = new UiDocument(200f, 200f);

        document.Load(".card { @apply p-4; }", StyleOrigin.UserAgent);

        Assert.Equal(16f, Padding(document, "card"));
    }

    /// <summary>The declarations land in the block they were written in, beside the hand-written ones.</summary>
    /// <remarks>
    ///     What makes <c>@apply</c> different from "load a utility sheet as well": the expansion is
    ///     part of the rule, so it carries that rule's selector and that rule's specificity, and a
    ///     declaration written after it wins.
    /// </remarks>
    [Fact]
    public void An_apply_shares_its_block_with_what_was_written_beside_it() {
        using var document = new UiDocument(200f, 200f);

        document.Load(".card { @apply p-4 flex; padding-left: 3px; }", StyleOrigin.UserAgent);

        var element = document.Root.Add("div", classNames: "card");
        document.Update();

        // The later declaration beats the expansion, because they are in one block in source order.
        Assert.Equal(3f, document.LengthOf(element.Style, document.PropertyId("padding-left")));

        // And the rest of the expansion is still there.
        Assert.Equal(16f, document.LengthOf(element.Style, document.PropertyId("padding-right")));
        Assert.True(element.Style.TryGet(document.PropertyId("display"), out var display));
        Assert.Equal("flex", document.Styles.Values.NameOf(display));
    }

    /// <summary>The theme arriving first, which is the easy half and the control for the next test.</summary>
    [Fact]
    public void An_apply_reads_a_theme_that_was_loaded_before_it() {
        using var document = new UiDocument(200f, 200f);

        document.Load(TenPixelSpacing, StyleOrigin.UserAgent);
        document.Load(".card { @apply p-4; }", StyleOrigin.UserAgent);

        Assert.Equal(40f, Padding(document, "card"));
    }

    /// <summary>
    ///     ⚠ <b>The ordering case: a sheet that defines the unit is installed <i>after</i> the sheet
    ///     that spends it, and the answer has to be the same.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is not a hypothetical arrangement, it is the only one the engine has.
    ///         <c>EditorShell</c> installs <c>ControlTheme</c>, then <c>AdvancedTheme</c>, then
    ///         <c>EditorTheme</c> — and the tokens live in the last of the three. Expanding each
    ///         sheet against what was known when it arrived would give the first two the shipped
    ///         palette's numbers and the third the editor's, in the same document, with nothing
    ///         anywhere saying so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted against an oracle first and a literal second, so that getting the
    ///         ordering wrong is a failure rather than a different number.</b> The oracle is the same
    ///         utility taken through the generator against the same merged theme — the path that has
    ///         always been right — so the claim under test is "an <c>@apply p-4</c> means what a
    ///         <c>class="p-4"</c> means", which stays true if the spacing scale is ever redefined.
    ///         The literal is there because an oracle wrong in the same way would agree with it, and
    ///         16px is named because it is the specific wrong answer this can silently produce.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_apply_is_measured_against_a_theme_that_arrives_after_it() {
        using var document = new UiDocument(200f, 200f);

        document.Load(".card { @apply p-4; }", StyleOrigin.UserAgent);
        document.Load(TenPixelSpacing, StyleOrigin.UserAgent);

        var tokens = ThemeTokens.CreateDefault();
        tokens.Apply(TenPixelSpacing);

        using var oracle = new UiDocument(200f, 200f);
        oracle.Load(new UtilityGenerator(tokens).Generate(["p-4"]), StyleOrigin.UserAgent);

        Assert.Equal(Padding(oracle, "p-4"), Padding(document, "card"));
        Assert.Equal(40f, Padding(document, "card"));

        // The number an expansion against the half-built theme produces. Named rather than merely
        // excluded by the assertion above, because it is what a reader of a failure needs to see.
        Assert.NotEqual(16f, Padding(document, "card"));
    }

    /// <summary>The re-expansion replays every sheet, not only the one holding the <c>@apply</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The reload throws the whole rule set away and rebuilds it, so a sheet that had
    ///     nothing to do with the tokens has to survive it intact.</b> That is the risk the mechanism
    ///     carries and this is the assertion that says it does not fire: two ordinary rules, loaded
    ///     either side of the one that gets re-expanded, still apply afterwards.
    /// </remarks>
    [Fact]
    public void The_reload_a_late_theme_causes_keeps_every_other_rule() {
        using var document = new UiDocument(200f, 200f);

        document.Load(".before { padding-left: 7px; }", StyleOrigin.UserAgent);
        document.Load(".card { @apply p-4; }", StyleOrigin.UserAgent);
        document.Load(TenPixelSpacing, StyleOrigin.UserAgent);
        document.Load(".after { padding-left: 9px; }", StyleOrigin.UserAgent);

        Assert.Equal(7f, Padding(document, "before"));
        Assert.Equal(40f, Padding(document, "card"));
        Assert.Equal(9f, Padding(document, "after"));
    }

    /// <summary>A theme that lands after nothing has used <c>@apply</c> costs no reload.</summary>
    /// <remarks>
    ///     ⚠ <b>The guard, checked by its side effect, because that is the only public witness.</b>
    ///     <c>StyleEngine.Reload</c> rebuilds the rule set and everything derived from it, so the
    ///     <c>Rules</c> object is a different one afterwards; nothing counts reloads. Every document
    ///     in this repository is this case — no sheet in the tree contains an <c>@apply</c> yet — so
    ///     a mechanism that reloaded whenever it saw an <c>@theme</c> would throw away the interning
    ///     cache and every animation in flight each time a panel installed a sheet, for nothing.
    /// </remarks>
    [Fact]
    public void A_theme_that_nothing_has_applied_yet_does_not_reload_the_document() {
        using var document = new UiDocument(200f, 200f);

        document.Load(".card { padding-left: 7px; }", StyleOrigin.UserAgent);

        var rules = document.Styles.Rules;
        document.Load(TenPixelSpacing, StyleOrigin.UserAgent);

        Assert.Same(rules, document.Styles.Rules);
        Assert.Equal(7f, Padding(document, "card"));
    }

    /// <summary>And the same witness, the other way round, so the test above is not vacuous.</summary>
    [Fact]
    public void A_theme_that_something_has_applied_does_reload_the_document() {
        using var document = new UiDocument(200f, 200f);

        document.Load(".card { @apply p-4; }", StyleOrigin.UserAgent);

        var rules = document.Styles.Rules;
        document.Load(TenPixelSpacing, StyleOrigin.UserAgent);

        Assert.NotSame(rules, document.Styles.Rules);
    }

    /// <summary>A hot-reloaded theme reaches an <c>@apply</c> in a sheet nobody saved.</summary>
    /// <remarks>
    ///     The case the sheet-count staleness check cannot see, because replacing a sheet leaves the
    ///     count where it was. Editing <c>--spacing</c> in a theme file and watching the panels not
    ///     move would be indistinguishable from a watcher that had died.
    /// </remarks>
    [Fact]
    public void Saving_a_theme_re_expands_the_applies_in_the_other_sheets() {
        using var document = new UiDocument(200f, 200f);

        document.Load(".card { @apply p-4; }", StyleOrigin.UserAgent);
        var theme = document.Load("@theme { --spacing: 4px; }", StyleOrigin.UserAgent);

        Assert.Equal(16f, Padding(document, "card"));

        document.ReloadStyles(theme, TenPixelSpacing);

        Assert.Equal(40f, Padding(document, "card"));
    }

    /// <summary>The text the engine keeps is the text it was given, not the expansion.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes the re-expansion possible at all, and what <c>HotReloadHost</c> depends
    ///     on.</b> A failed reload puts a sheet back from <c>SheetText</c>; if that returned the
    ///     expanded form, the rollback would install a sheet with no <c>@apply</c> left in it and the
    ///     next theme change would have nothing to re-expand.
    /// </remarks>
    [Fact]
    public void The_sheet_the_engine_keeps_is_the_one_that_was_written() {
        using var document = new UiDocument(200f, 200f);

        const string written = ".card { @apply p-4; }";
        var sheet = document.Load(written, StyleOrigin.UserAgent);

        Assert.Equal(written, document.Styles.SheetText(sheet));
    }
}
