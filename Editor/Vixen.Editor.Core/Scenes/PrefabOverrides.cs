// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Reflection;
using Vixen.Core.Yaml;

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

    /// <summary>The template gained an entity, and the instance has taken it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one report that is a change rather than a refusal, and doc 47 § 6 is why it took
    ///         two slices.</b> With the file carrying resolved values, "the author deleted this" and
    ///         "the template gained this since" look identical — so the add-back rule could not land
    ///         until <see cref="SceneEntityData.Removed" /> existed to tell them apart. It does, so this
    ///         is now propagation over structure: a child added to a prefab reaches every instance of
    ///         it, and a child a designer deleted stays deleted.
    ///     </para>
    ///     <para>
    ///         Still reported, because a level that gained entities is something a person should be told
    ///         about even when it is exactly right — it is the one thing a reconcile does that a diff of
    ///         the next save will show as new lines nobody typed.
    ///     </para>
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
    /// <param name="scene">The scene, rewritten in place.</param>
    /// <param name="prefab">Which prefab, as the reference text a scene writes.</param>
    /// <param name="template">The prefab file.</param>
    /// <param name="reports">Filled with what could not be resolved, or <see langword="null" /> not to ask.</param>
    /// <returns>How many members took the template's value, across every instance.</returns>
    /// <remarks>
    ///     One prefab's worth of
    ///     <see cref="Reconcile(SceneFile,IReadOnlyDictionary{string,SceneFile},ICollection{PrefabReport})" />,
    ///     which is where every rule lives. ⚠ A single template can express no nesting — an inner link
    ///     names a prefab this overload was not given — so a nested instance under it is left exactly as
    ///     the file had it rather than reconciled against the wrong half of the nesting.
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

        return Reconcile(
            scene,
            new Dictionary<string, SceneFile>(StringComparer.OrdinalIgnoreCase) { [prefab] = template },
            reports
        );
    }

    /// <summary>Brings every prefab instance in a scene back in step with the templates it came from.</summary>
    /// <param name="scene">The scene, rewritten in place.</param>
    /// <param name="templates">The prefab files, by the reference text a scene writes for each.</param>
    /// <param name="reports">Filled with what could not be resolved, or <see langword="null" /> not to ask.</param>
    /// <returns>How many members took the template's value, across every instance.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>An instance is a run, not an entity.</b> The unit of work is the topmost entity of a
    ///         contiguous run sharing one <see cref="SceneEntityData.Prefab" /> — the same definition
    ///         <c>SceneDocument.TryGetInstanceRoot</c> uses when a delete writes to
    ///         <see cref="SceneEntityData.Removed" />. ⚠ It has to be the same one: the reader of that
    ///         list and its writer disagreeing about which entity is the root is a level that regrows
    ///         what its designer deleted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every prefab at once rather than one at a time, because nesting cannot be seen from
    ///         one template.</b> A <c>.vxprefab</c> may hold an instance of another, and the writer
    ///         deliberately keeps the <i>inner</i> link on those nodes — so a scene node inside an
    ///         instance of A that carries B's link must take A's copy of that B entity, overrides and
    ///         all, and never B's own. One prefab at a time can only ever reach for B, which is every
    ///         override the author of A made, discarded on open. Doc 47 § 6.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two things a scene run can be paired with, and the outer one wins.</b> A run whose
    ///         root sits inside an instance of a different prefab is paired with the node <i>that</i>
    ///         template carries for the same link; only a run with no such outer instance is paired
    ///         with its own prefab's file by <see cref="SceneEntityData.Source" />. That is R7's single
    ///         level: one lookup outward, never a fixpoint.
    ///     </para>
    ///     <para>
    ///         <b>The dictionary should compare its keys case-insensitively</b>, which is how every
    ///         other comparison of a reference here is made — a reference this editor writes is
    ///         lower-case hex and a hand-edited one may not be.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A prefab that is missing entirely is simply not in the dictionary.</b> Its instances
    ///         keep their entities, their overrides and their values — an unbuilt or renamed asset must
    ///         not cost a level its content, and the caller reports it instead.
    ///     </para>
    /// </remarks>
    public static int Reconcile(
        SceneFile scene,
        IReadOnlyDictionary<string, SceneFile> templates,
        ICollection<PrefabReport>? reports = null
    ) {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(templates);

        if (templates.Count == 0) {
            return 0;
        }

        // Parent links, because a scene file nests its entities and every rule below is about what an
        // entity sits inside. ⚠ Built once and never rebuilt: add-back grafts children as it goes, and
        // a map refreshed mid-walk would be a map of a tree that is changing under it.
        Dictionary<SceneEntityData, SceneEntityData> parents = [];

        foreach (var top in scene.Roots) {
            Link(top, parents);
        }

        // ⚠ Materialised before anything is grafted, for the same reason — and because a grafted run is
        // a copy of a template that has already been reconciled, so it needs no turn of its own.
        List<SceneEntityData> instances = [];

        foreach (var candidate in scene.All()) {
            if (IsInstance(candidate) && RunRootOf(candidate, parents) == candidate) {
                instances.Add(candidate);
            }
        }

        var written = 0;

        foreach (var instance in instances) {
            written += ReconcileRun(instance, parents, templates, reports);
        }

        return written;
    }

    /// <summary>Finds one entity in a file by the prefab link it carries.</summary>
    /// <param name="file">The file.</param>
    /// <param name="prefab">The reference text.</param>
    /// <param name="source">The id inside that prefab.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether the file has one.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is how an outer prefab names an inner instance's entity.</b> A nested node
    ///         keeps the inner link on both sides — in the outer <c>.vxprefab</c> and in the scene the
    ///         outer was placed into — so the pair is the identity the two files share. The outer file's
    ///         own id for that node is never written anywhere the scene can see it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first match wins, so a template holding two instances of one prefab has two
    ///         entities with one identity.</b> That is the writer's decision showing through: declining
    ///         to record an outer link over an inner one is what makes nesting work at all, and its
    ///         price is that sibling copies of one prefab cannot be told apart by link alone. Both take
    ///         the same values, which is what they had the day they were placed.
    ///     </para>
    /// </remarks>
    public static bool TryFindLink(
        SceneFile file,
        string prefab,
        EntityId source,
        [MaybeNullWhen(false)] out SceneEntityData entity
    ) {
        ArgumentNullException.ThrowIfNull(file);

        if (!string.IsNullOrEmpty(prefab) && !source.IsNone) {
            foreach (var candidate in file.All()) {
                if (candidate.Source == source
                    && string.Equals(candidate.Prefab, prefab, StringComparison.OrdinalIgnoreCase)) {
                    entity = candidate;
                    return true;
                }
            }
        }

        entity = null;
        return false;
    }

    static void Link(SceneEntityData entity, Dictionary<SceneEntityData, SceneEntityData> parents) {
        foreach (var child in entity.Children) {
            parents[child] = entity;
            Link(child, parents);
        }
    }

    /// <summary>The topmost entity of the contiguous run sharing this one's prefab.</summary>
    /// <remarks>
    ///     <c>SceneDocument.TryGetInstanceRoot</c>'s rule, restated over the file. ⚠ Two instances of
    ///     one prefab parented to each other read as a single run here, exactly as they do there — a
    ///     shared under-reading, and the shared one is the safe one: it makes add-back do less rather
    ///     than write a removal to the wrong root.
    /// </remarks>
    static SceneEntityData RunRootOf(SceneEntityData entity, Dictionary<SceneEntityData, SceneEntityData> parents) {
        var root = entity;

        while (parents.TryGetValue(root, out var above)
               && IsInstance(above)
               && string.Equals(above.Prefab, entity.Prefab, StringComparison.OrdinalIgnoreCase)) {
            root = above;
        }

        return root;
    }

    /// <summary>The instance this run sits inside, if it sits inside one.</summary>
    /// <remarks>
    ///     Walked through anything in between: an unpacked node or a plain entity between the two is a
    ///     hierarchy somebody made by hand, and stopping at it would silently unnest the run.
    /// </remarks>
    static SceneEntityData? OuterOf(SceneEntityData root, Dictionary<SceneEntityData, SceneEntityData> parents) {
        for (var above = Above(root); above is not null; above = Above(above)) {
            if (IsInstance(above) && !string.Equals(above.Prefab, root.Prefab, StringComparison.OrdinalIgnoreCase)) {
                return above;
            }
        }

        return null;

        SceneEntityData? Above(SceneEntityData entity) =>
            parents.TryGetValue(entity, out var value) ? value : null;
    }

    static int ReconcileRun(
        SceneEntityData root,
        Dictionary<SceneEntityData, SceneEntityData> parents,
        IReadOnlyDictionary<string, SceneFile> templates,
        ICollection<PrefabReport>? reports
    ) {
        if (!TryPair(root, parents, templates, reports, out var reference, out var node)) {
            return 0;
        }

        var mine = Fold(root.Prefab);

        // What the template says this run should hold, keyed by what a scene node stamped from each
        // entity would carry — so a nested node, which keeps its inner link on both sides, matches by
        // the same key as an ordinary one.
        Dictionary<(string Prefab, EntityId Source), SceneEntityData> run = [];

        Collect(node, true);

        // What the scene already holds anywhere under this run's root. ⚠ A wider net than the run: a
        // nested instance's entities are not this run's to write and are still the answer to "does the
        // instance already have it".
        Dictionary<(string Prefab, EntityId Source), SceneEntityData> present = [];

        Register(root);

        var written = Apply(root, node, reports);

        // ⚠ Only this run's own entities. An instance of another prefab dragged in under this one is an
        // addition and is left alone — doc 47 § 5 — and a second run of the *same* prefab below this
        // one has a turn of its own, so writing to it from here would be two passes over one entity.
        foreach (var entity in Subtree(root)) {
            if (entity == root || !IsInstance(entity) || RunRootOf(entity, parents) != root) {
                continue;
            }

            if (run.TryGetValue((Fold(entity.Prefab), entity.Source), out var source)) {
                written += Apply(entity, source, reports);
            } else {
                reports?.Add(new(PrefabReportKind.OrphanedEntity, entity.Id, entity.Source.ToString()));
            }
        }

        Fill(node, root);

        return written;

        void Collect(SceneEntityData source, bool top) {
            var key = KeyOf(source, reference);

            run.TryAdd(key, source);

            // ⚠ A nested instance inside the template is recorded — so add-back can graft the whole of
            // it when the scene has none — and deliberately not descended into. Its interior belongs to
            // that run's turn, and this run writing to it as well is the one way an entity gets two
            // templates and the second one wins by accident.
            if (top || string.Equals(key.Prefab, mine, StringComparison.Ordinal)) {
                foreach (var child in source.Children) {
                    Collect(child, false);
                }
            }
        }

        void Register(SceneEntityData entity) {
            if (IsInstance(entity)) {
                present.TryAdd((Fold(entity.Prefab), entity.Source), entity);
            }

            foreach (var child in entity.Children) {
                Register(child);
            }
        }

        // ⚠⚠ The add-back rule — doc 47 row 4, and the reason row 3 had to land first. A template child
        // the instance does not hold is grafted in, unless the instance root says its author deleted it.
        void Fill(SceneEntityData source, SceneEntityData into) {
            foreach (var child in source.Children) {
                var key = KeyOf(child, reference);

                if (present.TryGetValue(key, out var already)) {
                    // Descend only where this run owns the entity: below a nested instance the
                    // template's children are that instance's turn to fill.
                    if (string.Equals(key.Prefab, mine, StringComparison.Ordinal)) {
                        Fill(child, already);
                    }

                    continue;
                }

                // ⚠⚠ The one line that keeps a designer's deletion deleted. `removed:` names the id the
                // *template* gave the entity, which is the `Source` half of the key for an ordinary
                // child and — because a nested node keeps its inner link — for a nested one too.
                // Comparing the id alone rather than the whole key is deliberate: a delete records what
                // went, and both halves of a nesting name what went the same way.
                if (root.Removed.Contains(key.Source)) {
                    continue;
                }

                var copy = Graft(child, reference);

                into.Children.Add(copy);
                Register(copy);
                reports?.Add(new(PrefabReportKind.AddedByTemplate, root.Id, key.Source.ToString()));
            }
        }
    }

    /// <summary>Which template entity a scene run is to be reconciled against.</summary>
    /// <remarks>
    ///     ⚠ <b>The outer template first, and that is nesting's whole implementation.</b> A run sitting
    ///     inside an instance of another prefab <i>is</i> that prefab's copy of it, overrides included,
    ///     and reaching past it to the inner prefab's own file would discard every one of them.
    /// </remarks>
    static bool TryPair(
        SceneEntityData root,
        Dictionary<SceneEntityData, SceneEntityData> parents,
        IReadOnlyDictionary<string, SceneFile> templates,
        ICollection<PrefabReport>? reports,
        out string reference,
        [MaybeNullWhen(false)] out SceneEntityData node
    ) {
        if (OuterOf(root, parents) is { } outer) {
            if (!templates.TryGetValue(outer.Prefab, out var above)) {
                // ⚠ The run sits inside an instance whose template is not here, and without it there is
                // no way to tell a nested node of that prefab from a separate instance somebody dragged
                // in under it. Reconciling against the inner prefab on that guess is the destructive
                // half — it discards every override the outer's author made — so the run is left
                // exactly as the file has it. The caller has already reported the prefab it could not
                // open, which is the sentence a person can act on.
                reference = string.Empty;
                node = null;

                return false;
            }

            if (TryFindLink(above, root.Prefab, root.Source, out var nested)) {
                reference = outer.Prefab;
                node = nested;

                return true;
            }
        }

        if (templates.TryGetValue(root.Prefab, out var own)) {
            if (TryFind(own, root.Source, out node)) {
                reference = root.Prefab;
                return true;
            }

            // The template deleted it. Every entity of the run is reported, because every one of them
            // names something that is gone — and not one of them is touched.
            foreach (var entity in Subtree(root)) {
                if (IsInstance(entity) && RunRootOf(entity, parents) == root) {
                    reports?.Add(new(PrefabReportKind.OrphanedEntity, entity.Id, entity.Source.ToString()));
                }
            }
        }

        reference = string.Empty;
        node = null;

        return false;
    }

    /// <summary>An entity and everything under it, the entity first.</summary>
    static IEnumerable<SceneEntityData> Subtree(SceneEntityData entity) {
        yield return entity;

        foreach (var child in entity.Children) {
            foreach (var descendant in Subtree(child)) {
                yield return descendant;
            }
        }
    }

    /// <summary>What a scene node stamped from this template entity would carry as its link.</summary>
    /// <remarks>
    ///     ⚠ <b>A template entity's identity is its inner link when it has one and its own id when it
    ///     does not</b>, because that is exactly what <c>SceneSerializer.Create</c> and
    ///     <c>Prefab.Instantiate</c> between them write onto the scene node: an inner link is kept, an
    ///     unlinked entity is stamped with the prefab being placed. One key over both cases is what
    ///     lets pairing and add-back be one walk rather than two that have to agree.
    /// </remarks>
    static (string Prefab, EntityId Source) KeyOf(SceneEntityData entity, string reference) =>
        IsInstance(entity) ? (Fold(entity.Prefab), entity.Source) : (Fold(reference), entity.Id);

    /// <summary>A reference folded for comparison, matching every other comparison of one here.</summary>
    static string Fold(string reference) => reference.ToLowerInvariant();

    /// <summary>Copies a template entity and its subtree into the shape a scene node has.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Through the format rather than member by member, and the reason is aliasing.</b> A
    ///         <see cref="SceneEntityData" />'s components are objects whose own members may be
    ///         reference types, so a member-wise copy would leave the level and the template it was read
    ///         from sharing one — and the next reconcile would write a template's value into itself. A
    ///         round trip through the format is by definition what the file would have held, which is
    ///         also the only definition of "a copy" this format has.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A fresh id per node with the template's id kept as the source</b>, which is what
    ///         <c>SceneSerializer.Instantiate</c> does when a prefab is placed by hand: a scene mints its
    ///         own identities and remembers which template entity each came from. An entity already
    ///         carrying an inner link keeps that link, its overrides and its removals verbatim, for the
    ///         reason the writer declines to record over one.
    ///     </para>
    /// </remarks>
    static SceneEntityData Graft(SceneEntityData template, string reference) {
        var copy = YamlSerializer.Parse<SceneEntityData>(YamlSerializer.ToYaml(template));

        Stamp(copy);

        return copy;

        void Stamp(SceneEntityData entity) {
            if (!IsInstance(entity)) {
                entity.Prefab = reference;
                entity.Source = entity.Id;
                entity.Overrides.Clear();
                entity.Removed.Clear();
            }

            entity.Id = EntityId.New();

            foreach (var child in entity.Children) {
                Stamp(child);
            }
        }
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
