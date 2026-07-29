// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Scenes;

/// <summary>A level, compiled. What a content build writes and a player loads.</summary>
/// <remarks>
///     <para>
///         <b>The runtime end of the scene pipeline, and the reason the authoring format can afford
///         to be unhurried.</b> A <c>.vxscene</c> is YAML because a person merges it; this is a chunk
///         in the object database because a player loads it on a frame budget. Nothing reads the
///         authored form at run time and nothing edits this one — <c>SceneCompiler</c> is the only
///         thing that has ever seen both.
///     </para>
///     <para>
///         <b>The version is read, unlike the authoring format's.</b> A scene in a shipped build is
///         loaded by a binary that may be older than the content — a downloaded update against an app
///         nobody reinstalled — so the check is not theoretical here. The member layout is handled by
///         the serializer's own two varints; this number is for a change the layout cannot express.
///     </para>
/// </remarks>
[DataContract("SceneAsset")]
public sealed class SceneAsset {
    /// <summary>The version this build writes and reads.</summary>
    public const int Current = 1;

    /// <summary>Which version of the compiled form this is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the scene is called, for diagnostics and for the editor's scene list.</summary>
    public string Name { get; set; } = "Scene";

    /// <summary>Its entities.</summary>
    public SceneContent Content { get; set; } = new();

    /// <summary>Loads it into a world as a scene of its own, additively.</summary>
    /// <param name="scenes">The manager that owns the world.</param>
    /// <param name="created">
    ///     Filled with one entity per node, in the asset's order, or an empty span not to ask.
    /// </param>
    /// <returns>The scene, which is what unloads it again.</returns>
    /// <exception cref="NotSupportedException">The asset is from a newer build than this one.</exception>
    /// <exception cref="SceneComponentException">It names a component this build does not have.</exception>
    /// <remarks>
    ///     Every entity is tagged with the new scene, so unloading is the query and destroy
    ///     <see cref="SceneManager.Unload" /> already does. The tag is part of each block's archetype
    ///     rather than added afterwards, so a load stays one bulk create per archetype.
    /// </remarks>
    public SceneHandle Load(SceneManager scenes, Span<Entity> created = default) {
        ArgumentNullException.ThrowIfNull(scenes);
        Check();

        var scene = scenes.Create(Name);
        Content.Instantiate(scenes.World, created, scene);

        return scene;
    }

    /// <summary>Creates its entities in a world, without a scene to tag them with.</summary>
    /// <param name="world">Where to put them.</param>
    /// <param name="created">
    ///     Filled with one entity per node, in the asset's order, or an empty span not to ask.
    /// </param>
    /// <returns>The roots.</returns>
    /// <exception cref="NotSupportedException">The asset is from a newer build than this one.</exception>
    /// <remarks>
    ///     For a test, a tool and anything holding a world without a <see cref="SceneManager" /> over
    ///     it. Entities loaded this way are not tagged, so no unload will find them — which is why
    ///     the form that takes a manager is the one a game uses.
    /// </remarks>
    public IReadOnlyList<Entity> Instantiate(World world, Span<Entity> created = default) {
        Check();
        return Content.Instantiate(world, created);
    }

    void Check() {
        if (Version > Current) {
            throw new NotSupportedException(
                $"'{Name}' was compiled as version {Version} and this build reads {Current}. Loading it would "
                + "make entities out of the parts that happen to still line up, which is worse than not loading "
                + "it: rebuild the content against this build, or ship the build the content was made for."
            );
        }
    }
}

/// <summary>A prefab, compiled. The instantiate plan doc 04 asks for, in a form that can be shipped.</summary>
/// <remarks>
///     <para>
///         <b>The same content as a scene, with one root and a different life.</b> A scene is loaded
///         once and unloaded; a prefab is stamped out repeatedly, so what a game wants from this is
///         not entities but a <see cref="Prefab" /> — the template world that makes an instantiation
///         a row copy per archetype.
///     </para>
///     <para>
///         ⚠ <b>Building the template costs one instantiation, and that is deliberate.</b>
///         <see cref="ToPrefab" /> makes the entities in a staging world and captures them, so the
///         asset carries no second description of what a template is and there is nothing to keep in
///         step. It is paid once per prefab asset, at load, against every instantiation for the rest
///         of the process — and the alternative, a capture format of its own, is a second serialiser
///         for the same components with its own way of being subtly different.
///     </para>
/// </remarks>
[DataContract("PrefabAsset")]
public sealed class PrefabAsset {
    /// <summary>The version this build writes and reads.</summary>
    public const int Current = 1;

    /// <summary>Which version of the compiled form this is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the prefab is called.</summary>
    public string Name { get; set; } = "Prefab";

    /// <summary>Its entities, rooted in exactly one of them.</summary>
    public SceneContent Content { get; set; } = new();

    /// <summary>Builds the template a game instantiates from.</summary>
    /// <returns>The prefab, which the caller owns and disposes.</returns>
    /// <exception cref="NotSupportedException">The asset is from a newer build than this one.</exception>
    /// <exception cref="InvalidOperationException">It does not have exactly one root.</exception>
    /// <exception cref="SceneComponentException">It names a component this build does not have.</exception>
    public Prefab ToPrefab() {
        if (Version > Current) {
            throw new NotSupportedException(
                $"'{Name}' was compiled as version {Version} and this build reads {Current}. Rebuild the content "
                + "against this build, or ship the build the content was made for."
            );
        }

        using var staging = new World($"PrefabAsset:{Name}");
        var roots = Content.Instantiate(staging, []);

        return roots.Count == 1
            ? Prefab.CaptureFrom(staging, roots[0], Name)
            : throw new InvalidOperationException(
                $"'{Name}' has {roots.Count} roots and a prefab has one. A prefab is a subtree, so a file with "
                + "two of them is a scene that was saved with the wrong extension."
            );
    }

    /// <summary>Stamps out one instance, without keeping the template.</summary>
    /// <param name="world">Where to put it.</param>
    /// <param name="at">Where to put the root, or <see langword="null" /> to keep the compiled transform.</param>
    /// <returns>The instance's root.</returns>
    /// <remarks>
    ///     ⚠ <b>For the one-off, and it says so.</b> This builds the template, uses it once and
    ///     throws it away, so instantiating in a loop through here is quadratic work for a linear
    ///     result. Anything that spawns more than once holds the <see cref="Prefab" />
    ///     <see cref="ToPrefab" /> returns.
    /// </remarks>
    public Entity Instantiate(World world, LocalTransform? at = null) {
        using var prefab = ToPrefab();
        return prefab.Instantiate(world, at);
    }
}
