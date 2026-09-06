// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Inspector.Drawers;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>Dragging an asset out of the browser and onto the field that should name it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's Content row: "drag into an inspector field ⛔".</b> The picker button beside
///         every asset field works, and it is three interactions — press, search, choose — for a thing
///         the user is already looking at in the panel next door. Dragging is the gesture people reach
///         for first in every editor that has it, and its absence is felt on the first mesh somebody
///         tries to assign.
///     </para>
///     <para>
///         ⚠ <b>A hit test rather than a drop handler on the field, and the drag system is why.</b> A
///         drag belongs to the element the press landed on for its whole life — that is what makes it
///         a drag rather than a series of moves — so the row the pointer is released *over* never
///         hears about it. <c>ProjectBrowser</c> reports the point, and this turns a point into a
///         field. It is the same arrangement <c>DropIntoScene</c> already uses for the viewport, for
///         the same reason.
///     </para>
///     <para>
///         ⚠ <b>That reason has expired, and this is the note for whoever ports it (#654).</b> The
///         paragraph above is a claim about the framework as it was, and <c>Core/Vixen.Ui/Drop.cs</c>
///         now answers it directly: <c>TrackDrag</c> "hit-tests past <c>Captured</c>, which nothing
///         else positional does", precisely because "asking the capture where the pointer is would
///         answer 'on the source', forever, which is exactly the drag that can never be dropped
///         anywhere". So a field <i>can</i> hear its own drop today — <c>UiElement.AllowDrop</c>,
///         <c>DragOverEvent</c> with Entered/Moved/Left in place of <see cref="Over" />'s manual
///         class bookkeeping, and <c>on:drop</c> in place of <see cref="Drop" />'s point-to-field
///         search — and <c>DropEffect</c> is the framework's own name for the accept/refuse split
///         drawn here by hand.
///     </para>
///     <para>
///         It is not ported, and the port is more than this file: <c>ProjectBrowser</c> would call
///         <c>UiDocument.BeginDrag</c> with a <c>DataObject</c> instead of raising
///         <c>DroppedOutside</c>, and the ordering policy <c>EditorApplication.Dropped</c> writes
///         down — a field that <i>refused</i> a drop still consumes it rather than falling through to
///         the scene — has to become a refusal the route can express, because a drop the nearest
///         target declines is otherwise offered to the one behind it. That is a behaviour change
///         across three consumers with no test that photographs it, which is why it is written here
///         rather than attempted.
///     </para>
///     <para>
///         ⚠ <b>Only ever one asset, where a drop into the scene takes the whole selection.</b> A
///         member names one thing. Dragging four assets onto a mesh field and having it take the
///         first is a coin toss over what "first" means — selection order is not what the user sees —
///         so a multiple drop is refused and says so, rather than assigning one of them.
///     </para>
/// </remarks>
sealed class AssetFieldDrop {
    readonly AssetPicker picker;

    /// <summary>The field currently outlined, so it can be un-outlined.</summary>
    /// <remarks>
    ///     Held rather than found again by walking the tree: the panel that had it may have been
    ///     closed, scrolled or rebuilt between one drag event and the next, and a search for "whatever
    ///     has the class on it" would then find nothing and leave the class on an element still alive
    ///     somewhere off screen.
    /// </remarks>
    UiElement? highlighted;

    /// <summary>Points the drop at a project's assets.</summary>
    /// <param name="picker">Which assets, and what each field will take — the same answer the picker's own list gives.</param>
    public AssetFieldDrop(AssetPicker picker) {
        ArgumentNullException.ThrowIfNull(picker);

        this.picker = picker;
    }

    /// <summary>Marks the field under a drag as one that would take it, or as one that would not.</summary>
    /// <param name="root">The document's root, to search from.</param>
    /// <param name="assets">What the drag is carrying.</param>
    /// <param name="x">Where the pointer is.</param>
    /// <param name="y">Ditto.</param>
    public void Over(UiElement root, IReadOnlyList<AssetId> assets, float x, float y) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(assets);

        Clear();

        if (float.IsNaN(x) || float.IsNaN(y) || Target(root, x, y) is not { } row) {
            return;
        }

        highlighted = row.Editor;

        // ⚠ Both outcomes are drawn, and the refusal is the one that matters. A field that lit up
        // identically for a texture it will not take, then did nothing on release and said nothing
        // about why, is the interaction people repeat three times before concluding the editor is
        // broken — the answer has to arrive while the pointer is still down and the drag can still
        // be taken somewhere else.
        row.Editor.AddClass(Accepts(row.Field, assets) ? AssetDrawer.DropTargetClass : AssetDrawer.DropRejectedClass);
    }

    /// <summary>Assigns a dropped asset to the field under the pointer, if one is there.</summary>
    /// <param name="root">The document's root, to search from.</param>
    /// <param name="assets">What the drag was carrying.</param>
    /// <param name="x">Where it was released.</param>
    /// <param name="y">Ditto.</param>
    /// <returns>What happened, for the shell to say and for the scene to know it should stay out of it.</returns>
    /// <remarks>
    ///     ⚠ <b>Written through the field and sealed, exactly as the picker does.</b> That is what
    ///     puts the assignment on the document's stack as one step and what makes a component's row
    ///     commit the box it was editing — <c>ComponentsView</c> hangs its <c>SetComponentCommand</c>
    ///     off <c>InspectorField.Changed</c>, so a drop that wrote the member directly would change a
    ///     copy nobody can see.
    /// </remarks>
    public AssetFieldDropResult Drop(UiElement root, IReadOnlyList<AssetId> assets, float x, float y) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(assets);

        Clear();

        if (Target(root, x, y) is not { } row) {
            return AssetFieldDropResult.NotAField;
        }

        if (assets.Count != 1) {
            return new(row.Field.Member.DisplayName, null, AssetFieldDropOutcome.TooMany);
        }

        var asset = assets[0];

        if (!picker.Accepts(row.Field.Member, asset)) {
            return new(row.Field.Member.DisplayName, picker.NameOf(asset), AssetFieldDropOutcome.WrongKind);
        }

        if (!AssetDrawer.Assign(row.Field, asset)) {
            // The field already names it, or the condition on the member means the edit reaches
            // nothing. Neither is a failure and neither is a change — and a notification saying it
            // was assigned would be a notification for nothing happening.
            return new(row.Field.Member.DisplayName, picker.NameOf(asset), AssetFieldDropOutcome.Unchanged);
        }

        row.Field.Seal();

        // ⚠ Redrawn here rather than left to the view. A row is filled in from its field when it is
        // built and after an edit the *drawer* made; this edit came from outside the drawer entirely,
        // so nothing else would ever put the new name into the label.
        InspectorRows.Show(row);

        return new(row.Field.Member.DisplayName, picker.NameOf(asset), AssetFieldDropOutcome.Assigned);
    }

    /// <summary>Takes the outline off whatever last had it.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="UiElement.IsRemoved" /> is checked, and it is not defensive noise.</b> The
    ///     panel can be rebuilt between two frames of one drag — an undo, a play-mode restore, a
    ///     component reload — and every path off a removed element throws rather than answering,
    ///     deliberately, because the node ids it still holds now address somebody else's slots. This
    ///     runs from inside a pointer handler, where an exception is a dead frame rather than a
    ///     refused edit, and the class it would have removed went away with the element.
    /// </remarks>
    public void Clear() {
        if (highlighted is { IsRemoved: false } element) {
            element.RemoveClass(AssetDrawer.DropTargetClass);
            element.RemoveClass(AssetDrawer.DropRejectedClass);
        }

        highlighted = null;
    }

    bool Accepts(InspectorField field, IReadOnlyList<AssetId> assets) =>
        assets.Count == 1 && picker.Accepts(field.Member, assets[0]);

    /// <summary>The asset field under a point, if there is one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Deepest match wins, and the walk therefore does not stop at the first hit.</b>
    ///         Rows nest: a list of materials is an <c>InspectorRow</c> whose editor contains one row
    ///         per element, and the outer one's bounds cover all of them. Taking the first would
    ///         assign to the list rather than to the element the pointer is actually over.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <i>editor</i>'s bounds, not the row's.</b> A row spans the panel and is mostly
    ///         its label; a drop on the word "Mesh" is aimed at the field, but so is every drop in the
    ///         forty pixels of empty space to the right of a short one. The editor is the part that
    ///         looks like a target, and it is the part that gets outlined.
    ///     </para>
    /// </remarks>
    static InspectorRow? Target(UiElement element, float x, float y) {
        if (!Inside(element, x, y)) {
            return null;
        }

        foreach (var child in element.Children) {
            if (Target(child, x, y) is { } deeper) {
                return deeper;
            }
        }

        return element is InspectorRow { Drawer: AssetDrawer, Field.CanWrite: true } row && Inside(row.Editor, x, y)
            ? row
            : null;
    }

    static bool Inside(UiElement element, float x, float y) {
        var bounds = element.Bounds;

        return x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height;
    }
}

/// <summary>What a drop onto an inspector field did.</summary>
public enum AssetFieldDropOutcome {
    /// <summary>There was no asset field under the pointer. Somebody else's drop, or nobody's.</summary>
    NotAField,

    /// <summary>The field now names the asset.</summary>
    Assigned,

    /// <summary>The field already named it, so nothing was recorded.</summary>
    Unchanged,

    /// <summary>The asset is not the kind this member takes.</summary>
    WrongKind,

    /// <summary>Several assets were dragged onto a member that names one.</summary>
    TooMany
}

/// <summary>The outcome of a drop, and enough to describe it.</summary>
/// <param name="Member">What the field is called.</param>
/// <param name="Asset">What was dropped on it, or <see langword="null" /> when that is not the point.</param>
/// <param name="Outcome">What happened.</param>
public readonly record struct AssetFieldDropResult(string? Member, string? Asset, AssetFieldDropOutcome Outcome) {
    /// <summary>Nothing was under the pointer.</summary>
    public static AssetFieldDropResult NotAField => new(null, null, AssetFieldDropOutcome.NotAField);

    /// <summary>Whether the drop was over a field at all — which is what stops the scene taking it.</summary>
    /// <remarks>
    ///     ⚠ <b>A refused drop still counts as handled.</b> Dragging a texture onto a mesh field and
    ///     getting an entity spawned in the middle of the level instead is a worse outcome than the
    ///     refusal, and it is one the user then has to undo.
    /// </remarks>
    public bool IsHandled => Outcome != AssetFieldDropOutcome.NotAField;
}
