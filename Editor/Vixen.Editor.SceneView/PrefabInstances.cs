// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core.Scenes;

namespace Vixen.Editor.SceneView;

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

/// <summary>Which of a scene's entities came from a prefab, from where in it, and which of their
///     members the instance claims as its own.</summary>
/// <remarks>
///     <para>
///         <b>Beside the document rather than in the world.</b> A link is editor bookkeeping: the
///         runtime has no notion of a prefab — a compiled scene is entities and components, with the
///         prefab flattened into it — so a component holding this would be thirty bytes an entity in
///         every shipping build to serve a panel that does not exist at run time. That is the same
///         argument <see cref="SceneDocument" /> makes about names.
///     </para>
///     <para>
///         ⚠ <b>In this assembly rather than beside the prefab document, because the writer is
///         here.</b> <see cref="SceneSerializer" /> is what turns a document into a
///         <c>SceneEntityData</c>, so a table it cannot see is a link that cannot be written down —
///         which is exactly the state doc 47 § 7 recorded as owed. A document owns one of these
///         (<see cref="SceneDocument.Prefabs" />), and it travels with the names through
///         <see cref="SceneDocument.PruneNames" />, <see cref="SceneDocument.Remap" /> and a
///         delete's snapshot for the reason every other table keyed by a handle does.
///     </para>
///     <para>
///         ⚠⚠ <b>The overrides are a list of names and never a comparison.</b> Doc 47 § 4: if being
///         overridden meant "differs from the template", an author who turned a lamp's intensity down
///         to <c>0</c> would have said something nothing can represent, and the next reconcile would
///         restore the template's brightness. Presence in the list <i>is</i> the override.
///     </para>
/// </remarks>
public sealed class PrefabInstances {
    readonly Dictionary<Entity, PrefabLink> links = [];

    /// <summary>Which members each linked entity claims, for the ones that claim any.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside the link rather than inside <see cref="PrefabLink" />.</b> A link is a value
    ///     two entities can compare equal on; an override list is a mutable collection, and a record
    ///     struct carrying one would have an equality nobody could predict. It is also what lets
    ///     <see cref="Record(Entity,PrefabLink,IEnumerable{string})" /> keep a file's order verbatim
    ///     while <see cref="Mark" /> sorts what it adds.
    /// </remarks>
    readonly Dictionary<Entity, List<string>> overrides = [];

    /// <summary>Which of the template's entities the author deleted, by instance root.</summary>
    /// <remarks>
    ///     ⚠⚠ <b>Recorded so that an absent child can never be mistaken for a new one.</b> The file
    ///     carries resolved values, so a deleted child is simply not in it — stable and unambiguous
    ///     only for as long as nothing adds a template's children back. This is the list doc 47 § 6
    ///     requires to exist <i>before</i> the add-back rule does, and its use today is to stop a
    ///     reconcile reporting a deliberate deletion as something the template gained.
    /// </remarks>
    readonly Dictionary<Entity, List<EntityId>> removed = [];

    static readonly string[] None = [];
    static readonly EntityId[] Nothing = [];

    /// <summary>How many entities are linked.</summary>
    public int Count => links.Count;

    /// <summary>Every link, by the entity that carries it.</summary>
    public IReadOnlyDictionary<Entity, PrefabLink> Links => links;

    /// <summary>Records where an entity came from.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="link">Where it came from.</param>
    /// <remarks>Whatever the entity already claimed as its own is kept.</remarks>
    public void Record(Entity entity, PrefabLink link) => links[entity] = link;

    /// <summary>Records where an entity came from and what it claims, in the order given.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="link">Where it came from.</param>
    /// <param name="claimed">The member paths the instance claims as its own.</param>
    /// <remarks>
    ///     ⚠ <b>Verbatim, and deliberately not sorted or filtered.</b> This is the reader's door: a
    ///     file's list comes back out of the next save in the order it went in, so opening and saving
    ///     a scene is a no-op in the diff — and a path naming nothing is <i>kept</i>, because an
    ///     override quietly pruned is the silent loss doc 47 exists to prevent.
    /// </remarks>
    public void Record(Entity entity, PrefabLink link, IEnumerable<string> claimed) {
        ArgumentNullException.ThrowIfNull(claimed);

        links[entity] = link;
        List<string> list = [.. claimed];

        if (list.Count > 0) {
            overrides[entity] = list;
        } else {
            overrides.Remove(entity);
        }
    }

    /// <summary>Where an entity came from, if it came from anywhere.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="link">Where it came from.</param>
    /// <returns>Whether it is an instance.</returns>
    public bool TryGet(Entity entity, out PrefabLink link) => links.TryGetValue(entity, out link);

    /// <summary>Which members an entity claims as its own rather than the template's.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The member paths, in the order they will be written.</returns>
    public IReadOnlyList<string> OverridesOf(Entity entity) =>
        overrides.TryGetValue(entity, out var claimed) ? claimed : None;

    /// <summary>Whether an entity claims a member as its own.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="path">The member path — <c>Member</c> or <c>Alias.Member</c>.</param>
    /// <returns>Whether it is claimed.</returns>
    public bool IsOverridden(Entity entity, string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!overrides.TryGetValue(entity, out var claimed)) {
            return false;
        }

        foreach (var entry in claimed) {
            if (string.Equals(entry, path, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Says that a member of an entity is the instance's own.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="path">The member path.</param>
    /// <returns>Whether it was not already claimed.</returns>
    /// <remarks>
    ///     ⚠ <b>Refused for an entity with no link.</b> An override is a statement about a template,
    ///     so one on an entity that came from nowhere is a list that would be written into a file
    ///     with no <c>prefab</c> key beside it — read back as nothing, which is a marking that
    ///     silently did not happen.
    ///     <para>
    ///         Sorted, for the reason the format sorts an entity's components: a list whose order
    ///         depends on which member somebody happened to edit first is a diff with no edit behind
    ///         it. <see cref="PrefabOverrides.Mark" /> does the same to a file's.
    ///     </para>
    /// </remarks>
    public bool Mark(Entity entity, string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!links.ContainsKey(entity) || IsOverridden(entity, path)) {
            return false;
        }

        if (!overrides.TryGetValue(entity, out var claimed)) {
            overrides[entity] = claimed = [];
        }

        claimed.Add(path);
        claimed.Sort(StringComparer.Ordinal);

        return true;
    }

    /// <summary>Gives a member back to the template, which is what "revert" means.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="path">The member path.</param>
    /// <returns>Whether it was claimed.</returns>
    /// <remarks>
    ///     ⚠ <b>This forgets the claim and does not restore the value.</b> The value comes back on the
    ///     next reconcile, which is the only place that has the template to read it from.
    /// </remarks>
    public bool Clear(Entity entity, string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!overrides.TryGetValue(entity, out var claimed)) {
            return false;
        }

        for (var index = 0; index < claimed.Count; index++) {
            if (string.Equals(claimed[index], path, StringComparison.OrdinalIgnoreCase)) {
                claimed.RemoveAt(index);

                if (claimed.Count == 0) {
                    overrides.Remove(entity);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>Which of the template's entities this instance's author deleted.</summary>
    /// <param name="entity">The instance root.</param>
    /// <returns>The template ids, in the order they will be written.</returns>
    public IReadOnlyList<EntityId> RemovedFrom(Entity entity) =>
        removed.TryGetValue(entity, out var gone) ? gone : Nothing;

    /// <summary>Says that the author deleted one of the template's entities from this instance.</summary>
    /// <param name="root">The instance root the removal is recorded on.</param>
    /// <param name="source">The id the <i>template</i> gave the entity that went.</param>
    /// <returns>Whether it was not already recorded.</returns>
    /// <remarks>
    ///     ⚠ <b>The template's id and not the instance's.</b> The instance's entity is gone and its own
    ///     identity with it; what is being spoken about is the template's entity, which is the only
    ///     half of the pair that still exists.
    /// </remarks>
    public bool Remove(Entity root, EntityId source) {
        if (source.IsNone || !links.ContainsKey(root)) {
            return false;
        }

        if (!removed.TryGetValue(root, out var gone)) {
            removed[root] = gone = [];
        }

        if (gone.Contains(source)) {
            return false;
        }

        gone.Add(source);
        return true;
    }

    /// <summary>Takes back a removal, which is what undoing the delete means.</summary>
    /// <param name="root">The instance root.</param>
    /// <param name="source">The template id.</param>
    /// <returns>Whether it was recorded.</returns>
    public bool Restore(Entity root, EntityId source) {
        if (!removed.TryGetValue(root, out var gone) || !gone.Remove(source)) {
            return false;
        }

        if (gone.Count == 0) {
            removed.Remove(root);
        }

        return true;
    }

    /// <summary>Records a whole removed list as a file gave it.</summary>
    /// <param name="root">The instance root.</param>
    /// <param name="gone">The template ids.</param>
    /// <remarks>
    ///     Verbatim, for <see cref="Record(Entity,PrefabLink,IEnumerable{string})" />'s reason: a file's
    ///     order comes back out of the next save unchanged, and an id naming an entity the template no
    ///     longer has is kept, because "the author deleted this" outlives the template it was said
    ///     about.
    /// </remarks>
    public void RecordRemoved(Entity root, IEnumerable<EntityId> gone) {
        ArgumentNullException.ThrowIfNull(gone);

        List<EntityId> list = [.. gone];

        if (list.Count > 0) {
            removed[root] = list;
        } else {
            removed.Remove(root);
        }
    }

    /// <summary>Forgets an entity's link, which is what "unpack prefab" means.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it had one.</returns>
    /// <remarks>
    ///     <para>
    ///         Unpacking one entity of an instance and not the rest is allowed on purpose: an author
    ///         who has already diverged from the template for one child should not have to break the
    ///         whole instance to say so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The overrides go with it.</b> "This member is mine rather than the template's" is
    ///         a sentence with no subject once there is no template, and a list left behind would be
    ///         written into the file the next time the entity was linked to something else.
    ///     </para>
    /// </remarks>
    public bool Forget(Entity entity) {
        overrides.Remove(entity);

        // ⚠ And the removed list, for the overrides' reason. "The author deleted one of the template's
        // children" is a sentence with no subject once this entity has no template, and one left behind
        // would silence a genuine report the next time the entity was linked to something else.
        removed.Remove(entity);

        return links.Remove(entity);
    }

    /// <summary>Forgets links whose entity is no longer alive.</summary>
    /// <param name="world">The world they lived in.</param>
    /// <returns>How many were forgotten.</returns>
    /// <remarks>
    ///     ⚠ <b>Not automatic</b>, for the reason <see cref="SceneDocument.PruneNames" /> gives: asking
    ///     "is this handle still alive" per link per frame is a scan nobody asked for. The document
    ///     calls it from <c>PruneNames</c>, so a delete or a play-mode restore takes the links with
    ///     the names rather than leaving one table describing the other's ghosts.
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
            overrides.Remove(entity);
            removed.Remove(entity);
        }

        return dead.Count;
    }

    /// <summary>Moves the links across a play-mode restore's translation table.</summary>
    /// <param name="translation">What <c>WorldSnapshot.Restore</c> returned.</param>
    /// <remarks>
    ///     ⚠ <b>The same reason the names and the stable ids travel.</b> Every entity gets a new
    ///     handle on restore, so a link table keyed by the old ones describes nothing — and a scene
    ///     saved after a play-mode stop would have quietly unpacked every prefab in it.
    /// </remarks>
    public void Remap(IReadOnlyDictionary<Entity, Entity> translation) {
        ArgumentNullException.ThrowIfNull(translation);

        Dictionary<Entity, PrefabLink> movedLinks = new(links.Count);
        Dictionary<Entity, List<string>> movedOverrides = new(overrides.Count);
        Dictionary<Entity, List<EntityId>> movedRemoved = new(removed.Count);

        foreach (var (entity, link) in links) {
            // An entity with no translation was created during play mode and no longer exists, so
            // its link goes with it rather than being carried over onto whatever took its slot.
            if (translation.TryGetValue(entity, out var now)) {
                movedLinks[now] = link;

                if (overrides.TryGetValue(entity, out var claimed)) {
                    movedOverrides[now] = claimed;
                }

                if (removed.TryGetValue(entity, out var gone)) {
                    movedRemoved[now] = gone;
                }
            }
        }

        links.Clear();
        overrides.Clear();
        removed.Clear();

        foreach (var (entity, link) in movedLinks) {
            links[entity] = link;
        }

        foreach (var (entity, claimed) in movedOverrides) {
            overrides[entity] = claimed;
        }

        foreach (var (entity, gone) in movedRemoved) {
            removed[entity] = gone;
        }
    }
}
