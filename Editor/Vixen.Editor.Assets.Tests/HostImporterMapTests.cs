// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Textures;
using Vixen.Testing.Importers;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>The instrument the three hosts are held to, calibrated against the pipeline that owns it (#1300).</summary>
/// <remarks>
///     The hosts' own tests live in their suites — <c>Vixen.Editor.App.Tests</c>, <c>Vixen.Cli.Tests</c>
///     and <c>Vixen.AssetCompiler.Tests</c> — because each one calls that host's link method, which
///     this assembly cannot see. What is asserted here is that the map means something.
/// </remarks>
public sealed class HostImporterMapTests {
    /// <summary>The pipeline's own registry is the map less exactly the rows a host's link provides.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves of the partition</b>: a linked row that also came out of the pipeline would
    ///     mean the importer had not moved, and a pipeline row missing from the map would mean the map
    ///     was captured without it.
    /// </remarks>
    [Fact]
    public void The_pipelines_own_registry_is_the_map_less_the_linked_rows() {
        var pipeline = HostImporterMap.Of(BuiltInImporters.Create(new ImporterContributions(), new ImporterContributions()));

        Assert.Equal(
            HostImporterMap.Expected.Except(HostImporterMap.LinkedOnly, StringComparer.Ordinal),
            pipeline
        );

        Assert.All(HostImporterMap.LinkedOnly, row => Assert.Contains(row, HostImporterMap.Expected));
    }

    /// <summary>⚠ A host that linked nothing it should have is red, and says which row.</summary>
    /// <remarks>
    ///     The calibration a list-completeness gate needs, on <c>EveryAttributedImporterTests</c>' terms:
    ///     a registry built with one importer missing must fail the same assertion the hosts pass.
    /// </remarks>
    [Fact]
    public void A_registry_missing_a_row_fails_the_gate() {
        var crippled = HostImporterMap.Of(new ImporterRegistry().Add(new TextureImporter()).AddFallback(new RawImporter()));

        Assert.Contains(".png TextureImporter", crippled);
        Assert.Contains("* RawImporter", crippled);
        Assert.Contains(".cube CubeLutImporter", HostImporterMap.Expected.Except(crippled, StringComparer.Ordinal));
    }
}
