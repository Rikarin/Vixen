// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>The operating system's text-size preference reaching <c>rem</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The join, and it is the half that would otherwise be a setting nothing honours.</b>
///         <c>UiDocument.RootFontSize</c> being settable is worth nothing on its own — anybody can
///         write a number to it — so these assertions go through <c>PlatformInput</c> and measure a
///         box, which is what goes red the day the platform read is disconnected.
///     </para>
///     <para>
///         ⚠ <b>Windows and GNOME answer this and macOS does not.</b> <c>MacOSAccessibility</c>
///         leaves the axis <c>null</c> because the Mac has no system-wide text scale — Dynamic Type
///         is a UIKit API — and <c>null</c> is read as one here rather than as "leave it alone", so
///         a platform that stops reporting a scale puts the text back instead of freezing it.
///     </para>
/// </remarks>
public class TextScaleWiringTests {
    static UiElement Probe(UiDocument document) {
        document.Load(".probe { width: 2rem; height: 1rem; }");

        var probe = document.Root.Add("div", classNames: "probe");
        document.Update();

        return probe;
    }

    [Fact]
    public void A_platform_that_reports_a_scale_of_one_and_a_half_measures_two_rem_at_forty_eight() {
        using var document = new UiDocument(400f, 400f);
        var probe = Probe(document);

        Assert.Equal(32f, probe.Width, 0.001f);

        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(TextScale: 1.5f));
        document.Update();

        Assert.Equal(48f, probe.Width, 0.001f);
    }

    [Fact]
    public void An_unknown_scale_leaves_the_text_at_its_ordinary_size() {
        using var document = new UiDocument(400f, 400f);
        var probe = Probe(document);

        PlatformInput.ApplyAccessibility(document, SystemAccessibility.Unknown);
        document.Update();

        Assert.Equal(32f, probe.Width, 0.001f);
    }

    [Fact]
    public void A_scale_that_goes_away_puts_the_text_back_rather_than_freezing_it() {
        using var document = new UiDocument(400f, 400f);
        var probe = Probe(document);

        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(TextScale: 2f));
        PlatformInput.ApplyAccessibility(document, SystemAccessibility.Unknown);
        document.Update();

        Assert.Equal(32f, probe.Width, 0.001f);
    }

    [Fact]
    public void A_document_that_chose_its_own_root_size_keeps_its_proportions() {
        // A twelve-pixel root and a scale of two is twenty-four, not thirty-two: the platform asked
        // for text half again as large as *this application's*, not for a size of its own.
        using var document = new UiDocument(400f, 400f, 12f);
        var probe = Probe(document);

        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(TextScale: 2f));
        document.Update();

        Assert.Equal(48f, probe.Width, 0.001f);
        Assert.Equal(24f, document.RootFontSize, 0.001f);
    }

    [Fact]
    public void Re_reading_the_same_scale_does_not_compound_it() {
        using var document = new UiDocument(400f, 400f);
        var probe = Probe(document);

        // ⚠ The defect a scale applied to the *current* root size would have, and it is invisible in
        // one call: `DesktopAccessibility` polls every sixteen pumps, so the text would grow by half
        // again four times a second until the window was one letter wide.
        for (var i = 0; i < 5; i++) {
            PlatformInput.ApplyAccessibility(document, new SystemAccessibility(TextScale: 1.5f));
        }

        document.Update();

        Assert.Equal(48f, probe.Width, 0.001f);
    }
}
