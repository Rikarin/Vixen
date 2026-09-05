// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Editor.Inspector;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Vixen.Ui.Testing.Visual;
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
///         ⚠ <b>Two oracles, because the first one cannot see the failure the issue describes.</b>
///         "Inert" has a closed form — the inner scroll region has nothing to scroll, so
///         <c>ScrollView.MaximumTop</c> is nought — and that is checkable identically on every
///         machine, which eyeballing is not. But it is arithmetic and says nothing about the
///         <i>drawing</i>, so the two things it is blind to are asserted separately:
///         <see cref="No_inner_scrollbar_is_painted_in_the_stacked_panel" /> draws the panel with
///         <c>SoftwareUiRasterizer</c> and compares it against the same panel whose inner bars
///         cannot paint, and
///         <see cref="A_wheel_over_an_inspector_scrolls_the_panel_and_not_the_inspector" /> sends a
///         wheel where the pointer actually is. ⚠ A bar that is inert and painted reddens the
///         picture and leaves the layout oracle green, which is what makes the second one worth its
///         cost — sabotaging <c>ScrollBar.OnDraw</c>'s <c>Range &lt;= 0f</c> guard is red here and
///         green above.
///     </para>
///     <para>
///         <b>A drawn frame rather than a real window, deliberately.</b> The picture is the CPU
///         renderer's, which does the shaders' own arithmetic and is exact on every machine — the
///         repo's rule is a closed-form oracle over eyeballing, and a check that needs a Vulkan
///         device is one that does not run on the machines this suite runs on. What that leaves
///         unseen is what lives below the geometry — a descriptor binding, a vertex layout, a
///         flipped projection — which is <c>Vixen.Graphics.Golden.Tests</c>' subject and not this
///         panel's.
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

    /// <summary>And the picture #527 asks for: nothing an inner scroller could draw is in it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What the layout oracle above cannot see, drawn instead of reasoned about.</b>
    ///         <c>MaximumTop</c> is nought is a claim about arithmetic; this is a claim about the
    ///         frame. The comparison is against the same stack with the inner bars given no width —
    ///         a scrollbar is absolutely positioned, so taking its width away moves no content and
    ///         changes nothing else in the picture — so the two frames can differ in exactly one
    ///         way: a pixel one of those bars painted. They are compared exactly, because
    ///         <c>SoftwareUiRasterizer</c> does the same arithmetic on every machine.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The panel's own bar is the instrument check, and without it this test passes on
    ///         a blank window.</b> A comparison that finds no difference proves nothing until the
    ///         same comparison is shown finding one — so the third capture takes the width off the
    ///         panel's own bar, which is live at this height, and the difference it produces is what
    ///         says a painted scrollbar is visible to this measurement at all.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(900f)]
    [InlineData(260f)]
    public void No_inner_scrollbar_is_painted_in_the_stacked_panel(float height) {
        var drawn = Picture(height);
        var without = Picture(height, "inspector scrollbar { width: 0px; height: 0px; }");

        var inner = ImageComparer.Compare(drawn, without, ImageTolerance.Exact);

        Assert.True(
            inner.Matches,
            $"{inner.DifferingPixels} pixels of the panel are drawn by a scrollbar inside an inspector, "
            + "so the inner scroller is inert and painted anyway — the two-bar arrangement, which the "
            + "layout oracle above cannot see"
        );

        if (!Build(height).Panel.Overflows) {
            // The tall case: the panel does not overflow either, so there is no painted bar anywhere
            // and the floor below has nothing to stand on. The short case carries it.
            return;
        }

        // The floor. `> scrollbar` is the panel's own bar and not the inspectors', which are further
        // down; at this height it is live, so this difference is a painted bar — and its absence
        // would mean the comparison is blind rather than the picture clean.
        var floor = ImageComparer.Compare(
            drawn,
            Picture(height, "dock-panel > scrollbar { width: 0px; }"),
            ImageTolerance.Exact
        );

        Assert.False(
            floor.Matches,
            "taking the width off the panel's own scrollbar changed no pixel, so this comparison "
            + "cannot see a scrollbar and the assertion above means nothing"
        );
    }

    /// <summary>And a wheel over an inspector scrolls the panel, which is the other half of "inert".</summary>
    /// <remarks>
    ///     ⚠ <b>The failure #527 describes is a wheel that moves the wrong thing</b>, and that is not
    ///     derivable from the geometry: a nested region with nothing to scroll could still claim the
    ///     event and swallow it, which reads as a panel that will not scroll while the pointer is
    ///     over most of its area. The property is stated as work — the panel's offset moved and the
    ///     inspector's did not — rather than as a pixel or a delay.
    /// </remarks>
    [Fact]
    public void A_wheel_over_an_inspector_scrolls_the_panel_and_not_the_inspector() {
        var (panel, first, _) = Build(260f);
        var document = documents[^1];

        Assert.True(panel.Overflows, "the panel does not overflow, so a wheel has nothing to move");
        Assert.Equal(0f, panel.ScrollTop, 0.5f);

        var bounds = first.Scroll.Bounds;

        document.Dispatch(
            new WheelEvent {
                X = bounds.X + (bounds.Width * 0.5f),
                Y = bounds.Y + (bounds.Height * 0.5f),
                DeltaY = 120f,
                Timestamp = TimeSpan.FromMilliseconds(16)
            }
        );

        for (var i = 0; i < 16 && document.Update(); i++) {
            document.Draw();
        }

        Assert.True(panel.ScrollTop > 0f, $"the panel did not move; it is at {panel.ScrollTop}");
        Assert.Equal(0f, first.Scroll.ScrollTop, 0.5f);
    }

    /// <summary>The stack, drawn.</summary>
    /// <remarks>
    ///     ⚠ The harness is not disposed here on purpose: <c>UiTest.Adopt</c>'s own remark says
    ///     <c>Dispose</c> disposes the document either way, and every document this class builds is
    ///     already owned by <see cref="documents" />.
    /// </remarks>
    Bitmap Picture(float height, string? css = null) {
        Build(height, css);

        var ui = UiTest.Adopt(documents[^1]);
        return ui.Capture();
    }

    (DockPanel Panel, InspectorView First, InspectorView Second) Build(float height, string? css = null) {
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

        if (css is not null) {
            document.Load(css);
        }

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
