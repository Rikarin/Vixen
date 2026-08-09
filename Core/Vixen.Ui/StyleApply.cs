// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Vixen.Ui.Styling.Utilities;

namespace Vixen.Ui;

/// <summary><c>@apply</c>, expanded as a sheet is installed rather than as one is built.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Until this existed, <c>@apply</c> was inert in every sheet in the repository and said
///         nothing about it.</b> <see cref="ApplyExpander" /> ran in exactly one place —
///         <c>Tools/Vixen.StyleGen</c>, over files named by <c>@(VixenStyleBase)</c> — and no project
///         set that item. Every hand-written sheet reaches the runtime as a raw
///         <c>EmbeddedResource</c> through its own <c>Theme.Css</c>, so an <c>@apply</c> written into
///         one arrived at ExCSS verbatim, was handed back as an unknown at-rule, and was dropped. The
///         only visible symptom was a block missing declarations nobody could see it had asked for.
///     </para>
///     <para>
///         ⚠ <b>Install time and not build time, and the two reasons are settled.</b> The build path
///         can never serve <c>Vixen.Ui.Controls</c> or <c>Vixen.Ui.Controls.Advanced</c> — they are
///         Core assemblies, doc 00's layer rules forbid them from referencing
///         <c>Tools/Vixen.StyleGen</c>, and between them they hold the two largest sheets in the tree.
///         And requiring an MSBuild item before an at-rule stops being ignored is precisely the
///         friction "UI development must be as straightforward as web development" rules out: a rule
///         that works in one project and silently does nothing in the next is worse than one that
///         does not exist.
///     </para>
///     <para>
///         ⚠ <b>The hard part is <i>when</i> the tokens are known, not where the expander runs.</b>
///         At build time StyleGen holds every <c>@theme</c> up front. At install time it does not: an
///         <c>@apply p-4</c> in <c>ControlTheme</c> is expanded while <c>EditorTheme</c> — which may
///         redefine <c>--spacing</c> — has not been loaded yet. Expanding against a half-built theme
///         does not fail loudly; <see cref="ThemeTokens.CreateDefault" /> answers every namespace, so
///         the utility resolves to the <i>shipped</i> number and the rule silently gets the wrong
///         padding. That is the exact false negative this subsystem exists to prevent, so the
///         expansion is against the document's <b>merged</b> theme and a sheet that arrives with new
///         tokens re-expands the ones that came before it. See <see cref="TokensCameLate" />.
///     </para>
///     <para>
///         The mechanism is <see cref="StyleEngine.Preprocessor" /> rather than a transform in front
///         of <see cref="UiDocument.Load" />, because a reload has to re-run it —
///         <see cref="StyleEngine.SetMedia" /> and <see cref="StyleEngine.Replace" /> both replay
///         <see cref="StyleEngine.SheetText" />, which is deliberately the text as it was handed in.
///     </para>
/// </remarks>
public sealed partial class UiDocument {
    /// <summary>Every <c>@theme</c> in the document, layered in load order over the shipped palette.</summary>
    /// <remarks>Rebuilt rather than patched, because <c>--color-*: initial</c> is a subtraction.</remarks>
    ThemeTokens? theme;

    /// <summary>How many sheets <see cref="theme" /> was merged from.</summary>
    /// <remarks>
    ///     ⚠ <b>A staleness check and not an optimisation.</b> <see cref="Load" /> forgets the merge
    ///     when the sheet it is given declares tokens, which covers every caller that goes through
    ///     this class — but <see cref="Styles" /> is public and <c>StyleEngine.Load</c> can be called
    ///     straight. Comparing the count catches that too, at the price of one integer compare per
    ///     preprocessed sheet.
    /// </remarks>
    int themeSheets = -1;

    ApplyExpander? expander;

    /// <summary>The transform <see cref="StyleEngine.Preprocessor" /> is pointed at.</summary>
    /// <remarks>
    ///     The <c>Contains</c> is not a micro-optimisation: <see cref="ApplyExpander.Expand" /> makes
    ///     the same test first thing, and skipping it here is what keeps a document that has never
    ///     seen an <c>@apply</c> — which is every document in this repository today — from building a
    ///     merged theme it will never read.
    /// </remarks>
    string ExpandApply(string css) {
        if (!css.Contains("@apply", StringComparison.Ordinal)) {
            return css;
        }

        var current = Expander();
        var expanded = current.Expand(css);

        // ⚠ Drained here and not left on the expander, because the expander is reused across sheets
        // and `Expand` clears the list on entry — so a caller reading `Diagnostics` afterwards sees
        // only the last sheet's refusals. This is the same silence the drain exists to end, and
        // `@apply` is the one at-rule whose failure is otherwise indistinguishable from success.
        StyleLog.ReportApplyRefusals(logger, current.Diagnostics);

        return expanded;
    }

    ApplyExpander Expander() {
        // Before the null test, because rebuilding the theme invalidates the expander that held the
        // old one — `ApplyExpander` takes its tokens once, in its constructor.
        var tokens = Theme();

        return expander ??= new ApplyExpander(tokens);
    }

    /// <summary>The merged theme, built on first use and forgotten whenever a sheet could change it.</summary>
    ThemeTokens Theme() {
        if (theme is not null && themeSheets == Styles.SheetCount) {
            return theme;
        }

        var merged = ThemeTokens.CreateDefault();

        // ⚠ In load order, because `@theme` layers: v4's model is that a later block overrides an
        // earlier one, which is what makes "the editor's tokens, plus the two this panel adds"
        // expressible. The build step reads them in the same order for the same reason.
        for (var i = 0; i < Styles.SheetCount; i++) {
            merged.Apply(Styles.SheetText(i));
        }

        theme = merged;
        themeSheets = Styles.SheetCount;
        expander = null;

        return merged;
    }

    /// <summary>Whether a sheet just loaded brings tokens an already-expanded <c>@apply</c> needed.</summary>
    /// <param name="css">The sheet about to be loaded.</param>
    /// <returns>Whether every sheet has to be expanded again once it is in.</returns>
    /// <remarks>
    ///     ⚠ <b>Asked <i>before</i> the sheet is loaded, so that "already expanded" means what it
    ///     says.</b> A sheet that carries both an <c>@theme</c> and an <c>@apply</c> of its own needs
    ///     no reload on its own account: it is added to <see cref="StyleEngine" />'s list before the
    ///     preprocessor runs, so its tokens are in the merge that expands it.
    ///     <para>
    ///         The <c>@theme</c> test is exact rather than conservative: <see cref="ThemeTokens.Apply" />
    ///         scans for that literal and reads nothing else, so a sheet without one cannot move a
    ///         token and cannot make any earlier expansion wrong.
    ///     </para>
    ///     <para>
    ///         The cost is bounded and paid by almost nobody. It is a substring search per load,
    ///         plus — only when a theme sheet lands after a sheet that used <c>@apply</c> — one
    ///         reload, which is the same reload <see cref="StyleEngine.SetMedia" /> already performs
    ///         when a resize flips a breakpoint.
    ///     </para>
    /// </remarks>
    bool TokensCameLate(string css) {
        if (!css.Contains("@theme", StringComparison.Ordinal)) {
            return false;
        }

        for (var i = 0; i < Styles.SheetCount; i++) {
            if (Styles.SheetText(i).Contains("@apply", StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
}
