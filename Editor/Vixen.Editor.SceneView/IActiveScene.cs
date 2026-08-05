// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>Which scene the editor is showing right now.</summary>
/// <remarks>
///     <para>
///         <b>Not the same question as "which scene is open".</b> An editor with a prefab open is
///         inspecting the prefab and showing the level behind it; a panel that counts entities has to
///         count the one in front of the user, or pressing Refresh in a prefab reports the level.
///     </para>
///     <para>
///         ⚠ <b>A contract rather than a published <c>SceneDocument</c>, because the answer moves.</b>
///         A module handed the document at activation would hold the one that was open when it
///         loaded — and would go on counting it after the user opened a second scene. This is asked
///         every time it is needed.
///     </para>
/// </remarks>
public interface IActiveScene {
    /// <summary>The scene the editor is showing, which is never null once a project is open.</summary>
    SceneDocument Current { get; }
}

/// <summary>Which eye the editor is looking through right now.</summary>
/// <remarks>
///     <para>
///         <b><see cref="IActiveScene" />'s companion, and it exists for one reason: a camera
///         position is an input to rendering questions, not decoration.</b> Doc 39's resolved volume
///         stack is per camera because <c>PostProcessVolumeSystem</c> weighs every volume by how far
///         the camera is from it — so a panel answering "why does it look like this" has to be able
///         to say which eye it answered for, and to move when the user flies somewhere else.
///     </para>
///     <para>
///         ⚠ <b>A <c>RenderView</c> rather than a <c>Vector3</c>, and rather than the viewport.</b>
///         The view is the thing a frame is actually drawn through and the thing the volume system
///         already takes; handing out a position would freeze at the moment of asking, and handing
///         out the <c>SceneViewport</c> would put every pane's input handling in reach of anything
///         that wanted a camera.
///     </para>
///     <para>
///         ⚠ <b>Null when no pane has focus</b>, which is an ordinary state — an editor showing only
///         an asset editor has no viewport at all — rather than a failure to report.
///     </para>
/// </remarks>
public interface IActiveView {
    /// <summary>The view the focused pane draws through, or null when no pane has focus.</summary>
    RenderView? Current { get; }
}
