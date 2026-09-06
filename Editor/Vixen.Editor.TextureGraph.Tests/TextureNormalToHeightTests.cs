// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § 4.6's <c>Normal → Height</c>, with no device: the round trip, and the budget.
/// </summary>
/// <remarks>
///     <para>
///         <b>The closed form is the round trip.</b> A height field has exactly one normal map under
///         <c>HeightToNormal.rvn</c>'s stated convention, and integrating that map back has exactly
///         one answer <em>up to an additive constant</em> — which the operation fixes by making the
///         mean zero. So the assertion is <c>height − mean(height)</c>, texel for texel, and it is
///         not a tolerance on a picture.
///     </para>
///     <para>
///         ⚠ <b>The normal maps here are built from the convention as <em>stated</em>, not from the
///         operation's own arithmetic.</b> <c>Encode</c> below is
///         <c>normalize(−∂h/∂u · intensity, −∂h/∂v · intensity, 1) · ½ + ½</c> written out from the
///         comment at the top of <c>HeightToNormal.rvn</c>, in the forward direction, from an
///         analytic gradient. The operation goes the other way, through a linear solve. Two
///         transcriptions of one formula would agree on everything including a flipped sign; a
///         forward formula and a solver that inverts it do not, which is what makes
///         <see cref="A_flipped_axis_does_not_survive_the_round_trip" /> possible to state at all.
///     </para>
///     <para>
///         ⚠ <b>Ask what this file prints on the day the operation stops running.</b> Every
///         assertion here is a comparison against a field with a real slope in it, so an
///         implementation that filled the output with zeros — which is what
///         <see cref="ITextureCpuOperation.Run" /> warns is the default failure — fails every one of
///         them by the whole amplitude rather than by a tolerance.
///         <see cref="A_budget_of_nothing_leaves_the_answer_flat" /> is the case that pins the
///         opposite end down: zeros are the <em>right</em> answer for a budget of zero, and it is the
///         only place they are.
///     </para>
/// </remarks>
public class TextureNormalToHeightTests {
    /// <summary>The grid the closed-form cases run on.</summary>
    /// <remarks>
    ///     Small on purpose. A conjugate gradient reaches the exact answer in at most one step per
    ///     unknown, so a grid this size can be solved to the last bit inside a budget a test can
    ///     afford — which is what lets the round trip be asserted as an equality with a tolerance set
    ///     by <c>R16Float</c> rather than by the solver.
    /// </remarks>
    const int Side = 24;

    /// <summary>
    ///     ⚠ Half carries about three decimal digits, so this is the format's tolerance and not the
    ///     solve's.
    /// </summary>
    /// <remarks>
    ///     The heights here run to about ±0.35, where the gap between two <c>R16Float</c> values is
    ///     roughly 2.4e-4. Ten of those is a tolerance that a converged solve passes and that no
    ///     wrong answer below gets anywhere near — the nearest miss, a halved amplitude, is out by
    ///     0.17.
    /// </remarks>
    const double Precision = 2.5e-3;

    /// <summary>What a curved field's round trip is allowed to be out by, and why it is not the above.</summary>
    /// <remarks>
    ///     ⚠ <b>This one is a discretisation error and not a solver's, which is why it has a name of
    ///     its own rather than being folded into a wider <see cref="Precision" />.</b> A normal map
    ///     records one slope per texel; the operation fits differences between neighbours, and the
    ///     two are the same number only where the slope is constant. On a curved field the map has
    ///     already averaged away a little of the curvature, and no budget puts it back — so the
    ///     residual is second order in the texel size, which
    ///     <see cref="A_curved_height_field_integrates_back_to_itself" /> demonstrates by refining
    ///     the grid rather than by widening this number. Measured on the fixture field at 24×24:
    ///     0.0158, and 0.0043 at 48×48.
    /// </remarks>
    const double Curved = 2e-2;

    public static TheoryData<double, double> Planes =>
        new() { { 0.7, 0d }, { 0d, 0.7 }, { 0.5, -0.4 }, { -0.6, -0.3 } };

    /// <summary>A plane's normal map integrates back to the plane, exactly.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one input whose answer needs no argument about discretisation.</b> A plane's
    ///         slope is the same at every texel, so the difference between two neighbours is the same
    ///         at every edge, and the least-squares system the operation assembles has an exact
    ///         solution rather than a best fit. Whatever the solver then does is measured against a
    ///         number that is right by construction.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both axes, and both signs of each.</b> A single plane tilted one way passes under
    ///         a transposed grid, a flipped green and a swapped pair of gradients alike — the four
    ///         cases here are what separate them, and the diagonal ones are what separate a
    ///         transpose from an axis flip.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Planes))]
    public void A_planes_normal_map_integrates_back_to_the_plane(double slopeU, double slopeV) {
        var height = Plane(Side, Side, slopeU, slopeV);
        var solved = RoundTrip(height, Side, Side, iterations: 1024);

        AssertMatches(Centred(height), solved, Precision);
    }

    /// <summary>A curved height field survives the round trip, and what is left over is the grid.</summary>
    /// <remarks>
    ///     ⚠ <b>The plane case cannot see an operation that integrated a constant.</b> A field whose
    ///     slope is the same everywhere is recovered by anything that gets the average slope right,
    ///     including an implementation that ignored every texel but one; this one's slope changes
    ///     sign twice along each axis, so every texel of the normal map has to be read and placed.
    ///     It is also the case where the discrete gradient and the analytic one differ, which is why
    ///     the normals here are built from the field's own neighbours rather than from calculus — the
    ///     operation is being asked to invert a discrete difference, and inverting a continuous one
    ///     is a different and slightly wrong question.
    /// </remarks>
    [Fact]
    public void A_curved_height_field_integrates_back_to_itself() {
        var coarse = Error(Centred(Bumps(Side, Side)), RoundTrip(Bumps(Side, Side), Side, Side, 2048, discrete: true));

        Assert.True(coarse < Curved, $"the round trip was out by {coarse}");

        // ⚠ And the remaining disagreement is the *discretisation* rather than the solve, which is
        // the claim a tolerance on its own cannot make. A centred difference is the average of two
        // one-texel slopes and is therefore already a mild blur of the gradient; a blur is not
        // invertible, so no budget removes what it took out. What identifies it is its *order*:
        // sampling the same continuous field twice as finely measured 0.0158 then 0.0043, a factor of
        // 3.7 for a halved texel, which is the second-order behaviour a discretisation has and is
        // nothing a wrong sign, a transposed index or a mis-scaled decode would show — those do not
        // shrink with the grid at all.
        var fine = Error(
            Centred(Bumps(Side * 2, Side * 2)),
            RoundTrip(Bumps(Side * 2, Side * 2), Side * 2, Side * 2, 2048, discrete: true)
        );

        Assert.True(fine < coarse / 3d, $"refining the grid did not cut the error: {coarse} then {fine}");
    }

    /// <summary>
    ///     ⚠ A flipped axis in the map is a different height field, so the round trip cannot be
    ///     accidentally symmetric.
    /// </summary>
    /// <remarks>
    ///     <b>What this closes is the assertion that passes under a sign error.</b> The green channel
    ///     is where a normal-map convention goes wrong and it is famously invisible — a lit surface
    ///     looks plausible either way. Here the two answers differ by twice the field, everywhere,
    ///     which is a number rather than an opinion; and it makes the equality above a claim about
    ///     the convention and not merely about the magnitude.
    /// </remarks>
    [Fact]
    public void A_flipped_axis_does_not_survive_the_round_trip() {
        var height = Plane(Side, Side, 0d, 0.7);
        var normals = Encode(height, Side, Side, intensity: 1f, discrete: false);

        for (var at = 0; at < Side * Side; at++) {
            var green = (double)BitConverter.ToHalf(normals.AsSpan((at * 8) + 2, 2));

            BitConverter.TryWriteBytes(normals.AsSpan((at * 8) + 2, 2), (Half)(1d - green));
        }

        var solved = Solve(normals, Side, Side, iterations: 1024);
        var expected = Centred(height);

        // Not merely "different": the flip negates the field, so every texel is out by twice its own
        // value and the two agree only along the line where the height is zero.
        for (var at = 0; at < expected.Length; at++) {
            Assert.Equal(-expected[at], solved[at], Precision);
        }
    }

    /// <summary>A budget of zero leaves the answer flat, which is the only time flat is right.</summary>
    /// <remarks>
    ///     <b>The instrument for every other case in this file.</b> The solver starts from whatever
    ///     the solution array holds and this operation has nothing to warm-start from, so zero
    ///     iterations is zero everywhere — and every assertion above would also pass a
    ///     <see cref="ITextureCpuOperation.Run" /> that returned early, if the field it compared
    ///     against were flat too. This says the fields are not.
    /// </remarks>
    [Fact]
    public void A_budget_of_nothing_leaves_the_answer_flat() {
        var height = Plane(Side, Side, 0.7, 0d);

        Assert.All(RoundTrip(height, Side, Side, iterations: 0), value => Assert.Equal(0d, value, 1e-6));
        Assert.Contains(Centred(height), value => Math.Abs(value) > 0.1);
    }

    /// <summary>
    ///     ⚠ The iteration count is a budget and not a convergence test: more of it is strictly
    ///     closer, and no amount of it is exact.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 42 § D5's decision, asserted rather than described.</b> The solver has no
    ///         tolerance and cannot be given one, because a residual comparison decides differently
    ///         on different hardware and a bake is meant to be byte-identical. What that costs is
    ///         visible here: at a budget of 8 the answer is measurably wrong, at 32 less so, at 128
    ///         less again, and the sequence is strictly decreasing — which is what a spent budget
    ///         looks like and is not what a converged solve looks like, since a converged solve would
    ///         return the same number for all three.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Strictly decreasing rather than "small at the end".</b> An assertion that the
    ///         largest budget is accurate would pass against an implementation that ignored the
    ///         parameter entirely and always solved to convergence — which is precisely the change
    ///         somebody would make to "fix" a slow bake, and precisely the change § D5 forbids.
    ///     </para>
    /// </remarks>
    [Fact]
    public void More_budget_is_strictly_closer_and_none_of_it_is_a_tolerance() {
        var height = Bumps(64, 64);
        var expected = Centred(height);

        var errors = new[] { 8, 32, 128 }
            .Select(budget => Error(expected, RoundTrip(height, 64, 64, budget, discrete: true)))
            .ToArray();

        Assert.True(errors[0] > errors[1], $"8 iterations was not worse than 32: {errors[0]} against {errors[1]}");
        Assert.True(errors[1] > errors[2], $"32 iterations was not worse than 128: {errors[1]} against {errors[2]}");

        // And the cheapest budget is wrong by an amount a picture would show, so the ordering above
        // is not three ways of saying "converged".
        Assert.True(errors[0] > 10d * Precision, $"8 iterations was already exact: {errors[0]}");
    }

    /// <summary>The same budget twice is the same answer, bit for bit.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The property a fixed budget exists to buy.</b> Nothing here reads a clock, a thread
    ///         count or a residual — and if the two runs ever differ, the cause is a reduction that
    ///         was allowed to sum out of order, which is the defect doc 41 § D14 rules out and which
    ///         no assertion about accuracy would notice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Said plainly, because it is the kind of assertion this workstream is auditing:
    ///         this cannot fail against the implementation that exists.</b>
    ///         <c>NormalToHeightOperation</c> is scalar and sequential — no <c>Parallel</c>, no
    ///         <c>Vector&lt;T&gt;</c>, no task — so a pure function called twice on one input agrees
    ///         by construction, and a predicate with no false case is worse than the flake it
    ///         replaced. It is kept as a <em>tripwire</em> against the change that would introduce
    ///         one, which is precisely the change somebody makes to speed a Poisson solve up; what it
    ///         is not is evidence about today's solver. The two lines below are: a run that produced
    ///         nothing would satisfy an equality between two empty arrays, so the length is asserted
    ///         first.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_same_plan_solved_twice_is_the_same_bytes() {
        var height = Bumps(32, 32);
        var normals = Encode(height, 32, 32, intensity: 1f, discrete: true);

        var first = Bytes(normals, 32, 32, 37);
        var second = Bytes(normals, 32, 32, 37);

        Assert.Equal(32 * 32 * 2, first.Length);
        Assert.Equal(first, second);
    }

    /// <summary>
    ///     ⚠ <c>intensity</c> is undone rather than ignored, and zero is refused by returning flat.
    /// </summary>
    /// <remarks>
    ///     <b>A normal map baked at an intensity of 2 encodes twice the slope the height field had</b>,
    ///     so integrating it as though the intensity were 1 gives a height twice as tall — a
    ///     plausible picture, and the wrong one. Zero is the one value with no inverse: that map is
    ///     flat whatever the height was, so there is nothing to recover and the answer is flat rather
    ///     than an infinity per texel.
    /// </remarks>
    [Theory]
    [InlineData(2f)]
    [InlineData(0.5f)]
    public void Intensity_is_undone(float intensity) {
        var height = Plane(Side, Side, 0.6, -0.2);
        var normals = Encode(height, Side, Side, intensity, discrete: false);

        AssertMatches(Centred(height), Solve(normals, Side, Side, 1024, intensity), Precision);

        // And read at the wrong intensity it is out by exactly that factor, which is the picture a
        // node that dropped the parameter would draw.
        var wrong = Solve(normals, Side, Side, 1024);

        AssertMatches(Centred(height).Select(value => value * intensity).ToArray(), wrong, Precision * intensity);
    }

    /// <summary>An intensity of zero has no inverse, and the answer says so by being flat.</summary>
    [Fact]
    public void An_intensity_of_zero_integrates_to_nothing() {
        var height = Plane(Side, Side, 0.6, -0.2);
        var normals = Encode(height, Side, Side, intensity: 1f, discrete: false);

        Assert.All(Solve(normals, Side, Side, 1024, intensity: 0f), value => Assert.Equal(0d, value, 1e-6));
    }

    /// <summary>A picture larger than the ceiling is refused with the number in the message.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused rather than attempted, because the failure mode is not a slow bake.</b> The
    ///     system is one unknown per texel with five stored entries per row and three copies of that
    ///     alive at once; at 4096² it is gigabytes, and a process that asks for them dies without
    ///     saying why. This is checked against the plan rather than against a live allocation, so the
    ///     test costs nothing.
    /// </remarks>
    [Fact]
    public void A_picture_past_the_ceiling_is_refused_by_size() {
        var side = 4096;

        var plan = new TexturePlan {
            BaseWidth = side,
            BaseHeight = side,
            Images = [new(TextureFormat.Rgba16Float, External: true), new(TextureFormat.R16Float)],
            Ops = [TextureCpuOperations.NormalToHeight(1, 0)],
            Outputs = [1]
        };

        Assert.Empty(plan.Check());

        // Nothing is allocated for the input: the refusal is on the output's extent and happens
        // before a single texel is read, which is the point of having it.
        var invocation = new TextureCpuInvocation(
            plan,
            0,
            [new TextureCpuImage(TextureFormat.Rgba16Float, side, side, [])],
            new TextureCpuImage(TextureFormat.R16Float, side, side, [])
        );

        var failure = Assert.Throws<InvalidOperationException>(() => plan.Ops[0].Cpu!.Run(invocation));

        Assert.Contains(NormalToHeightOperation.MaxTexels.ToString(), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The plan the operation is reached through holds together on its own terms.</summary>
    /// <remarks>
    ///     The op names no <c>.rvn</c> and <see cref="TexturePlan.Check" /> has to be fine with that,
    ///     which is the half of <a href="https://github.com/Rikarin/Vixen/issues/688">#688</a>'s seam
    ///     this operation is the first production user of.
    /// </remarks>
    [Fact]
    public void The_operation_is_an_op_a_plan_accepts() {
        var plan = Plan(Side, Side, 64, 1f);

        Assert.Empty(plan.Check());
        Assert.NotNull(plan.Ops[0].Cpu);
        Assert.Equal(NormalToHeightOperation.OpKernel, plan.Ops[0].Kernel);
    }

    /// <summary>A height field that is a plane, in height units, over <c>u</c> and <c>v</c> in [0, 1].</summary>
    static double[] Plane(int width, int height, double slopeU, double slopeV) {
        var field = new double[width * height];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                field[(y * width) + x] = (slopeU * x / width) + (slopeV * y / height);
            }
        }

        return field;
    }

    /// <summary>A height field with curvature in it, and a different period on each axis.</summary>
    /// <remarks>
    ///     Different periods so that a transposed answer is a different picture; and both periods are
    ///     whole cycles across the image, so the field is smooth at every texel rather than having a
    ///     step somewhere the difference is not a slope.
    /// </remarks>
    static double[] Bumps(int width, int height) {
        var field = new double[width * height];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var u = (double)x / width;
                var v = (double)y / height;

                field[(y * width) + x] =
                    (0.25 * Math.Sin(2d * Math.PI * u)) + (0.15 * Math.Cos(4d * Math.PI * v)) +
                    (0.1 * Math.Sin(2d * Math.PI * (u + v)));
            }
        }

        return field;
    }

    /// <summary>The field with its own mean taken out, which is what the operation returns.</summary>
    static double[] Centred(double[] field) {
        var mean = field.Sum() / field.Length;

        return field.Select(value => value - mean).ToArray();
    }

    /// <summary>
    ///     A height field as the normal map <c>HeightToNormal.rvn</c>'s stated convention gives it.
    /// </summary>
    /// <param name="field">The heights.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="intensity">The kernel's <c>intensity</c>.</param>
    /// <param name="discrete">
    ///     Whether the slope is a difference between neighbours rather than the analytic derivative.
    ///     A plane's two agree; a curved field's do not, and the operation inverts the difference.
    /// </param>
    /// <returns>The texels, <c>Rgba16Float</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>Written from the comment at the top of that kernel, forwards.</b>
    ///     <c>n = normalize(−s · intensity, 1)</c> with <c>s</c> the slope per unit of UV and
    ///     <c>v</c> pointing down the image, encoded <c>n · ½ + ½</c>. Nothing here calls the
    ///     operation or shares a line with it.
    /// </remarks>
    static byte[] Encode(double[] field, int width, int height, float intensity, bool discrete) {
        var texels = new byte[width * height * 8];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var at = (y * width) + x;

                // A centred difference where the neighbours exist and a one-sided one at the border,
                // then scaled by the extent to become a slope per unit of UV rather than per texel.
                var slopeU = Difference(field, width, height, x, y, 1, 0) * width;
                var slopeV = Difference(field, width, height, x, y, 0, 1) * height;

                if (!discrete) {
                    // The analytic slope of the plane the caller built, recovered from two texels far
                    // enough apart that the difference is the derivative.
                    slopeU = (field[(y * width) + width - 1] - field[y * width]) * width / (width - 1);
                    slopeV = (field[((height - 1) * width) + x] - field[x]) * height / (height - 1);
                }

                var nx = -slopeU * intensity;
                var ny = -slopeV * intensity;
                var length = Math.Sqrt((nx * nx) + (ny * ny) + 1d);

                Write(texels, (at * 8) + 0, (nx / length * 0.5) + 0.5);
                Write(texels, (at * 8) + 2, (ny / length * 0.5) + 0.5);
                Write(texels, (at * 8) + 4, (1d / length * 0.5) + 0.5);
                Write(texels, (at * 8) + 6, 1d);
            }
        }

        return texels;
    }

    static void Write(byte[] texels, int at, double value) =>
        BitConverter.TryWriteBytes(texels.AsSpan(at, 2), (Half)value);

    /// <summary>One texel's height slope along an axis, per texel step.</summary>
    static double Difference(double[] field, int width, int height, int x, int y, int stepX, int stepY) {
        var backX = Math.Clamp(x - stepX, 0, width - 1);
        var backY = Math.Clamp(y - stepY, 0, height - 1);
        var forwardX = Math.Clamp(x + stepX, 0, width - 1);
        var forwardY = Math.Clamp(y + stepY, 0, height - 1);

        var span = Math.Abs(forwardX - backX) + Math.Abs(forwardY - backY);

        return span == 0
            ? 0d
            : (field[(forwardY * width) + forwardX] - field[(backY * width) + backX]) / span;
    }

    /// <summary>Encode a height field, integrate it back, and hand over the heights.</summary>
    static double[] RoundTrip(double[] field, int width, int height, int iterations, bool discrete = false) =>
        Solve(Encode(field, width, height, 1f, discrete), width, height, iterations);

    /// <summary>Runs the operation over an encoded normal map and decodes what it wrote.</summary>
    static double[] Solve(byte[] normals, int width, int height, int iterations, float intensity = 1f) {
        var raw = Bytes(normals, width, height, iterations, intensity);
        var heights = new double[width * height];

        for (var at = 0; at < heights.Length; at++) {
            heights[at] = (double)BitConverter.ToHalf(raw.AsSpan(at * 2, 2));
        }

        return heights;
    }

    /// <summary>Runs the operation and hands back the output image's raw texels.</summary>
    static byte[] Bytes(byte[] normals, int width, int height, int iterations, float intensity = 1f) {
        var plan = Plan(width, height, iterations, intensity);
        var output = new TextureCpuImage(TextureFormat.R16Float, width, height, new byte[width * height * 2]);

        plan.Ops[0].Cpu!.Run(
            new TextureCpuInvocation(
                plan,
                0,
                [new TextureCpuImage(TextureFormat.Rgba16Float, width, height, normals)],
                output
            )
        );

        return output.Bytes;
    }

    static TexturePlan Plan(int width, int height, int iterations, float intensity) =>
        new() {
            BaseWidth = width,
            BaseHeight = height,
            Images = [new(TextureFormat.Rgba16Float, External: true), new(TextureFormat.R16Float)],
            Ops = [TextureCpuOperations.NormalToHeight(1, 0, iterations, intensity)],
            Outputs = [1]
        };

    /// <summary>The largest disagreement between two fields.</summary>
    static double Error(double[] expected, double[] actual) {
        var worst = 0d;

        for (var at = 0; at < expected.Length; at++) {
            worst = Math.Max(worst, Math.Abs(expected[at] - actual[at]));
        }

        return worst;
    }

    static void AssertMatches(double[] expected, double[] actual, double tolerance) {
        Assert.Equal(expected.Length, actual.Length);

        for (var at = 0; at < expected.Length; at++) {
            Assert.Equal(expected[at], actual[at], tolerance);
        }
    }
}
