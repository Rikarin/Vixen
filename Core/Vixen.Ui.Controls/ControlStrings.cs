// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls;

/// <summary>Every word the standard control set says, declared once.</summary>
/// <remarks>
///     <para>
///         <b>The control library's own strings, in the same shape an application declares its
///         own.</b> Thirteen labels were English literals baked into control constructors, which
///         meant "Clear" in every search box, "Dismiss" on every toast and "Previous tab" on every
///         docked group, inside every localised window, with no way for the application to reach
///         them — and the only party who could close that seam was this repository.
///     </para>
///     <para>
///         ⚠ <b>One class for both control assemblies.</b> Six of these are used from
///         <c>Vixen.Ui.Controls.Advanced</c>, which references this assembly. A second declaration
///         class over there would hand a translator two files to find and would put the boundary
///         between "a control" and "an advanced control" — an assembly split about compile cost —
///         into a translator's workflow, where it means nothing.
///     </para>
///     <para>
///         ⚠ <b>Two ids for the two "Close"s, deliberately.</b> A dialog's dismiss button and a dock
///         tab's are the same English word and are not the same string: a language that distinguishes
///         closing a question from closing a document needs to say so, and an id that says where the
///         string is used is what makes that possible. Merging them saves one line and cannot be
///         undone without a translator's file changing shape.
///     </para>
///     <para>
///         ⚠ <b>A control reads these in its constructor, so it shows the language it was built
///         in.</b> That is not a live binding: <c>Strings.Catalog</c> is a signal and an expression
///         that reads it re-runs, but an assignment in <c>OnCreated</c> is not an expression. A
///         control set that re-labelled itself would need an effect per label and a place to dispose
///         it; what this buys today is that the words are *translatable at all*, and that a control
///         built after the language changed is built in the new one.
///     </para>
/// </remarks>
public static class ControlStrings {
    /// <summary>The button that empties a text field.</summary>
    public static StringId TextInputClear { get; } = new("ui.control.text-input.clear", "Clear");

    /// <summary>The button that dismisses a dialog.</summary>
    public static StringId DialogClose { get; } = new("ui.control.dialog.close", "Close");

    /// <summary>The button that dismisses a toast.</summary>
    public static StringId ToastDismiss { get; } = new("ui.control.toast.dismiss", "Dismiss");

    /// <summary>The toggle that opens a combo box's suggestion list.</summary>
    public static StringId SelectSuggestions { get; } = new("ui.control.select.suggestions", "Show suggestions");

    /// <summary>The button that closes a docked panel.</summary>
    public static StringId DockClose { get; } = new("ui.control.dock.close", "Close");

    /// <summary>The button that scrolls a tab strip back.</summary>
    public static StringId DockPreviousTab { get; } = new("ui.control.dock.previous-tab", "Previous tab");

    /// <summary>The button that scrolls a tab strip on.</summary>
    public static StringId DockNextTab { get; } = new("ui.control.dock.next-tab", "Next tab");

    /// <summary>The button that puts a property back to its default.</summary>
    public static StringId PropertyGridReset { get; } = new("ui.control.property-grid.reset", "Reset");

    /// <summary>The prompt in a property grid's filter box.</summary>
    public static StringId PropertyGridSearch { get; } = new("ui.control.property-grid.search", "Search");

    /// <summary>The caption over a colour picker's HDR intensity slider.</summary>
    public static StringId ColorPickerIntensity { get; } = new("ui.control.color-picker.intensity", "Intensity");

    /// <summary>The eyedropper button.</summary>
    public static StringId ColorPickerEyedropper { get; } =
        new("ui.control.color-picker.eyedropper", "Pick a colour from the screen");

    /// <summary>The arrow that steps a pager back.</summary>
    public static StringId PaginationPrevious { get; } = new("ui.control.pagination.previous-page", "Previous page");

    /// <summary>The arrow that steps a pager on.</summary>
    public static StringId PaginationNext { get; } = new("ui.control.pagination.next-page", "Next page");

    /// <summary>What a screen reader calls the bar down the side of a scrolling area.</summary>
    /// <remarks>
    ///     ⚠ <b>Three declarations that are not on screen anywhere, and that is the point of
    ///     them.</b> A scrollbar and a hexadecimal field have no visible caption — the first is a
    ///     shape and the second is beside a colour — so their <i>only</i> words are the ones a
    ///     screen reader says. Putting them here rather than as literals in the control is what
    ///     stops a localised window having two English announcements in it that nobody can see to
    ///     report.
    /// </remarks>
    public static StringId ScrollBarVertical { get; } = new("ui.control.scrollbar.vertical", "Vertical scroll bar");

    /// <inheritdoc cref="ScrollBarVertical" />
    public static StringId ScrollBarHorizontal { get; } =
        new("ui.control.scrollbar.horizontal", "Horizontal scroll bar");

    /// <summary>The colour picker's hexadecimal field, which has no caption beside it.</summary>
    public static StringId ColorPickerHex { get; } = new("ui.control.color-picker.hex", "Hexadecimal");

    /// <summary>The gradient editor's choice of how two stops are mixed.</summary>
    /// <remarks>
    ///     ⚠ <b>The control's name, not its options.</b> The three colour-space names it offers —
    ///     <c>sRGB</c>, <c>Linear light</c>, <c>Perceptual (Oklab)</c> — are deliberately <i>not</i>
    ///     in this table: a colour space is a term of art a translator should generally leave alone,
    ///     <c>sRGB</c> is not translatable at all, and a mixed set of three would be worse than
    ///     none. What a field <i>is</i> is a different question from what it holds, and it is the
    ///     one a screen-reader user cannot work out from the answer.
    /// </remarks>
    public static StringId GradientEditorSpace { get; } = new("ui.control.gradient-editor.space", "Colour space");

    /// <summary>The gradient editor's opacity slider, shown when an alpha stop is chosen.</summary>
    public static StringId GradientEditorOpacity { get; } = new("ui.control.gradient-editor.opacity", "Opacity");

    /// <summary>Every string above, for a translator to start from.</summary>
    /// <remarks>
    ///     ⚠ <b>Spelled out rather than reflected over</b>, for the reason <c>Strings.Template</c>
    ///     gives: a list gathered by walking the properties at run time is a list an application's
    ///     trimming settings are entitled to shorten, and a template that is short by whatever the
    ///     trimmer removed is worse than no template.
    /// </remarks>
    public static IReadOnlyList<StringId> All { get; } = [
        TextInputClear,
        DialogClose,
        ToastDismiss,
        SelectSuggestions,
        DockClose,
        DockPreviousTab,
        DockNextTab,
        PropertyGridReset,
        PropertyGridSearch,
        ColorPickerIntensity,
        ColorPickerEyedropper,
        PaginationPrevious,
        PaginationNext,
        ScrollBarVertical,
        ScrollBarHorizontal,
        ColorPickerHex,
        GradientEditorSpace,
        GradientEditorOpacity
    ];
}
