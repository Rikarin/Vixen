// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Plugin;

namespace Vixen.Editor.AssetEditors;

/// <summary>What doc 34's editors cannot do for themselves: reach another asset.</summary>
/// <remarks>
///     <para>
///         <b>Four documents, four hooks, one place.</b> A proxy shape set needs the rig it hangs
///         off, a move set needs the sets it overlays, a clip needs the scene it was marked up
///         against, and a harness plan needs everything it names by path. Each of them deliberately
///         refuses to go looking — a document that knew how a project is laid out would be a document
///         no test could open — so each declares the shape of the answer and somebody with a project
///         supplies it. This module is that somebody.
///     </para>
///     <para>
///         ⚠ <b>The panels are inert without it, which is what makes this the last mile rather than
///         a nicety.</b> Unbound, the shape viewport draws nothing, Run says it has no project and
///         Propose Contacts says it has no scene — all three honest, all three useless.
///     </para>
///     <para>
///         ⚠ <b>It was a line in the editor's application, and that is the finding.</b> Doc 36 § F2:
///         binding a freshly-opened document to the project it belongs to was
///         <c>EditorApplication.Bound</c>, which meant a second feature with resolvers would have
///         been a second line in a class that already had too many. `AssetEditorRegistry.Opened` is
///         the seam that replaces it — and it is on the registry rather than on the project, because
///         <c>EditorProject.Register</c> runs from a document's base constructor and would hand a
///         subscriber a half-built one.
///     </para>
/// </remarks>
public sealed class AssetEditorsModule : IEditorPlugin {
    /// <summary>What the host activates it under, and what a plugin depending on it names.</summary>
    public const string ModuleId = "vixen.asset-editors";

    /// <summary>What a plugin-management panel calls it.</summary>
    public const string ModuleName = "Asset Editors";

    AnimationBinder binder = null!;

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var editors = context.Services.Require<AssetEditorRegistry>();

        binder = new AnimationBinder(context.Services.Require<EditorProject>());

        editors.Opened += binder.Bind;
        context.OnUnload(() => editors.Opened -= binder.Bind);
    }
}
