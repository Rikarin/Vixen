// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>
///     The two C# ports of the conic sweep compute the number the shader computes, to the last bit.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both ports divided by <see cref="MathF.Tau" /> where both shaders multiply by the
///         rounded reciprocal, and nothing could see it</b> —
///         <see href="https://github.com/Rikarin/Vixen/issues/1256">#1256</see>. The arithmetic
///         census in <c>SharedUiShaderTests</c> walks SPIR-V modules, so a C# port is outside it by
///         construction; <c>UiCompositingTests</c> compares the device with the software rasteriser
///         at a tolerance a last-place bit of a ramp never reaches. So the two ports agreed with each
///         other, said so in a comment, and were both one bit from the module that ships.
///     </para>
///     <para>
///         <b>The oracle is the shader's own line, read out of <c>Ui.rvn</c> rather than written here
///         again.</b> A test that re-derives the constant is a third copy that can drift with the
///         other two; this one takes it from the file the applications draw with, and asserts the
///         line was found — in both <c>UiBox.Parameter</c> and <c>UiMask.Progress</c>, which is what
///         says the extraction is reading the right thing.
///     </para>
///     <para>
///         ⚠ <b>And the instrument is checked: the division and the multiplication must disagree
///         somewhere in the sweep, or the test could not be false.</b> They differ on a measured
///         fraction of angles — the count is asserted as a floor rather than reported — and the
///         sabotage (putting <c>/ MathF.Tau</c> back in either port) is red on exactly those.
///     </para>
/// </remarks>
public class ConicSweepTests {
    /// <summary>Half the box the sweep is evaluated over. Any size; the angle is what matters.</summary>
    static readonly Vector2 Half = new(40f, 24f);

    /// <summary>How many angles the sweep visits.</summary>
    const int Steps = 3600;

    /// <summary>Offsets from the centre, ten per degree and a little off the axes, so no angle is exact.</summary>
    static IEnumerable<Vector2> Offsets() {
        for (var step = 0; step < Steps; step++) {
            var radians = (step + 0.37f) * MathF.PI / (Steps / 2f);

            yield return new Vector2(MathF.Sin(radians) * 31.7f, -MathF.Cos(radians) * 19.3f);
        }
    }

    /// <summary>The reciprocal literal on the shader's conic line, as a float.</summary>
    static float ShaderReciprocal() {
        var path = Path.Combine(RepositoryRoot(), "Platform", "Vixen.Ui.Desktop", "Shaders", "Ui.rvn");

        Assert.True(File.Exists(path), $"{path} is missing, and it is the module the applications draw with.");

        var matches = Regex.Matches(File.ReadAllText(path), @"frac\(angle \* (0\.\d+)f \+ 1f\)");

        // Once in `UiBox.Parameter` and once in `UiMask.Progress`. Fewer means the line moved and
        // this is reading nothing; more means a third copy appeared and should be checked too.
        Assert.Equal(2, matches.Count);

        var literals = matches.Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();

        Assert.Single(literals);

        return float.Parse(literals[0], CultureInfo.InvariantCulture);
    }

    /// <summary>What the shader's line evaluates to for one angle, spelled exactly as it spells it.</summary>
    static float ShaderTurns(float angle, float reciprocal) {
        var turns = (angle * reciprocal) + 1f;

        return turns - MathF.Floor(turns);
    }

    [Fact]
    public void The_mask_sweeps_the_shaders_number_and_not_a_quotient_that_ought_to_agree() {
        var reciprocal = ShaderReciprocal();
        var axis = new Vector2(0f, -1f);

        // From zero to one across the sweep, so the coverage *is* the progress.
        var mask = new UiMask(
            Vector2.Zero,
            Half,
            axis,
            new Vector3(0f, 0f, 1f),
            new GradientStops(0f, 0.5f, 1f),
            GradientShape.Conic,
            Via: false
        );

        var disagreements = 0;

        foreach (var offset in Offsets()) {
            var angle = MathF.Atan2(offset.X, -offset.Y) - MathF.Atan2(axis.X, -axis.Y);
            var expected = ShaderTurns(angle, reciprocal);
            var quotient = (angle / MathF.Tau) + 1f;

            if (quotient - MathF.Floor(quotient) != expected) {
                disagreements++;
            }

            Assert.Equal(expected, mask.Coverage(offset));
        }

        // The instrument: measured at 162 of 3600 on 2026-09-22 (and 9 of 360 before the sweep was
        // widened), so the floor is well under it. If the division and the multiplication agreed
        // everywhere this sweep could not go red, whatever the port did.
        Assert.True(disagreements > 40, $"the quotient and the product disagreed on only {disagreements} of {Steps} angles.");
    }

    [Fact]
    public void The_rasteriser_sweeps_the_shaders_number_and_not_a_quotient_that_ought_to_agree() {
        var reciprocal = ShaderReciprocal();
        var axis = new Vector2(0.6f, -0.8f);

        var shape = new UiShape(
            Half,
            0f,
            default,
            GradientShape.Conic,
            GradientSpace.Linear,
            axis,
            new Color4(1f, 1f, 1f, 1f),
            default,
            hasVia: false,
            GradientStops.Default
        );

        var disagreements = 0;

        foreach (var offset in Offsets()) {
            var angle = MathF.Atan2(offset.X, -offset.Y) - MathF.Atan2(axis.X, -axis.Y);
            var expected = ShaderTurns(angle, reciprocal);
            var quotient = (angle / MathF.Tau) + 1f;

            if (quotient - MathF.Floor(quotient) != expected) {
                disagreements++;
            }

            Assert.Equal(expected, SoftwareUiRasterizer.Parameter(shape, offset, Half));
        }

        // Measured at 172 of 3600 on 2026-09-22 with this axis.
        Assert.True(disagreements > 40, $"the quotient and the product disagreed on only {disagreements} of {Steps} angles.");
    }

    static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
