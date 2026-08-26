// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     The advanced control set's half of the same claim: what it announces and what it displays are
///     the same words, in every language.
/// </summary>
/// <remarks>
///     <para>
///         <b>Seven of <c>ControlStrings</c>' declarations are used from this assembly</b> — a dock
///         tab's close button, the two tab-strip arrows, a property grid's reset button and its
///         filter box, and a colour picker's eyedropper, intensity caption and hex field. The plain
///         control assembly's copy of this test cannot reach any of them: the two test projects
///         cannot see each other, which is why there are two windows and one declaration class.
///     </para>
///     <para>
///         ⚠ <b>Two of them are the reason this file exists rather than a second copy of a passing
///         test.</b> Eleven of the thirteen strings are assigned to a <c>ButtonBase.Label</c>, which
///         <c>NativeAccessibleName</c> answers with — so those reached a screen reader for free, and
///         the reconciliation found no accessible name anywhere that hardcodes a literal. The other
///         two did not reach one at all: a property grid's filter box put its string in the
///         <i>placeholder</i>, which is deliberately not a name, and a colour picker's intensity
///         caption is a separate element that nothing related the slider to. Both showed the
///         translation and announced nothing, and neither suite could see it. The last two tests
///         here are those two, held down.
///     </para>
///     <para>
///         The colour picker's hex field is the third of that shape and was found by the population
///         rather than by the reconciliation: it has no caption at all, so its only words are the
///         announced ones.
///     </para>
///     <para>
///         The rest of the reasoning — why a pseudo-locale, why the window is built after the
///         language is chosen, and why the assertion is about the declarations rather than about a
///         list of controls — is on <c>Vixen.Ui.Controls.Tests.AccessibleNameLocalisationTests</c>
///         and is not repeated here.
///     </para>
/// </remarks>
/// <summary>The test classes that change the language, which is a process-wide static.</summary>
/// <remarks>
///     ⚠ <b><c>Strings.Use</c> is static, so two test classes that both call it cannot run at the
///     same time.</b> xunit runs different classes in parallel, and this cost a green run: a
///     reference window built under a pseudo-locale had its catalogue swapped out from under it by
///     the class next door, and the symptom was one test failing in a full run and passing on its
///     own. <c>SharedTypeRegistry</c> is the same arrangement one assembly over, for the same kind
///     of reason.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedCatalogue {
    public const string Name = "StringCatalogue";
}

[Collection(SharedCatalogue.Name)]
public class AccessibleNameLocalisationTests {
    [Fact]
    public void No_accessible_name_in_a_reference_window_is_still_the_source_language() {
        using var fixture = new AdvancedFixture();

        try {
            Strings.Use(Pseudo());

            var root = fixture.Document.Root;

            var docking = root.Add<DockingHost>();
            var hierarchy = docking.AddPanel("hierarchy", "Hierarchy");
            hierarchy.CanClose = true;
            docking.AddPanel("inspector", "Inspector");

            root.Add<PropertyGrid>();

            var picker = root.Add<ColorPicker>();
            picker.AllowHdr = true;

            fixture.Update();

            // ⚠ First, because a check over a tree is satisfied perfectly by an empty tree.
            Assert.Empty(AccessibilitySnapshot.Unnamed(root));

            // ⚠ Second, the non-vacuity guard: every string this window is here to exercise has to
            // be audible in it.
            var spoken = Spoken(root);

            foreach (var id in Covered) {
                Assert.Contains(id.Text, spoken);
            }

            // ⚠ Third, the class assertion.
            Assert.Empty(AccessibilitySnapshot.Untranslated(root, ControlStrings.All));
        } finally {
            Strings.Use(null);
        }
    }

    /// <summary>A property grid's filter box announces the words it prompts with.</summary>
    /// <remarks>
    ///     ⚠ <b>The half of the drift that was not a hardcoded literal.</b> The string went to
    ///     <c>Placeholder</c>, and a <c>TextField</c>'s accessible name is <c>null</c> on purpose —
    ///     a placeholder is a hint and it disappears the moment there is a value, so a form named
    ///     from placeholders loses its names as it is filled in. The consequence here was a filter
    ///     box that showed the translation and said nothing, which no accessibility test could see
    ///     (nothing asserted it had a name) and no localisation test could see (the placeholder was
    ///     perfectly translated).
    /// </remarks>
    [Fact]
    public void A_property_grids_filter_box_is_named_by_the_same_string_it_prompts_with() {
        using var fixture = new AdvancedFixture();

        try {
            Strings.Use(Pseudo());

            var grid = fixture.Add<PropertyGrid>();

            Assert.Equal(ControlStrings.PropertyGridSearch.Text, grid.Search.Placeholder);
            Assert.Equal(ControlStrings.PropertyGridSearch.Text, grid.Search.AccessibleName);
        } finally {
            Strings.Use(null);
        }
    }

    /// <summary>A colour picker's intensity slider is named by the caption above it.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half, and it is the one relations exist for.</b> The caption is a separate
    ///     element carrying a localised string; the slider had no words of its own and nothing said
    ///     the two were connected, so the translation was on screen and unreachable. One
    ///     <see cref="AccessibleRelation.LabelledBy" /> is the whole fix, and because a relation is
    ///     read on demand rather than copied, a re-labelled caption re-labels the slider with no
    ///     second write anywhere.
    /// </remarks>
    [Fact]
    public void A_colour_pickers_intensity_slider_is_named_by_its_caption() {
        using var fixture = new AdvancedFixture();

        try {
            Strings.Use(Pseudo());

            var picker = fixture.Add<ColorPicker>();

            Assert.Equal(ControlStrings.ColorPickerIntensity.Text, picker.IntensitySlider.AccessibleName);
        } finally {
            Strings.Use(null);
        }
    }

    /// <summary>The declarations the window above puts on screen.</summary>
    static readonly StringId[] Covered = [
        ControlStrings.DockClose,
        ControlStrings.DockPreviousTab,
        ControlStrings.DockNextTab,
        ControlStrings.PropertyGridSearch,
        ControlStrings.ColorPickerEyedropper,
        ControlStrings.ColorPickerIntensity,
        ControlStrings.ColorPickerHex
    ];

    /// <summary>Every string the control set declares, in a language that is not the source one.</summary>
    /// <remarks>
    ///     A pseudo-locale rather than a hand-written table, so that a string declared tomorrow is
    ///     covered without this file being edited. See the plain control assembly's copy for the
    ///     argument.
    /// </remarks>
    static StringCatalog Pseudo() {
        var catalog = new StringCatalog("qps");

        foreach (var id in ControlStrings.All) {
            catalog.Set(id.Id, "«" + id.Source + "»");
        }

        return catalog;
    }

    static HashSet<string> Spoken(UiElement root) {
        var said = new HashSet<string>(StringComparer.Ordinal);
        Collect(root, said);

        return said;
    }

    static void Collect(UiElement element, HashSet<string> said) {
        if (element.IsInAccessibilityTree) {
            if (element.AccessibleName is { Length: > 0 } name) {
                said.Add(name);
            }

            if (element.AccessibleDescription is { Length: > 0 } description) {
                said.Add(description);
            }
        }

        foreach (var child in element.Children) {
            Collect(child, said);
        }
    }
}
