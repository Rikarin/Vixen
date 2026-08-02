// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry;

/// <summary>Everything true of a mesh that a tool might need to know before acting on it.</summary>
/// <param name="NonManifold">Edges with more than two faces.</param>
/// <param name="Boundary">Edges with one — the open rim of an unclosed surface.</param>
/// <param name="Reversed">Edges whose two faces walk them the same way, so one of them is inside out.</param>
/// <param name="Degenerate">Faces with no area.</param>
/// <param name="Orphans">Positions no face uses.</param>
/// <remarks>
///     <para>
///         <b>A report, not a verdict, and doc 24's D2 is the argument.</b> A half-edge structure
///         cannot represent a non-manifold edge, so a kernel built on one refuses the ordinary
///         blockout case — a wall meeting a floor in a T, a boolean result, an imported mesh with a
///         stray internal face. This one represents them and says where they are, which is what lets a
///         tool answer "this operation needs a manifold edge" instead of producing something wrong.
///     </para>
///     <para>
///         ⚠ <b>None of these is an error on its own.</b> A block-out under construction is boundary
///         edges all the way down; a wall built by mirroring is non-manifold where the halves meet;
///         an inside-out face is a mistake a designer made and can undo. What is an error is a mesh
///         whose <i>tables</i> disagree with each other — a corner naming no position, a face of two
///         corners — and those are refused at the door rather than reported here.
///     </para>
///     <para>
///         <b>This is the assertion helper doc 24's testing table calls the highest-value item.</b>
///         Every operation's tests run it afterwards, because a mesh operation that corrupts the edge
///         table produces geometry that looks correct and fails three operations later, in a mesh a
///         designer has spent an hour on, with no way to attribute it.
///     </para>
/// </remarks>
public readonly record struct MeshReport(
    IReadOnlyList<int> NonManifold,
    IReadOnlyList<int> Boundary,
    IReadOnlyList<int> Reversed,
    IReadOnlyList<int> Degenerate,
    int Orphans
) {
    /// <summary>Whether every edge has one face or two.</summary>
    public bool IsManifold => NonManifold.Count == 0;

    /// <summary>Whether the surface has no rim: every edge shared by exactly two faces.</summary>
    public bool IsClosed => IsManifold && Boundary.Count == 0;

    /// <summary>Whether every pair of neighbours agrees which way is out.</summary>
    public bool IsConsistent => Reversed.Count == 0;

    /// <summary>Whether the mesh is tidy: closed, consistent, no slivers and nothing left over.</summary>
    /// <remarks>
    ///     What a primitive satisfies and what an operation that claims to preserve a solid should
    ///     still satisfy afterwards. A mesh under construction routinely does not, which is why this
    ///     is one property among five rather than the only one.
    /// </remarks>
    public bool IsSolid => IsClosed && IsConsistent && Degenerate.Count == 0 && Orphans == 0;

    /// <summary>What is wrong, in a sentence, or <see langword="null" /> when the answer is nothing.</summary>
    /// <remarks>What a test's failure message says, so that a broken invariant names itself rather
    ///     than being a false somebody has to go and look up.</remarks>
    public string? Describe() {
        List<string> problems = [];

        if (NonManifold.Count > 0) {
            problems.Add($"{NonManifold.Count} non-manifold edge(s)");
        }

        if (Boundary.Count > 0) {
            problems.Add($"{Boundary.Count} boundary edge(s)");
        }

        if (Reversed.Count > 0) {
            problems.Add($"{Reversed.Count} inconsistently wound edge(s)");
        }

        if (Degenerate.Count > 0) {
            problems.Add($"{Degenerate.Count} face(s) with no area");
        }

        if (Orphans > 0) {
            problems.Add($"{Orphans} orphaned position(s)");
        }

        return problems.Count == 0 ? null : string.Join(", ", problems);
    }
}
