// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Ai;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Editor.AssetEditors.Audio;
using Vixen.Editor.AssetEditors.Code;
using Vixen.Editor.AssetEditors.Compositor;
using Vixen.Editor.AssetEditors.Content;
using Vixen.Editor.AssetEditors.Fonts;
using Vixen.Editor.AssetEditors.Frame;
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

        return Fill(
            new AssetEditorRegistry(),
            new TextureEditorFactory(),
            new ModelEditorFactory(),
            new MaterialEditorFactory(),
            new SceneEditorFactory(scenes),
            new PrefabEditorFactory(prefabs),
            new ShaderEditorFactory(),
            new MarkupEditorFactory(),
            new AddressableGroupEditorFactory(),
            new CompositorEditorFactory(),
            new StandardFrameEditorFactory(),
            new ShaderGraphEditorFactory(),
            new VfxEditorFactory(),
            new AnimationClipEditorFactory(),
            new AnimationGraphEditorFactory(),
            new BehaviorTreeEditorFactory(),
            new UtilitySetEditorFactory(),
            new GoapDomainEditorFactory(),
            new QueryEditorFactory(),
            new MoveSetEditorFactory(),
            new ProxyShapeEditorFactory(),
            new HarnessEditorFactory(),
            new ShapeVocabularyEditorFactory(),
            new SequenceEditorFactory(),
            new AudioMixerEditorFactory(),
            new InputActionsEditorFactory(),
            new FontEditorFactory()
        );
    }

    /// <summary>The editors that need nothing from the host, for a test or a headless tool.</summary>
    /// <returns>The registry.</returns>
    /// <remarks>
    ///     Everything except the scene and prefab editors, which need a world. Useful precisely
    ///     because it is the set that can be built with no ECS in the way — which is what makes the
    ///     registry testable without one.
    /// </remarks>
    public static AssetEditorRegistry CreateWorldless() =>
        Fill(
            new AssetEditorRegistry(),
            new TextureEditorFactory(),
            new ModelEditorFactory(),
            new MaterialEditorFactory(),
            new ShaderEditorFactory(),
            new MarkupEditorFactory(),
            new AddressableGroupEditorFactory(),
            new CompositorEditorFactory(),
            new StandardFrameEditorFactory(),
            new ShaderGraphEditorFactory(),
            new VfxEditorFactory(),
            new AnimationClipEditorFactory(),
            new AnimationGraphEditorFactory(),
            new BehaviorTreeEditorFactory(),
            new UtilitySetEditorFactory(),
            new GoapDomainEditorFactory(),
            new QueryEditorFactory(),
            new MoveSetEditorFactory(),
            new ProxyShapeEditorFactory(),
            new HarnessEditorFactory(),
            new ShapeVocabularyEditorFactory(),
            new SequenceEditorFactory(),
            new AudioMixerEditorFactory(),
            new InputActionsEditorFactory(),
            new FontEditorFactory()
        );

    /// <summary>Registers a set into a registry and hands it back.</summary>
    /// <param name="registry">Where they go.</param>
    /// <param name="editors">The editors.</param>
    /// <returns>The registry.</returns>
    /// <remarks>
    ///     ⚠ <b>A loop rather than a chain, because <see cref="AssetEditorRegistry.Add" /> now hands
    ///     back the removal instead of itself</b> — <a href="https://github.com/Rikarin/Vixen/issues/739">#739</a>.
    ///     The removals are dropped here on purpose and only here: this is the set the build ships,
    ///     registered once into a registry that lives as long as the process. The case that keeps
    ///     them is a plugin's, which is what the change was for.
    /// </remarks>
    static AssetEditorRegistry Fill(AssetEditorRegistry registry, params IAssetEditorFactory[] editors) {
        foreach (var editor in editors) {
            registry.Add(editor);
        }

        return registry;
    }
}
