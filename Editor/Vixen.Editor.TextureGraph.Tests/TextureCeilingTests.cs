// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     The kernel ceilings table against the <c>.rvn</c> files it transcribes, in both directions.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/1011">#1011</a>, and the reason it
///         needed a sweep rather than three more lines.</b>
///         <a href="https://github.com/Rikarin/Vixen/issues/678">#678</a> built the walk that reports
///         a plan whose resolved length is past a kernel's own loop bound, and built its table on
///         <c>TextureFilters</c> — where it covered the Filters family and nothing else. Three
///         kernels outside that family clip exactly the same way and had no entry:
///         <c>Curvature/radius</c>, <c>HeightToNormal/width</c> and <c>EdgeDetect/width</c>, all
///         three <c>clamp(int(max(p, 1f)), 1, 256)</c> applied to the number as it arrives. So doc 48
///         § D8's invariant — the same graph is the same material at any bake — was broken in three
///         kernels with nothing anywhere to say so.
///     </para>
///     <para>
///         ⚠ <b>A table checked only by the plans that happen to exercise it goes stale silently.</b>
///         The old table's own doc comment said "the five entries below" over four entries, which
///         reads as a typo and was not: the sweep below finds seven, so at least one entry really had
///         been dropped in review. Nothing could have noticed — every existing assertion about the
///         ceilings drives off the table itself, so a missing line makes them assert less rather than
///         fail. This file reads the <b>kernel sources</b>, which is where the number the loop stops
///         at is actually written.
///     </para>
///     <para>
///         <b>What counts as clipped, precisely.</b> A <c>float</c> parameter that appears inside a
///         <c>clamp(…)</c> whose bound is one of the kernel's own <c>const val … : int</c>
///         constants. That is the shape all seven have and it is what makes the sweep able to tell
///         them from the counts — <c>samples</c>, <c>octaves</c>, <c>count</c> are clamped the same
///         way and are <c>int</c>, because a count is not a length and <c>TexturePlan.Resolve</c>
///         never scales one.
///     </para>
///     <para>
///         ⚠ <b>And it is what lets <c>Blur</c> stay out without an exemption.</b>
///         <c>Blur.rvn</c>'s <c>MaxTaps</c> is used as <c>max(1f, wide / float(MaxTaps))</c> — a
///         divisor that spreads the taps — and never as a <c>clamp</c> bound on the radius, so the
///         sweep does not see it and nobody has to remember to excuse it. An exemption list here
///         would be a list whose entries outlive their reasons.
///     </para>
/// </remarks>
public class TextureCeilingTests {
    /// <summary>A <c>float</c> parameter's declaration, which is what separates a length from a count.</summary>
    static readonly Regex Parameter = new(@"^\s*var\s+(\w+)\s*:\s*float\b", RegexOptions.Multiline);

    /// <summary>A kernel's own loop bound.</summary>
    static readonly Regex Constant = new(@"^\s*const val\s+(\w+)\s*:\s*int\s*=\s*(\d+)", RegexOptions.Multiline);

    /// <summary>One <c>clamp(…)</c>, up to the end of its line.</summary>
    static readonly Regex Clamp = new(@"clamp\(([^\r\n]*)\)");

    /// <summary>Every parameter some kernel clamps against a constant of its own, with that constant.</summary>
    /// <remarks>
    ///     ⚠ <b>Comment lines are dropped before the sweep, and that is load-bearing rather than
    ///     tidy.</b> Every one of these kernels explains its ceiling in prose directly above it —
    ///     <c>Sharpen.rvn</c>'s header quotes its own <c>clamp</c> — so a sweep over the raw text
    ///     would find pairs that no code performs and would then demand table entries for them. The
    ///     failure would be a demand to write down a ceiling that does not exist, which is a worse
    ///     sentence than the one this file is for.
    /// </remarks>
    public static IEnumerable<((string Kernel, string Parameter) Key, int Ceiling)> Clipped() {
        foreach (var kernel in TextureKernels.Names) {
            var source = string.Join(
                '\n',
                TextureKernels.Source(kernel)
                    .Split('\n')
                    .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            );

            var parameters = Parameter.Matches(source).Select(match => match.Groups[1].Value).ToArray();
            var constants = Constant.Matches(source)
                .ToDictionary(match => match.Groups[1].Value, match => int.Parse(match.Groups[2].Value, null));

            foreach (Match clamp in Clamp.Matches(source)) {
                var arguments = clamp.Groups[1].Value;

                foreach (var (name, ceiling) in constants) {
                    if (!Regex.IsMatch(arguments, $@"\b{Regex.Escape(name)}\b")) {
                        continue;
                    }

                    foreach (var parameter in parameters) {
                        if (Regex.IsMatch(arguments, $@"\b{Regex.Escape(parameter)}\b")) {
                            yield return ((kernel, parameter), ceiling);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Every <c>float</c> parameter a kernel clamps against its own loop bound has a line in the
    ///     ceilings table, and every line in the table is one of those.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both directions, because they fail differently.</b> A clamped parameter with no
    ///         entry is #1011 itself: the bake silently produces a different material and nothing
    ///         reports it. An entry naming a parameter nothing clamps any more is a line that will go
    ///         on refusing plans a kernel would have run perfectly — a false refusal an artist cannot
    ///         argue with, and the reason the table can only be as long as the kernels are.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Ask what this prints on the day the sweep matches nothing.</b> It would find no
    ///         clipped parameters, the table would be non-empty, and the equality would fail naming
    ///         all seven as unclipped — which is the right failure and not a quiet pass.
    ///         <see cref="Assert.NotEmpty{T}(IEnumerable{T})" /> below says so directly anyway, so a
    ///         table emptied at the same time as the regexes broke cannot read as agreement.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_clamped_texels_at_base_parameter_has_a_ceiling() {
        var clipped = Clipped().Select(entry => entry.Key).Distinct().Order().ToArray();

        Assert.NotEmpty(clipped);

        var listed = TextureKernelCeilings.Ceilings.Keys.Order().ToArray();

        Assert.Equal(clipped, listed);
    }

    /// <summary>No ceiling in the table is above the constant the kernel actually stops at.</summary>
    /// <remarks>
    ///     ⚠ <b>At most, rather than equal, and <c>BlurHq</c> is why.</b> That kernel reaches
    ///     <c>ceil(sigma · 3)</c> texels, so the sigma that first touches its <c>MaxRadius</c> of 64
    ///     is 64⁄3 — a derived number, and the one line in the table that is arithmetic rather than a
    ///     transcription. What must never happen is a table value <em>above</em> the constant: the
    ///     walk would then stay silent about exactly the plans it exists to report, which is the
    ///     failure mode where an instrument reports success on the day it stops working.
    /// </remarks>
    [Fact]
    public void No_ceiling_is_above_the_constant_its_kernel_loops_to() {
        foreach (var (key, ceiling) in Clipped().Distinct()) {
            // ⚠ A missing entry is the other test's failure and not this one's. Indexing would make
            // one dropped line fail both with a KeyNotFoundException, which says nothing about which
            // of the two properties broke — the shape this file exists to avoid.
            if (!TextureKernelCeilings.Ceilings.TryGetValue(key, out var listed)) {
                continue;
            }

            Assert.True(
                listed <= ceiling,
                $"{key.Kernel}/{key.Parameter} is listed at {listed} and the kernel loops to {ceiling}, so a "
                + "plan between the two would be clamped with nothing said."
            );
        }
    }

    /// <summary>
    ///     ⚠ A curvature radius that fits at the resolution it was authored for runs off the kernel's
    ///     loop at a four-times bake — and until #1011 nothing said so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The instrument first: the same plan at its own bake is reported clean.</b> A walk
    ///         that reported every op, or one that matched no kernel name at all, would be
    ///         indistinguishable from a working one in the second half alone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>100 is inside the kernel's 256 and 400 is not</b>, which is the whole of § D8's
    ///         complaint: the artist changed the bake resolution and the kernel quietly gave back a
    ///         256-texel central difference. The picture stays plausible — a curvature is a smooth
    ///         grey either way — so nothing downstream and nobody looking at the result would say so.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_curvature_radius_that_fits_at_one_bake_is_reported_at_four_times_it() {
        Assert.Empty(TextureKernelCeilings.Verify(Plan(0, TextureSurfaces.Curvature(1, 0, radius: 100f))));

        var problems = TextureKernelCeilings.Verify(Plan(-2, TextureSurfaces.Curvature(1, 0, radius: 100f)));

        var line = Assert.Single(problems);

        Assert.Contains("Curvature", line, StringComparison.Ordinal);
        Assert.Contains("400", line, StringComparison.Ordinal);
    }

    /// <summary>The same, for the other two kernels the Filters-family table could not see.</summary>
    /// <remarks>
    ///     ⚠ <b>Written as one plan rather than a theory per kernel.</b> A theory with an
    ///     <c>InlineData</c> per kernel is the shape that passes silently when an eighth clipped
    ///     parameter appears and nobody adds a case;
    ///     <see cref="Every_clamped_texels_at_base_parameter_has_a_ceiling" /> is what makes an eighth
    ///     fail by existing, and this only has to show the walk reaches outside one family.
    /// </remarks>
    [Fact]
    public void The_surface_and_analysis_kernels_are_reported_too() {
        var plan = Plan(
            -2,
            TextureSurfaces.HeightToNormal(1, 0, width: 100f),
            TextureAnalysis.EdgeDetect(2, 1, width: 100f)
        );

        var problems = TextureKernelCeilings.Verify(plan);

        Assert.Equal(2, problems.Length);
        Assert.Contains(problems, line => line.Contains("HeightToNormal", StringComparison.Ordinal));
        Assert.Contains(problems, line => line.Contains("EdgeDetect", StringComparison.Ordinal));
    }

    /// <summary>The name the plan and the filter suites still call, answering out of the one table.</summary>
    /// <remarks>
    ///     ⚠ <b>The forwarder is the seam #1011 could most easily have broken.</b>
    ///     <c>TexturePlan.Validate</c> calls <c>TextureFilters.Verify</c>, so a move that left that
    ///     name walking an emptied table would have made every ceiling stop being reported while every
    ///     existing test — all of which drive off the table — went on passing.
    /// </remarks>
    [Fact]
    public void The_filters_entry_point_answers_out_of_the_one_table() {
        var plan = Plan(-2, TextureSurfaces.Curvature(1, 0, radius: 100f));

        Assert.Equal(TextureKernelCeilings.Verify(plan), TextureFilters.Verify(plan));
        Assert.NotEmpty(TextureFilters.Verify(plan));
    }

    static TexturePlan Plan(int bake, params TextureOp[] ops) =>
        new() {
            BaseWidth = 1024,
            BaseHeight = 1024,
            BakeLevelOffset = bake,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float)
            ],
            Ops = [.. ops],
            Outputs = [1]
        };
}
