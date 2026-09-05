// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Inspector;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>The grass and foliage panels' arrangement: a panel that scrolls around views that do not.</summary>
/// <remarks>
///     <para>
///         <b>The claim under test is the one <c>TerrainModulePanels</c> acts on and had never
///         checked.</b> Those panels stack an <c>InspectorView</c> — which owns its own
///         <c>ScrollView</c> — with section titles, verbs and fact rows, and deliberately keep
///         <i>panel</i> scrolling rather than calling <c>DockPanel.Fills</c>. The argument for that is
///         written in <c>AdvancedTheme.vcss</c> beside <c>dock-panel.scrolls &gt; *</c>: "Items that
///         fill by growing are unaffected: <c>flex-grow</c> still hands them the whole panel and their
///         own scrollers take it from there."
///     </para>
///     <para>
///         ⚠ <b>That was an argument and not an observation, which is what #527 is about.</b> A nested
///         scroller that is not in fact inert gives the classic two-scrollbar behaviour — a wheel that
///         moves the wrong thing — and nothing in the suite would have noticed, because every part
///         passes on its own and the failure is an interaction between three stylesheets.
///     </para>
///     <para>
///         ⚠ <b>This is a layout oracle and not the picture the issue asks for.</b> "Inert" has a
///         closed form — the inner scroll region has nothing to scroll, so
///         <c>ScrollView.MaximumTop</c> is nought — and that is checkable headlessly and identically
///         on every machine, which eyeballing is not. What it cannot see is anything about the
///         <i>drawing</i>: a bar that is inert and still painted, or a wheel that routes to the wrong
///         region. Confirming it in a real window is still owed.
///     </para>
///     <para>
///         ⚠ <b>The stacking is reproduced rather than driven through <c>EditorShell</c>.</b> The panel
///         builders are closures registered with a shell that wants a project, a command registry and a
///         notification centre; the parts here — the <c>DockPanel</c>, the <c>InspectorView</c>, the
///         settings object it inspects and all three theme sheets — are the real ones, and the stack is
///         the one at <c>TerrainModulePanels.cs</c>'s grass panel.
///     </para>
/// </remarks>
public class StackedPanelScrollTests : IDisposable {
    readonly List<UiDocument> documents = [];

    public void Dispose() {
        foreach (var document in documents) {
            document.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The grass panel's stack, in a panel of the height a docked column really has.</summary>
    /// <param name="height">How tall the document is, and so how tall the panel is.</param>
    /// <remarks>
    ///     Both directions of the flexbox rule, because they are different arithmetic and only one of
    ///     them is the case anybody reasoned about. At 900 pixels the stack fits and the free space is
    ///     positive, so <c>flex-grow</c> is what sizes the inspector; at 260 it does not fit, the free
    ///     space is negative, <c>flex-grow</c> contributes nothing and what saves the arrangement is
    ///     <c>dock-panel.scrolls &gt; *</c> refusing to shrink. The second is the one that would give
    ///     two scrollbars if that rule were missing, and it is the one no argument had covered.
    /// </remarks>
    [Theory]
    [InlineData(900f)]
    [InlineData(260f)]
    public void The_inner_inspector_scroller_is_inert_in_a_scrolling_panel(float height) {
        var (panel, first, second) = Build(height);

        // The floors. Every bound below is about a panel that laid something out, and a panel that
        // built nothing would satisfy all of them by having no content to scroll and no rows to
        // scroll past.
        Assert.True(panel.Scrolls, "the panel opted out of scrolling, so this measures nothing");
        Assert.True(panel.Height > 0f, "the panel has no height, so nothing was laid out");
        foreach (var inspector in new[] { first, second }) {
            Assert.True(inspector.Height > 0f, "an inspector has no height");
            Assert.True(inspector.Scroll.Content.Height > 0f, "an inspector realised no rows");

            // And the property: the inner region has nothing to scroll, so a wheel over it reaches
            // the panel rather than moving the rows under it.
            Assert.Equal(0f, inspector.Scroll.MaximumTop, 0.5f);
        }
    }

    /// <summary>And the short panel really is the overflowing case, so the theory above is not vacuous.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the interesting half of the theory proves nothing.</b> "The inner scroller
    ///     is inert" is most easily satisfied by a panel tall enough for everything, where there is no
    ///     contention to resolve — so the short case has to be shown to be a panel that genuinely holds
    ///     more than it can show.
    /// </remarks>
    [Fact]
    public void The_short_panel_is_the_one_that_overflows() {
        var (panel, _, _) = Build(260f);

        Assert.True(
            panel.Overflows,
            $"the panel holds {(panel.MaximumScroll + panel.Height).ToString("0", CultureInfo.InvariantCulture)} "
            + $"pixels of content in {panel.Height.ToString("0", CultureInfo.InvariantCulture)} and reports no "
            + "overflow, so the stack was squashed rather than scrolled"
        );
    }

    (DockPanel Panel, InspectorView First, InspectorView Second) Build(float height) {
        var document = new UiDocument(320f, height);

        documents.Add(document);

        ControlTheme.Install(document);
        AdvancedTheme.Install(document);
        InspectorTheme.Install(document);

        document.Load(
            "root { width: 320px; height: "
            + height.ToString("0", CultureInfo.InvariantCulture)
            + "px; }"
        );

        var host = document.Root.Add<DockingHost>();
        var panel = host.AddPanel("foliage", "Foliage");

        // The foliage panel's stack, which is the harder of the two: a row of verbs and *two*
        // inspectors, so the growing items are in contention with each other as well as with the
        // fixed ones.
        var verbs = panel.Add("verb-row");

        foreach (var label in new[] { "Add type", "Remove type", "Add selected asset" }) {
            verbs.Add<Button>().Label = label;
        }

        var first = panel.Add<InspectorView>();

        first.EditedDocument = null;
        first.Inspect(new TerrainGrassSettings());

        var second = panel.Add<InspectorView>();

        second.EditedDocument = null;
        second.Inspect(new TerrainBrushSettings());

        // Settled, with a ceiling that is a hang check and not a budget.
        for (var i = 0; i < 16 && document.Update(); i++) {
            document.Draw();
        }

        return (panel, first, second);
    }
}
