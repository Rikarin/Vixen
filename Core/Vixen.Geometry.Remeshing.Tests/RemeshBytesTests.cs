// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>
///     One pinned remesh, hashed, so that three CI runners can be compared against each other rather
///     than each against itself.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this fills.</b> docs/plan/41 § Exit 3 asks for <i>"ten runs × {1, 4, 16} threads ×
///         three platforms, byte-identical output"</i>. <see cref="RemeshDeterminismTests" /> covers the
///         first two axes and is careful about what it covers; the third has never been measured,
///         because every assertion in it is <i>self-relative</i>. Ten runs here agree with ten runs
///         here. All three legs of <c>ci.yml</c>'s <c>test</c> job would pass while producing three
///         different meshes, and § D14 calls determinism a gate rather than an aspiration — today the
///         gate proves reproducibility and not portability.
///     </para>
///     <para>
///         ⚠ <b>Cross-platform float reproducibility is the half only a comparison can find.</b>
///         <c>Math.Fma</c> contracting a multiply-add on one JIT and not another, an x87 spill, a
///         different <c>Vector&lt;T&gt;</c> width on arm64 — each of those changes the field solve and
///         none of them is visible to a machine comparing itself. What crosses between the runners is
///         the manifest this writes; <c>ci.yml</c>'s <c>remesh-bytes</c> job is what diffs them.
///     </para>
///     <para>
///         ⚠ <b>The four legs are in the manifest rather than collapsed into one hash, and that is what
///         buys the full grid.</b> Each runner writes a line per worker count, so the diff between two
///         runners is per-thread-count: a difference that appears only at sixteen workers on macOS
///         names itself. Collapsing them would still catch it and would not say where.
///     </para>
///     <para>
///         ⚠ <b>The fixture is procedural and nothing is imported, which is not a convenience.</b>
///         <c>Build.ContentBytes</c> refused a model for this reason and the refusal applies here
///         twice over: the three legs install three different builds of Assimp — <c>libassimp5</c> from
///         apt, Homebrew's, whatever Silk.NET resolves on Windows — so a <c>.obj</c> fixture would make
///         this red on day one for a difference in a native library rather than in the remesher. The
///         same argument rules out <c>vixen remesh</c>, whose input goes through <c>ModelReader</c>.
///         <see cref="MeshShapes" /> builds the sphere from arithmetic on all three.
///     </para>
///     <para>
///         ⚠ <b>The manifest is written before the comparison below, deliberately.</b> The upload in CI
///         is <c>if: always()</c>, so a leg whose own determinism broke still ships the bytes that show
///         what it produced — and a manifest that is missing is a red comparison job rather than a
///         quiet pass, which is <c>Build.RemeshBytes</c>'s assertion.
///     </para>
/// </remarks>
public class RemeshBytesTests {
    /// <summary>Where the manifest goes, when a build asks for one. Unset in an ordinary test run.</summary>
    /// <remarks>
    ///     ⚠ <b>Unset does not skip.</b> Everything below still runs and still asserts; the variable
    ///     decides whether a file is written, not whether the work happens. A test that early-returns
    ///     when its environment is not set is the instrument that reports success on the day it does
    ///     not run, and this repository has shipped several.
    /// </remarks>
    const string Directory = "VIXEN_REMESH_BYTES";

    /// <summary>The worker counts § Exit 3 names, plus the leg that has no scheduler at all.</summary>
    static readonly (string Name, int Workers)[] Legs = [
        ("calling-thread", 0), ("1-worker", 1), ("4-workers", 4), ("16-workers", 16)
    ];

    /// <summary>The same input on every runner, hashed into a manifest CI compares between them.</summary>
    [Fact]
    public void The_same_remesh_on_every_runner_is_the_same_bytes() {
        var settings = new RemeshSettings { TargetQuads = 400 };
        var manifest = new StringBuilder();
        var hashes = new List<(string Name, string Hash)>();

        foreach (var (name, workers) in Legs) {
            EditMesh quads;
            RemeshReport report;

            if (workers == 0) {
                quads = Remesher.Remesh(RemesherTests.Fixture("sphere"), settings, out report);
            } else {
                using var scheduler = new JobScheduler(workers);

                Assert.Equal(workers, scheduler.WorkerCount);

                quads = Remesher.Remesh(RemesherTests.Fixture("sphere"), settings, out report, scheduler);
            }

            Guard(name, report, quads);

            var hash = Convert.ToHexStringLower(SHA256.HashData(Canonical(quads, report)));

            hashes.Add((name, hash));
            manifest.Append(CultureInfo.InvariantCulture, $"{hash}  {name}\n");
        }

        Write(manifest.ToString());

        // The within-runner half, which is RemeshDeterminismTests' subject and is asserted here too
        // because a manifest of four disagreeing legs is not something to ship to a comparison job.
        foreach (var (name, hash) in hashes) {
            Assert.True(
                hash == hashes[0].Hash,
                $"{name} hashed to {hash} and {hashes[0].Name} to {hashes[0].Hash}. "
                + "docs/plan/41 § D14 asks for the same bits at every worker count."
            );
        }
    }

    /// <summary>The fixture has to be worth comparing before a comparison over it means anything.</summary>
    /// <remarks>
    ///     <see cref="RemeshDeterminismTests" />' guard, for its reason: a remesh that refused produces
    ///     an empty mesh, three runners producing nothing agree with each other, and a gate that cannot
    ///     tell <i>identical</i> from <i>nothing was compared</i> is the failure this repository keeps
    ///     having.
    /// </remarks>
    static void Guard(string leg, RemeshReport report, EditMesh mesh) {
        Assert.True(report.QuadCount > 200, $"{leg}: {report.QuadCount} quads — {string.Join(" · ", report.Warnings)}");
        Assert.Equal(report.QuadCount, mesh.FaceCount);
        Assert.True(report.Conditioning.Triangles > 800, $"{leg}: {report.Conditioning.Triangles} triangles will not split.");

        Assert.True(
            report.Singularities.Count >= 8,
            $"{leg}: a field with no structure in it produces identical output whatever the order."
        );
    }

    /// <summary>Everything measured, as bytes, in one fixed order and one fixed endianness.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Floats go in as <see cref="BitConverter.SingleToInt32Bits" />, never as text.</b> A
    ///         formatted float is a comparison with a tolerance wearing a hash's clothes: two runners
    ///         landing in the same neighbourhood would print the same digits and agree, which is
    ///         precisely the drift this is looking for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Little-endian written explicitly.</b> Every runner in the matrix is little-endian
    ///         today, so this changes nothing today — and the day one is not, the hash difference would
    ///         be the machine's byte order rather than the remesher's arithmetic, reported as a
    ///         determinism defect. <see cref="BinaryPrimitives" /> costs nothing and removes the
    ///         question.
    ///     </para>
    ///     <para>
    ///         <b>The report is in as well as the mesh</b>, for <see cref="RemeshDeterminismTests" />'
    ///         reason: half of docs/plan/41 § Part 4 is computed from artefacts the output mesh does not
    ///         carry, so a hash over positions and corners alone would leave every one of them unswept.
    ///         An elapsed time is a clock reading and is not here; what a stage handled is a measurement
    ///         and is.
    ///     </para>
    /// </remarks>
    static byte[] Canonical(EditMesh mesh, RemeshReport report) {
        var bytes = new List<byte>();

        Add(mesh.PositionCount);
        Add(mesh.FaceCount);
        Add(mesh.CornerCount);

        for (var position = 0; position < mesh.PositionCount; position++) {
            var point = mesh.Positions[position];

            Add(BitConverter.SingleToInt32Bits(point.X));
            Add(BitConverter.SingleToInt32Bits(point.Y));
            Add(BitConverter.SingleToInt32Bits(point.Z));
        }

        for (var corner = 0; corner < mesh.CornerCount; corner++) {
            Add(mesh.Corners[corner]);
        }

        Add(report.QuadCount);
        Add(report.NonQuadCount);
        Add(report.SingularitiesOnFeatures);
        Add(report.Singularities.Count);

        foreach (var singularity in report.Singularities) {
            Add(singularity.Position);
            Add(singularity.Valence);
            Add(singularity.Index);
        }

        Add(BitConverter.SingleToInt32Bits(report.MaxDeviation));
        Add(BitConverter.SingleToInt32Bits(report.MeanDeviation));
        Add(BitConverter.SingleToInt32Bits(report.MinScaledJacobian));
        Add(BitConverter.SingleToInt32Bits(report.FeatureReproductionError));

        Add(report.Conditioning.Triangles);
        Add(report.Conditioning.Welded);
        Add(report.Conditioning.Reoriented);
        Add(report.Conditioning.Unorientable);
        Add(report.Conditioning.Despecked);
        Add(report.Conditioning.Cut);
        Add(report.Conditioning.Filled);
        Add(report.Conditioning.Shrinkwrapped ? 1 : 0);

        Add(report.Stages.Count);

        foreach (var stage in report.Stages) {
            Add((int) stage.Stage);
            Add(stage.Elements);
        }

        Add(report.Warnings.Count);

        foreach (var warning in report.Warnings) {
            bytes.AddRange(Encoding.UTF8.GetBytes(warning));
        }

        return [.. bytes];

        void Add(int value) {
            Span<byte> four = stackalloc byte[4];

            BinaryPrimitives.WriteInt32LittleEndian(four, value);
            bytes.AddRange(four);
        }
    }

    /// <summary>Writes the manifest where a build asked for it, and nowhere otherwise.</summary>
    /// <remarks>
    ///     ⚠ <b>LF, written as bytes.</b> This file is diffed across three operating systems and a
    ///     <c>StreamWriter</c> on Windows would put CRLF in it — which the comparison would report as
    ///     Windows remeshing differently from Linux. <c>Build.ContentBytes</c> learned the same thing.
    /// </remarks>
    static void Write(string manifest) {
        var into = Environment.GetEnvironmentVariable(Directory);

        if (string.IsNullOrWhiteSpace(into)) {
            return;
        }

        System.IO.Directory.CreateDirectory(into);
        File.WriteAllBytes(Path.Combine(into, "manifest.txt"), Encoding.UTF8.GetBytes(manifest));
    }
}
