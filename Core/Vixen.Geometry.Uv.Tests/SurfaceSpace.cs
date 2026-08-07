// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>One way a surface can be hard to unwrap. <see cref="ShapeCorpus" />' twelve, as an axis.</summary>
/// <remarks>
///     ⚠ <b>Each of these is something the charter or the flattener has a named answer for, so a
///     failure names the answer that did not fire.</b> docs/plan/42 § D5 refuses an annulus, a handle,
///     a chart in two pieces and a pinch <i>before</i> a solve runs, and § D3's recursion splits
///     whatever fails its distortion bound; every entry below is one of those doors.
/// </remarks>
enum SurfaceDefect : byte {
    /// <summary>Nothing. The primitive as <see cref="MeshShapes" /> built it.</summary>
    None,

    /// <summary>Half of it, with no cap — so a closed solid becomes an open surface with a rim.</summary>
    Cut,

    /// <summary>Face groups in threes, which <see cref="UvSettings.KeepGroups" /> partitions on first.</summary>
    Grouped,

    /// <summary>A second copy alongside, so one chart request covers two components.</summary>
    Detached,

    /// <summary>Two triangles meeting at exactly one position: a disk by Euler characteristic and not one.</summary>
    Pinched,

    /// <summary>Every face carrying its own corners, so no two faces are adjacent at all.</summary>
    Unwelded,

    /// <summary>A triangle whose three corners are collinear: no area, no normal, no plane.</summary>
    Degenerate,

    /// <summary>The same surface with its positions permuted — nothing about the shape has changed.</summary>
    Renumbered,

    /// <summary>Flattened onto a slab a fiftieth as tall, so every triangle is nearly degenerate.</summary>
    Squashed
}

/// <summary>A surface described by the handful of numbers a shrinker can make smaller.</summary>
/// <param name="Shape">What the body is.</param>
/// <param name="Sides">How many divisions round it. Clamped to what the shape can build.</param>
/// <param name="Steps">How many along it.</param>
/// <param name="Defects">What is wrong with it, applied in the order given. Repeats are multiplicity.</param>
/// <param name="Scale">What every position is multiplied by, once the defects have been applied.</param>
/// <remarks>
///     ⚠ <b>A description rather than the mesh, and that is the whole reason this is a record of five
///     scalars.</b> The same argument <c>BrokenMeshSpace</c>'s <c>MeshRecipe</c> makes: CsCheck shrinks
///     the values it generated, not what they were turned into, so a failing four-thousand-triangle
///     mesh is not a finding anybody can act on and a failing
///     <c>(Torus, 3, 1, [Pinched], 1)</c> is one line to paste into a test.
/// </remarks>
readonly record struct SurfaceRecipe(ShapeKind Shape, int Sides, int Steps, SurfaceDefect[] Defects, float Scale) {
    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Round-trippable, and that is the whole point of printing it at all.</b> A fixed number
    ///     of decimal places prints a float that charts differently, so the paste reproduces nothing
    ///     and the next person concludes the property is flaky. <c>R</c> is the shortest string that
    ///     parses back to the same float.
    /// </remarks>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"new({nameof(ShapeKind)}.{Shape}, {Sides}, {Steps}, [{string.Join(", ", Defects.Select(Name))}], "
            + $"{Scale:R}f)"
        );

    static string Name(SurfaceDefect defect) => $"{nameof(SurfaceDefect)}.{defect}";
}

/// <summary>The space <see cref="ShapeCorpus" />' twelve hand-built surfaces are twelve points in.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/42's exit criterion 2 is a statement over arbitrary input</b> — "zero flipped
///         triangles on 100 % of the corpus, or an explicit refusal naming the chart. No exceptions, no
///         hangs" — and a corpus is a sample. <see cref="ShapeCorpus" /> builds twelve by hand, chosen
///         so that each one fails a different way; this is the space they were chosen from, so that the
///         thirteenth does not have to be thought of before it can be tested.
///     </para>
///     <para>
///         ⚠ <b><see cref="ShapeCorpus" /> is not replaced and must not be.</b> § B6 is explicit that a
///         test measuring <i>distortion</i> against a moving corpus cannot tell a regression from a
///         reseed, so every figure this assembly quotes stays on its fixed shapes. What moves here is
///         only the set of claims that are true of every surface.
///     </para>
///     <para>
///         ⚠ <b>Built from <see cref="MeshShapes" /> rather than by hand, which is the opposite of
///         <see cref="ShapeCorpus" />' decision and for a compatible reason.</b> That corpus exists to
///         quote distortion figures, so every one of its shapes is deliberately cut open — a closed
///         surface has no parameterization at all and would measure nothing. This one exists to state
///         robustness, where a closed solid is the <i>interesting</i> input: it is what an importer
///         hands the unwrapper, and § D3's recursion cutting it into disks is the behaviour under test.
///     </para>
///     <para>
///         ⚠ <b>Nothing below draws from a random stream.</b> Every defect is a deterministic function
///         of the recipe, because a generator that reached for its own randomness would shrink a recipe
///         into a <i>different</i> mesh, and the minimal case CsCheck reported would not reproduce.
///     </para>
/// </remarks>
static class SurfaceSpace {
    /// <summary>Which permutation <see cref="SurfaceDefect.Renumbered" /> uses. Fixed, for the reason above.</summary>
    const uint Shuffle = 0x9E3779B9u;

    /// <summary>The shapes a body can be, closed and open alike.</summary>
    /// <remarks>
    ///     ⚠ Deliberately small and deliberately cheap. The claims here are about topology and about
    ///     refusals, and neither gets truer on a shape with ten thousand triangles — but a suite that
    ///     unwrapped one two hundred times would take an hour.
    /// </remarks>
    static readonly ShapeKind[] Shapes = [
        ShapeKind.Box,
        ShapeKind.Plane,
        ShapeKind.Cylinder,
        ShapeKind.Cone,
        ShapeKind.Sphere,
        ShapeKind.Capsule,
        ShapeKind.Torus,
        ShapeKind.Stairs,
        ShapeKind.Ramp,
        ShapeKind.Arch,
        ShapeKind.Pipe,
        ShapeKind.DoorFrame
    ];

    /// <summary>Recipes at unit scale — the axis a scale property varies for itself.</summary>
    public static readonly Gen<SurfaceRecipe> Recipe = Gen.Select(
        Gen.OneOfConst(Shapes),
        Gen.Int[3, 8],
        Gen.Int[1, 3],
        Gen.Enum<SurfaceDefect>().Array[0, 3],
        (shape, sides, steps, defects) => new SurfaceRecipe(shape, sides, steps, defects, 1f)
    );

    /// <summary>The same, at a size drawn from six orders of magnitude.</summary>
    /// <remarks>
    ///     ⚠ <b>Powers of ten rather than powers of two, and neither endpoint is a binary fraction.</b>
    ///     <c>UvChartInvarianceTests</c> records why the distinction matters: a scale that is a power of
    ///     two leaves every position exactly proportional, and a test run only there would pass on a
    ///     build where a chart sat within an ulp of a threshold.
    /// </remarks>
    public static readonly Gen<SurfaceRecipe> Sized = Gen.Select(
        Recipe,
        Gen.OneOfConst(1e-3f, 1e-2f, 0.1f, 1f, 10f, 1e+2f, 1e+3f),
        (recipe, scale) => recipe with { Scale = scale }
    );

    /// <summary>Builds the mesh a recipe describes.</summary>
    /// <param name="recipe">The recipe.</param>
    /// <returns>The mesh, with the defects applied in order and then scaled.</returns>
    public static EditMesh Build(SurfaceRecipe recipe) {
        var mesh = MeshShapes.Create(
            ShapeParameters.Default(recipe.Shape) with {
                Sides = Math.Max(3, recipe.Sides),
                Steps = Math.Max(1, recipe.Steps)
            }
        );

        foreach (var defect in recipe.Defects) {
            mesh = Apply(mesh, defect);
        }

        return Scaled(mesh, recipe.Scale);
    }

    static EditMesh Apply(EditMesh mesh, SurfaceDefect defect) =>
        defect switch {
            SurfaceDefect.None => mesh,
            SurfaceDefect.Cut => Cut(mesh),
            SurfaceDefect.Grouped => Grouped(mesh),
            SurfaceDefect.Detached => Detached(mesh),
            SurfaceDefect.Pinched => Pinched(mesh),
            SurfaceDefect.Unwelded => Unwelded(mesh),
            SurfaceDefect.Degenerate => Degenerate(mesh),
            SurfaceDefect.Renumbered => ShapeCorpus.Renumber(mesh, Shuffle),
            SurfaceDefect.Squashed => Squashed(mesh),
            _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, "No such defect.")
        };

    /// <summary>Half the solid, uncapped — a closed surface with a rim cut into it.</summary>
    /// <remarks>
    ///     ⚠ <b>The cut is refused rather than forced, and the recipe still means something when it
    ///     is.</b> <see cref="MeshBoolean.PlaneCut" /> returns null on input it cannot cut, and a
    ///     generator that threw there would be testing the boolean rather than the unwrapper — so the
    ///     uncut body goes through, which is a mesh the space already contains.
    /// </remarks>
    static EditMesh Cut(EditMesh source) {
        var box = source.Bounds;
        var plane = new Plane(Vector3.UnitY, -0.5f * (box.Minimum.Y + box.Maximum.Y));
        var half = MeshBoolean.PlaneCut(source, plane, keepFront: false, cap: false);

        return half is null || half.IsEmpty ? source : half;
    }

    /// <summary>Face groups in threes, which the charter partitions on before it measures anything.</summary>
    static EditMesh Grouped(EditMesh source) {
        var mesh = Positions(source);

        for (var face = 0; face < source.FaceCount; face++) {
            mesh.AddFace(source.CornersOf(face), face % 3);
        }

        return mesh;
    }

    /// <summary>A second copy of the body, well clear of the first.</summary>
    static EditMesh Detached(EditMesh source) {
        var mesh = Copy(source);
        var box = source.Bounds;
        var span = MathF.Max(1e-6f, (box.Maximum - box.Minimum).Length());

        MeshOperations.Append(mesh, source, Matrix4x4.FromTranslation(new(4f * span, 0f, 0f)));

        return mesh;
    }

    /// <summary>Two triangles meeting at exactly one position, appended alongside.</summary>
    /// <remarks>
    ///     ⚠ <b>The one non-disk the Euler characteristic cannot see</b>, which is why § D5 checks for
    ///     it separately: χ counts one, and the surface still has no injective map to the plane.
    /// </remarks>
    static EditMesh Pinched(EditMesh source) {
        var mesh = Copy(source);
        var box = source.Bounds;
        var span = MathF.Max(1e-6f, (box.Maximum - box.Minimum).Length());
        var corner = box.Maximum + new Vector3(span, 0f, 0f);

        var pinch = mesh.AddPosition(corner);
        var a = mesh.AddPosition(corner + new Vector3(span, 0f, 0f));
        var b = mesh.AddPosition(corner + new Vector3(span, span, 0f));
        var c = mesh.AddPosition(corner - new Vector3(span, 0f, 0f));
        var d = mesh.AddPosition(corner - new Vector3(span, span, 0f));

        mesh.AddFace([pinch, a, b]);
        mesh.AddFace([pinch, c, d]);

        return mesh;
    }

    /// <summary>Every face carrying its own corners, so nothing is adjacent to anything.</summary>
    static EditMesh Unwelded(EditMesh source) {
        var mesh = new EditMesh();

        for (var face = 0; face < source.FaceCount; face++) {
            var corners = source.CornersOf(face);
            var copied = new int[corners.Length];

            for (var corner = 0; corner < corners.Length; corner++) {
                copied[corner] = mesh.AddPosition(source.Positions[corners[corner]]);
            }

            mesh.AddFace(copied, source.Faces[face].Group);
        }

        return mesh;
    }

    /// <summary>Three collinear positions carrying a face — no area, no normal, no plane.</summary>
    static EditMesh Degenerate(EditMesh source) {
        var mesh = Copy(source);
        var box = source.Bounds;
        var extent = MathF.Max(1e-6f, (box.Maximum - box.Minimum).X);

        var a = mesh.AddPosition(box.Maximum);
        var b = mesh.AddPosition(box.Maximum + new Vector3(extent, 0f, 0f));
        var c = mesh.AddPosition(box.Maximum + new Vector3(2f * extent, 0f, 0f));

        mesh.AddFace([a, b, c]);

        return mesh;
    }

    /// <summary>The same surface pressed onto a slab a fiftieth as tall.</summary>
    static EditMesh Squashed(EditMesh source) =>
        Transformed(source, position => new(position.X, position.Y * 0.02f, position.Z));

    static EditMesh Scaled(EditMesh source, float scale) =>
        scale == 1f ? source : Transformed(source, position => position * scale);

    static EditMesh Copy(EditMesh source) => Transformed(source, position => position);

    static EditMesh Transformed(EditMesh source, Func<Vector3, Vector3> move) {
        var mesh = new EditMesh();

        foreach (var position in source.Positions) {
            mesh.AddPosition(move(position));
        }

        for (var face = 0; face < source.FaceCount; face++) {
            mesh.AddFace(source.CornersOf(face), source.Faces[face].Group);
        }

        return mesh;
    }

    static EditMesh Positions(EditMesh source) {
        var mesh = new EditMesh();

        foreach (var position in source.Positions) {
            mesh.AddPosition(position);
        }

        return mesh;
    }
}
