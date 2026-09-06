// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Changing what <c>rem</c> measures against, which for this document's whole life was fixed.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>rem</c> and <c>em</c> both worked and the root size was a construction-time
///         constant</b> — a <c>readonly</c> field taken from an optional constructor parameter — so
///         no operating-system text-size preference could reach a single length in the document. The
///         setting is real on Windows and on GNOME; what was missing was anywhere to put it.
///     </para>
///     <para>
///         ⚠ <b>Every surface, and the second one is what a field-only fix would have missed.</b> The
///         root size is passed <i>by value</i> into <c>LengthContext.ForViewport</c> at
///         <c>UiSurface.Measure</c>, so each surface holds its own copy — and a torn-off window is
///         measured once when it is created and never again unless something asks it to be.
///     </para>
/// </remarks>
public class RootFontSizeTests {
    static UiDocument Laid(out UiElement probe, float root = LengthContext.InitialFontSize) {
        var document = new UiDocument(400f, 400f, root);
        document.Load(".probe { width: 2rem; height: 1rem; }");
        probe = document.Root.Add("div", classNames: "probe");
        document.Update();

        return document;
    }

    [Fact]
    public void A_two_rem_box_follows_the_root_size_without_the_document_being_rebuilt() {
        using var document = Laid(out var probe);

        Assert.Equal(32f, probe.Width, 0.001f);

        // ⚠ No `Load`, no new document, no new element: the whole point is that a host reading a
        // changed setting does not have to rebuild the tree it has already built.
        document.RootFontSize = 24f;
        document.Update();

        Assert.Equal(48f, probe.Width, 0.001f);
    }

    [Fact]
    public void A_torn_off_surface_measures_against_the_new_size_too() {
        using var document = new UiDocument(400f, 400f);
        document.Load(".probe { width: 2rem; height: 1rem; }");

        var panel = document.CreateSurface(200f, 200f);
        var probe = panel.Root.Add("div", classNames: "probe");
        document.Update();

        Assert.Equal(32f, probe.Width, 0.001f);

        document.RootFontSize = 24f;
        document.Update();

        // ⚠ The assertion a fix that only wrote the document's field would fail: this surface was
        // measured when it was created and nothing else would ever measure it again.
        Assert.Equal(48f, probe.Width, 0.001f);
        Assert.Equal(24f, panel.Metrics.RootFontSize, 0.001f);
    }

    [Fact]
    public void A_surface_created_after_the_change_gets_the_new_size() {
        using var document = new UiDocument(400f, 400f);
        document.Load(".probe { width: 2rem; height: 1rem; }");
        document.RootFontSize = 24f;

        var panel = document.CreateSurface(200f, 200f);
        var probe = panel.Root.Add("div", classNames: "probe");
        document.Update();

        Assert.Equal(48f, probe.Width, 0.001f);
    }

    [Fact]
    public void The_constructor_parameter_is_still_what_it_starts_at() {
        using var document = Laid(out var probe, 20f);

        Assert.Equal(20f, document.RootFontSize, 0.001f);
        Assert.Equal(40f, probe.Width, 0.001f);
    }

    [Fact]
    public void Writing_the_same_size_is_not_a_relayout() {
        using var document = Laid(out var probe);

        document.RootFontSize = LengthContext.InitialFontSize;
        document.Update();

        Assert.Equal(32f, probe.Width, 0.001f);
    }

    [Fact]
    public void Text_sized_in_rem_is_re_measured_and_not_only_re_styled() {
        using var document = new UiDocument(400f, 200f);
        document.Fonts.Register("Test", Font());

        document.Load("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            label { font-family: Test; font-size: 1rem; }
            """);

        var label = document.Root.Add("label");
        label.Text = "AB";
        document.Update();

        var before = label.Width;
        Assert.True(before > 0f, "the label measured itself from its text");

        document.RootFontSize = 32f;
        document.Update();

        // ⚠ <b>The case a resize needs a `Layout.Invalidate` for, and this one does not — which was
        // measured rather than assumed.</b> The label's width and height are `auto` in both passes,
        // so its layout style is identical and `SetStyle` dirties nothing; what changes is the
        // measure function's answer. `Forget` is enough to make that re-run, so the invalidation a
        // scale change needs is deliberately absent from `RootFontSize` — removing it left every
        // assertion in this file green, which is what settled it.
        Assert.True(
            label.Width > before * 1.5f,
            $"a doubled root font size should roughly double a 1rem label: {before} → {label.Width}"
        );
    }

    static FontFace Font() {
        using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void A_root_size_of_nothing_is_refused_rather_than_measured(float size) {
        using var document = Laid(out _);

        // A zero root would make every `rem` in the document zero, which is a window that lays out
        // successfully and shows nothing — the failure this repository calls a zero that means "off".
        Assert.Throws<ArgumentOutOfRangeException>(() => document.RootFontSize = size);
    }
}
