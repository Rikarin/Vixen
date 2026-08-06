// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>One way an input can be broken. <see cref="BrokenMeshes" />' eighteen, as an axis.</summary>
/// <remarks>
///     ⚠ <b>Each of these is a defect the conditioner has a named step for, so a failure names a
///     step.</b> docs/plan/41 § D3's seven steps are weld, orient, de-speck, repair, fill,
///     pre-remesh, shrinkwrap; every entry below is something one of them exists to survive.
/// </remarks>
enum MeshDefect : byte {
    /// <summary>Nothing. The base shape as the generator built it.</summary>
    None,

    /// <summary>Every triangle carrying its own three positions — a TRELLIS export's ratio of three.</summary>
    Unwelded,

    /// <summary>A face whose three corners are collinear: no area, no normal and no plane.</summary>
    ZeroArea,

    /// <summary>Two positions at exactly the same place, so one edge has no length.</summary>
    ZeroLengthEdge,

    /// <summary>Positions snapped to a voxel grid — marching cubes' staircase at the cell frequency.</summary>
    Staircase,

    /// <summary>Debris an SDF hallucinated round the body.</summary>
    Specks,

    /// <summary>A wall meeting a floor in a T: one edge with three faces.</summary>
    TJunction,

    /// <summary>A second copy of the shape driven through the first.</summary>
    SelfIntersection,

    /// <summary>A strip of triangles too thin to have a meaningful normal.</summary>
    Slivers,

    /// <summary>Every face listed a second time, so every edge has four.</summary>
    DuplicateFaces,

    /// <summary>An open grid with no rim closed, appended alongside.</summary>
    OpenRim,

    /// <summary>A one-sided surface, which no assignment of windings makes consistent.</summary>
    Mobius,

    /// <summary>A share of the faces turned inside out.</summary>
    FlippedWinding,

    /// <summary>A second component a hundredth of a percent of the first's area — below the de-speck.</summary>
    TinyComponent,

    /// <summary>A second component forty percent of the first's area — above it, and must survive.</summary>
    LargeComponent
}

/// <summary>A mesh described by the handful of numbers a shrinker can make smaller.</summary>
/// <param name="Shape">What the body is.</param>
/// <param name="Sides">How many divisions round it. Clamped to what the shape can build.</param>
/// <param name="Steps">How many along it.</param>
/// <param name="Defects">What is wrong with it, applied in the order given. Repeats are multiplicity.</param>
/// <param name="Multiplicity">How many of the defects that come in numbers — specks, slivers, components.</param>
/// <param name="Degeneracy">What share of positions are snapped onto their neighbour, in <c>[0, 1]</c>.</param>
/// <param name="Scale">What every position is multiplied by, once the mesh has been triangulated.</param>
/// <remarks>
///     ⚠ <b>A description rather than the mesh, and that is the whole reason this is a record of six
///     scalars.</b> CsCheck shrinks the values it generated, not what they were turned into. A failing
///     four-thousand-triangle mesh is not a finding anybody can act on; a failing
///     <c>(Box, 3, 1, [Mobius], 1, 0, 1)</c> is one line to paste into a test.
/// </remarks>
readonly record struct MeshRecipe(
    ShapeKind Shape,
    int Sides,
    int Steps,
    MeshDefect[] Defects,
    int Multiplicity,
    float Degeneracy,
    float Scale
) {
    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Round-trippable, and that is the whole point of printing it at all.</b> A shrunk case
    ///     is only a finding if it can be pasted into a test, and a fixed number of decimal places
    ///     prints a float that conditions differently — so the paste reproduces nothing and the next
    ///     person concludes the property is flaky. <c>R</c> is the shortest string that parses back to
    ///     the same float.
    /// </remarks>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"new({nameof(ShapeKind)}.{Shape}, {Sides}, {Steps}, [{string.Join(", ", Defects.Select(Name))}], "
            + $"{Multiplicity}, {Degeneracy:R}f, {Scale:R}f)"
        );

    static string Name(MeshDefect defect) => $"{nameof(MeshDefect)}.{defect}";
}

/// <summary>The space <see cref="BrokenMeshes" />' eighteen named defects are eighteen points in.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41's robustness criterion is a statement over arbitrary broken input</b> — "a
///         corpus of 200 deliberately broken meshes… produces a valid all-quad result or a
///         <c>RemeshReport</c> naming the stage that refused, and never an exception or a hang" — and
///         two hundred is a number somebody chose, not a corpus somebody has. <see cref="BrokenMeshes" />
///         builds eighteen of them by hand, which is eighteen points chosen for being <i>interesting</i>;
///         this is the space they were chosen from, so that the nineteenth does not have to be thought
///         of before it can be tested.
///     </para>
///     <para>
///         ⚠ <b><see cref="BrokenMeshes" /> is not replaced and must not be.</b> Its eighteen are named,
///         so a failure says "mobius" rather than a seed, and several of them are the fixtures the
///         conditioning tests reason about in prose. What moves here is only the set of claims that
///         are true of every mesh.
///     </para>
///     <para>
///         ⚠ <b>Nothing below draws from a random stream.</b> Every defect is a deterministic function
///         of the recipe, because a generator that reached for its own randomness would shrink a recipe
///         into a <i>different</i> mesh, and the minimal case CsCheck reported would not reproduce.
///     </para>
/// </remarks>
static class BrokenMeshSpace {
    /// <summary>The shapes a body can be. Every one of them closed, or noted where it is not.</summary>
    /// <remarks>
    ///     ⚠ Deliberately small and deliberately cheap. The claims here are about topology and about
    ///     thresholds, and neither gets truer on a shape with ten thousand triangles — but a suite that
    ///     conditioned one two hundred times would take minutes.
    /// </remarks>
    static readonly ShapeKind[] Shapes = [
        ShapeKind.Box,
        ShapeKind.Sphere,
        ShapeKind.Cylinder,
        ShapeKind.Cone,
        ShapeKind.Torus,
        ShapeKind.Capsule,
        ShapeKind.Stairs,
        ShapeKind.Ramp,
        ShapeKind.Pipe,
        ShapeKind.Plane
    ];

    /// <summary>Recipes at unit scale — the axis a scale property varies for itself.</summary>
    public static readonly Gen<MeshRecipe> Recipe = Gen.Select(
        Gen.OneOfConst(Shapes),
        Gen.Int[3, 10],
        Gen.Int[1, 5],
        Gen.Enum<MeshDefect>().Array[0, 3],
        Gen.Int[1, 6],
        Gen.Single[0f, 0.35f],
        (shape, sides, steps, defects, multiplicity, degeneracy) =>
            new MeshRecipe(shape, sides, steps, defects, multiplicity, degeneracy, 1f)
    );

    /// <summary>The same, at a size drawn from six orders of magnitude.</summary>
    /// <remarks>
    ///     ⚠ <b>Powers of ten rather than powers of two, and neither endpoint is a binary fraction.</b>
    ///     <c>ScaleInvarianceTests</c>' own remarks record why: a test that scaled by powers of two
    ///     would pass on a build where every threshold comparison sat exactly on its boundary.
    /// </remarks>
    public static readonly Gen<MeshRecipe> Sized = Gen.Select(
        Recipe,
        Gen.OneOfConst(1e-3f, 1e-2f, 0.1f, 1f, 10f, 1e+2f, 1e+3f),
        (recipe, scale) => recipe with { Scale = scale }
    );

    /// <summary>Builds the mesh a recipe describes.</summary>
    /// <param name="recipe">The recipe.</param>
    /// <returns>The mesh, triangulated and then scaled.</returns>
    /// <remarks>
    ///     ⚠ <b>Triangulated before it is scaled, and <see cref="BrokenMeshes.Triangulated" /> records
    ///     why: ear clipping is itself scale-sensitive.</b> A quad from a generator is not exactly
    ///     planar, so which corner is an ear can land either way under a change of size — measured on
    ///     <see cref="BrokenMeshes.Specks" />, 1248 of 2016 indices differ between a thousandth and a
    ///     thousand times. Scaling a mesh that is already triangles takes that out of every comparison
    ///     built on this, so what is left is a statement about the code under test.
    /// </remarks>
    public static EditMesh Build(MeshRecipe recipe) {
        var mesh = Body(recipe);

        foreach (var defect in recipe.Defects) {
            mesh = Apply(mesh, defect, recipe);
        }

        mesh = Degenerate(mesh, recipe.Degeneracy);

        return BrokenMeshes.Scaled(BrokenMeshes.Triangulated(mesh), recipe.Scale);
    }

    static EditMesh Body(MeshRecipe recipe) =>
        MeshShapes.Create(
            ShapeParameters.Default(recipe.Shape) with {
                Sides = Math.Max(3, recipe.Sides),
                Steps = Math.Max(1, recipe.Steps)
            }
        );

    static EditMesh Apply(EditMesh mesh, MeshDefect defect, MeshRecipe recipe) {
        switch (defect) {
            case MeshDefect.None:
                return mesh;

            case MeshDefect.Unwelded:
                return BrokenMeshes.Unwelded(mesh);

            case MeshDefect.ZeroArea:
                return WithCollinearFace(mesh);

            case MeshDefect.ZeroLengthEdge:
                return WithDoubledPosition(mesh);

            case MeshDefect.Staircase:
                return Snapped(mesh, 0.12f);

            case MeshDefect.Specks:
                return Appended(mesh, Speck(), recipe.Multiplicity, 1.6f);

            case MeshDefect.TJunction:
                return Merged(mesh, BrokenMeshes.TJunction());

            case MeshDefect.SelfIntersection:
                return Merged(mesh, mesh, new(0.9f, 0.9f, 0.9f));

            case MeshDefect.Slivers:
                return Merged(mesh, BrokenMeshes.Slivers(4 * recipe.Multiplicity));

            case MeshDefect.DuplicateFaces:
                return Doubled(mesh);

            case MeshDefect.OpenRim:
                return Merged(mesh, BrokenMeshes.OpenSurface());

            case MeshDefect.Mobius:
                return Merged(mesh, BrokenMeshes.Mobius(2 * (2 + recipe.Multiplicity)));

            case MeshDefect.FlippedWinding:
                return Flipped(mesh, 2 + recipe.Multiplicity);

            case MeshDefect.TinyComponent:
                return Appended(mesh, Scaled(mesh, 0.01f), recipe.Multiplicity, 8f);

            case MeshDefect.LargeComponent:
                return Appended(mesh, Scaled(mesh, MathF.Sqrt(0.4f)), recipe.Multiplicity, 8f);

            default:
                throw new ArgumentOutOfRangeException(nameof(defect), defect, "No such defect.");
        }
    }

    /// <summary>Three collinear positions carrying a face — no area, no normal, no plane.</summary>
    static EditMesh WithCollinearFace(EditMesh source) {
        var mesh = Copy(source);
        var extent = MathF.Max(1e-6f, (source.Bounds.Maximum - source.Bounds.Minimum).X);

        var a = mesh.AddPosition(source.Bounds.Maximum);
        var b = mesh.AddPosition(source.Bounds.Maximum + new Vector3(extent, 0f, 0f));
        var c = mesh.AddPosition(source.Bounds.Maximum + new Vector3(2f * extent, 0f, 0f));

        mesh.AddFace([a, b, c]);

        return mesh;
    }

    /// <summary>A quad whose two halves share an edge of length zero.</summary>
    static EditMesh WithDoubledPosition(EditMesh source) {
        var mesh = Copy(source);
        var corner = source.Bounds.Maximum;
        var extent = MathF.Max(1e-6f, (source.Bounds.Maximum - source.Bounds.Minimum).X);

        var a = mesh.AddPosition(corner);
        var b = mesh.AddPosition(corner + new Vector3(extent, 0f, 0f));
        var c = mesh.AddPosition(corner + new Vector3(extent, 0f, extent));
        var d = mesh.AddPosition(corner + new Vector3(extent, 0f, extent));
        var e = mesh.AddPosition(corner + new Vector3(0f, 0f, extent));

        mesh.AddFace([a, b, c]);
        mesh.AddFace([a, d, e]);

        return mesh;
    }

    /// <summary>Positions rounded onto a grid, as a fraction of the mesh's own extent.</summary>
    /// <remarks>
    ///     ⚠ A fraction of the extent rather than a length, so a recipe means the same thing at every
    ///     <see cref="MeshRecipe.Scale" /> — which is the precondition for a scale property being about
    ///     the code rather than about the fixture.
    /// </remarks>
    static EditMesh Snapped(EditMesh source, float fraction) {
        var box = source.Bounds;
        var cell = fraction * MathF.Max(1e-9f, (box.Maximum - box.Minimum).Length());
        var mesh = new EditMesh();

        foreach (var position in source.Positions) {
            mesh.AddPosition(
                new(
                    MathF.Round(position.X / cell) * cell,
                    MathF.Round(position.Y / cell) * cell,
                    MathF.Round(position.Z / cell) * cell
                )
            );
        }

        CopyFaces(source, mesh);

        return mesh;
    }

    /// <summary>Snaps a share of the positions onto the one before them, making zero-length edges.</summary>
    /// <param name="source">The mesh.</param>
    /// <param name="rate">What share, in <c>[0, 1]</c>.</param>
    /// <returns>The mesh with that share collapsed.</returns>
    /// <remarks>
    ///     ⚠ <b>Chosen by a hash of the index rather than by a random draw</b>, so that the same recipe
    ///     always collapses the same positions and a shrunk case reproduces. Knuth's multiplicative
    ///     constant, and nothing about it needs to be a good hash — only a fixed one.
    /// </remarks>
    static EditMesh Degenerate(EditMesh source, float rate) {
        if (rate <= 0f || source.PositionCount < 2) {
            return source;
        }

        var threshold = (uint)(rate * uint.MaxValue);
        var mesh = new EditMesh();

        for (var index = 0; index < source.PositionCount; index++) {
            var collapse = index > 0 && (uint)index * 2654435761u < threshold;

            mesh.AddPosition(source.Positions[collapse ? index - 1 : index]);
        }

        CopyFaces(source, mesh);

        return mesh;
    }

    /// <summary>The same mesh with a share of its faces turned inside out.</summary>
    static EditMesh Flipped(EditMesh source, int every) {
        var mesh = new EditMesh();

        foreach (var position in source.Positions) {
            mesh.AddPosition(position);
        }

        for (var face = 0; face < source.FaceCount; face++) {
            var corners = source.CornersOf(face);

            if (face % every == 0) {
                var reversed = new int[corners.Length];

                for (var corner = 0; corner < corners.Length; corner++) {
                    reversed[corner] = corners[corners.Length - 1 - corner];
                }

                mesh.AddFace(reversed, source.Faces[face].Group);
            } else {
                mesh.AddFace(corners, source.Faces[face].Group);
            }
        }

        return mesh;
    }

    /// <summary>The same mesh with every face listed twice, so every edge has four.</summary>
    static EditMesh Doubled(EditMesh source) {
        var mesh = new EditMesh();

        foreach (var position in source.Positions) {
            mesh.AddPosition(position);
        }

        for (var pass = 0; pass < 2; pass++) {
            CopyFaces(source, mesh);
        }

        return mesh;
    }

    /// <summary>Copies of one mesh scattered round another at a fraction of its own diagonal.</summary>
    static EditMesh Appended(EditMesh source, EditMesh other, int count, float reach) {
        var mesh = Copy(source);
        var box = source.Bounds;
        var span = reach * MathF.Max(1e-9f, (box.Maximum - box.Minimum).Length());

        for (var index = 0; index < count; index++) {
            var angle = index * 0.7f;

            MeshOperations.Append(
                mesh,
                other,
                Matrix4x4.FromTranslation(
                    new(MathF.Cos(angle) * span, MathF.Sin(angle * 1.3f) * span, MathF.Sin(angle) * span)
                )
            );
        }

        return mesh;
    }

    static EditMesh Merged(EditMesh source, EditMesh other) => Merged(source, other, Vector3.Zero);

    /// <summary>Two meshes in one file, the second offset by a fraction of the first's own extent.</summary>
    static EditMesh Merged(EditMesh source, EditMesh other, Vector3 offset) {
        var mesh = Copy(source);
        var box = source.Bounds;
        var span = MathF.Max(1e-9f, (box.Maximum - box.Minimum).Length());

        MeshOperations.Append(mesh, other, Matrix4x4.FromTranslation(offset * span));

        return mesh;
    }

    static EditMesh Speck() => MeshShapes.Create(ShapeParameters.Default(ShapeKind.Box) with { Size = new(0.02f) });

    static EditMesh Scaled(EditMesh source, float factor) => BrokenMeshes.Scaled(source, factor);

    static EditMesh Copy(EditMesh source) => BrokenMeshes.Scaled(source, 1f);

    static void CopyFaces(EditMesh source, EditMesh into) {
        for (var face = 0; face < source.FaceCount; face++) {
            into.AddFace(source.CornersOf(face), source.Faces[face].Group);
        }
    }
}
