// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.App;

/// <summary>Part C's <b>Undo History⋯</b>: what has been done, and how far back a click goes.</summary>
/// <remarks>
///     <para>
///         <b><see cref="CommandStack.History" /> has existed since the stack was written, and its
///         own remarks name this panel as the reason.</b> Every entry has a name — "Set Roughness",
///         "Align With View" — because a command supplies one so that the Edit menu can read "Undo
///         Set Roughness", and a list of those names is the whole of what an undo history is.
///     </para>
///     <para>
///         ⚠ <b>Clicking an entry undoes back to it rather than undoing that one.</b> An undo stack
///         is a sequence and not a set: removing the third of ten edits would need every later
///         command rebased against a world that no longer matches, which <c>CommandStack</c>'s own
///         remarks call a research project. Going back to a point is the operation the stack does
///         support, and it is the one people mean.
///     </para>
///     <para>
///         ⚠ <b>The clean mark is drawn, because it is the only thing here that answers "what have I
///         not saved".</b> The stack knows where the last write was; a history that showed ten
///         identical rows leaves the user to count.
///     </para>
/// </remarks>
sealed partial class UndoHistoryView : Control {
    readonly List<Button> entries = [];

    Func<CommandStack?>? source;

    /// <summary>What the list was last built from, so a frame that changed nothing costs a compare.</summary>
    (CommandStack? Stack, int Count, int Depth) shown = (null, -1, -1);

    /// <inheritdoc />
    protected override string TagName => "history-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>Where the entries go.</summary>
    public ScrollView List { get; private set; } = null!;

    /// <summary>Which stack it is showing, or <see langword="null" /> when there is none.</summary>
    public CommandStack? Stack => source?.Invoke();

    /// <summary>Points the panel at whichever stack is current.</summary>
    /// <param name="stack">
    ///     Asked every refresh rather than held, because the active document changes and a panel
    ///     holding one stack would go on showing a closed document's history.
    /// </param>
    public void Show(Func<CommandStack?> stack) {
        ArgumentNullException.ThrowIfNull(stack);

        source = stack;
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("history-toolbar");

        var label = Toolbar.Add<TextBlock>();
        label.Text = EditorStrings.HistoryHint.Text;

        List = Part<ScrollView>();
    }

    /// <summary>Rebuilds the list if the stack it is showing has moved.</summary>
    /// <remarks>
    ///     ⚠ <b>Called once a frame by the application, and it compares before it rebuilds.</b> A
    ///     stack is signal-backed and nothing in the editor's loop flushes the reactive scheduler, so
    ///     polling is the same trade this application already makes for the selections — but a
    ///     gizmo drag pushes an entry per frame, and a panel that rewrote two hundred and fifty-six
    ///     labels each time would be doing it during the one operation where frames are precious.
    /// </remarks>
    public void Tick() {
        var stack = Stack;
        var state = (stack, stack?.History.Count ?? -1, stack?.Depth.Value ?? -1);

        if (state == shown) {
            return;
        }

        shown = state;
        Refresh();
    }

    /// <summary>Rebuilds the list from the stack, whether or not it has moved.</summary>
    public void Refresh() {
        var stack = Stack;
        var history = stack?.History ?? [];

        // ⚠ The rows are pooled and only ever grow, because a history panel is rebuilt on every
        // edit — which for a gizmo drag is once a frame — and a panel that allocated a button per
        // entry per frame would be the leak doc 20 says the console must not be.
        while (entries.Count < history.Count + 1) {
            var button = List.Content.Add<Button>();
            var index = entries.Count;

            button.Variant = ControlVariant.Subtle;
            button.AddClass("history-entry");
            button.Clicked += _ => Rewind(index);

            entries.Add(button);
        }

        // The first row is where the document was before anything was done to it, which is the one
        // place a history has to be able to get back to and the one that is not an entry.
        Fill(0, EditorStrings.HistoryOriginal.Text, stack is not null);

        for (var index = 0; index < history.Count; index++) {
            Fill(index + 1, history[index].Name, true);
        }

        for (var index = history.Count + 1; index < entries.Count; index++) {
            entries[index].AddClass("hidden");
        }

        void Fill(int slot, string text, bool enabled) {
            var button = entries[slot];

            button.RemoveClass("hidden");
            button.Label = text;
            button.Disabled = !enabled;

            // Checked is "this is where the document is now", which is the depth of the undo list.
            if (stack is not null && slot == stack.Depth.Value) {
                button.State |= ElementState.Checked;
            } else {
                button.State &= ~ElementState.Checked;
            }
        }
    }

    /// <summary>Undoes or redoes until the stack is at a depth.</summary>
    /// <param name="depth">How many entries should be undoable afterwards.</param>
    /// <remarks>
    ///     ⚠ <b>Bounded by the number of entries rather than by "until it stops".</b> A stack that
    ///     refused an undo — a command that threw on the way back — would otherwise be a loop in the
    ///     frame thread, and a history panel is the last place that should be able to hang the editor.
    /// </remarks>
    public void Rewind(int depth) {
        if (Stack is not { } stack) {
            return;
        }

        for (var guard = stack.History.Count + stack.Depth.Value; guard >= 0 && stack.Depth.Value > depth; guard--) {
            if (!stack.Undo()) {
                break;
            }
        }

        for (var guard = stack.History.Count; guard >= 0 && stack.Depth.Value < depth; guard--) {
            if (!stack.Redo()) {
                break;
            }
        }

        Refresh();
    }

    /// <summary>How many rows the list is showing, for a test to count.</summary>
    public int Count => entries.Count(button => !button.HasClass("hidden"));

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(UndoHistoryView)} ({Count} entries)");
}
