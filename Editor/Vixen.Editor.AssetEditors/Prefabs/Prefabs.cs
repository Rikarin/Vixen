// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;

namespace Vixen.Editor.AssetEditors.Prefabs;

/// <summary>Where one entity in a scene came from.</summary>
/// <param name="Prefab">Which prefab asset.</param>
/// <param name="Source">Which entity inside it, by the id the prefab file gave that entity.</param>
/// <remarks>
///     ⚠ <b>The template is named by the file's id and not by a handle.</b> A prefab is a document
///     that may not be open, and even when it is, its entities live in a different world — so a
///     handle would name a slot in the wrong world. <see cref="EntityId" /> is exactly the identity
///     that survives that, which is why the scene format has one.
/// </remarks>
public readonly record struct PrefabLink(AssetId Prefab, EntityId Source);

/// <summary>Which of a scene's entities came from a prefab, and from where in it.</summary>
/// <remarks>
///     <para>
///         <b>Beside the document rather than in the world.</b> A link is editor bookkeeping: the
///         runtime has no notion of a prefab — a compiled scene is entities and components, with the
///         prefab flattened into it — so a component holding this would be thirty bytes an entity in
///         every shipping build to serve a panel that does not exist at run time. That is the same
///         argument <c>SceneDocument</c> makes about names.
///     </para>
///     <para>
///         ⚠ <b>Not yet written to the <c>.vxscene</c>.</b> A link recorded here survives an editing
///         session and does not survive closing the project, so an instance placed today is an
///         ordinary subtree tomorrow. Persisting it needs a field on <c>SceneEntityData</c> and a
///         decision about what a scene does when the prefab it names has changed underneath it —
///         doc 08's R7, and the next thing the scene format has to grow. What is here is the half
///         that does not need the format to move.
///     </para>
/// </remarks>
public sealed class PrefabInstances {
    readonly Dictionary<Entity, PrefabLink> links = [];

    /// <summary>How many entities are linked.</summary>
    public int Count => links.Count;

    /// <summary>Every link, by the entity that carries it.</summary>
    public IReadOnlyDictionary<Entity, PrefabLink> Links => links;

    /// <summary>Records where an entity came from.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="link">Where it came from.</param>
    public void Record(Entity entity, PrefabLink link) => links[entity] = link;

    /// <summary>Where an entity came from, if it came from anywhere.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="link">Where it came from.</param>
    /// <returns>Whether it is an instance.</returns>
    public bool TryGet(Entity entity, out PrefabLink link) => links.TryGetValue(entity, out link);

    /// <summary>Forgets an entity's link, which is what "unpack prefab" means.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it had one.</returns>
    /// <remarks>
    ///     Unpacking one entity of an instance and not the rest is allowed on purpose: an author who
    ///     has already diverged from the template for one child should not have to break the whole
    ///     instance to say so.
    /// </remarks>
    public bool Forget(Entity entity) => links.Remove(entity);

    /// <summary>Forgets links whose entity is no longer alive.</summary>
    /// <param name="world">The world they lived in.</param>
    /// <returns>How many were forgotten.</returns>
    /// <remarks>
    ///     ⚠ <b>Not automatic</b>, for the reason <c>SceneDocument.PruneNames</c> gives: asking "is
    ///     this handle still alive" per link per frame is a scan nobody asked for. Called after a
    ///     delete, or after a play-mode restore.
    /// </remarks>
    public int Prune(World world) {
        ArgumentNullException.ThrowIfNull(world);

        List<Entity> dead = [];

        foreach (var entity in links.Keys) {
            if (!world.IsAlive(entity)) {
                dead.Add(entity);
            }
        }

        foreach (var entity in dead) {
            links.Remove(entity);
        }

        return dead.Count;
    }
}

/// <summary>Placing a prefab in a scene, and finding the template an instance came from.</summary>
public static class Prefab {
    /// <summary>What a prefab is written as.</summary>
    public const string Extension = SceneFile.PrefabExtension;

    /// <summary>Puts a prefab's entities into a scene and records where each one came from.</summary>
    /// <param name="scene">The scene to place it in.</param>
    /// <param name="instances">Where the links are recorded.</param>
    /// <param name="asset">Which prefab asset this is.</param>
    /// <param name="file">The prefab, as its file holds it.</param>
    /// <param name="parent">What to hang it from, or <see cref="Entity.Null" /> for a root.</param>
    /// <returns>The instance's root entity.</returns>
    /// <exception cref="ArgumentException">The file does not have exactly one root.</exception>
    /// <remarks>
    ///     ⚠ <b>Exactly one root, refused rather than tolerated.</b> <c>SceneCompiler</c> refuses a
    ///     prefab with two roots, so instantiating one here would place a subtree that the build
    ///     cannot compile — an editor that let it in would defer the error to a build somebody else
    ///     runs. A file with none is the same refusal from the other side.
    /// </remarks>
    public static Entity Instantiate(
        SceneDocument scene,
        PrefabInstances instances,
        AssetId asset,
        SceneFile file,
        Entity parent = default
    ) {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(file);

        if (file.Roots.Count != 1) {
            throw new ArgumentException(
                $"A prefab has one root and this one has {file.Roots.Count}. Either it should be saved as a "
                + "scene, or its entities want a root of their own to hang from — which is the same refusal "
                + "SceneCompiler makes when it compiles one.",
                nameof(file)
            );
        }

        Dictionary<Entity, EntityId> sources = [];
        var root = SceneSerializer.Instantiate(scene, file.Roots[0], parent, sources);

        foreach (var (entity, source) in sources) {
            instances.Record(entity, new(asset, source));
        }

        return root;
    }

    /// <summary>Finds one entity inside a prefab file, by the id the file gave it.</summary>
    /// <param name="file">The prefab.</param>
    /// <param name="id">The id.</param>
    /// <param name="data">The entity.</param>
    /// <returns>Whether the file has it.</returns>
    public static bool TryFind(SceneFile file, EntityId id, [MaybeNullWhen(false)] out SceneEntityData data) {
        ArgumentNullException.ThrowIfNull(file);

        foreach (var root in file.Roots) {
            if (Search(root, id) is { } found) {
                data = found;
                return true;
            }
        }

        data = null;
        return false;
    }

    static SceneEntityData? Search(SceneEntityData data, EntityId id) {
        if (data.Id == id) {
            return data;
        }

        foreach (var child in data.Children) {
            if (Search(child, id) is { } found) {
                return found;
            }
        }

        return null;
    }
}

/// <summary>What an inspected object was made from, so the inspector can mark and revert overrides.</summary>
/// <remarks>
///     <para>
///         <b>Objects, not entities, and that is what keeps this out of the application.</b> An
///         inspector edits <i>objects</i> — whatever the shell decided an entity's row of editors is
///         — and what it needs answered is "does this object's member differ from the one it was made
///         from". So a caller pairs each inspected object with the object standing for the template,
///         and everything else falls out of comparing one member on two objects.
///     </para>
///     <para>
///         <see cref="Prefab.TryFind" /> is how the pairing is normally found: the instance's link
///         names an entity in a prefab file, and the caller builds the same kind of wrapper over that
///         entity's data as it builds over an entity in the world.
///     </para>
///     <para>
///         ⚠ <b>An object with no pairing is not overridden and has no prefab value.</b> That is the
///         ordinary answer for a scene object that never came from a prefab, and it is why
///         <see cref="InspectorField.CanRevertToPrefab" /> is false there rather than the revert
///         button quietly writing a default.
///     </para>
/// </remarks>
public sealed class PrefabSource : IPrefabSource {
    readonly Dictionary<object, object> templates = new(ReferenceEqualityComparer.Instance);

    /// <summary>How many objects are paired.</summary>
    public int Count => templates.Count;

    /// <summary>Says that an object was made from another one.</summary>
    /// <param name="instance">The object an inspector will show.</param>
    /// <param name="template">The object standing for what it was made from.</param>
    public void Link(object instance, object template) {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(template);

        templates[instance] = template;
    }

    /// <summary>Forgets a pairing.</summary>
    /// <param name="instance">The object.</param>
    /// <returns>Whether it had one.</returns>
    public bool Unlink(object instance) {
        ArgumentNullException.ThrowIfNull(instance);
        return templates.Remove(instance);
    }

    /// <summary>Forgets every pairing.</summary>
    public void Clear() => templates.Clear();

    /// <inheritdoc />
    public bool IsOverridden(object target, InspectorMember member) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(member);

        if (!templates.TryGetValue(target, out var template)) {
            return false;
        }

        // Boxed equality rather than a typed comparison, because this has to work for every member
        // type at once and `Equals` on a boxed struct is the value comparison. The cost is one box
        // per member per repaint of a row, which is a panel's budget rather than a frame's.
        return !Equals(member.GetBoxed(target), member.GetBoxed(template));
    }

    /// <inheritdoc />
    public bool TryGetPrefabValue(object target, InspectorMember member, out object? value) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(member);

        if (!templates.TryGetValue(target, out var template)) {
            value = null;
            return false;
        }

        value = member.GetBoxed(template);
        return true;
    }
}
