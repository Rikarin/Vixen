// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;

namespace Vixen.Editor.Assets.Scenes;

/// <summary>Turns an authored scene into the one a player loads.</summary>
/// <remarks>
///     <para>
///         <b>The step [08](../../../docs/plan/08-asset-pipeline-and-addressables.md) calls the
///         compile, and the seam it exists to keep.</b> Import produces editor-domain objects and
///         compile produces runtime-domain chunks: a <see cref="SceneFile" /> is a nested tree of
///         named entities with tagged components, and a <see cref="SceneContent" /> is flat tables
///         and archetype-ordered blobs. Everything the runtime does not need — the nesting, the
///         names when a build strips them, the spelling of every number — is spent here, once, at
///         build time.
///     </para>
///     <para>
///         <b>Nothing is loaded into a world to compile it.</b> The authored components arrive from
///         the YAML binder already boxed, and a binder writes a boxed value straight into a column —
///         so a content build never constructs an ECS world, an archetype or an entity. That matters
///         because a build compiles every scene in a project, in parallel worker processes, and a
///         world per scene would be the largest thing in each of them.
///     </para>
///     <para>
///         <b>Deterministic, because doc 12 gates the build on it.</b> Entities are numbered by the
///         same depth-first walk the file is written in, blocks are ordered by their component names,
///         entities within a block are ascending, and columns are in name order. Two builds of one
///         scene on two operating systems produce the same bytes, so an unchanged level ships nothing
///         in a content update.
///     </para>
/// </remarks>
public static class SceneCompiler {
    /// <summary>
    ///     What this compiler produces. <b>Bumping it recompiles every scene in every project</b>,
    ///     which is what a change to the compiled layout needs.
    /// </summary>
    public const int Version = 1;

    /// <summary>Compiles a scene file into the runtime asset.</summary>
    /// <param name="file">The authored scene.</param>
    /// <param name="report">Where to say what is wrong with it.</param>
    /// <param name="keepNames">Whether the compiled asset carries what each entity is called.</param>
    /// <returns>The asset, or <see langword="null" /> if anything was reported as an error.</returns>
    public static SceneAsset? CompileScene(
        SceneFile file,
        Action<ImportSeverity, string> report,
        bool keepNames = true
    ) {
        ArgumentNullException.ThrowIfNull(file);

        var content = Compile(file, report, keepNames);

        return content is null ? null : new SceneAsset { Name = file.Name, Content = content };
    }

    /// <summary>Compiles a prefab file into the runtime asset.</summary>
    /// <param name="file">The authored prefab, which is a scene with one root.</param>
    /// <param name="report">Where to say what is wrong with it.</param>
    /// <param name="keepNames">Whether the compiled asset carries what each entity is called.</param>
    /// <returns>The asset, or <see langword="null" /> if anything was reported as an error.</returns>
    /// <remarks>
    ///     ⚠ <b>The single root is checked here rather than at load.</b> A prefab with two roots is a
    ///     scene that was saved with the wrong extension, and a build is where somebody can still do
    ///     something about it — the runtime's own check is a backstop for content that reached a
    ///     player anyway.
    /// </remarks>
    public static PrefabAsset? CompilePrefab(
        SceneFile file,
        Action<ImportSeverity, string> report,
        bool keepNames = true
    ) {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(report);

        if (file.Roots.Count != 1) {
            report(
                ImportSeverity.Error,
                $"A prefab is one subtree and this has {file.Roots.Count} roots. Either it is a scene that was "
                + "saved as a .vxprefab, or its entities want a root of their own to hang from."
            );

            return null;
        }

        var content = Compile(file, report, keepNames);

        return content is null ? null : new PrefabAsset { Name = file.Name, Content = content };
    }

    /// <summary>Flattens a file into tables and blocks.</summary>
    /// <param name="file">The authored scene.</param>
    /// <param name="report">Where to say what is wrong with it.</param>
    /// <param name="keepNames">Whether the compiled content carries what each entity is called.</param>
    /// <returns>The content, or <see langword="null" /> if anything was reported as an error.</returns>
    /// <remarks>
    ///     ⚠ <b>Every problem in the file is reported, and then it fails once.</b> A compiler that
    ///     stopped at the first bad component would make fixing a hand-merged scene a sequence of
    ///     builds; the errors are counted and the return is null at the end.
    /// </remarks>
    public static SceneContent? Compile(SceneFile file, Action<ImportSeverity, string> report, bool keepNames = true) {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(report);

        var order = new List<SceneEntityData>();
        var parents = new List<int>();

        foreach (var root in file.Roots) {
            Walk(root, -1, order, parents);
        }

        var failures = 0;
        var content = new SceneContent {
            Count = order.Count,
            Parents = [.. parents],
            Positions = new Vector3[order.Count],
            Rotations = new Quaternion[order.Count],
            Scales = new Vector3[order.Count],
            Names = keepNames ? new string[order.Count] : [],
            Ids = new Guid[order.Count]
        };

        var seen = new Dictionary<EntityId, string>();

        // Keyed by the block's component names joined, which is what an archetype is in this format.
        // Sorted, so the block order is the file's and not the dictionary's.
        var blocks = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        var members = new Dictionary<string, List<AuthoredComponent>>(StringComparer.Ordinal);
        var carried = new List<List<AuthoredComponent>>(order.Count);

        for (var index = 0; index < order.Count; index++) {
            var data = order[index];

            content.Positions[index] = data.Position;

            // A zero quaternion and a zero scale are what a hand-written entity that left the field
            // out looks like, and both are taken as the identity at load. Normalised here as well as
            // there, so that what a build ships does not depend on the reader being forgiving.
            content.Rotations[index] = data.Rotation == default ? Quaternion.Identity : data.Rotation;
            content.Scales[index] = data.Scale == default ? Vector3.One : data.Scale;
            content.Ids[index] = data.Id.Value;

            if (keepNames) {
                content.Names[index] = data.Name;
            }

            if (!data.Id.IsNone && !seen.TryAdd(data.Id, data.Name)) {
                failures++;

                report(
                    ImportSeverity.Error,
                    $"'{data.Name}' and '{seen[data.Id]}' both have the id {data.Id}. An entity id is what a "
                    + "reference into this scene means, so two entities cannot share one — this is what "
                    + "copying an entity's block by hand produces."
                );
            }

            carried.Add(Binders(data, report, ref failures));
        }

        if (failures > 0) {
            return null;
        }

        for (var index = 0; index < order.Count; index++) {
            var key = string.Join(' ', carried[index].Select(component => component.Binder.Name));

            if (!blocks.TryGetValue(key, out var entries)) {
                blocks[key] = entries = [];
                members[key] = carried[index];
            }

            entries.Add(index);
        }

        var built = new List<SceneBlock>(blocks.Count);

        foreach (var (key, entries) in blocks) {
            var columns = new List<SceneColumn>();

            for (var column = 0; column < members[key].Count; column++) {
                var binder = members[key][column].Binder;
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new SerializationWriter(buffer);

                // The entities in a block carry the same components in the same order — that is what
                // makes them one block — so the column's index is the same index into each of them.
                foreach (var entity in entries) {
                    binder.WriteValue(ref writer, carried[entity][column].Value);
                }

                writer.Flush();
                columns.Add(new() { Component = binder.Name, Data = buffer.WrittenSpan.ToArray() });
            }

            built.Add(new() { Entities = [.. entries], Columns = [.. columns] });
        }

        content.Blocks = [.. built];

        try {
            // ⚠ The compiler checks its own output, because the alternative is a build that succeeds
            // and a game that fails to load a level. Everything the validator looks at is something
            // this method decided, so a failure here is a bug in it and not in the file — and the
            // exception says which, before the chunk is written.
            content.Validate();
        } catch (ArgumentException failure) {
            report(
                ImportSeverity.Error,
                $"The compiled scene did not check out, which is a bug in the compiler rather than in the file: "
                + failure.Message
            );

            return null;
        }

        return content;
    }

    /// <summary>One authored component: how to write it, and what it says.</summary>
    /// <param name="Binder">What turns it into bytes.</param>
    /// <param name="Value">The value the YAML binder bound, boxed.</param>
    readonly record struct AuthoredComponent(ISceneComponentBinder Binder, object Value);

    /// <summary>Which components an entity carries, in the order a block writes its columns.</summary>
    /// <remarks>
    ///     Sorted by name, so that two entities with the same components land in the same block
    ///     whatever order the file listed them in — which is the difference between a level of six
    ///     blocks and a level of six hundred.
    /// </remarks>
    static List<AuthoredComponent> Binders(
        SceneEntityData data,
        Action<ImportSeverity, string> report,
        ref int failures
    ) {
        var binders = new List<AuthoredComponent>();

        foreach (var component in data.Components) {
            if (component is null) {
                failures++;
                report(ImportSeverity.Error, $"'{data.Name}' has an empty entry in its components.");
                continue;
            }

            if (component is LocalTransform) {
                failures++;

                report(
                    ImportSeverity.Error,
                    $"'{data.Name}' lists a LocalTransform among its components, and its position, rotation and "
                    + "scale are already the authored transform. Two answers to one question is worse than "
                    + "either: put the values in the entity's own fields."
                );

                continue;
            }

            if (!SceneComponentRegistry.TryGet(component.GetType(), out var binder)) {
                failures++;

                report(
                    ImportSeverity.Error,
                    $"'{data.Name}' carries a {component.GetType().Name}, which nothing declared as a scene "
                    + "component. A compiled scene names a component by its contract and loads it by that name, "
                    + "so the type needs [Component] beside its [DataContract], in an assembly the build and the "
                    + "game both have."
                );

                continue;
            }

            if (binders.Any(existing => existing.Binder.ComponentType == binder.ComponentType)) {
                failures++;

                report(
                    ImportSeverity.Error,
                    $"'{data.Name}' carries two {binder.Name} components. An entity has one of each, so one of "
                    + "them would silently win — which is a merge that took both sides of a hunk."
                );

                continue;
            }

            binders.Add(new(binder, component));
        }

        binders.Sort((left, right) => string.CompareOrdinal(left.Binder.Name, right.Binder.Name));
        return binders;
    }

    static void Walk(SceneEntityData data, int parent, List<SceneEntityData> order, List<int> parents) {
        var index = order.Count;
        order.Add(data);
        parents.Add(parent);

        foreach (var child in data.Children) {
            Walk(child, index, order, parents);
        }
    }
}
