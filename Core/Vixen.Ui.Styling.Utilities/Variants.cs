// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>What a variant does to the rule it prefixes.</summary>
/// <param name="SelectorSuffix">Appended to the class selector — <c>:hover</c>, <c>[data-x]</c>.</param>
/// <param name="SelectorPrefix">Prepended, for the variants that need an ancestor — <c>.dark </c>.</param>
/// <param name="AtRule">An at-rule to wrap the whole thing in, such as a media query.</param>
public readonly record struct VariantEffect(string SelectorSuffix, string SelectorPrefix, string? AtRule) {
    /// <summary>
    ///     The property this variant moves the utility's declaration from, and the one it moves it
    ///     to — or <c>null</c> for every variant that only touches the selector.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The shape <c>selection:</c> needed and the three strings could not say.</b>
    ///     <c>TextField</c> paints its highlight from <c>--selection-color</c> on its own style, so
    ///     <c>selection:bg-blue-200</c> is a <i>property</i> rewrite — <c>background-color</c> becomes
    ///     <c>--selection-color</c> — and no suffix, prefix or at-rule expresses that. A utility whose
    ///     declarations are not all <c>From</c> is refused by the generator rather than emitted
    ///     unmoved, because <c>selection:text-white</c> as <c>color: white</c> is a class that means
    ///     something else.
    /// </remarks>
    public (string From, string To)? Rewrite { get; init; }
}

/// <summary>Turns a variant prefix into what it does to the selector.</summary>
/// <remarks>
///     <para>
///         Variants are the reason a utility system scales past the trivial. Without them a hover
///         state means a hand-written rule, and the whole argument — that styling a new panel is
///         zero new CSS — falls over at the first button.
///     </para>
///     <para>
///         Three shapes, and they compose. Most are a <b>suffix</b> on the selector
///         (<c>hover:</c> → <c>:hover</c>). Breakpoints and <c>dark:</c> under the media strategy are
///         an <b>at-rule</b> around it. <c>group-*</c>, <c>peer-*</c> and <c>dark:</c> under the
///         class strategy need something <i>before</i> the element, which is the only reason
///         <see cref="VariantEffect.SelectorPrefix" /> exists.
///     </para>
///     <para>
///         ⚠ <b>A fourth shape is an at-rule that is not a media query</b>: <c>@sm:</c>,
///         <c>@max-lg:</c>, <c>@min-[480px]:</c> and their <c>/name</c> forms wrap the rule in a
///         <c>@container</c> instead. Nothing about the mechanism had to change for it —
///         <see cref="VariantEffect.AtRule" /> is already a string and
///         <c>UtilityGenerator</c> already nests a chain of them — which is why the blocker was
///         never the wiring: it was that the <c>--container-*</c> scale did not exist, so the only
///         numbers <c>@sm</c> could have been resolved against were the breakpoints, and those are
///         a window's rather than a box's.
///     </para>
///     <para>
///         ⚠ <b>And one shape that is not about the selector at all</b>: <c>selection:</c> moves the
///         utility's <i>property</i> — <c>background-color</c> onto <c>--selection-color</c>, which
///         is what the text controls paint their highlight from — through
///         <see cref="VariantEffect.Rewrite" />. Everything else here only decides where a rule
///         applies; this decides what it says.
///     </para>
///     <para>
///         The arbitrary form <c>[&amp;>*]:</c> substitutes the selector for the <c>&amp;</c>, which
///         is the escape hatch that stops the variant list from having to be complete. It is also
///         why the class-name parser has to be bracket-aware: that variant contains a <c>&gt;</c>
///         and could contain a <c>:</c>.
///     </para>
/// </remarks>
public static class Variants {
    static readonly Dictionary<string, string> States = new(StringComparer.Ordinal) {
        ["hover"] = ":hover",
        ["focus"] = ":focus",
        ["focus-visible"] = ":focus-visible",
        ["focus-within"] = ":focus-within",
        ["active"] = ":active",
        ["disabled"] = ":disabled",
        ["enabled"] = ":enabled",
        ["checked"] = ":checked",
        ["first"] = ":first-child",
        ["last"] = ":last-child",
        ["only"] = ":only-child",
        ["odd"] = ":nth-child(2n+1)",
        ["even"] = ":nth-child(2n)",
        ["empty"] = ":empty",

        // ⚠ The three of-type keywords needed a matcher change, which is the one claim the task they
        // came from got wrong. A child index is stored on every element; an of-type index is a
        // position among the siblings sharing a tag and is counted on demand — see
        // `StyleTree.TypeIndexOf`. Registering these against the old matcher would have refused them
        // at compile time, which is the honest failure; registering them against a matcher that
        // folded them into the child tests would have been the quiet one.
        ["first-of-type"] = ":first-of-type",
        ["last-of-type"] = ":last-of-type",
        ["only-of-type"] = ":only-of-type",

        // ⚠ The three of A13's seventeen that had a control behind them, and the ratio was the
        // finding rather than the count. Of the fourteen that named a model this framework did not
        // have, five are still refused and each one now carries the condition that reverses it —
        // because this table's own history is refusals expiring unobserved. `open` was refused for
        // "ExCSS cannot parse `:open`", which is still literally true and stopped being a blocker
        // the day `:user-valid` shipped with the identical problem; the eight form-validity names
        // were refused for "there is no validation anywhere in `Vixen.Ui.Controls`", which stopped
        // being true without anyone coming back here. ⚠ <b>Both were found by a person re-reading
        // the sentence, and a fourth audit would have been the only thing standing between the next
        // one and another year.</b> Re-checked at HEAD on 2026-09-06 and again on 2026-09-22, and
        // all five still hold — `Forms.cs` is `LabeledContent` and not a form, and
        // `Vixen.Ui.Controls/Navigation.cs` is a breadcrumb and a pager over `ButtonBase` with no
        // URL, no history and no fragment anywhere behind them:
        //
        //   `visited` — nothing records that a place has been visited
        //     [expires-on Vixen.Ui.Styling.ElementState.Visited]
        //   `target` — no fragment identifies an element as the one navigated to
        //     [expires-on Vixen.Ui.Styling.ElementState.Target]
        //   `autofill` — no credential store, so no field is ever filled by one
        //     [expires-on Vixen.Ui.Controls.TextField.Autofilled]
        //   `default` — the default button announces itself with a `default` CLASS, not a state bit
        //     [expires-on Vixen.Ui.Styling.ElementState.Default]
        //   `inert` — a subtree flag nothing carries; `Disabled` is per control and does not descend
        //     [expires-on Vixen.Ui.UiElement.Inert]
        //
        // ⚠ <b>`default`'s anchor FIRED on 2026-09-22 and the refusal survived it, which is the
        // third worked case and the first of this shape.</b> #666 landed `Button.IsDefault`, the
        // clause went red, and re-reading the sentence is what it bought: the refusal read "there is
        // no form" and that half is STILL true — `IsDefault`'s one production caller is
        // `DialogService.AddButton`, inside an `Overlay`, and `IsCancel` has none at all for the
        // reason its own remarks give. But the sentence was wrong about which thing was missing.
        // What `IsDefault` writes is a key equivalent and a `default` CLASS, deliberately, so a
        // theme can already draw the button Return will press; what `:default` needs is an
        // `ElementState` bit, and `ElementState` has none. Registering it against the arrival would
        // have been this table's own named defect — an entry with no writer, matching nothing —
        // committed because a tripwire went red. ⚠ <b>So the anchor moved to the thing genuinely
        // absent rather than the plausible-sounding one</b>, which is `ring-offset-*`'s lesson
        // applied a step earlier: the first spelling named a design decision (how a button says it
        // is the default) and had two answers, of which the class was chosen; the bit has one.
        //
        // ⚠ <b>Three anchors now name the BIT rather than the model — the two navigation ones and,
        // since the fire above, `default` — and that is a limitation of the clause grammar rather
        // than a choice.</b> `expires-on` requires the type half to resolve today, so a refusal
        // waiting on a whole concept that has no type yet — a URL, a history, a form — has nothing
        // to hang on but the state bit the concept would eventually write. It is the weaker
        // tripwire: it fires on whoever lands the bit, not on whoever lands the model. ⚠ But the
        // fire above is the argument that weaker-and-exact beats stronger-and-approximate: naming a
        // member of a type that exists is only the stronger form when that member is the thing
        // actually missing, and `Button.IsDefault` was a guess at how a default button would be
        // spelled rather than the bit `:default` needs. The remaining two — `autofill` and `inert` —
        // name a member of a type that exists AND the thing genuinely absent, which is the strong
        // form on both counts.
        //
        // ⚠ <b>A table entry here is worth nothing without a writer</b>, which is what the item this
        // came from underestimated: `:read-only` compiled against a bit no control sets resolves,
        // indexes and matches nothing at all. `TextField` writes two of these and `CheckBox` and
        // `ProgressBar` write the third.
        ["read-only"] = ":read-only",
        ["placeholder-shown"] = ":placeholder-shown",
        ["indeterminate"] = ":indeterminate",

        // ⚠ <b>The eight form-validity names were refused for "there is no validation anywhere in
        // `Vixen.Ui.Controls`", and that stopped being true without anyone coming back here</b> —
        // which is the finding rather than these four lines. `TextField` carries `Required`,
        // `Validator`, `Validate`, `Revalidate`, `ValidationMessage` and `IsValid`, and it is the
        // writer for all four registered below.
        //
        // ⚠ <b>All eight now, and the last two took a rewrite rather than a bit.</b> `user-valid` and
        // `user-invalid` were `:open`'s problem — ExCSS 4.3.2 has no literal for either name, so the
        // whole compound came back as an `UnknownSelector` and no pseudo-class code ever ran. What
        // unblocked them was not a parser upgrade: `SelectorCompiler` already re-reads a selector
        // ExCSS could not parse, for `:where()`, and both names now ride that same scan.
        ["required"] = ":required",
        ["optional"] = ":optional",
        ["valid"] = ":valid",
        ["invalid"] = ":invalid",

        // ⚠ <b>These two were refused for "the condition cannot be held", and the cure was in the
        // control rather than here.</b> `NumericInput` clamped to `[Minimum, Maximum]` in its
        // coerce, so a value outside the range could not exist for any length of time by any route
        // and `:out-of-range` would have compiled, indexed and matched nothing — the exact failure
        // this table exists to refuse. It now holds what it is given and reports the violation, and
        // only the arrows, the spinner and the scrub still clamp.
        ["in-range"] = ":in-range",
        ["out-of-range"] = ":out-of-range",

        // ⚠ Two bits each rather than one, which is what makes these different from `valid:` and
        // `invalid:` above rather than a rename of them: the verdict *and* the user having had a go.
        // `TextField` is the writer, and it never clears the interaction — what changes back is the
        // verdict, since having been in a field is not something that stops being true.
        ["user-valid"] = ":user-valid",
        ["user-invalid"] = ":user-invalid",

        // ⚠ <b>The refusal above says `open` is "refused one layer further out, by the parser", and
        // that sentence is true and stopped being a blocker in the same batch that wrote it.</b>
        // `:user-valid` had the identical problem and was not solved by a parser upgrade — it rides
        // `SelectorCompiler.TryRewrite`, which re-reads a selector ExCSS could not parse, and there
        // is nothing about `:open` that needs a second mechanism. The bit is `ElementState.Open`;
        // `Expander` and `SelectBase` are the writers, which is the half a table entry cannot be
        // worth anything without.
        ["open"] = ":open"

        // ⚠ <b>`placeholder` and `selection` are NOT here, and the reason is not the one A12's audit
        // trail records.</b> `Rikarin/Vixen#233` is refused as a whole on the generated box — nothing
        // materialises `::before`, `::after` or `::marker` — and its last pass offers these two as
        // the lead that could land without that machinery, because each "names a box or a run that
        // already exists". Measured at HEAD, they are two different problems and only one of them is
        // a selector problem at all:
        //
        //   `::placeholder` really is an element. `TextField` builds it as `Part("field-placeholder")`
        //   — a direct child with its own tag, styled by `ControlTheme.vcss` at `field-placeholder`
        //   and `.empty field-placeholder`. So a variant for it is a selector rewrite and nothing
        //   more. ⚠ But it cannot be a row in THIS table, and that is the trap: four other variants
        //   compose over it. `not-`, `has-`, `group-` and `peer-` all read a `States` value and wrap
        //   or prefix it, so a DESCENDANT-shaped suffix gives `not-placeholder:` the selector
        //   `:not( field-placeholder)` — an element that is not a placeholder, rather than a field
        //   with no placeholder — and `group-placeholder:` the prefix `.group field-placeholder `.
        //   Every one of those is valid CSS meaning something else, which is F6's own failure mode
        //   one level up. It lives in `Parts` below — a category of its own, with coverage rows of
        //   its own.
        //
        //   `::selection` is not a box. `TextField` paints the highlight itself, from a colour it
        //   reads off its OWN style as the custom property `--selection-color` — see the
        //   `selectionColor` id it interns and the `ColorOf` beside the fallback. So
        //   `selection:bg-blue-200` has to rewrite the utility's PROPERTY rather than its selector,
        //   which three strings could not say; it is `Rewrites` below and `VariantEffect.Rewrite`,
        //   a fourth shape rather than a generated box.
    };

    /// <summary>The variants that move a utility's declaration onto the property a control reads.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One entry, and a table rather than a branch because it is the shape and not the
    ///         entry that is new.</b> v4's <c>selection:</c> is <c>&amp; *::selection, &amp;::selection</c>
    ///         — the element and everything in it. Here no box is selected text; <c>TextField</c> and
    ///         <c>CodeEditor</c> paint their bands from <c>--selection-color</c>, and a custom property
    ///         inherits, so writing it on the element reaches the element and every descendant the
    ///         way v4's two selectors do, without either of them.
    ///     </para>
    ///     <para>
    ///         Only the background moves, because only the background has a reader: v4's
    ///         <c>selection:text-white</c> recolours the selected glyphs, and nothing here paints
    ///         selected glyphs in a colour of their own. It is refused — not a class — rather than
    ///         emitted as a <c>color</c> on the element.
    ///     </para>
    /// </remarks>
    static readonly Dictionary<string, (string From, string To)> Rewrites = new(StringComparer.Ordinal) {
        ["selection"] = ("background-color", "--selection-color")
    };

    /// <summary>The variants that rewrite a utility's property rather than its selector.</summary>
    /// <remarks>
    ///     Exposed for the reason <see cref="StateVariants" /> is: a second entry with no test that a
    ///     control actually reads its property fails the build rather than joining the silent ones.
    /// </remarks>
    public static IReadOnlyCollection<string> RewriteVariants => Rewrites.Keys;

    /// <summary>The variants that name a <i>part</i> of a control — a child box that already exists.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A table of its own and not one more row in <see cref="States" />, because four
    ///         variants compose over that table and every one of them would mean something else over
    ///         a child combinator.</b> <c>not-</c> wraps a state in <c>:not()</c>, <c>has-</c> in
    ///         <c>:has()</c>, <c>group-</c> and <c>peer-</c> put it on an ancestor or a sibling. A
    ///         suffix that descends — <c>&gt; field-placeholder</c> — read through any of them is
    ///         either not a selector (<c>:not(&gt; field-placeholder)</c>) or a valid one selecting
    ///         the wrong thing (<c>.group &gt; field-placeholder </c> as a prefix asks for the
    ///         placeholder's descendants). Keeping the two tables apart is what lets the four compose
    ///         over <see cref="States" /> without a case each, and it is why
    ///         <c>not-placeholder:</c> is <i>not a class</i> rather than a class that styles a field
    ///         with no placeholder.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the half of A12 (<c>Rikarin/Vixen#233</c>) that needs no generated box,
    ///         and it is three entries.</b> v4's <c>placeholder:</c> is <c>&amp;::placeholder</c>, and
    ///         <c>TextField.OnCreated</c> already builds that box as <c>Part("field-placeholder")</c>,
    ///         a direct child with a tag of its own that <c>ControlTheme.vcss</c> styles at
    ///         <c>field-placeholder</c>. So the variant is a child combinator onto that tag and
    ///         nothing more. <c>before</c>, <c>after</c> and <c>marker</c> name boxes nothing
    ///         generates; <c>selection</c> names a colour the field reads off its own style; no
    ///         control here is a file input, so <c>file</c> names nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>backdrop</c> was listed with <c>file</c> as naming "controls that do not
    ///         exist", and that was false.</b> v4's <c>backdrop:</c> is <c>&amp;::backdrop</c>, the
    ///         box a modal <c>&lt;dialog&gt;</c> draws behind itself over the page. Both modal
    ///         overlays here build that box: <c>Dialog.OnCreated</c> makes
    ///         <c>Part("dialog-backdrop")</c> and <c>Drawer.OnCreated</c> makes
    ///         <c>Part("drawer-backdrop")</c>, each a direct child that <c>ControlTheme.vcss</c>
    ///         styles and paints (#233). So it is <see cref="PlaceholderPart" />'s shape over the two
    ///         tags, as <c>:is()</c>. The class goes on the overlay, which is where v4 puts it on the
    ///         <c>&lt;dialog&gt;</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The child combinator and not the descendant one.</b> The part is a direct child
    ///         of the control that owns it, so <c>&gt;</c> is what the control says. A space would
    ///         reach a field nested anywhere below — a filter row inside a panel that is itself
    ///         inside a field's ancestor — and an outer <c>placeholder:text-red</c> would colour the
    ///         inner field's prompt. <c>VariantCoverageTests</c> holds a grandchild row for exactly
    ///         that.
    ///     </para>
    /// </remarks>
    static readonly Dictionary<string, string> Parts = new(StringComparer.Ordinal) {
        ["placeholder"] = PlaceholderPart,
        ["backdrop"] = BackdropPart,
        ["details-content"] = DetailsContentPart
    };

    /// <summary>The child <c>Expander</c> builds its collapsible body as, as a scope onto the expander.</summary>
    /// <remarks>
    ///     ⚠ <b>The third part variant, and the list that held it said it waited on the generated box
    ///     (#233).</b> v4's <c>details-content:</c> is <c>&amp;::details-content</c>, the box a
    ///     <c>&lt;details&gt;</c> puts everything except its <c>&lt;summary&gt;</c> in, hidden while
    ///     it is closed. <c>Expander</c> is that element here: <c>ExpanderHeader</c> is the summary,
    ///     and <c>Expander.OnCreated</c> builds the body as <c>Part("expander-content")</c>, a direct
    ///     child <c>ControlTheme.vcss</c> keeps at <c>display: none</c> until <c>.open</c>. So it is
    ///     <see cref="PlaceholderPart" />'s shape, a child combinator onto that tag, and the class goes
    ///     on the expander as v4 puts it on the <c>&lt;details&gt;</c>.
    /// </remarks>
    internal const string DetailsContentPart = " > expander-content";

    /// <summary>The children <c>Dialog</c> and <c>Drawer</c> build the sheet behind them as, as a scope onto the overlay.</summary>
    /// <remarks>
    ///     ⚠ <b>Both tags, because an author writing <c>backdrop:</c> means the modal's backdrop
    ///     whichever modal it is.</b> Naming only <c>dialog-backdrop</c> would leave a drawer's class
    ///     styling nothing, which looks exactly like the variant working on dialogs only by accident.
    ///     <c>:is()</c> takes the more specific of its arguments, and both are one type selector, so
    ///     the rule has the same specificity it would have over either tag alone.
    /// </remarks>
    internal const string BackdropPart = " > :is(dialog-backdrop, drawer-backdrop)";

    /// <summary>The child <c>TextField</c> builds its prompt as, as a scope onto the control.</summary>
    /// <remarks>
    ///     One constant for the variant and for the <c>placeholder-*</c> colour family in
    ///     <c>UtilityFamilies</c>, because both are "the part the field calls its placeholder" and
    ///     two spellings of that would be two things to move the day the control renames it.
    /// </remarks>
    internal const string PlaceholderPart = " > field-placeholder";

    /// <summary>The variants that are a media feature rather than a selector.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two of these were already answerable and needed no condition at all.</b>
    ///         <c>portrait</c> and <c>landscape</c> are <c>(orientation: …)</c>, which
    ///         <c>MediaQuery</c> has always derived from the surface's own width and height — so
    ///         they were a table entry and nothing else, while the item they arrived in was sized as
    ///         "one condition each". The other axes did need one, and needed a field on
    ///         <c>MediaContext</c> to answer it from.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>print</c> and <c>noscript</c> resolve and can never match, deliberately.</b>
    ///         Paged media is permanently out of scope and a Vixen document always scripts, so both
    ///         are one comparison that is false — which is worth having rather than refusing,
    ///         because a stylesheet shared with a web codebase then loads unchanged instead of
    ///         failing a block. They are the two entries a coverage gate cannot ask for a positive
    ///         scene from, and <c>VariantCoverageTests</c> names them for that reason.
    ///     </para>
    /// </remarks>
    static readonly Dictionary<string, string> MediaFeatures = new(StringComparer.Ordinal) {
        ["motion-safe"] = "(prefers-reduced-motion: no-preference)",
        ["motion-reduce"] = "(prefers-reduced-motion: reduce)",
        ["contrast-more"] = "(prefers-contrast: more)",
        ["contrast-less"] = "(prefers-contrast: less)",
        ["forced-colors"] = "(forced-colors: active)",
        ["inverted-colors"] = "(inverted-colors: inverted)",
        ["portrait"] = "(orientation: portrait)",
        ["landscape"] = "(orientation: landscape)",
        ["print"] = "print",
        ["noscript"] = "(scripting: none)",
        ["pointer-none"] = "(pointer: none)",
        ["pointer-coarse"] = "(pointer: coarse)",
        ["pointer-fine"] = "(pointer: fine)",
        ["any-pointer-none"] = "(any-pointer: none)",
        ["any-pointer-coarse"] = "(any-pointer: coarse)",
        ["any-pointer-fine"] = "(any-pointer: fine)"
    };

    /// <summary>The variants that are one <c>@media</c> feature on the element's own surface.</summary>
    /// <remarks>
    ///     Exposed for the same reason <see cref="StateVariants" /> is: the coverage test enumerates
    ///     it, so a seventeenth entry with no scene fails the build rather than joining the silent
    ///     ones. That gate is what a whole dead breakpoint family cost before it existed.
    /// </remarks>
    public static IReadOnlyCollection<string> MediaVariants => MediaFeatures.Keys;

    /// <summary>The <c>nth-*</c> families, longest prefix first.</summary>
    /// <remarks>
    ///     ⚠ <b>Order is the whole of this table.</b> <c>nth-last-of-type-3</c> begins with
    ///     <c>nth-last-</c> and with <c>nth-</c>, so a shorter prefix tested first would resolve it
    ///     to <c>:nth-last-child(of-type-3)</c> — a selector ExCSS refuses, which is at least loud,
    ///     and <c>nth-of-type-3</c> to <c>:nth-child(of-type-3)</c>, which is the same shape.
    /// </remarks>
    static readonly (string Prefix, string Function)[] NthFamilies = [
        ("nth-last-of-type-", "nth-last-of-type"),
        ("nth-of-type-", "nth-of-type"),
        ("nth-last-", "nth-last-child"),
        ("nth-", "nth-child")
    ];

    /// <summary>The variants that are a pseudo-class on the element itself.</summary>
    /// <remarks>
    ///     ⚠ <b>Exposed so that a test can fail on a variant nobody tested, which is the shape of
    ///     failure this table has actually had.</b> Every entry below <c>hover</c> and <c>focus</c>
    ///     went untested end to end, and a whole variant family being inert while the suite stayed
    ///     green is not hypothetical here — every breakpoint was dead until the document started
    ///     handing the cascade a <see cref="MediaContext" />. A list the coverage test can enumerate
    ///     turns "someone remembered" into "the build checks", and it is also what
    ///     <c>group-*</c> and <c>peer-*</c> compose over, so one table drives all three.
    /// </remarks>
    public static IReadOnlyCollection<string> StateVariants => States.Keys;

    /// <summary>The variants that select a part of a control rather than a state of it.</summary>
    /// <remarks>
    ///     Exposed for the reason <see cref="StateVariants" /> is: <c>VariantCoverageTests</c>
    ///     enumerates it, so a second part registered without a scene proving that the child is
    ///     styled and the element is not fails the build rather than joining the silent ones.
    /// </remarks>
    public static IReadOnlyCollection<string> PartVariants => Parts.Keys;

    /// <summary>Works out what a variant does.</summary>
    /// <param name="variant">The variant, without its colon.</param>
    /// <param name="tokens">The theme, for breakpoints and the dark-mode strategy.</param>
    /// <param name="effect">Receives what it does.</param>
    /// <returns>Whether it is a variant this system knows.</returns>
    public static bool TryResolve(string variant, ThemeTokens tokens, out VariantEffect effect) {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(tokens);

        effect = default;

        if (States.TryGetValue(variant, out var state)) {
            effect = new VariantEffect(state, string.Empty, null);
            return true;
        }

        if (Parts.TryGetValue(variant, out var part)) {
            effect = new VariantEffect(part, string.Empty, null);
            return true;
        }

        if (Rewrites.TryGetValue(variant, out var rewrite)) {
            // No suffix, so `not-` and `has-` — which wrap one — refuse it below, and `group-` and
            // `peer-` never see it: they read `States`.
            effect = new VariantEffect(string.Empty, string.Empty, null) { Rewrite = rewrite };
            return true;
        }

        if (TryScreen(variant, tokens, out effect)) {
            return true;
        }

        if (variant.Length > 1 && variant[0] == '@' && TryContainer(variant.AsSpan(1), tokens, out effect)) {
            return true;
        }

        if (MediaFeatures.TryGetValue(variant, out var feature)) {
            effect = new VariantEffect(string.Empty, string.Empty, $"@media {feature}");
            return true;
        }

        if (TryNth(variant, out effect)) {
            return true;
        }

        // ⚠ Only over a variant that is a bare suffix, which rules out more than it looks like.
        // `not-sm:` is `@media not (min-width: …)` in v4 and `not-dark:` under the class strategy is
        // a selector with an ancestor in it; negating a *prefix* is not negating the rule, and
        // negating an at-rule is a different production entirely. Refusing them here means
        // `not-sm:p-4` is not a class rather than being a class that means something else — the
        // distinction F6 was written about. The arbitrary form is refused for the third reason: its
        // `&` has to land somewhere, and `:not(&>*)` is not a selector.
        // ⚠ The same bare-suffix rule `not-` follows, plus one refusal of its own. A `:has()`
        // argument that begins with a combinator — v4's `has-[>_.x]` — is a *relative* selector, and
        // ExCSS 4.3.2 parses `:has(> .x)` into the same node it parses `:has(.x)` into: the
        // combinator is gone before the compiler can refuse it, and the rule would silently mean
        // "any descendant" where the author wrote "a child". This is the one place the text is still
        // intact, so this is where it is refused.
        if (variant.StartsWith("has-", StringComparison.Ordinal)
            && TryResolve(variant["has-".Length..], tokens, out var contained)
            && contained is { SelectorPrefix.Length: 0, AtRule: null, SelectorSuffix.Length: > 0 }
            && !IsArbitrary(contained)
            && IsWrappable(contained.SelectorSuffix)) {
            effect = new VariantEffect($":has({contained.SelectorSuffix})", string.Empty, null);
            return true;
        }

        // ⚠ And the combinator refusal `has-` makes, for a different reason: `:not(> x)` is not a
        // selector at all, so a part variant read through `not-` would reach the compiler as text
        // it refuses. Refusing it here keeps `not-placeholder:` "not a class" rather than a
        // diagnostic about a class — the distinction the whole `Parts` table exists for.
        if (variant.StartsWith("not-", StringComparison.Ordinal)
            && TryResolve(variant["not-".Length..], tokens, out var negated)
            && negated is { SelectorPrefix.Length: 0, AtRule: null, SelectorSuffix.Length: > 0 }
            && !IsArbitrary(negated)
            && IsWrappable(negated.SelectorSuffix)) {
            effect = new VariantEffect($":not({negated.SelectorSuffix})", string.Empty, null);
            return true;
        }

        if (variant.Equals("dark", StringComparison.Ordinal)) {
            effect = tokens.DarkMode == DarkModeStrategy.Class
                ? new VariantEffect(string.Empty, ".dark ", null)
                : new VariantEffect(string.Empty, string.Empty, "@media (prefers-color-scheme: dark)");

            return true;
        }

        if (variant is "ltr" or "rtl") {
            // The same shape as `dark:` under the class strategy — an ancestor declares it and the
            // utility applies below. An *attribute* rather than a class because `direction` is a CSS
            // property here, so there is nothing else in the tree for a selector to match on; the
            // consequence is that an element cannot select on its own direction, only an ancestor's.
            effect = new VariantEffect(string.Empty, $"[dir={variant}] ", null);
            return true;
        }

        if (variant.StartsWith("group-", StringComparison.Ordinal)
            && States.TryGetValue(variant["group-".Length..], out var groupState)) {
            effect = new VariantEffect(string.Empty, $".group{groupState} ", null);
            return true;
        }

        if (variant.StartsWith("peer-", StringComparison.Ordinal)
            && States.TryGetValue(variant["peer-".Length..], out var peerState)) {
            // A sibling rather than an ancestor, which is what makes `peer-checked:` able to style a
            // label from the state of the input before it.
            effect = new VariantEffect(string.Empty, $".peer{peerState} ~ ", null);
            return true;
        }

        if (variant.StartsWith("data-", StringComparison.Ordinal)) {
            effect = new VariantEffect(AttributeSelector("data-", variant["data-".Length..]), string.Empty, null);
            return true;
        }

        if (variant.StartsWith("aria-", StringComparison.Ordinal)) {
            // ⚠ `="true"` and not presence, which is the one place `aria-` must not follow `data-`.
            // An ARIA state is a tri-state whose *false* is spelled out: a collapsed disclosure
            // carries `aria-expanded="false"`, it does not drop the attribute. So `[aria-expanded]`
            // — what the shorthand used to emit — is true of the collapsed element as well as the
            // expanded one, and `aria-expanded:` styled both. WAI-ARIA 1.2 § 6.3 and Tailwind's own
            // eight built-ins agree on `="true"`; a non-boolean state such as `aria-sort` has no
            // shorthand in either and wants the arbitrary form.
            effect = new VariantEffect(
                AttributeSelector("aria-", variant["aria-".Length..], shorthand: "true"),
                string.Empty,
                null
            );

            return true;
        }

        if (variant.Length > 2 && variant[0] == '[' && variant[^1] == ']') {
            // The escape hatch. `&` stands for the class selector, so `[&>*]:p-4` becomes
            // `.\[\&\>\*\]\:p-4 > *`.
            var inner = variant[1..^1].Replace('_', ' ');
            effect = new VariantEffect(inner, string.Empty, null);
            return true;
        }

        return false;
    }

    /// <summary>Reads <c>nth-3</c>, <c>nth-last-3</c>, their <c>-of-type</c> pairs and the <c>[an+b]</c> form.</summary>
    /// <param name="variant">The variant, without its colon.</param>
    /// <param name="effect">Receives the pseudo-class suffix.</param>
    /// <returns>Whether it is an <c>nth-*</c> variant.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The shorthand argument is a positive integer and nothing else, which is narrower
    ///         than <c>an+b</c> on purpose.</b> v4 spells <c>nth-3</c> for the third child and puts
    ///         everything else in the arbitrary form, so accepting <c>nth-2n</c> here would invent a
    ///         spelling Tailwind does not have — and it would collide with nothing today and with
    ///         whatever v5 does with it later. Anything unrecognised falls through to "not a
    ///         variant", so <c>nth-foo:p-4</c> is not a class rather than a class that emits a
    ///         selector the compiler then refuses.
    ///     </para>
    ///     <para>
    ///         The arbitrary form keeps the underscore-to-space rule the <c>[&amp;>*]</c> escape
    ///         hatch uses, so <c>nth-[2n+1]</c> and <c>nth-[odd]</c> both reach the compiler as
    ///         written and <c>an+b</c> is parsed once, by ExCSS, rather than twice.
    ///     </para>
    /// </remarks>
    static bool TryNth(string variant, out VariantEffect effect) {
        effect = default;

        foreach (var (prefix, function) in NthFamilies) {
            if (!variant.StartsWith(prefix, StringComparison.Ordinal)) {
                continue;
            }

            var argument = variant[prefix.Length..];

            if (argument.Length > 2 && argument[0] == '[' && argument[^1] == ']') {
                argument = argument[1..^1].Replace('_', ' ');
            } else if (!IsChildNumber(argument)) {
                return false;
            }

            effect = new VariantEffect($":{function}({argument})", string.Empty, null);
            return true;
        }

        return false;

        static bool IsChildNumber(string text) {
            if (text.Length == 0) {
                return false;
            }

            foreach (var character in text) {
                if (character is < '0' or > '9') {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Whether a suffix can be put inside <c>:has()</c> or <c>:not()</c> at all.</summary>
    /// <param name="suffix">A resolved variant's <c>SelectorSuffix</c>.</param>
    /// <returns>Whether wrapping it produces a selector.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A length check and not a bare index, because <c>Length: &gt; 0</c> is not the
    ///         question being asked.</b> Both call sites pattern-match a non-empty suffix and then
    ///         wanted its first non-space character; a suffix that is non-empty and <em>all</em>
    ///         space satisfies the first and has no second, so the index threw out of the middle of
    ///         sheet generation rather than at a boundary anybody could diagnose. Nothing in the
    ///         <c>States</c> or <c>Parts</c> tables spells one today — every value starts with
    ///         <c>:</c> or, for the one part entry, <c>&gt; </c> — so this is the day-it-becomes-
    ///         expressible guard rather than a live bug.
    ///     </para>
    ///     <para>
    ///         ⚠ And whitespace-only is a <em>refusal</em> rather than an admission, which is the
    ///         same verdict a combinator gets and for the same reason: <c>:not( )</c> is not a
    ///         selector, so admitting it would put text the compiler rejects into a rule instead of
    ///         leaving the class unrecognised. That distinction is what the <c>Parts</c> table
    ///         exists for.
    ///     </para>
    /// </remarks>
    internal static bool IsWrappable(string suffix) {
        var trimmed = suffix.AsSpan().TrimStart();

        return trimmed.Length > 0 && trimmed[0] is not ('>' or '+' or '~');
    }

    /// <summary>Whether a variant's effect goes where <c>&amp;</c> is rather than after the selector.</summary>
    /// <param name="effect">The effect.</param>
    /// <returns>Whether it contains a placeholder.</returns>
    public static bool IsArbitrary(VariantEffect effect) =>
        effect.SelectorSuffix.Contains('&', StringComparison.Ordinal);

    /// <summary>Reads <c>sm</c>, <c>max-sm</c>, <c>min-sm</c> and the arbitrary <c>max-[600px]</c>/<c>min-[600px]</c>.</summary>
    /// <param name="variant">The variant, without its colon.</param>
    /// <param name="tokens">The theme, for the <c>--breakpoint-*</c> scale.</param>
    /// <param name="effect">Receives the <c>@media</c> wrapper.</param>
    /// <returns>Whether it is a viewport-width variant.</returns>
    /// <remarks>
    ///     <para>
    ///         The bare breakpoint and <c>min-*</c> are one condition — <c>min-width</c>, inclusive,
    ///         so the breakpoints stack the way everybody expects: an <c>md:</c> rule applies at
    ///         <c>lg:</c> too unless something overrides it — and they emit the same text, so
    ///         <c>sm:</c> and <c>min-sm:</c> share one group. Which of two overlapping ones wins is
    ///         the generator's order and not anything here; see <see cref="AtRuleOrder" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>max-*</c> emits <c>(width &lt; …)</c>, exclusive, for the reason
    ///         <see cref="TryContainer" />'s <c>@max-*</c> does:</b> v4's <c>max-sm</c> is
    ///         <c>(width &lt; 40rem)</c>, and the only width at which that and a <c>max-width</c>
    ///         disagree is the threshold itself. <see cref="MediaQuery" /> reads the range syntax
    ///         through the same <c>FeatureRange</c> the container evaluator does. This half of #609
    ///         never landed while the container half did, and #1346 is the record of it.
    ///     </para>
    /// </remarks>
    static bool TryScreen(string variant, ThemeTokens tokens, out VariantEffect effect) {
        effect = default;

        var exclusive = false;
        var rest = variant.AsSpan();

        if (tokens.Screens.TryGetValue(variant, out var named)) {
            effect = new VariantEffect(string.Empty, string.Empty, $"@media (min-width: {Css(named)})");
            return true;
        }

        if (rest.StartsWith("min-", StringComparison.Ordinal)) {
            rest = rest[4..];
        } else if (rest.StartsWith("max-", StringComparison.Ordinal)) {
            exclusive = true;
            rest = rest[4..];
        } else {
            return false;
        }

        string width;

        if (rest.Length > 2 && rest[0] == '[' && rest[^1] == ']') {
            // Verbatim, as `@min-[…]` is: the author wrote a length, and which units compare is
            // `MediaQuery`'s question. ⚠ It reads `rem` and `em` since #1373, against the document's
            // text size, so `min-[40rem]:` — v4's own unit — is 640 pixels at 100 % and moves with a
            // user's text-size preference. Until then it was a block the loader dropped. The theme's
            // named breakpoints do the same since #1417, which kept their unit — see `Css`.
            width = rest[1..^1].ToString().Replace('_', ' ');
        } else if (tokens.Screens.TryGetValue(rest.ToString(), out var scale)) {
            width = Css(scale);
        } else {
            return false;
        }

        var condition = exclusive ? $"(width < {width})" : $"(min-width: {width})";
        effect = new VariantEffect(string.Empty, string.Empty, $"@media {condition}");

        return true;
    }

    /// <summary>A theme width as the query text, in the unit the theme wrote it in.</summary>
    /// <remarks>
    ///     ⚠ <b>Verbatim, and the unit is the point</b> (#1417). This used to write every named
    ///     breakpoint and container size in pixels, converted at 16 a rem when the theme was read, so
    ///     <c>sm:</c> was <c>(min-width: 640px)</c> at every text size. As <c>40rem</c> it reaches
    ///     <see cref="MediaQuery" /> and <see cref="ContainerQuery" />, which measure it against the
    ///     document's live root font size — the arbitrary <c>min-[40rem]:</c> did already.
    /// </remarks>
    static string Css(StyleValue width) =>
        width.Number.ToString("0.####", CultureInfo.InvariantCulture) + (width.Unit == StyleUnit.Rem ? "rem" : "px");

    /// <summary>Reads <c>@sm</c>, <c>@max-lg</c>, <c>@min-[480px]</c> and their <c>/name</c> forms.</summary>
    /// <param name="rest">The variant with its <c>@</c> already taken off.</param>
    /// <param name="tokens">The theme, for the <c>--container-*</c> scale.</param>
    /// <param name="effect">Receives the <c>@container</c> wrapper.</param>
    /// <returns>Whether it is a container variant.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Driven off <see cref="ThemeTokens.Containers" /> and never off
    ///         <see cref="ThemeTokens.Screens" />, which is the decision this whole variant family
    ///         waited on.</b> The two namespaces spell the same names — <c>sm</c>, <c>lg</c>,
    ///         <c>2xl</c> — and mean numbers two-thirds apart: a 40 rem window against a 24 rem box.
    ///         A <c>@sm:</c> resolved against the breakpoints is a threshold no dockable panel in an
    ///         editor ever reaches, so every rule it wrote would be valid CSS that never matched,
    ///         and nothing in CSS warns about a query that is merely always false.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>@max-*</c> emits <c>(width &lt; …)</c> and not <c>max-width</c>, because v4's
    ///         <c>@max-sm</c> is <c>(width &lt; 24rem)</c> and the two spellings differ on exactly one
    ///         width — the threshold itself.</b> A container measured at precisely <c>24rem</c> takes
    ///         the <c>@max-sm:</c> rule under <c>max-width</c> and does not under <c>&lt;</c>, and
    ///         nothing about a one-pixel disagreement reads as a bug: it reads as an author
    ///         mis-picking their breakpoint. <see cref="ContainerQuery" /> learned the range operators
    ///         for this. <c>@min-*</c> stays inclusive, which is what v4 does too and what
    ///         <see cref="TryScreen" /> gives every breakpoint and <c>min-*</c>. ⚠ The viewport
    ///         family's <c>max-*</c> did not exist at all until #1346, although #609 — which this
    ///         remark was written for — was closed as if both halves had landed.
    ///     </para>
    ///     <para>
    ///         The name goes after the last <c>/</c>, which is where v4 puts it and is unambiguous
    ///         here because a length never contains one: <c>@sm/main</c> asks the nearest ancestor
    ///         <i>called</i> <c>main</c>, and <c>@sm</c> asks the nearest container of any name.
    ///     </para>
    /// </remarks>
    static bool TryContainer(ReadOnlySpan<char> rest, ThemeTokens tokens, out VariantEffect effect) {
        effect = default;

        var name = string.Empty;
        var slash = rest.LastIndexOf('/');

        if (slash >= 0) {
            // `@sm/` names nothing, and a name is an identifier rather than anything at all.
            if (slash == rest.Length - 1 || !IsIdentifier(rest[(slash + 1)..])) {
                return false;
            }

            name = rest[(slash + 1)..].ToString();
            rest = rest[..slash];
        }

        var exclusive = false;

        if (rest.StartsWith("min-", StringComparison.Ordinal)) {
            rest = rest[4..];
        } else if (rest.StartsWith("max-", StringComparison.Ordinal)) {
            exclusive = true;
            rest = rest[4..];
        }

        string width;

        if (rest.Length > 2 && rest[0] == '[' && rest[^1] == ']') {
            // The arbitrary form. Verbatim, because the author wrote a length and the units this
            // engine can compare are `MediaQuery.TryLength`'s rather than this table's.
            width = rest[1..^1].ToString().Replace('_', ' ');

            if (width.Length == 0) {
                return false;
            }
        } else if (tokens.Containers.TryGetValue(rest.ToString(), out var scale)) {
            width = Css(scale);
        } else {
            return false;
        }

        var subject = name.Length == 0 ? string.Empty : name + " ";

        var condition = exclusive ? $"(width < {width})" : $"(min-width: {width})";
        effect = new VariantEffect(string.Empty, string.Empty, $"@container {subject}{condition}");

        return true;
    }

    static bool IsIdentifier(ReadOnlySpan<char> text) {
        foreach (var c in text) {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) {
                return false;
            }
        }

        return text.Length > 0;
    }

    static string AttributeSelector(string prefix, string rest, string? shorthand = null) {
        // `data-[state=open]:` — the arbitrary form is verbatim whatever the family, because the
        // author has written the comparison out.
        if (rest.Length > 1 && rest[0] == '[' && rest[^1] == ']') {
            return $"[{prefix}{rest[1..^1].Replace('_', ' ')}]";
        }

        // The shorthand `data-open:`, which means the attribute is present — and `aria-expanded:`,
        // which means it is present *and* `"true"`. See the `aria-` branch above for why the two
        // differ.
        return shorthand is null ? $"[{prefix}{rest}]" : $"[{prefix}{rest}=\"{shorthand}\"]";
    }
}
