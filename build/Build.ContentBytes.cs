// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
///     Builds one fixed content fixture and writes down what came out of it, so that three CI legs
///     can be compared against each other rather than each against itself.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this fills.</b> `Tools/Vixen.Cli.Tests` asserts that two builds on one machine
///         are byte-identical, and that two projects at different paths in opposite creation order
///         agree. Both are self-relative: they would pass on all three runners even if the three
///         runners produced three different catalogs. Unlike the wire format, whose oracle is bytes
///         committed in `Core/Vixen.Net.Tests/Wire`, content has no committed oracle — a catalog is
///         large, it changes whenever an importer legitimately changes, and a golden would be
///         updated rather than read. So the oracle is the other runners, and what crosses between
///         them is this manifest.
///     </para>
///     <para>
///         ⚠ <b>The target is pinned and that is not a detail.</b> A build is a function of its
///         target, the target is written into the catalog, and the target nobody names is
///         `ProjectWorkspace.HostTarget` — the operating system doing the building. Left to default,
///         the three legs would produce `"Linux"`, `"Windows"` and `"MacOS"` in the catalog's ordinal
///         string table, of two different lengths, moving every offset after them and the trailing
///         CRC. The comparison would have gone red on its first run for a reason that is not a
///         defect. Measured here before this file was written: two builds of this fixture for
///         `Windows` agree on all four files, and the same fixture for `Linux` differs in
///         `catalog.bin` and `catalog.bin.hash` and in nothing else.
///         `VixenCommandTests.TheSameContentBuiltForTwoTargetsIsNotTheSameBytes` keeps that true.
///     </para>
///     <para>
///         ⚠ <b>No models in the fixture, deliberately.</b> A `.obj` would be imported through
///         Assimp, and the three legs install three different builds of it — `libassimp5` from apt on
///         Ubuntu, Homebrew's on macOS, whatever Silk.NET resolves on Windows. Mesh bytes differing
///         between two versions of a native library is a real thing to know, but it is not a
///         determinism defect in Vixen and it would make this leg red on day one. The fixture is text
///         through `RawImporter`, sized past `ChunkFormat.MinimumCompressedSize` so that the LZ4
///         encoder's output <i>is</i> inside the compared bytes — which the existing determinism
///         tests never reach, their fixtures being the strings "the hero" and "the villain".
///     </para>
/// </remarks>
partial class Build {
    /// <summary>Which target to build the fixture for, on every runner.</summary>
    /// <remarks>
    ///     Any one of them would do; what matters is that it is the same string everywhere and that
    ///     no runner is allowed to pick. `Windows` because it is the alphabetically-first thing a
    ///     reader will guess is arbitrary, and it is.
    /// </remarks>
    const string ContentBytesTarget = "Windows";

    AbsolutePath ContentBytesDirectory => ArtifactsDirectory / "content-bytes";

    AbsolutePath ContentBytesFixture => RootDirectory / "Testing" / "ContentDeterminism";

    Target ContentBytes => definition => definition
        .Description("Builds the content-determinism fixture and writes a manifest of the bytes it produced")
        .DependsOn(Compile)
        .Produces(ContentBytesDirectory / "**")
        .Executes(() => {
                ContentBytesDirectory.CreateOrCleanDirectory();

                // Copied out of the tree rather than built in place: a content build writes Library/
                // and a scan repairs sidecars, and neither belongs in somebody's working copy.
                var project = ContentBytesDirectory / "project";
                var output = ContentBytesDirectory / "build";

                ContentBytesFixture.Copy(project, ExistsPolicy.MergeAndOverwrite);

                DotNetRun(settings => settings
                    .SetProjectFile(RootDirectory / "Tools" / "Vixen.Cli" / "Vixen.Cli.csproj")
                    .SetConfiguration(Configuration)
                    .EnableNoBuild()
                    // A list rather than one string: the paths are a runner's workspace and can
                    // contain spaces, and Nuke quotes each element rather than the whole line.
                    .SetApplicationArguments(
                        new List<string> {
                            "content",
                            "build",
                            "--project",
                            project,
                            "--target",
                            ContentBytesTarget,
                            "--output",
                            output
                        }
                    )
                );

                var files = output.GlobFiles("*").OrderBy(file => file.Name, StringComparer.Ordinal).ToList();

                // ⚠ A manifest of nothing is a manifest three runners agree on. The fixture has three
                // addressable assets in one group, so a build that worked writes the catalog, its
                // hash, the scene manifest and one bundle — and a build that quietly produced an
                // empty directory has to fail here rather than downstream, where it would read as
                // agreement.
                Assert.True(
                    files.Count == 4,
                    $"the fixture built {files.Count} files rather than 4, so there is nothing worth comparing between runners."
                );

                var manifest = new StringBuilder();

                foreach (var file in files) {
                    var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

                    // The bundle's name carries its own content hash, so the name is part of the
                    // claim and not only a key.
                    manifest.Append(CultureInfo.InvariantCulture, $"{hash}  {file.Name}\n");
                }

                // LF, written as bytes, because this file is compared across three operating systems
                // and a StreamWriter on Windows would put CRLF in it — which would be a difference
                // this gate reported as a content difference.
                File.WriteAllBytes(ContentBytesDirectory / "manifest.txt", Encoding.UTF8.GetBytes(manifest.ToString()));

                Log.Information("Content manifest for {Target}:\n{Manifest}", ContentBytesTarget, manifest.ToString());
            }
        );
}
