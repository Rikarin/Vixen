// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Xunit;

namespace Tests;

/// <summary>
///     Every number in the engine's quality table, per tier, written down.
/// </summary>
/// <remarks>
///     <para>
///         <b>Written because three deliberately altered tier numbers passed the whole suite.</b>
///         <c>Platform/Vixen.Graphics.Golden.Tests/StandardFrameTierImageTests</c> renders each tier
///         and compares it against a picture, and moving Medium's cascade resolution from 1024 to
///         512, High's froxel slices from 64 to 32 and Medium's bloom pyramid from four levels to
///         three changed <em>nothing a picture could see</em> at that fixture's size — the largest
///         of the three moved the frame's average channel by 0.010 of a level. That is not a fault
///         in the fixture: a fidelity knob is a cost trade that is meant to be nearly invisible,
///         which is exactly why a picture is the wrong instrument for it.
///     </para>
///     <para>
///         <see cref="RenderQualityTests.The_engine_table_is_complete_for_every_tier" /> is the
///         right instrument and was spot-checking eleven of two hundred and forty numbers. So all
///         three edits were silent everywhere: no structural test named them and no golden could see
///         them. This is the sheet that makes any of them fail a build, by name.
///     </para>
///     <para>
///         ⚠ <b>Reflected over <see cref="ResolvedQuality" /> rather than listing the fields.</b> A
///         hand-written list is a list that goes stale the first time somebody adds a knob — the new
///         field would simply not be in it, and the one thing this test exists to prevent is a
///         quality number nobody is watching. Reflecting means a new knob appears in the snapshot
///         and fails until somebody writes its value down, which is the review this is for.
///     </para>
///     <para>
///         <b>When it fails legitimately</b> — somebody retuned a tier on purpose — the diff names
///         the tier, the field, the old value and the new one. Read it, agree with it, paste it in.
///         A snapshot nobody reads before updating is a snapshot that asserts nothing, which is the
///         same warning the golden images carry.
///     </para>
/// </remarks>
public class QualityTableSnapshotTests {
    /// <summary>The whole table, as one sheet.</summary>
    /// <remarks>
    ///     One string rather than four, because the interesting property of a quality ladder is
    ///     between its columns: a tier that stopped differing from the one below it is visible here
    ///     and would not be in four separate snapshots compared one at a time.
    /// </remarks>
    const string Expected = """
        Low
          bloom = False
          bloomFilterRadius = 1
          bloomLevels = 3
          cascadeCount = 2
          cascadeResolution = 1024
          constantBias = 0.0015
          culling = Off
          depthOfField = False
          dfaoSamples = 8
          dfaoScale = 0.5
          dilationPasses = 1
          dofMaximumRadius = 24
          dofSamples = 8
          fog = False
          foliageCellBudget = 128
          foliageCullDistanceScale = 0.7
          foliageDensityScale = 0.6
          fxaa = Performance
          grassCullDistanceScale = 0.6
          grassDensityScale = 0.5
          grassResidentCells = 128
          irradianceBudget = 2
          lensFlare = False
          localExposure = False
          localExposureTaps = 4
          lodBias = 1
          maxLights = 64
          maxLightsPerObject = 4
          mipBias = 0.5
          motionBlur = False
          motionBlurSamples = 4
          particleBudgetScale = 0.5
          probeTileSize = 32
          punctualResolution = 256
          punctualTilesPerSide = 4
          reflectionSteps = 16
          renderScale = 1
          roughnessThreshold = 0.5
          screenTraces = False
          shadowDistance = 75
          slopeBias = 0.004
          splitLambda = 0.75
          ssaoDirections = 4
          ssaoScale = 0.5
          ssaoSteps = 4
          streamingPoolMegabytes = 1024
          surfaceCacheSize = 1024
          taaVarianceClipping = False
          terrainLodNearRange = 48
          terrainStreamingMegabytes = 32
          traceScale = 0.5
          vignette = False
          virtualFirstExtent = 10
          virtualGeometry = False
          virtualLevels = 8
          virtualPagesPerFrame = 8
          volumetricFar = 48
          volumetricFog = False
          volumetricShadows = False
          volumetricSlices = 32
        Medium
          bloom = True
          bloomFilterRadius = 1
          bloomLevels = 4
          cascadeCount = 4
          cascadeResolution = 1024
          constantBias = 0.0015
          culling = Indirect
          depthOfField = False
          dfaoSamples = 12
          dfaoScale = 0.5
          dilationPasses = 1
          dofMaximumRadius = 24
          dofSamples = 12
          fog = True
          foliageCellBudget = 192
          foliageCullDistanceScale = 0.85
          foliageDensityScale = 0.8
          fxaa = Balanced
          grassCullDistanceScale = 0.8
          grassDensityScale = 0.75
          grassResidentCells = 192
          irradianceBudget = 4
          lensFlare = False
          localExposure = False
          localExposureTaps = 6
          lodBias = 0
          maxLights = 128
          maxLightsPerObject = 8
          mipBias = 0
          motionBlur = False
          motionBlurSamples = 6
          particleBudgetScale = 0.75
          probeTileSize = 16
          punctualResolution = 256
          punctualTilesPerSide = 6
          reflectionSteps = 24
          renderScale = 1
          roughnessThreshold = 0.5
          screenTraces = False
          shadowDistance = 120
          slopeBias = 0.004
          splitLambda = 0.75
          ssaoDirections = 6
          ssaoScale = 0.5
          ssaoSteps = 4
          streamingPoolMegabytes = 2048
          surfaceCacheSize = 2048
          taaVarianceClipping = True
          terrainLodNearRange = 64
          terrainStreamingMegabytes = 48
          traceScale = 0.5
          vignette = False
          virtualFirstExtent = 10
          virtualGeometry = True
          virtualLevels = 8
          virtualPagesPerFrame = 12
          volumetricFar = 48
          volumetricFog = False
          volumetricShadows = False
          volumetricSlices = 32
        High
          bloom = True
          bloomFilterRadius = 1
          bloomLevels = 5
          cascadeCount = 4
          cascadeResolution = 2048
          constantBias = 0.0015
          culling = Indirect
          depthOfField = True
          dfaoSamples = 16
          dfaoScale = 0.5
          dilationPasses = 1
          dofMaximumRadius = 24
          dofSamples = 16
          fog = True
          foliageCellBudget = 256
          foliageCullDistanceScale = 1
          foliageDensityScale = 1
          fxaa = Balanced
          grassCullDistanceScale = 1
          grassDensityScale = 1
          grassResidentCells = 256
          irradianceBudget = 8
          lensFlare = True
          localExposure = True
          localExposureTaps = 6
          lodBias = 0
          maxLights = 256
          maxLightsPerObject = 8
          mipBias = 0
          motionBlur = True
          motionBlurSamples = 8
          particleBudgetScale = 1
          probeTileSize = 16
          punctualResolution = 256
          punctualTilesPerSide = 8
          reflectionSteps = 32
          renderScale = 1
          roughnessThreshold = 0.5
          screenTraces = True
          shadowDistance = 150
          slopeBias = 0.004
          splitLambda = 0.75
          ssaoDirections = 8
          ssaoScale = 0.5
          ssaoSteps = 6
          streamingPoolMegabytes = 3072
          surfaceCacheSize = 4096
          taaVarianceClipping = True
          terrainLodNearRange = 64
          terrainStreamingMegabytes = 64
          traceScale = 1
          vignette = True
          virtualFirstExtent = 10
          virtualGeometry = True
          virtualLevels = 8
          virtualPagesPerFrame = 16
          volumetricFar = 64
          volumetricFog = True
          volumetricShadows = True
          volumetricSlices = 64
        Epic
          bloom = True
          bloomFilterRadius = 1
          bloomLevels = 6
          cascadeCount = 4
          cascadeResolution = 2048
          constantBias = 0.0015
          culling = Indirect
          depthOfField = True
          dfaoSamples = 24
          dfaoScale = 1
          dilationPasses = 1
          dofMaximumRadius = 24
          dofSamples = 24
          fog = True
          foliageCellBudget = 384
          foliageCullDistanceScale = 1.25
          foliageDensityScale = 1
          fxaa = Quality
          grassCullDistanceScale = 1.25
          grassDensityScale = 1
          grassResidentCells = 384
          irradianceBudget = 16
          lensFlare = True
          localExposure = True
          localExposureTaps = 12
          lodBias = 0
          maxLights = 512
          maxLightsPerObject = 8
          mipBias = 0
          motionBlur = True
          motionBlurSamples = 12
          particleBudgetScale = 1
          probeTileSize = 8
          punctualResolution = 512
          punctualTilesPerSide = 8
          reflectionSteps = 64
          renderScale = 1
          roughnessThreshold = 0.5
          screenTraces = True
          shadowDistance = 200
          slopeBias = 0.004
          splitLambda = 0.75
          ssaoDirections = 12
          ssaoScale = 1
          ssaoSteps = 8
          streamingPoolMegabytes = 4096
          surfaceCacheSize = 8192
          taaVarianceClipping = True
          terrainLodNearRange = 96
          terrainStreamingMegabytes = 128
          traceScale = 1
          vignette = True
          virtualFirstExtent = 10
          virtualGeometry = True
          virtualLevels = 8
          virtualPagesPerFrame = 32
          volumetricFar = 96
          volumetricFog = True
          volumetricShadows = True
          volumetricSlices = 128

        """;

    [Fact]
    public void The_engine_quality_table_is_what_it_says_it_is() {
        var actual = Sheet();

        // Whole-sheet rather than field by field: xunit's string comparison names the first line
        // that differs, which is the tier and the field and both values in one message — and a loop
        // of sixty assertions would stop at the first one and hide the rest of a retune.
        Assert.Equal(Expected.ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
    }

    /// <summary>
    ///     Every tier's resolved numbers, in a stable order.
    /// </summary>
    /// <remarks>
    ///     Sorted by name and not by declaration order: a field moved within
    ///     <see cref="ResolvedQuality" /> is not a change to the table, and a snapshot that failed
    ///     for it would train its readers to update it without looking.
    /// </remarks>
    static string Sheet() {
        var properties = typeof(ResolvedQuality)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();

        var text = new StringBuilder();

        foreach (var tier in (QualityTier[])[QualityTier.Low, QualityTier.Medium, QualityTier.High, QualityTier.Epic]) {
            var resolved = RenderQuality.Resolve(tier);

            text.Append(tier).Append('\n');

            foreach (var property in properties) {
                var value = property.GetValue(resolved);

                text.Append("  ")
                    .Append(char.ToLowerInvariant(property.Name[0]))
                    .Append(property.Name.AsSpan(1))
                    .Append(" = ")
                    // Invariant, because a machine whose culture writes 0,5 would fail this and
                    // nothing else, which is a morning nobody should spend.
                    .Append(Convert.ToString(value, CultureInfo.InvariantCulture))
                    .Append('\n');
            }
        }

        return text.ToString();
    }
}
