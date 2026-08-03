// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Animation;
using Xunit;

namespace Tests;

/// <summary>The file that says what a variation run is, and how far off is too far.</summary>
public sealed class HarnessPlanImporterTests {
    [Fact]
    public void TheImporterClaimsItsExtensionAndWritesItsContract() {
        Assert.Equal([".vxharness"], new HarnessPlanImporter().Extensions);

        Assert.True(TypeRegistry.TryGetByAlias(HarnessPlanImporter.PlanType, out var plan));
        Assert.Equal(typeof(HarnessPlanContent), plan.Type);
    }

    [Fact]
    public async Task APlanCompilesAndItsAxesMultiply() {
        const string Yaml = """
            name: reach the rail
            clip: Assets/Anim/Reach.vxanim
            rig: Assets/Bodies/Hero.gltf
            shapes: Assets/Bodies/Hero.vxproxyshapes
            samples: 16
            bodies: [0.8, 1.0, 1.25]
            ground:
              - degrees: 0
                height: 0
              - degrees: 12
                height: 0.1
            props:
              - slot: rail
                values:
                  - name: thin
                    position: 0.4 1.0 0.2
                  - name: thick
                    position: 0.4 1.0 0.2
                    scale: 2 2 2
            thresholds:
              residual: 0.02
              penetration: 0.01
              reach: true
            """;

        var result = await Import(Yaml);

        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);

        var plan = Serializer.Read<HarnessPlanContent>(Assert.Single(result.Artifacts).Content.ToArray());

        // Three bodies × two grounds × two rails.
        Assert.Equal(12, plan.Configurations);
        Assert.Equal(0.02f, plan.Thresholds.Residual, 4);
        Assert.True(plan.Thresholds.Bake().Reach);
        Assert.Equal(2f, plan.Props[0].Values[1].Scale.X, 3);
    }

    /// <summary>⚠ A gate whose thresholds are all zero is a green build that means nothing.</summary>
    [Fact]
    public async Task APlanThatJudgesNothingIsWarnedAboutRatherThanRefused() {
        var result = await Import("name: sweep\nclip: a.vxanim\nrig: b.gltf\nbodies: [1.0]\n");

        // Not an error: a plan run for its matrix rather than as a gate is a legitimate thing.
        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("judges nothing", StringComparison.Ordinal)
        );
    }

    /// <summary>⚠ Axes multiply, and a run of thousands is one somebody started by accident.</summary>
    [Fact]
    public async Task AnAccidentallyEnormousRunIsWarnedAbout() {
        // Forty bodies and four grounds is a hundred and sixty configurations — over the line, and
        // exactly the shape of plan somebody writes by adding one axis to a reasonable one.
        var bodies = string.Join(
            ", ",
            Enumerable.Range(1, 40).Select(step => (step / 10f).ToString("0.0#", CultureInfo.InvariantCulture))
        );

        var result = await Import(
            $"name: sweep\nclip: a.vxanim\nrig: b.gltf\nbodies: [{bodies}]\n"
            + "ground:\n  - degrees: 0\n    height: 0\n  - degrees: 5\n    height: 0\n"
            + "  - degrees: 10\n    height: 0\n  - degrees: 15\n    height: 0\n"
            + "thresholds:\n  residual: 0.02\n"
        );

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("by accident", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task APlanWithNothingToPlayIsRefused() {
        var result = await Import("name: nothing\nthresholds:\n  residual: 0.02\n");

        Assert.Empty(result.Artifacts);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error
                && entry.Message.Contains("always passes", StringComparison.Ordinal)
        );
    }

    /// <summary>Two samples is the fewest with a velocity between them, and velocity catches a snap.</summary>
    [Fact]
    public async Task AOneSampleRunIsRefused() {
        var result = await Import("name: x\nclip: a.vxanim\nrig: b.gltf\nsamples: 1\nthresholds:\n  residual: 0.02\n");

        Assert.Empty(result.Artifacts);
        Assert.Contains(result.Diagnostics, entry => entry.Message.Contains("snaps", StringComparison.Ordinal));
    }

    static async Task<ImportResult> Import(string text) {
        var path = new VirtualPath("/Assets/reach.vxharness");
        var files = new MemoryFileProvider();
        var importer = new HarnessPlanImporter();

        files.Seed(path, text);

        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }
}
