// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Vixen.Editor.TextureGraph;

/// <summary>
///     Every kernel loop bound a resolved <see cref="TextureParameterUnit.TexelsAtBase" /> parameter
///     can be clamped by, and the walk that reports a plan past one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One table for the whole assembly, and moving it here is the whole of
///         <a href="https://github.com/Rikarin/Vixen/issues/1011">#1011</a>.</b> It used to sit on
///         <c>TextureFilters</c> because <a href="https://github.com/Rikarin/Vixen/issues/678">#678</a>'s
///         batch did not own <c>TexturePlan.cs</c> — and the consequence was not a tidiness problem
///         but a hole: a kernel ceiling is not a property of the Filters family, so
///         <c>Surface/Curvature</c> and <c>Surface/Height to Normal</c> and
///         <c>Analysis/Edge Detect</c> clipped exactly the same way with nothing to report it. Three
///         of the seven lines below were missing, which is doc 48 § D8's invariant broken in three
///         kernels at once.
///     </para>
///     <para>
///         ⚠ <b>The count in the old comment was right and the table was wrong.</b> That doc comment
///         said "the five entries below are the kernels that do clip" over four entries — the sort of
///         off-by-one that reads as a typo. It was not: a sweep of the <c>.rvn</c> files for a
///         <c>const val Max…</c> applied to a parameter a builder declares <c>TexelsAtBase</c> finds
///         <em>seven</em>, so at least one entry really had been dropped in review and the missing
///         ones were live instances of the bug rather than a miscount.
///     </para>
///     <para>
///         <b>What keeps it whole is a sweep rather than a reviewer.</b>
///         <c>TextureCeilingTests.Every_clamped_texels_at_base_parameter_has_a_ceiling</c> reads the
///         embedded kernel sources — which are the source of truth, because the number the loop stops
///         at is written there — and requires a line here for every parameter a kernel clamps against
///         one of its own constants. A table checked only by the plans that happen to exercise it is a
///         table that goes stale the next time a kernel gains a loop bound.
///     </para>
///     <para>
///         ⚠ <b><c>Blur</c> is deliberately not in this table, and that is the more interesting half
///         of #678's answer.</b> <c>Blur.rvn</c>'s <c>MaxTaps</c> is a budget on the number of
///         <em>taps</em> rather than a ceiling on the width: past it the same width is covered by the
///         same number of taps spaced further apart, so the box thins rather than narrowing and the
///         width the plan resolved is always the width the picture has. There is nothing to report
///         because nothing is clipped — and a future slice that gives one of the seven below a tap
///         budget takes its line out of here rather than raising the number.
///     </para>
/// </remarks>
internal static class TextureKernelCeilings {
    /// <summary>
    ///     What each looping kernel's own ceiling is, in the texels of the image it writes.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>These are the constants in the <c>.rvn</c> files, and a plan past one of them is
    ///     silently clamped by the kernel.</b> The ceilings exist for a reason no artist can see — a
    ///     radius arriving as a NaN is a loop no invocation leaves, which on a GPU is a device loss
    ///     rather than a slow bake — but a silent clamp is #678: a graph that fits at the resolution
    ///     it was authored at stops being the same material at four times the size, because
    ///     <c>TexturePlan.Resolve</c> has multiplied the radius by four and the kernel has quietly
    ///     put it back.
    ///     <para>
    ///         ⚠ <b><c>BlurHq</c>'s entry is a sigma against a radius ceiling and is the one line here
    ///         that is arithmetic rather than a transcription.</b> The kernel reaches
    ///         <c>ceil(sigma · Tail)</c> texels with <c>Tail = 3</c>, so the sigma that first touches
    ///         <c>MaxRadius</c> is a third of it. Reading 64 off the constant would report nothing
    ///         until three times the sigma that actually clips.
    ///     </para>
    /// </remarks>
    public static ImmutableDictionary<(string Kernel, string Parameter), float> Ceilings { get; } =
        new Dictionary<(string, string), float> {
            [(TextureFilters.BlurHq, "sigma")] = 64f / 3f,
            [(TextureFilters.DirectionalBlur, "length")] = 64f,
            [(TextureFilters.NonUniformBlur, "maxRadius")] = 12f,
            [(TextureFilters.Sharpen, "radius")] = 8f,

            [(TextureSurfaceKernels.Curvature, "radius")] = 256f,

            // ⚠ The three #1011 added. Each is `clamp(int(max(p, 1f)), 1, Max…)` applied to the
            // number as it arrives — so an authored 64 clips at a 4× bake and an authored 128 at a
            // 2× one. 256 is large enough that no plan in the tree reaches it today, which is
            // exactly why this was a fuse rather than a fire and why nothing found it.
            [(TextureSurfaceKernels.HeightToNormal, "width")] = 256f,
            [(TextureAnalysisKernels.EdgeDetect, "width")] = 256f
        }.ToImmutableDictionary();

    /// <summary>
    ///     Every op in a plan whose resolved length is past the kernel's own loop ceiling.
    /// </summary>
    /// <param name="plan">The plan to walk.</param>
    /// <returns>One line per offending op, empty when there is nothing to say.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>#678, and the reason it needs a walk of its own rather than a line in
    ///         <c>TexturePlan.Validate</c>.</b> A kernel that clamps its radius to a constant breaks
    ///         doc 48 § D8's invariant at a large bake: 20 texels at a 1K base is 80 at a 4K bake, and
    ///         a ceiling of 64 quietly gives back a 64-texel blur — the same graph, a different
    ///         material, and no message anywhere. The number that has to be checked is therefore the
    ///         <em>resolved</em> one, which depends on <see cref="TexturePlan.BakeLevelOffset" /> and
    ///         on the image the op writes. Neither the plan nor the kernel knows both halves; this
    ///         does.
    ///     </para>
    ///     <para>
    ///         <b>It reports rather than throws</b>, matching <c>TexturePlan.Validate</c>'s shape, so
    ///         a caller can put the lines in front of an artist beside the resolution they chose —
    ///         which is the decision that actually caused it.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<string> Verify(TexturePlan plan) {
        ArgumentNullException.ThrowIfNull(plan);

        var problems = ImmutableArray.CreateBuilder<string>();

        for (var index = 0; index < plan.Ops.Length; index++) {
            var op = plan.Ops[index];

            foreach (var ((kernel, parameter), ceiling) in Ceilings) {
                if (!string.Equals(op.Kernel, kernel, StringComparison.Ordinal)) {
                    continue;
                }

                if (op.Find(parameter) is not { } authored) {
                    continue;
                }

                var resolved = plan.Resolve(index, authored);

                if (resolved <= ceiling) {
                    continue;
                }

                problems.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Op {index} runs '{kernel}' with {parameter} {authored.Value}, which is {resolved} at the "
                        + $"resolution it writes — past the {ceiling} the kernel loops to. It would be clamped, "
                        + $"silently, so the graph is a different material at this bake than at the one it was "
                        + $"authored for. Lower the {parameter} or bake smaller."
                    )
                );
            }
        }

        return problems.ToImmutable();
    }
}
