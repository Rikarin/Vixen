// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;

namespace Vixen.Editor.Core.Scenes;

/// <summary>Why a prefab an instance names could not be opened.</summary>
/// <remarks>
///     ⚠ <b>Every one of these leaves the instance exactly as the file had it.</b> A renamed, unbuilt
///     or not-yet-imported prefab must not cost a level its content — that is the whole reason doc 47
///     § 3 chose a format that keeps resolved values. An unresolved template degrades to an ordinary
///     subtree carrying a dead link, which comes back the moment the asset does.
/// </remarks>
public enum PrefabUnresolvedKind {
    /// <summary>The reference is not a <c>vx:</c> reference at all.</summary>
    /// <remarks>A hand-edited or badly merged <c>prefab</c> key.</remarks>
    NotAReference,

    /// <summary>Nothing in the project has that GUID.</summary>
    /// <remarks>Deleted, or a scene opened before the asset it names has been scanned in.</remarks>
    NotInProject,

    /// <summary>The asset is there and the file behind it is not.</summary>
    NoFile,

    /// <summary>The file is there and is not a prefab this editor can read.</summary>
    /// <remarks>
    ///     Malformed YAML, or a scene from a newer editor — <c>SceneFile.FromYaml</c> refuses the
    ///     second on purpose, and refusing to reconcile against a file this build only half
    ///     understands is the same refusal.
    /// </remarks>
    Unreadable
}

/// <summary>One prefab a scene names and this reconcile could not open.</summary>
/// <param name="Prefab">The reference text, exactly as the scene file carries it.</param>
/// <param name="Kind">Why it could not be opened.</param>
/// <param name="Detail">The path or the parser's message, for a person to act on.</param>
public readonly record struct PrefabUnresolved(string Prefab, PrefabUnresolvedKind Kind, string Detail) {
    /// <summary>Renders it as its kind and what it names.</summary>
    /// <returns>The report in text.</returns>
    public override string ToString() => $"{Kind}: {Prefab}";
}

/// <summary>What one reconcile of a scene against its prefabs did.</summary>
/// <param name="Instances">How many entities in the scene carry a prefab link.</param>
/// <param name="Templates">How many distinct prefabs were opened and reconciled against.</param>
/// <param name="Written">How many members took their template's value.</param>
/// <param name="Reports">What could not be resolved inside a template that <i>was</i> opened.</param>
/// <param name="Unresolved">The prefabs that could not be opened at all.</param>
/// <remarks>
///     <see cref="Changed" /> is the one a caller usually wants: a scene whose prefabs have not moved
///     reconciles to nothing, and telling somebody about that is noise.
/// </remarks>
public sealed record PrefabReconcileReport(
    int Instances,
    int Templates,
    int Written,
    IReadOnlyList<PrefabReport> Reports,
    IReadOnlyList<PrefabUnresolved> Unresolved
) {
    /// <summary>A reconcile that did nothing and had nothing to say.</summary>
    public static PrefabReconcileReport None { get; } = new(0, 0, 0, [], []);

    /// <summary>Whether anything happened that is worth telling a person about.</summary>
    public bool Changed => Written > 0 || Reports.Count > 0 || Unresolved.Count > 0;
}

/// <summary>Bringing a scene back in step with the prefabs it was authored against.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An editor-side pass at open time, and it can never be anything else.</b>
///         <c>ImportContext</c> gives an importer its own GUID, its own source path and a file
///         provider over <i>paths</i> — and no way to turn an <c>AssetId</c> into one. So
///         <c>SceneCompiler</c> cannot open the prefab an instance names, which is the constraint that
///         picked the whole format:
///         <see href="../../docs/plan/47-prefab-overrides-and-nested-prefabs.md">doc 47</see> § 2.
///         The editor <i>can</i> — <see cref="AssetDatabase" /> is the one place that knows which GUID
///         is at which path today — and this is the half of the wall that has a door in it.
///     </para>
///     <para>
///         <b>Over the file, before it reaches a document.</b> Reconciliation is a rewrite of
///         <see cref="SceneEntityData" /> values, so doing it to the parsed file rather than to a world
///         means it needs no <c>World</c>, no <c>SceneDocument</c> and no viewport — and the loader
///         that runs afterwards is the same loader that runs when nothing was stale.
///     </para>
///     <para>
///         ⚠ <b>The file on disk is not rewritten and the document does not open dirty.</b> What is
///         repaired is what the editor holds; the bytes catch up on the next save the author makes.
///         An editor that wrote to a level merely because somebody looked at it would put changes in
///         a working tree that nobody asked for, and would do it to every level in a project after a
///         prefab was touched.
///     </para>
///     <para>
///         ⚠ <b>One pass, outer over inner, and deliberately not a fixpoint</b> — R7's single level of
///         nesting. It needs no ordering machinery: every entity carries at most one link, so the sets
///         of entities each template touches are disjoint and the passes cannot interfere.
///     </para>
/// </remarks>
public static class PrefabReconcile {
    /// <summary>Brings every prefab instance in a scene back in step with its template.</summary>
    /// <param name="scene">The scene file, rewritten in place.</param>
    /// <param name="assets">The project's index, which is what turns a GUID into a path.</param>
    /// <returns>What it did.</returns>
    public static PrefabReconcileReport Run(SceneFile scene, AssetDatabase assets) {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(assets);

        // In the order the file names them, so that two runs over one scene report in one order —
        // a set would make the report's order depend on hashing, which is a diff with no edit behind
        // it the moment anybody writes one of these to a log.
        List<string> named = [];
        var instances = 0;

        foreach (var entity in scene.All()) {
            if (!PrefabOverrides.IsInstance(entity)) {
                continue;
            }

            instances++;

            if (!named.Contains(entity.Prefab, StringComparer.OrdinalIgnoreCase)) {
                named.Add(entity.Prefab);
            }
        }

        if (named.Count == 0) {
            return PrefabReconcileReport.None;
        }

        List<PrefabReport> reports = [];
        List<PrefabUnresolved> unresolved = [];
        var written = 0;
        var templates = 0;

        foreach (var prefab in named) {
            if (!TryOpen(prefab, assets, out var template, out var why)) {
                unresolved.Add(why);
                continue;
            }

            templates++;
            written += PrefabOverrides.Reconcile(scene, prefab, template, reports);
        }

        return new(instances, templates, written, reports, unresolved);
    }

    /// <summary>Opens the prefab a reference names, as its file holds it.</summary>
    /// <param name="prefab">The reference text a scene carries.</param>
    /// <param name="assets">The project's index.</param>
    /// <param name="template">The prefab file.</param>
    /// <param name="why">Why not, when it could not be opened.</param>
    /// <returns>Whether it was opened.</returns>
    /// <remarks>
    ///     Public because reconciling is not the only thing that wants the template: the inspector's
    ///     override indication pairs an instance's objects with the template's, and a second way of
    ///     finding the same file would be a second set of rules about what a missing one means.
    /// </remarks>
    public static bool TryOpen(
        string prefab,
        AssetDatabase assets,
        out SceneFile template,
        out PrefabUnresolved why
    ) {
        ArgumentException.ThrowIfNullOrEmpty(prefab);
        ArgumentNullException.ThrowIfNull(assets);

        template = null!;

        // ⚠ Through `AssetReference.TryParse` rather than by trimming the prefix, for the reason the
        // scene's own asset key gives: what is in the file is a reference, which may carry a
        // sub-asset — `vx:<guid>#<sub>` — and a reader that assumed the short form would silently
        // take the wrong half of one.
        if (!AssetReference.TryParse(prefab, out var reference) || reference.IsNull) {
            why = new(prefab, PrefabUnresolvedKind.NotAReference, prefab);
            return false;
        }

        if (!assets.TryGetByGuid(reference.Asset, out var entry) || entry.IsFolder) {
            why = new(prefab, PrefabUnresolvedKind.NotInProject, reference.Asset.ToString());
            return false;
        }

        var path = assets.Paths.Absolute(entry.Path);

        if (!File.Exists(path)) {
            why = new(prefab, PrefabUnresolvedKind.NoFile, entry.Path);
            return false;
        }

        try {
            template = SceneFile.FromYaml(File.ReadAllText(path));
        } catch (Exception error)
            when (error is YamlParseException or YamlBindingException or NotSupportedException or IOException) {
            // ⚠ Caught and reported rather than thrown out of an open. A single unreadable prefab
            // would otherwise stop a level opening at all — which turns "one asset is broken" into
            // "the level is gone", and the level is the file that cannot be rebuilt.
            why = new(prefab, PrefabUnresolvedKind.Unreadable, error.Message);
            return false;
        }

        why = default;
        return true;
    }
}
