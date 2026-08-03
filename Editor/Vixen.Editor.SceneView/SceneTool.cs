// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.SceneView;

/// <summary>Something you can be doing in the viewport, offered inside whatever mode is active.</summary>
/// <param name="Id">What everything refers to it by. Prefix a plugin's with the plugin's own id.</param>
/// <param name="Title">What its button says.</param>
/// <param name="Input">What gets the pane's input while the tool is the active one.</param>
/// <param name="Target">
///     The component type that has to be on the selection for the tool to be offered, or
///     <see langword="null" /> for a tool that always applies. Read by whatever draws the tool strip;
///     the pane itself does not filter, because it does not know what a component is.
/// </param>
/// <param name="Order">Where among the tools, low first. Ties keep registration order.</param>
/// <remarks>
///     <para>
///         <b>Finer than an <c>IEditorMode</c>, and the distinction is Unity's.</b> A mode is a
///         statement about what a click means globally — Select, Terrain, Foliage — and there are a
///         handful. A tool is one of the things you can be doing <i>inside</i> one, scoped to what is
///         selected, and there are as many as the selection has verbs. Making every tool a mode is
///         how a mode bar becomes a toolbar with a worse name.
///     </para>
///     <para>
///         ⚠ <b>The pane offers the active tool first refusal before the mode gets it.</b> A tool is
///         the more specific claim: it was chosen for this selection, where the mode was chosen for
///         the session. <see cref="SceneViewport.ActiveTool" /> is what sets one, and it is
///         deliberately the pane's rather than the shell's — two panes side by side can be in
///         different tools the way they are already in different view modes.
///     </para>
/// </remarks>
public sealed record SceneTool(
    string Id,
    string Title,
    IViewportInput Input,
    Type? Target = null,
    int Order = 0
);
