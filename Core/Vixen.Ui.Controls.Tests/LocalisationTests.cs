// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     Doc 46 § A3's acceptance criterion, and the one assertion it is: a language change re-labels
///     an interface that is already on screen.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Against a real <c>.vxml</c>, and that is the part that cannot be substituted.</b>
///         The claim is not "a signal notifies" — <c>Vixen.Ui.Reactive.Tests</c> proves that about
///         signals in general. The claim is that the path from <c>Strings.Use</c> to a label on a
///         live element exists end to end and costs the application nothing, and the only way to
///         measure that is to compile the markup the generator compiles and drive the frames the
///         document drives.
///     </para>
///     <para>
///         ⚠ <b><see cref="Reset" /> in a finally, because <c>Strings</c> is static.</b> A test that
///         left a Czech catalogue in place would be a test that made every later test in the
///         assembly depend on the order xunit happened to pick.
///     </para>
/// </remarks>
public class LocalisationTests {
    [Fact]
    public void A_language_change_re_labels_an_interface_that_is_already_on_screen() {
        using var fixture = new ControlFixture();

        try {
            var sheet = new LocalisedSheet();
            BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);

            // Frame one: nothing loaded, so the label is the source text at the declaration.
            fixture.Update();
            Assert.Equal("Close", sheet.Close.Label);

            // Between the two frames, and this is every line the application writes.
            Strings.Use(new StringCatalog("cs").Set(LocalisedSheet.CloseText.Id, "Zavřít"));

            // Frame two.
            fixture.Update();
            Assert.Equal("Zavřít", sheet.Close.Label);

            // And back, so the assertion is about following the catalogue rather than about one
            // write happening to land.
            Strings.Use(null);
            fixture.Update();
            Assert.Equal("Close", sheet.Close.Label);
        } finally {
            Reset();
        }
    }

    /// <summary>
    ///     ⚠ The same claim with the frame removed, which is what tells a re-label apart from a
    ///     rebuild.
    /// </summary>
    /// <remarks>
    ///     The element does not change when the catalogue does — it changes when the document next
    ///     flushes its effects. If this assertion ever fails it means something is re-running
    ///     bindings from inside <c>Strings.Use</c>, which is the per-mutation callback the signal
    ///     graph exists to not be.
    /// </remarks>
    [Fact]
    public void The_label_follows_the_frame_rather_than_the_write() {
        using var fixture = new ControlFixture();

        try {
            var sheet = new LocalisedSheet();
            BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);
            fixture.Update();

            Strings.Use(new StringCatalog("cs").Set(LocalisedSheet.CloseText.Id, "Zavřít"));
            Assert.Equal("Close", sheet.Close.Label);

            fixture.Update();
            Assert.Equal("Zavřít", sheet.Close.Label);
        } finally {
            Reset();
        }
    }

    static void Reset() => Strings.Use(null);
}
