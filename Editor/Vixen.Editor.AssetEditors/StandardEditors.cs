// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Editor.AssetEditors.Audio;
using Vixen.Editor.AssetEditors.Code;
using Vixen.Editor.AssetEditors.Compositor;
using Vixen.Editor.AssetEditors.Content;
using Vixen.Editor.AssetEditors.Fonts;
using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.AssetEditors.Input;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Editor.AssetEditors.Scenes;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.AssetEditors.Vfx;

namespace Vixen.Editor.AssetEditors;

/// <summary>The editors this assembly ships, as a set a host can register in one line.</summary>
/// <remarks>
///     <para>
///         Registration stays explicit — see <see cref="AssetEditorRegistry" /> for why nothing is
///         discovered — and this is the list, in one place, rather than nine lines every host has to
///         keep in step. A host that wants a different set builds its own registry and adds what it
///         wants; a plugin adds to whichever registry it was handed.
///     </para>
///     <para>
///         ⚠ <b>The scene and the prefab editors need a world and this cannot decide where from.</b>
///         Whether two open scenes share one world is an application's decision with consequences for
///         play mode and for how much memory ten open scenes cost — so the suppliers are arguments,
///         and they are two arguments rather than one because a prefab is edited in isolation and
///         sharing the scene's world would silently take that away.
///     </para>
/// </remarks>
public static class StandardEditors {
    /// <summary>Every editor this assembly ships, registered into a fresh registry.</summary>
    /// <param name="scenes">Where an opened scene's world comes from.</param>
    /// <param name="prefabs">Where an opened prefab's own world comes from.</param>
    /// <returns>The registry.</returns>
    public static AssetEditorRegistry CreateDefault(
        Func<AssetEditorRequest, World> scenes,
        Func<AssetEditorRequest, World> prefabs
    ) {
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(prefabs);

        return new AssetEditorRegistry()
            .Add(new TextureEditorFactory())
            .Add(new ModelEditorFactory())
            .Add(new MaterialEditorFactory())
            .Add(new SceneEditorFactory(scenes))
            .Add(new PrefabEditorFactory(prefabs))
            .Add(new ShaderEditorFactory())
            .Add(new MarkupEditorFactory())
            .Add(new AddressableGroupEditorFactory())
            .Add(new CompositorEditorFactory())
            .Add(new ShaderGraphEditorFactory())
            .Add(new VfxEditorFactory())
            .Add(new AnimationClipEditorFactory())
            .Add(new AnimationGraphEditorFactory())
            .Add(new MoveSetEditorFactory())
            .Add(new ProxyShapeEditorFactory())
            .Add(new SequenceEditorFactory())
            .Add(new AudioMixerEditorFactory())
            .Add(new InputActionsEditorFactory())
            .Add(new FontEditorFactory());
    }

    /// <summary>The editors that need nothing from the host, for a test or a headless tool.</summary>
    /// <returns>The registry.</returns>
    /// <remarks>
    ///     Everything except the scene and prefab editors, which need a world. Useful precisely
    ///     because it is the set that can be built with no ECS in the way — which is what makes the
    ///     registry testable without one.
    /// </remarks>
    public static AssetEditorRegistry CreateWorldless() =>
        new AssetEditorRegistry()
            .Add(new TextureEditorFactory())
            .Add(new ModelEditorFactory())
            .Add(new MaterialEditorFactory())
            .Add(new ShaderEditorFactory())
            .Add(new MarkupEditorFactory())
            .Add(new AddressableGroupEditorFactory())
            .Add(new CompositorEditorFactory())
            .Add(new ShaderGraphEditorFactory())
            .Add(new VfxEditorFactory())
            .Add(new AnimationClipEditorFactory())
            .Add(new AnimationGraphEditorFactory())
            .Add(new MoveSetEditorFactory())
            .Add(new ProxyShapeEditorFactory())
            .Add(new SequenceEditorFactory())
            .Add(new AudioMixerEditorFactory())
            .Add(new InputActionsEditorFactory())
            .Add(new FontEditorFactory());
}
