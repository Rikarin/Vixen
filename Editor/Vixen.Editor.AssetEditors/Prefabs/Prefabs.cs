// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;

namespace Vixen.Editor.AssetEditors.Prefabs;

/// <summary>Placing a prefab in a scene, and finding the template an instance came from.</summary>
public static class Prefab {
    /// <summary>What a prefab is written as.</summary>
    public const string Extension = SceneFile.PrefabExtension;

    /// <summary>Whether an asset is a prefab, by what it is called.</summary>
    /// <param name="path">The asset's path.</param>
    /// <returns>Whether a drop of it means "place an instance".</returns>
    /// <remarks>
    ///     The extension and not the importer tag, because a drag begins in the project browser and a
    ///     browser knows a file's name before anything has imported it — which is the state a
    ///     freshly-saved prefab is in for as long as the import takes.
    /// </remarks>
    public static bool Claims(string path) =>
        !string.IsNullOrEmpty(path)
        && System.IO.Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Places an instance of a prefab in a scene, undoably.</summary>
    /// <param name="scene">The scene to place it in.</param>
    /// <param name="assets">The project's index, which is what turns the GUID into a path.</param>
    /// <param name="asset">Which prefab.</param>
    /// <param name="parent">What to hang it from, or <see cref="Entity.Null" /> for a root.</param>
    /// <param name="root">The instance's root entity.</param>
    /// <param name="why">Why not, when the prefab could not be opened.</param>
    /// <returns>Whether an instance was placed.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The verb doc 47 § 7 records as the blocker, and it is the whole of it.</b> Until
    ///         something in the shell placed a prefab, the link keys on <c>SceneEntityData</c> had no
    ///         value to hold and <see cref="SceneSerializer" /> had nothing real to round-trip — a
    ///         finished consumer that nothing called.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Through <see cref="SceneDocument.Place" />, so one Ctrl+Z takes the instance
    ///         back.</b> A prefab is a subtree of however many entities the file holds, and one placed
    ///         with <c>Add</c> would be a dozen entities the undo stack has never heard of.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A prefab that cannot be opened is reported and places nothing.</b> It is the same
    ///         refusal set a reconcile uses — <see cref="PrefabReconcile.TryOpen" /> — because "the
    ///         asset has not been imported yet" must mean one thing in the editor rather than two.
    ///     </para>
    /// </remarks>
    public static bool TryPlace(
        SceneDocument scene,
        AssetDatabase assets,
        AssetId asset,
        Entity parent,
        out Entity root,
        out PrefabUnresolved why
    ) {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(assets);

        root = Entity.Null;

        // The reference text rather than the bare id, because that is the spelling the scene file
        // will carry and the spelling every report about it should name.
        var reference = new AssetReference(asset).ToString();

        if (!PrefabReconcile.TryOpen(reference, assets, out var file, out why)) {
            return false;
        }

        // ⚠ Asked here rather than left to `Instantiate` to throw. That refusal is right and it is an
        // `ArgumentException`, which is the wrong shape for a drag somebody made with a mouse: an
        // exception out of a drop is a crash dialog, and what the author needs is the same sentence
        // every other unopenable prefab gets.
        if (file.Roots.Count != 1) {
            why = new(
                reference,
                PrefabUnresolvedKind.Unreadable,
                $"A prefab has one root and this one has {file.Roots.Count}."
            );

            return false;
        }

        var label = assets.TryGetByGuid(asset, out var entry)
            ? System.IO.Path.GetFileNameWithoutExtension(entry.Name)
            : string.Empty;

        root = scene.Place(
            string.IsNullOrEmpty(label) ? "Place Prefab" : $"Place {label}",
            () => Instantiate(scene, asset, file, parent)
        );

        return !root.IsNull;
    }

    /// <summary>Puts a prefab's entities into a scene and records where each one came from.</summary>
    /// <param name="scene">The scene to place it in.</param>
    /// <param name="asset">Which prefab asset this is.</param>
    /// <param name="file">The prefab, as its file holds it.</param>
    /// <param name="parent">What to hang it from, or <see cref="Entity.Null" /> for a root.</param>
    /// <returns>The instance's root entity.</returns>
    /// <exception cref="ArgumentException">The file does not have exactly one root.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exactly one root, refused rather than tolerated.</b> <c>SceneCompiler</c> refuses a
    ///         prefab with two roots, so instantiating one here would place a subtree that the build
    ///         cannot compile — an editor that let it in would defer the error to a build somebody else
    ///         runs. A file with none is the same refusal from the other side.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A link the prefab file already carried is not overwritten, and that is what makes
    ///         a nested prefab a nested prefab.</b> A <c>.vxprefab</c> may hold an instance of another
    ///         one — doc 47 § 6 — whose entities arrive here already carrying the <i>inner</i> link,
    ///         put there by <see cref="SceneSerializer" /> as it read them. Recording the outer link
    ///         over the top would flatten one level of nesting on every placement, silently: the
    ///         subtree would still be there and would answer to the wrong template for ever after.
    ///     </para>
    ///     <para>
    ///         The links go on the document — <see cref="SceneDocument.Prefabs" /> — rather than into a
    ///         table the caller supplies, because the writer reads them from there. A second table
    ///         would be a set of links that never reaches the file.
    ///     </para>
    /// </remarks>
    public static Entity Instantiate(
        SceneDocument scene,
        AssetId asset,
        SceneFile file,
        Entity parent = default
    ) {
        ArgumentNullException.ThrowIfNull(scene);
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
            if (!scene.Prefabs.TryGet(entity, out _)) {
                scene.Prefabs.Record(entity, new(asset, source));
            }
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
