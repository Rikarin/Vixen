// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

public class ScratchRate {
    static readonly Gen<(MeshRecipe Recipe, bool Transfer, bool Uvs, int Rounds)> Case = Gen.Select(
        BrokenMeshSpace.Sized,
        Gen.Bool,
        Gen.Bool,
        Gen.Int[0, 2],
        (recipe, transfer, uvs, rounds) => (recipe, transfer, uvs, rounds)
    );

    [Fact]
    public void Rate() {
        var produced = 0;
        var refused = 0;
        var log = new System.Text.StringBuilder();

        Case.Sample(
            entry => {
                var (recipe, transfer, uvs, rounds) = entry;

                var settings = new RemeshSettings {
                    TargetQuads = 96,
                    TransferAttributes = transfer,
                    GenerateUvs = uvs,
                    Conditioning = new() { PreRemeshIterations = rounds }
                };

                var mesh = BrokenMeshSpace.Build(recipe);
                var quads = Remesher.Remesh(mesh, settings, out var report);

                if (quads.FaceCount == 0) {
                    refused++;

                    log.AppendLine(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"REFUSED: {recipe} transfer={transfer} uvs={uvs} rounds={rounds} "
                            + $"— {string.Join(" · ", report.Warnings)}"
                        )
                    );
                } else {
                    produced++;
                }
            },
            iter: long.Parse(
                Environment.GetEnvironmentVariable("SCRATCH_ITER") ?? "600",
                CultureInfo.InvariantCulture
            ),
            threads: 1
        );

        log.AppendLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"produced={produced} refused={refused} total={produced + refused} "
                + $"rate={refused * 100.0 / (produced + refused):F3}%"
            )
        );

        File.WriteAllText(
            "/private/tmp/claude-501/-Users-jiu-Projects-Vixen--claude-worktrees-adoring-raman-80fd04/7a2a98d1-9e77-41ec-bd5a-57d012f05ac4/scratchpad/rate.txt",
            log.ToString()
        );
    }
}
