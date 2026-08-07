// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>A model, open for editing: what came out of it, and the settings that decide what will.</summary>
/// <remarks>
///     <para>
///         <b>The panel is <c>ModelImportView.vxml</c>; this file exists only to make it public and
///         sealed.</b> The markup compiler emits a partial class with no accessibility modifier,
///         which is <c>internal</c> — deliberately, so that a component is not public API by
///         accident — and this one is constructed from another assembly.
///     </para>
///     <para>
///         ⚠ <b>It is still a <c>Control</c>, and that is what <c>@inherits</c> bought.</b> Doc 36
///         § F7 wave 1a could not port this panel: the emitter hardcoded <c>Component</c>, and a
///         component is not a <see cref="Vixen.Ui.UiElement" /> — so
///         <c>panel.Add&lt;ModelImportView&gt;()</c>, which is how both of its callers make one,
///         would have had to become <c>BuildContext.Build&lt;…&gt;(document, panel)</c> and every
///         test that walks the tree looking for it would have needed a second finder. The header is
///         one line and none of that happened; the three <c>ref</c>s are what replaced the
///         <c>Part&lt;T&gt;()</c> assignments.
///     </para>
///     <para>
///         <b>The part list is the sidecar's, not an import's.</b> A model is a model and also a
///         mesh per material, a skeleton and a clip per animation; the last import wrote all of them
///         into <c>subAssets</c>, so the list opens instantly and says what is actually addressable
///         today. Re-importing to populate a panel would put Assimp between a double-click and a
///         window.
///     </para>
///     <para>
///         ⚠ <b>An asset that has never been imported shows an empty list and says so</b>, rather
///         than showing nothing and letting it read as a model with no meshes in it. That state is
///         ordinary — a file just dropped into the project has it until the next import pass.
///     </para>
///     <para>
///         ⚠ <b>No LOD preview and no viewport.</b> Doc 11 asks for both. Drawing a mesh needs a
///         device and a render target, which is the application's — the same wall the texture
///         preview meets. The levels themselves now exist: <c>ModelCompiler</c> writes a
///         <c>Meshlets</c> sub-asset holding the whole cluster hierarchy, so what is missing is a
///         way to draw a cut through it rather than anything to draw.
///     </para>
/// </remarks>
public sealed partial class ModelImportView;
