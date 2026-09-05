// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls;

/// <summary>Every word the standard control set says, declared once.</summary>
/// <remarks>
///     <para>
///         <b>The control library's own strings, in the same shape an application declares its
///         own.</b> Thirteen labels — fifteen, with the two dialog-button defaults that doc 46 § A3
///         recorded as owed to itself — were English literals baked into control constructors, which
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

    /// <summary>Why an empty field with <c>Required</c> set is not acceptable.</summary>
    /// <remarks>
    ///     ⚠ <b>The only validation message this assembly writes, and the reason is that it is the
    ///     only one it can.</b> Every other rule is a fact about what the field is <i>for</i> — an
    ///     address, a version, a name not already taken — which lives in the application and comes
    ///     back through <c>TextField.Validator</c> in the application's own words. "Something must go
    ///     here" is true of a required field without knowing anything about it.
    /// </remarks>
    public static StringId FieldRequired { get; } = new("ui.control.field.required", "Required");

    /// <summary>The button that dismisses a dialog.</summary>
    public static StringId DialogClose { get; } = new("ui.control.dialog.close", "Close");

    /// <summary>What a confirming button says when the caller does not name it.</summary>
    /// <remarks>
    ///     ⚠ <b>A default rather than a label.</b> Every dialog the shell puts up says something more
    ///     specific — Open, Replace, Discard — and passes it; this is what <c>DialogService.Confirm</c>
    ///     falls back to. It was the literal <c>"OK"</c> until the catalogue was promoted, which is
    ///     the row doc 46 § A3 records as owed to itself.
    /// </remarks>
    public static StringId DialogConfirm { get; } = new("ui.control.dialog.confirm", "OK");

    /// <summary>And what the button that backs out of it says.</summary>
    public static StringId DialogCancel { get; } = new("ui.control.dialog.cancel", "Cancel");

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

    /// <summary>The hue band, which is a rainbow and has no words on it.</summary>
    /// <remarks>
    ///     ⚠ <b>Four more of the same kind as the hexadecimal field above: their only words are the
    ///     ones a screen reader says.</b> A band, a square and a row of chips are pictures — nothing
    ///     in a colour picker is captioned, because a colour explains itself to anyone who can see
    ///     it. That is exactly why the sub-parts had no names to give when they became reachable.
    /// </remarks>
    public static StringId ColorPickerHue { get; } = new("ui.control.color-picker.hue", "Hue");

    /// <summary>The alpha band beneath it.</summary>
    /// <remarks>
    ///     ⚠ <b>A second id for the word <see cref="GradientEditorOpacity" /> already carries</b>,
    ///     on the two-Closes rule this class states at the top: a band under a colour picker and a
    ///     slider in a gradient editor are the same English word and need not be the same string.
    /// </remarks>
    public static StringId ColorPickerAlpha { get; } = new("ui.control.color-picker.alpha", "Opacity");

    /// <summary>The two-dimensional square the marker moves in.</summary>
    /// <remarks>
    ///     ⚠ <b>Named for what it is rather than for its axes, because its axes change.</b> The
    ///     square is saturation against value in <c>ColorModel.Hsv</c> and chroma against lightness
    ///     in <c>ColorModel.OkLch</c>; a name that said either would be wrong half the time, and a
    ///     name that said both would be a sentence.
    /// </remarks>
    public static StringId ColorPickerField { get; } = new("ui.control.color-picker.field", "Colour field");

    /// <summary>The row of saved colours under it.</summary>
    public static StringId ColorPickerPalette { get; } = new("ui.control.color-picker.palette", "Saved colours");

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

    /// <summary>The rail of colour stops under the bar.</summary>
    /// <remarks>
    ///     ⚠ <b>The two rails need names for the one reason a caption cannot supply: they are
    ///     identical.</b> Both are a horizontal strip of markers over a gradient bar, with nothing
    ///     written beside either, and which list a rail carries is the only thing that distinguishes
    ///     them — visually it is the fact that one is above the bar and one below, which is not a
    ///     fact a screen reader has.
    /// </remarks>
    public static StringId GradientEditorColorStops { get; } =
        new("ui.control.gradient-editor.color-stops", "Colour stops");

    /// <summary>The rail of alpha stops over it.</summary>
    /// <inheritdoc cref="GradientEditorColorStops" select="remarks" />
    public static StringId GradientEditorAlphaStops { get; } =
        new("ui.control.gradient-editor.alpha-stops", "Opacity stops");

    /// <summary>The set of nodes on a graph canvas.</summary>
    /// <remarks>
    ///     ⚠ <b>The set, not the canvas.</b> A <c>NodeCanvas</c> is deliberately unnamed — what it
    ///     is a view of is the application's sentence, usually the panel title above it — but the
    ///     surface inside it is the <c>listbox</c> the nodes are <c>option</c>s of, and a set with
    ///     no name is one a reader cannot tell from the next canvas along in a split view.
    /// </remarks>
    public static StringId NodeCanvasNodes { get; } = new("ui.control.node-canvas.nodes", "Nodes");

    /// <summary>The bar between a split view's two panes.</summary>
    /// <remarks>
    ///     ⚠ <b>The same kind as the scrollbars above — a shape with no caption — and it became
    ///     necessary the moment the bar became focusable.</b> A separator nobody can reach is a
    ///     line; a separator the Tab key lands on is a control, and a control a reader announces
    ///     as nothing at all is one a keyboard user has no way to identify. Named for what it is
    ///     rather than for what it divides, because what it divides is the application's sentence.
    /// </remarks>
    public static StringId SplitViewDivider { get; } = new("ui.control.split-view.divider", "Divider");

    /// <summary>Every string above, for a translator to start from.</summary>
    /// <remarks>
    ///     ⚠ <b>Spelled out rather than reflected over</b>, for the reason <c>Strings.Template</c>
    ///     gives: a list gathered by walking the properties at run time is a list an application's
    ///     trimming settings are entitled to shorten, and a template that is short by whatever the
    ///     trimmer removed is worse than no template.
    /// </remarks>
    public static IReadOnlyList<StringId> All { get; } = [
        TextInputClear,
        FieldRequired,
        DialogClose,
        DialogConfirm,
        DialogCancel,
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
        ColorPickerHue,
        ColorPickerAlpha,
        ColorPickerField,
        ColorPickerPalette,
        GradientEditorSpace,
        GradientEditorOpacity,
        GradientEditorColorStops,
        GradientEditorAlphaStops,
        NodeCanvasNodes,
        SplitViewDivider
    ];
}
