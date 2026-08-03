// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
