// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.SceneView;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

namespace Vixen.Editor.Blockout;

/// <summary>docs/plan/41 § D16's blockout row: a `Retopologize` verb, one undo entry, result selected.</summary>
/// <remarks>
///     <para>
///         <b>"Its natural input is a boolean result, which is exactly the hard-surface case."</b> Doc
///         24's booleans produce a solid whose triangulation is whatever the CSG happened to emit —
///         long slivers along every cut, a fan across every cap — and that mesh has no loops to cut, no
///         rings to select and nothing to subdivide. Retopology gives the fourteen verbs above it
///         something to work on again, which is the argument docs/plan/41 § D15 makes for quads at all.
///     </para>
///     <para>
///         ⚠ <b>One undo entry, and it records the whole mesh.</b> Doc 24's D3: a topology change has
///         no inverse, so the entry is the mesh as it was rather than a description of what happened —
///         the same shape <c>BlockoutBoolean</c>'s own <c>Record</c> takes, and for the same reason. A
///         retopology replaces every vertex and every face, so an entry that tried to be clever about
///         it would be an entry that restores nothing.
///     </para>
///     <para>
///         ⚠ <b>A refusal leaves the mesh alone and is not an undo entry.</b> The remesher refuses by
///         returning an empty mesh with the stage named in its report — docs/plan/41's seventh exit
///         criterion — and a verb that pushed that onto the stack would give a designer an undo step
///         that undoes nothing, which is worse than a verb that visibly did not fire.
///     </para>
/// </remarks>
public static class BlockoutRetopology {
    /// <summary>Retopologises the selected solids, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="settings">What the output should be.</param>
    /// <param name="reports">Where each mesh's report goes, or null.</param>
    /// <returns>How many entities were retopologised.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> or <paramref name="settings" /> is null.</exception>
    public static int Run(
        SceneDocument document,
        RemeshSettings settings,
        IList<RemeshReport>? reports = null
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(settings);

        var done = 0;
        var made = new List<(Entity Entity, EditMesh Mesh)>();

        // Every remesh runs before the transaction opens, and the ordering is deliberate: the solve is
        // seconds rather than milliseconds, and an undo transaction held open across it is one an
        // autosave or a collaborator's edit can land inside.
        foreach (var entity in document.Selection.Items.ToArray()) {
            if (document.MeshOf(entity) is not { } mesh || mesh.IsEmpty) {
                continue;
            }

            var quads = Remesher.Remesh(mesh, settings, out var report);

            reports?.Add(report);

            if (!quads.IsEmpty) {
                made.Add((entity, quads));
            }
        }

        if (made.Count == 0) {
            return 0;
        }

        using (document.Stack.BeginTransaction("Retopologize")) {
            foreach (var (entity, quads) in made) {
                Record(document, entity, quads);
                done++;
            }

            document.Selection.Set([.. made.Select(pair => pair.Entity)]);
        }

        return done;
    }

    /// <summary>Replaces an entity's geometry with the quads, undoably.</summary>
    /// <remarks>
    ///     ⚠ <b>A derivation and a parametric shape are both collapsed first.</b> A boolean result is a
    ///     function of its operands and a retopology is not one of them, so leaving the node in place
    ///     would mean the next refresh quietly put the triangle soup back — which reads as the verb
    ///     having silently failed some time after it visibly worked.
    /// </remarks>
    static void Record(SceneDocument document, Entity entity, EditMesh made) {
        var was = document.MeshOf(entity) is { } mesh ? new EditMesh(mesh) : null;

        if (document.IsDerived(entity)) {
            document.Stack.Execute(new BooleanCommand(document, entity, null, "Retopologize"));
        }

        if (document.IsParametric(entity)) {
            document.Stack.Execute(ShapeCommand.Demote(document, entity, "Retopologize"));
        }

        document.SetMesh(entity, made);
        document.Stack.Execute(EditMeshCommand.Rebuilt(document, entity, was, "Retopologize"));
    }
}
