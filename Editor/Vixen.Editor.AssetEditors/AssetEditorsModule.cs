// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Frame;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Ui.HotReload;

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

        Frames(context, editors);
    }

    /// <summary>Doc 39's frame panel, bound to the four things it cannot reach for itself.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The fifth hook, and it is the same shape as the other four.</b> The frame editor
    ///         needs the contribution registry to find its own markup forms, the reload host so
    ///         editing that markup shows up without a restart, the shown scene to fold volumes
    ///         against, and the focused view to fold them <em>from</em>. All four are published in
    ///         <c>PluginServices</c>; none of them exists when <see cref="StandardEditors" /> builds
    ///         the registry, which is why they are properties set here rather than constructor
    ///         arguments there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Found by name rather than constructed here, because a host may have built its own
    ///         registry.</b> An editor assembled from a different set of factories should get the
    ///         binding for the frame editor it has, or no binding at all — not a second frame editor
    ///         that clashes with the first over <c>.vxcompositor</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every one of the four is optional and the panel degrades honestly without it.</b>
    ///         No registry means the generated rows in declaration order; no host means the markup
    ///         does not follow an edit; no scene means the volume panel says there is nothing to
    ///         fold; no view means it folds from the origin. A required service here would make the
    ///         whole editor refuse to start in a host that has three of them.
    ///     </para>
    /// </remarks>
    static void Frames(PluginContext context, AssetEditorRegistry editors) {
        if (!editors.TryGetByName(StandardFrameEditorFactory.EditorName, out var found)
            || found is not StandardFrameEditorFactory frames) {
            return;
        }

        frames.Scene = context.Services.TryGet<IActiveScene>(out var scene) ? scene : null;
        frames.Eye = context.Services.TryGet<IActiveView>(out var eye) ? eye : null;

        if (!context.Services.TryGet<IEditorRegistry>(out var registry)) {
            return;
        }

        frames.Contributions = registry;

        foreach (var contribution in StandardFrameEditorFactory.Contribute(
            registry,
            context.Services.TryGet<HotReloadHost>(out var reload) ? reload : null
        )) {
            context.Owns(contribution);
        }

        context.OnUnload(() => {
            frames.Contributions = null;
            frames.Scene = null;
            frames.Eye = null;
        });
    }
}
