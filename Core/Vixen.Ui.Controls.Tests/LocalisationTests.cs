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
[Collection(SharedCatalogue.Name)]
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

    /// <summary>
    ///     The control set's own words go through the catalogue, so a localised window is localised
    ///     all the way down to the button in the corner of the search box.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Built after the language is chosen, which is the behaviour rather than a convenience
    ///     of the test.</b> A control assigns its labels in <c>OnCreated</c>, so it shows the
    ///     language that was in use when it was built — see <c>ControlStrings</c>. Building first
    ///     and translating second would assert something the control set does not do.
    /// </remarks>
    [Fact]
    public void The_control_sets_own_labels_go_through_the_catalogue() {
        using var fixture = new ControlFixture();

        try {
            Strings.Use(
                new StringCatalog("cs")
                    .Set(ControlStrings.TextInputClear.Id, "Vymazat")
                    .Set(ControlStrings.ToastDismiss.Id, "Zavřít")
            );

            var search = fixture.Add<SearchBox>();
            var toast = fixture.Add<Toast>();

            Assert.Equal("Vymazat", search.ClearButton.Label);
            Assert.Equal("Zavřít", toast.CloseButton.Label);
        } finally {
            Reset();
        }
    }

    /// <summary>
    ///     ⚠ Every declared id is distinct, which is the one thing a hand-written declaration table
    ///     gets wrong.
    /// </summary>
    /// <remarks>
    ///     A duplicate id is not a compile error and not a run-time one: it is two labels that a
    ///     translator can only ever give the same word to, found by somebody reading a Czech build.
    ///     The two <c>"Close"</c>s are the case this guards — they are the same English word under
    ///     two ids on purpose.
    /// </remarks>
    [Fact]
    public void Declared_control_ids_are_unique() {
        Assert.Equal(ControlStrings.All.Count, ControlStrings.All.Select(id => id.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The template a translator starts from covers everything the control set says.</summary>
    [Fact]
    public void A_template_holds_every_string_the_control_set_declares() {
        try {
            var template = Strings.Template("cs", ControlStrings.All);
            Assert.Equal(ControlStrings.All.Count, template.Count);

            Strings.Use(template);

            foreach (var id in ControlStrings.All) {
                Assert.Equal(id.Source, id.Text);
            }

            Assert.Empty(Strings.Missing);
        } finally {
            Reset();
        }
    }

    static void Reset() => Strings.Use(null);
}
