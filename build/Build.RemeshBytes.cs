// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
///     Remeshes one pinned fixture at every worker count docs/plan/41 § Exit 3 names and writes down
///     what came out, so three CI legs can be compared against each other rather than each against
///     itself.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this fills.</b> § Exit 3 asks for <i>"ten runs × {1, 4, 16} threads × three
///         platforms, byte-identical output"</i>. <c>RemeshDeterminismTests</c> covers the first two
///         axes honestly and the third has never been measured: every assertion in it is self-relative,
///         so all three legs of <c>ci.yml</c>'s <c>test</c> job would pass while producing three
///         different meshes. Unlike the wire format, whose oracle is bytes committed in
///         <c>Core/Vixen.Net.Tests/Wire</c>, a remesh has no committed oracle — it changes whenever a
///         stage legitimately changes and a golden would be updated rather than read. So the oracle is
///         the other runners, and what crosses between them is this manifest.
///     </para>
///     <para>
///         ⚠ <b>This is <c>ContentBytes</c>' shape on purpose.</b> The same question was answered for
///         the content build — a target that produces one artefact per leg, an <c>if: always()</c>
///         upload, and a job at the bottom of <c>ci.yml</c> that downloads all three and diffs them.
///         Answering it a second time in a second shape would be worse than not answering it. ⚠ The
///         issue text that asked for this cites <b>#190</b> as that precedent and #190 is
///         <i>"Owed 53: Raven — negative diagnostic fixtures"</i>; the CI row that actually tracks a
///         three-OS determinism run is <b>#218</b>. Follow <c>Build.ContentBytes</c>, not the number.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is imported, and that rules out <c>vixen remesh</c>.</b> A CLI run would
///         have been the closer parallel to <c>ContentBytes</c>, but its input goes through
///         <c>ModelReader</c> and therefore Assimp — and the three legs install three different builds
///         of it, <c>libassimp5</c> from apt on Ubuntu, Homebrew's on macOS, whatever Silk.NET resolves
///         on Windows. Mesh bytes differing between two builds of a native library is a real thing to
///         know and is not a determinism defect in Vixen; it would have made this leg red on day one
///         for the wrong reason. <c>ContentBytes</c> refused a model in its fixture for the same
///         reason, in its own words. The fixture is <c>MeshShapes</c>' sphere, built from arithmetic.
///     </para>
///     <para>
///         ⚠ <b>The four legs are hashed separately rather than into one number.</b> A difference that
///         appears only at sixteen workers on arm64 names itself in the diff instead of collapsing into
///         "the manifests differ".
///     </para>
///     <para>
///         ⚠ <b>The assertion below is this target's own instrument check.</b> The manifest is written
///         by a test, so the way this quietly stops working is the environment variable failing to
///         reach the test host — which produces no file, and a missing file downstream is three
///         manifests that were never compared. Four well-formed lines or nothing. Measured: renaming
///         the variable by one character leaves the test <i>green</i> and fails this target, which is
///         the split the two halves are for.
///     </para>
///     <para>
///         ⚠ <b><c>--no-build</c> means a stale assembly writes a stale manifest, and locally that
///         bites.</b> In CI the job's own <c>Test</c> step has just compiled the tree, so the binary is
///         the checkout; a developer running <c>nuke RemeshBytes --skip Compile</c> after editing a
///         stage will hash the previous build and read it as a result. Measured while this was being
///         written — a one-part-in-ten-million change in <c>SurfaceProjector</c> survived a revert
///         that way and moved the hash. The target keeps <c>DependsOn(Compile)</c> for that reason;
///         <c>--skip</c> is the caller taking it off.
///     </para>
/// </remarks>
partial class Build {
    /// <summary>How many legs the fixture is remeshed at. § Exit 3's three, plus the unscheduled one.</summary>
    const int RemeshBytesLegs = 4;

    AbsolutePath RemeshBytesDirectory => ArtifactsDirectory / "remesh-bytes";

    Target RemeshBytes => definition => definition
        .Description("Remeshes the determinism fixture at every worker count and writes a manifest of the bytes it produced")
        .DependsOn(Compile)
        .Produces(RemeshBytesDirectory / "**")
        .Executes(() => {
                RemeshBytesDirectory.CreateOrCleanDirectory();

                // Set on this process so the test host inherits it, which is what GoldenImages does
                // and for the same reason: Nuke's typed environment API has moved between versions
                // and the inherited environment has not.
                Environment.SetEnvironmentVariable("VIXEN_REMESH_BYTES", RemeshBytesDirectory);

                DotNetTest(settings => settings
                    .SetProjectFile(RootDirectory / "Core" / "Vixen.Geometry.Remeshing.Tests"
                        / "Vixen.Geometry.Remeshing.Tests.csproj")
                    .SetConfiguration(Configuration)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetFilter("FullyQualifiedName~RemeshBytesTests")
                    .SetResultsDirectory(TestResultsDirectory)
                );

                var manifest = RemeshBytesDirectory / "manifest.txt";

                Assert.True(
                    manifest.FileExists(),
                    $"no manifest at '{manifest}'. The test ran without VIXEN_REMESH_BYTES reaching it, "
                    + "so there is nothing for the comparison job to compare and it would have reported agreement."
                );

                var lines = File.ReadAllLines(manifest);

                // ⚠ Four lines and four *well-formed* lines are two different claims, and a
                // half-written manifest satisfies the second on its own. Three of those diff clean.
                var hashed = lines.Count(line => Regex.IsMatch(line, "^[0-9a-f]{64}  [^ ]+$"));

                Assert.True(
                    lines.Length == RemeshBytesLegs && hashed == RemeshBytesLegs,
                    $"the manifest has {lines.Length} lines of which {hashed} name a hashed leg, and there are "
                    + $"{RemeshBytesLegs} legs. Nothing worth comparing between runners was produced."
                );

                Log.Information("Remesh manifest:\n{Manifest}", string.Join("\n", lines));
            }
        );
}
