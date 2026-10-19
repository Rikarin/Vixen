// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;

namespace Vixen.Editor.SceneView;

/// <summary>What the Scene panel is looking at, which must outlive the panel: how it is split, and each pane's camera and saved views.</summary>
/// <remarks>
///     <para>
///         <b>The workspace's per-view model (doc 50 § D2, #1303).</b> A Scene panel is built afresh every
///         time it is opened, and its <see cref="SceneViewport" />s with it, so anything a pane keeps on
///         itself goes with the old one. The panel factory reads this on build and the panes write
///         through it — it is <c>IWorkspace.ViewState&lt;SceneViewState&gt;("scene")</c> in the editor.
///     </para>
///     <para>
///         ⚠ <b>The nine bookmarks used to be the pane's own array</b>, so closing the Scene panel threw
///         every saved view away while the camera beside them — parked on the application — came back.
///     </para>
/// </remarks>
public sealed class SceneViewState {
    /// <summary>How many panes the widest arrangement has.</summary>
    public const int MaxPanes = 4;

    readonly PaneState[] panes = [new(), new(), new(), new()];

    /// <summary>How the panel is split.</summary>
    public ViewportArrangement Arrangement { get; set; } = ViewportArrangement.Single;

    /// <summary>Each pane's state, in reading order, for the widest arrangement.</summary>
    public IReadOnlyList<PaneState> Panes => panes;

    /// <summary>Forgets where every pane's camera was, keeping the saved views.</summary>
    /// <remarks>
    ///     ⚠ <b>What a rearrangement does, and only to the cameras.</b> A saved camera restored into a
    ///     freshly split layout would overwrite the quad layout's top/front/side presets with copies of
    ///     wherever the single pane was looking. A bookmark is restored only when asked for, so it has no
    ///     preset to overwrite and is kept.
    /// </remarks>
    public void ForgetCameras() {
        foreach (var pane in panes) {
            pane.Camera = null;
        }
    }
}

/// <summary>One pane's durable state: where its camera was, and its nine saved views.</summary>
public sealed class PaneState {
    /// <summary>Where the camera was when last read, or <see langword="null" /> for a pane nobody has looked through.</summary>
    public ViewBookmark? Camera { get; set; }

    /// <summary>The numbered views.</summary>
    public ViewBookmarks Bookmarks { get; } = new();
}

/// <summary>A pane's nine numbered views, empty where nothing has been saved.</summary>
/// <remarks>
///     Nine, because the gesture is <c>Ctrl+1..9</c> to set and <c>1..9</c> to recall and a tenth slot
///     would have no key — see <see cref="SceneViewport.BookmarkSlots" />.
/// </remarks>
public sealed class ViewBookmarks : IReadOnlyList<ViewBookmark?> {
    readonly ViewBookmark?[] slots = new ViewBookmark?[SceneViewport.BookmarkSlots];

    /// <inheritdoc />
    public int Count => slots.Length;

    /// <summary>One slot.</summary>
    /// <param name="slot">Which, from zero.</param>
    /// <returns>What is in it.</returns>
    public ViewBookmark? this[int slot] {
        get => slots[slot];
        set => slots[slot] = value;
    }

    /// <inheritdoc />
    public IEnumerator<ViewBookmark?> GetEnumerator() => ((IEnumerable<ViewBookmark?>)slots).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
