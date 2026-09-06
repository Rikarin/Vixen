// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

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

/// <summary>
///     What a control <i>announces</i> and what it <i>displays</i> are the same words, in every
///     language.
/// </summary>
/// <remarks>
///     <para>
///         <b>The defect this exists for is invisible to both of the test suites either side of
///         it.</b> Doc 46 § A2 gave every element a role and an accessible name, answered by a
///         virtual; § A3 put the control set's thirteen English literals through
///         <see cref="ControlStrings" />. They landed hours apart, on the same controls, and nobody
///         had checked they agreed. An accessibility test asserts the name exists; a localisation
///         test asserts the label translates; neither can see a control whose label is Czech and
///         whose accessible name is English.
///     </para>
///     <para>
///         ⚠ <b>And the two categories are indistinguishable from outside.</b>
///         <c>UiElement.NativeAccessibleName</c> defaults to <c>Text</c> and
///         <c>ButtonBase</c> overrides it with <c>Label</c> — both <i>computed</i>, so a control
///         that answers from what it already shows is localised for free. A control that answered
///         with a literal of its own would compile, pass every existing test, and look perfectly
///         correct in English. The only place the difference appears is in another language.
///     </para>
///     <para>
///         ⚠ <b>Written against the declarations rather than against a list of controls, which is
///         the whole point.</b> A test naming <c>SearchBox</c>, <c>Toast</c> and <c>Dialog</c> says
///         nothing about the control populated tomorrow, and § A2 still owes forty-odd of them.
///         <see cref="AccessibilitySnapshot.Untranslated" /> asks a question about the class —
///         <i>is any announced word in this window still the source text of a string that has a
///         translation loaded</i> — and that question keeps its meaning as controls are added.
///     </para>
///     <para>
///         ⚠ <b>The window is built <i>after</i> the language is chosen, and that is the behaviour
///         rather than a convenience.</b> A control assigns its labels in <c>OnCreated</c>, so it
///         shows the language it was built in — <see cref="ControlStrings" /> says so at length and
///         <see cref="LocalisationTests" /> asserts it. Building first and translating second would
///         assert something the control set does not claim to do; what this asserts is what it does
///         claim, which is that a control built in Czech is Czech all the way down to what a screen
///         reader says.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class AccessibleNameLocalisationTests {
    /// <summary>Every string the control set declares, in a language that is not the source one.</summary>
    /// <remarks>
    ///     ⚠ <b>A pseudo-locale rather than a hand-written Czech table, and it is what stops this
    ///     test rotting.</b> A table with a row per string has to be edited every time one is
    ///     declared, and a class test that must be edited to keep covering the class has become the
    ///     instance test it was written to replace. Marking each source text is enough for the
    ///     assertion — what is being detected is a word that did <i>not</i> go through the
    ///     catalogue, and any translation at all makes that visible.
    ///     <para>
    ///         <see cref="LocalisationTests" /> uses real Czech, because its claim is about one
    ///         string following one catalogue. This one's claim is about all of them at once.
    ///     </para>
    /// </remarks>
    static StringCatalog Pseudo() {
        var catalog = new StringCatalog("qps");

        foreach (var id in ControlStrings.All) {
            catalog.Set(id.Id, "«" + id.Source + "»");
        }

        return catalog;
    }

    /// <summary>Every word the accessibility tree under an element would say.</summary>
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

    /// <summary>
    ///     A window of every control in this assembly that says a word of its own, in a language
    ///     that is not English.
    /// </summary>
    [Fact]
    public void No_accessible_name_in_a_reference_window_is_still_the_source_language() {
        using var fixture = new ControlFixture();

        try {
            // ⚠ Before anything is built. See the type's remarks.
            Strings.Use(Pseudo());

            var root = fixture.Document.Root;

            var search = root.Add<SearchBox>();
            search.AddAccessibleRelation(AccessibleRelation.LabelledBy, Caption(root, "Filter"));

            var toast = root.Add<Toast>();
            toast.Message = "Saved";

            var dialog = root.Add<Dialog>();
            dialog.Title = "Delete asset";

            var combo = root.Add<ComboBox>();
            combo.Editor.AddAccessibleRelation(AccessibleRelation.LabelledBy, Caption(root, "Shader"));
            combo.AddOption("lit", "Lit");

            var pages = root.Add<Pagination>();
            pages.PageCount = 5;

            // The two scroll bars, whose *only* words are the announced ones — they have nothing on
            // screen and no caption, so a literal here would be an English announcement nobody
            // could see to report.
            root.Add<ScrollView>();

            // And the split bar, for the same reason one step further on: it is a six-pixel line
            // with nothing written on it, and it only needed a name at all once it became a tab
            // stop. ⚠ Added after `Strings.Use` like everything else here — the name is read in
            // `OnCreated`, so a split built above this line would carry the English one.
            root.Add<SplitView>();

            fixture.Update();

            // ⚠ **First, and for A2's reason: a check over a tree can be satisfied by an empty
            // tree.** Every widget-role element here must have a role *and* a non-empty name before
            // any statement about which language those names are in means anything.
            Assert.Empty(AccessibilitySnapshot.Unnamed(root));

            // ⚠ **Second, the non-vacuity guard.** Every string this window exercises has to be
            // audible in it — otherwise a control that stopped saying anything at all would pass the
            // assertion below by having nothing left to get wrong.
            var spoken = Spoken(root);

            foreach (var id in Covered) {
                Assert.Contains(id.Text, spoken);
            }

            // ⚠ **Third, the class assertion.** Not "these controls are translated" but "nothing in
            // this window is still saying the source text of a string that has somewhere else to
            // go", which is a sentence that stays true as the remaining controls are populated.
            Assert.Empty(AccessibilitySnapshot.Untranslated(root, ControlStrings.All));
        } finally {
            Strings.Use(null);
        }
    }

    /// <summary>The declarations the window above puts on screen.</summary>
    /// <remarks>
    ///     Named so that the guard above fails with the id that went missing rather than with a
    ///     count. The advanced control assembly covers the rest in its own copy of this test — the
    ///     two cannot see each other, which is why there are two windows and one
    ///     <see cref="ControlStrings" />.
    /// </remarks>
    static readonly StringId[] Covered = [
        ControlStrings.TextInputClear,
        ControlStrings.DialogClose,
        ControlStrings.ToastDismiss,
        ControlStrings.SelectSuggestions,
        ControlStrings.PaginationPrevious,
        ControlStrings.PaginationNext,
        ControlStrings.ScrollBarVertical,
        ControlStrings.ScrollBarHorizontal,
        ControlStrings.SplitViewDivider
    ];

    /// <summary>Words beside a field, which is the only thing a field's name is ever made of.</summary>
    static TextBlock Caption(UiElement parent, string text) {
        var caption = parent.Add<TextBlock>();
        caption.Text = text;

        return caption;
    }
}
