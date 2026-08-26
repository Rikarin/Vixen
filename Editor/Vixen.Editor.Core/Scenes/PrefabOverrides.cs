// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Reflection;

namespace Vixen.Editor.Core.Scenes;

/// <summary>What a reconcile could not resolve, and had to leave alone.</summary>
/// <remarks>
///     ⚠ <b>Every one of these is a report and none of them is a deletion.</b> A prefab that has moved
///     under a level is an authoring problem a person settles, and the one thing an editor must not do
///     is settle it by throwing away the half it cannot explain — a round trip that loses an override
///     is silent, and a subtree deleted on open is unrecoverable. See
///     <see href="../../docs/plan/47-prefab-overrides-and-nested-prefabs.md">doc 47</see> § 5.
/// </remarks>
public enum PrefabReportKind {
    /// <summary>The instance names an entity the template no longer has.</summary>
    /// <remarks>The template deleted it. The entity is kept; the editor offers unpack or delete.</remarks>
    OrphanedEntity,

    /// <summary>An override names a member nothing on the entity has.</summary>
    /// <remarks>
    ///     A component removed and re-added, or a rename in flight. The entry stays in the file: an
    ///     override quietly pruned is the failure this whole design exists to prevent.
    /// </remarks>
    OrphanedOverride,

    /// <summary>The template has a component this instance does not.</summary>
    /// <remarks>
    ///     Reported rather than added. Adding one means constructing a value the instance never had, and
    ///     doc 47's slice keeps <see cref="PrefabOverrides.Apply" /> to writing members it can see on
    ///     both sides.
    /// </remarks>
    MissingComponent,

    /// <summary>The template has an entity this instance does not name.</summary>
    /// <remarks>
    ///     ⚠ Reported rather than added, and doc 47 § 6 is why: with the file carrying resolved values,
    ///     "the author deleted this" and "the template added this since" look identical. Adding the
    ///     add-back rule requires an explicit removed-child list in the same change.
    /// </remarks>
    AddedByTemplate
}

/// <summary>One thing a reconcile could not do.</summary>
/// <param name="Kind">What kind of problem it is.</param>
/// <param name="Entity">Which entity in the instance, by the id the scene file gave it.</param>
/// <param name="Detail">The template id, the member path or the alias the report is about.</param>
public readonly record struct PrefabReport(PrefabReportKind Kind, EntityId Entity, string Detail) {
    /// <summary>Renders it as its kind and what it names.</summary>
    /// <returns>The report in text.</returns>
    public override string ToString() => $"{Kind}: {Detail}";
}

/// <summary>
///     Which of a prefab instance's members are its own, and what a scene does when the prefab has
///     changed underneath it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Pure functions over the authoring format, and deliberately nothing else.</b> No
///         <c>World</c>, no <c>SceneDocument</c>, no project on disk — an override is a statement about
///         a file, so deciding what it means needs only the file and the file it points at. That is also
///         what makes every case in
///         <see href="../../docs/plan/47-prefab-overrides-and-nested-prefabs.md">doc 47</see> § 5
///         testable without an editor.
///     </para>
///     <para>
///         ⚠ <b>Reconciliation is an editor-side pass and runs at open time only.</b> Neither the content
///         build nor the runtime can do it: an importer is given an <c>AssetId</c> and no way to resolve
///         one to a path, so <c>SceneCompiler</c> could not open the prefab an instance names even if it
///         wanted to. That constraint is the reason the file carries resolved values at all rather than
///         the sparse patch everybody reaches for first — doc 47 § 2 and § 3.
///     </para>
///     <para>
///         <b>A member path is <c>Member</c> or <c>Alias.Member</c>.</b> A bare name is one of the
///         entity's own four; a dotted one names a <c>[DataContract]</c> alias in
///         <see cref="SceneEntityData.Components" /> and a member on it. Matching is
///         case-insensitive so that a hand-edited file is forgiving; <see cref="Mark" /> writes the
///         canonical spelling.
///     </para>
/// </remarks>
public static class PrefabOverrides {
    /// <summary>The entity's own members that an override may name.</summary>
    /// <remarks>
    ///     ⚠ <b>A fixed list rather than every member the descriptor has.</b> <c>Children</c> and
    ///     <c>Components</c> are structure rather than values, and an override that claimed one would be
    ///     asking <see cref="Apply" /> to graft a subtree — which is the add-back rule doc 47 § 6 says
    ///     must not land without a removed-child list beside it.
    /// </remarks>
    public static readonly string[] OwnMembers = [
        nameof(SceneEntityData.Name),
        nameof(SceneEntityData.Position),
        nameof(SceneEntityData.Rotation),
        nameof(SceneEntityData.Scale)
    ];

    /// <summary>Whether an entity is a prefab instance at all.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it carries both halves of a link.</returns>
    /// <remarks>
    ///     Both halves, because either alone is a half-written file rather than an instance: an asset
    ///     with no source names a prefab without saying which of its entities this is, and a source with
    ///     no asset names an entity in a file nobody named.
    /// </remarks>
    public static bool IsInstance(SceneEntityData entity) {
        ArgumentNullException.ThrowIfNull(entity);

        return !string.IsNullOrEmpty(entity.Prefab) && !entity.Source.IsNone;
    }

    /// <summary>Whether a member is the instance's own rather than the template's.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="path">The member path.</param>
    /// <returns>Whether it is overridden.</returns>
    /// <remarks>
    ///     ⚠ <b>Presence in the list, and nothing about the value.</b> Comparing against the template —
    ///     which is what the inspector's <c>PrefabSource</c> does, correctly, for a live pairing — cannot
    ///     answer this for a file: an override to a value that happens to equal the template's is still
    ///     an override, and an override to zero is the case that makes it matter.
    /// </remarks>
    public static bool IsOverridden(SceneEntityData entity, string path) {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrEmpty(path);

        foreach (var entry in entity.Overrides) {
            if (string.Equals(entry, path, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Says that a member is the instance's own.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="path">The member path.</param>
    /// <returns>Whether it was not already marked.</returns>
    /// <remarks>
    ///     Idempotent, because the caller is an inspector reacting to an edit and an entity whose
    ///     position is nudged twice has one override rather than two.
    /// </remarks>
    public static bool Mark(SceneEntityData entity, string path) {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (IsOverridden(entity, path)) {
            return false;
        }

        entity.Overrides.Add(path);

        // Sorted, for the reason the format sorts an entity's components: a list whose order depends on
        // which member somebody happened to edit first is a diff with no edit behind it.
        entity.Overrides.Sort(StringComparer.Ordinal);

        return true;
    }

    /// <summary>Gives a member back to the template, which is what "revert" means.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="path">The member path.</param>
    /// <returns>Whether it was marked.</returns>
    /// <remarks>
    ///     ⚠ <b>This forgets the override and does not restore the value.</b> The value comes back on the
    ///     next <see cref="Apply" />, which is the only place that has the template to read it from — so
    ///     a revert is "stop claiming this" and reconciliation is what makes it true.
    /// </remarks>
    public static bool Clear(SceneEntityData entity, string path) {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrEmpty(path);

        for (var index = 0; index < entity.Overrides.Count; index++) {
            if (string.Equals(entity.Overrides[index], path, StringComparison.OrdinalIgnoreCase)) {
                entity.Overrides.RemoveAt(index);
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a member of an entity by its path.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="path">The member path.</param>
    /// <param name="value">Its value.</param>
    /// <returns>Whether the path names anything.</returns>
    public static bool TryRead(SceneEntityData entity, string path, out object? value) {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!TryResolve(entity, path, out var target, out var member)) {
            value = null;
            return false;
        }

        value = member.GetValue(target);
        return true;
    }

    /// <summary>Writes a member of an entity by its path.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="path">The member path.</param>
    /// <param name="value">What to write.</param>
    /// <returns>Whether the path names anything writable.</returns>
    public static bool TryWrite(SceneEntityData entity, string path, object? value) {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!TryResolve(entity, path, out var target, out var member) || !member.CanWrite) {
            return false;
        }

        member.SetValue(target, value);
        return true;
    }

    /// <summary>Brings one instance entity back in step with the template entity it came from.</summary>
    /// <param name="instance">The entity in the scene.</param>
    /// <param name="template">The entity in the prefab.</param>
    /// <param name="reports">Filled with what could not be resolved, or <see langword="null" /> not to ask.</param>
    /// <returns>How many members took the template's value.</returns>
    /// <remarks>
    ///     <para>
    ///         Every member the two have in common is written from the template unless the instance
    ///         claims it. That is propagation, and it is the reason a prefab is a link rather than a
    ///         stamp.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It writes values and removes nothing.</b> Not an entity, not a key, not an override
    ///         entry — a component the instance has and the template does not is an addition and is left
    ///         alone; one the template has and the instance does not is reported. Doc 47 § 5's invariant.
    ///     </para>
    /// </remarks>
    public static int Apply(
        SceneEntityData instance,
        SceneEntityData template,
        ICollection<PrefabReport>? reports = null
    ) {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(template);

        var written = 0;

        foreach (var name in OwnMembers) {
            if (!IsOverridden(instance, name) && TryRead(template, name, out var value)) {
                written += TryWrite(instance, name, value) ? 1 : 0;
            }
        }

        foreach (var component in template.Components) {
            if (!TryDescribe(component, out var descriptor)) {
                continue;
            }

            if (!TryFindComponent(instance, descriptor.Alias, out var mine)) {
                reports?.Add(new(PrefabReportKind.MissingComponent, instance.Id, descriptor.Alias));
                continue;
            }

            foreach (var member in descriptor.Members) {
                if (!member.IsSerialized || !member.CanWrite) {
                    continue;
                }

                var path = $"{descriptor.Alias}.{member.Name}";

                if (!IsOverridden(instance, path)) {
                    member.SetValue(mine, member.GetValue(component));
                    written++;
                }
            }
        }

        // ⚠ After the writing, and reported rather than removed. An override naming a member nothing has
        // is a component that was taken off and will very likely come back — the entry is the author's
        // statement and outlives the shape it was made against.
        foreach (var path in instance.Overrides) {
            if (!TryResolve(instance, path, out _, out _)) {
                reports?.Add(new(PrefabReportKind.OrphanedOverride, instance.Id, path));
            }
        }

        return written;
    }

    /// <summary>Brings every instance of one prefab in a scene back in step with it.</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="prefab">Which prefab, as the reference text a scene writes.</param>
    /// <param name="template">The prefab file.</param>
    /// <param name="reports">Filled with what could not be resolved, or <see langword="null" /> not to ask.</param>
    /// <returns>How many members took the template's value, across every instance.</returns>
    /// <remarks>
    ///     <para>
    ///         Every entity naming <paramref name="prefab" /> is reconciled against the template entity
    ///         its <see cref="SceneEntityData.Source" /> names, wherever it sits in the scene — which is
    ///         what makes an instance that has been reparented, or one entity of which has been unpacked,
    ///         an ordinary case rather than one this has to detect.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A prefab that is missing entirely is not this function's case at all.</b> The caller
    ///         that could not load the template simply does not call it, and the scene keeps its
    ///         instances, its overrides and its values — an unbuilt or renamed asset must not cost a
    ///         level its content.
    ///     </para>
    /// </remarks>
    public static int Reconcile(
        SceneFile scene,
        string prefab,
        SceneFile template,
        ICollection<PrefabReport>? reports = null
    ) {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrEmpty(prefab);
        ArgumentNullException.ThrowIfNull(template);

        var written = 0;
        HashSet<EntityId> reached = [];

        foreach (var entity in scene.All()) {
            if (!IsInstance(entity) || !string.Equals(entity.Prefab, prefab, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (!TryFind(template, entity.Source, out var source)) {
                reports?.Add(new(PrefabReportKind.OrphanedEntity, entity.Id, entity.Source.ToString()));
                continue;
            }

            reached.Add(entity.Source);
            written += Apply(entity, source, reports);
        }

        // ⚠ Only worth asking once something in the scene is an instance of this prefab. A scene with no
        // instances of it would otherwise report every entity in the template as newly added, which is
        // true and useless.
        if (reached.Count > 0) {
            foreach (var entity in template.All()) {
                if (!entity.Id.IsNone && !reached.Contains(entity.Id)) {
                    reports?.Add(new(PrefabReportKind.AddedByTemplate, EntityId.None, entity.Id.ToString()));
                }
            }
        }

        return written;
    }

    /// <summary>Finds one entity in a file by the id the file gave it.</summary>
    /// <param name="file">The file.</param>
    /// <param name="id">The id.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether the file has it.</returns>
    public static bool TryFind(SceneFile file, EntityId id, [MaybeNullWhen(false)] out SceneEntityData entity) {
        ArgumentNullException.ThrowIfNull(file);

        if (!id.IsNone) {
            foreach (var candidate in file.All()) {
                if (candidate.Id == id) {
                    entity = candidate;
                    return true;
                }
            }
        }

        entity = null;
        return false;
    }

    static bool TryResolve(
        SceneEntityData entity,
        string path,
        [MaybeNullWhen(false)] out object target,
        [MaybeNullWhen(false)] out MemberDescriptor member
    ) {
        var dot = path.IndexOf('.', StringComparison.Ordinal);

        if (dot < 0) {
            // ⚠ Only the four, and `Own` is checked before the descriptor is asked. `SceneEntityData`'s
            // descriptor also has `Children` and `Components`, and a path that reached those would let a
            // file ask `Apply` to graft a subtree — see `OwnMembers`.
            foreach (var name in OwnMembers) {
                if (string.Equals(name, path, StringComparison.OrdinalIgnoreCase)
                    && TryDescribe(entity, out var self)
                    && FindMember(self, name) is { } own) {
                    target = entity;
                    member = own;

                    return true;
                }
            }

            target = null;
            member = null;

            return false;
        }

        if (TryFindComponent(entity, path.AsSpan(..dot), out var component)
            && TryDescribe(component, out var descriptor)
            && FindMember(descriptor, path.AsSpan((dot + 1)..)) is { } found) {
            target = component;
            member = found;

            return true;
        }

        target = null;
        member = null;

        return false;
    }

    static bool TryFindComponent(SceneEntityData entity, ReadOnlySpan<char> alias, [MaybeNullWhen(false)] out object component) {
        foreach (var candidate in entity.Components) {
            if (TryDescribe(candidate, out var descriptor)
                && alias.Equals(descriptor.Alias, StringComparison.OrdinalIgnoreCase)) {
                component = candidate;
                return true;
            }
        }

        component = null;
        return false;
    }

    static bool TryDescribe(object value, [MaybeNullWhen(false)] out TypeDescriptor descriptor) =>
        TypeRegistry.TryGet(value.GetType(), out descriptor);

    /// <summary>
    ///     A member by name, case-insensitively — <see cref="TypeDescriptor.FindMember" /> is ordinal.
    /// </summary>
    /// <remarks>
    ///     Forgiving on read so that a hand-edited or hand-merged path still resolves; <see cref="Mark" />
    ///     is what writes the canonical spelling back.
    /// </remarks>
    static MemberDescriptor? FindMember(TypeDescriptor descriptor, ReadOnlySpan<char> name) {
        foreach (var member in descriptor.Members) {
            if (name.Equals(member.Name, StringComparison.OrdinalIgnoreCase)) {
                return member;
            }
        }

        return null;
    }
}
