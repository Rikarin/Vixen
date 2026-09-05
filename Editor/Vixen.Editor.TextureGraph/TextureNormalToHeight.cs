// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Geometry.Uv.Solving;

namespace Vixen.Editor.TextureGraph;

/// <summary>Doc 48 § 4.6's <c>Normal → Height</c>: the Poisson solve that inverts a normal map.</summary>
/// <remarks>
///     <para>
///         <b>The one entry in doc 48's catalogue that is not a compute kernel</b>, and
///         <see cref="ITextureCpuOperation" /> carries the whole argument for why. In short: there is
///         no GPU formulation of a Poisson solve worth having at this size, so this is not an escape
///         from writing a kernel — it is the case § D3's exception was written for.
///     </para>
///     <para>
///         <b>What it inverts is stated by <c>HeightToNormal.rvn</c> and nothing else.</b> That kernel
///         derives its convention from what <c>TexturedNormalMapSurface</c> samples and lands on
///         <c>n = normalize(−∂h/∂u · intensity, −∂h/∂v · intensity, 1)</c>, encoded <c>n·½ + ½</c>,
///         with <c>v</c> pointing <em>down</em> the image. So the decode here is that expression
///         solved for the two slopes, and <see cref="Intensity" /> is the same knob read the other
///         way round. ⚠ A green flip belongs in <c>Normal Transform</c> and deliberately not here:
///         two nodes that each flip it agree on every flat surface and disagree everywhere it
///         matters.
///     </para>
///     <para>
///         ⚠ <b>A gradient field determines a height only up to an additive constant, so this one
///         chooses the constant and says which.</b> The answer has mean zero. That is not a stylistic
///         choice — it is the only part of the answer the input does not contain, and picking it by
///         min-max normalisation instead would make the node's output depend on a single extreme
///         texel and would break the round trip below for every input but one. A graph that wants a
///         <c>[0, 1]</c> height puts a <c>Levels</c> after it, which is the node whose whole job that
///         is.
///     </para>
///     <para>
///         <b>The closed form, and it is what the tests assert:</b> take any height field, produce a
///         normal map from it by <c>HeightToNormal</c>'s stated formula, run this, and the result is
///         the original height minus its own mean — to within the solver's iteration budget, and no
///         closer, because the budget is a count rather than a tolerance.
///     </para>
///     <para>
///         ⚠ <b>The solver is doc 42 § B1's and is deliberately not a second one.</b>
///         <c>Vixen.Geometry.Uv/Solving</c> is a general `SparseMatrix` / preconditioned
///         `ConjugateGradient` pair that happens to live behind an unwrapper's front door; a grid
///         Poisson is the easiest system it will ever be handed. The one thing this client cannot use
///         is the reason that solver has the shape it has: <b>there is no warm start here.</b> A
///         local–global loop hands back the previous iterate and converges in a handful of steps; a
///         bake is one solve from cold, so <see cref="Iterations" /> is the entire story and its
///         default is chosen against that rather than against <c>UvSettings.SolverIterations</c>.
///     </para>
/// </remarks>
sealed class NormalToHeightOperation : ITextureCpuOperation {
    /// <summary>What an op naming this operation calls itself.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a kernel name and there is no <c>.rvn</c> with this name</b>, which is the whole
    ///     of the third category doc 48 § 4.11 counts separately. <see cref="TextureOp.Kernel" />
    ///     carries it so that every message in this assembly can say "op 3 runs 'NormalToHeight'"
    ///     without first asking which kind of op it was.
    /// </remarks>
    public const string OpKernel = "NormalToHeight";

    /// <summary>The parameter carrying the solver's budget.</summary>
    public const string Iterations = "iterations";

    /// <summary>The parameter carrying <c>HeightToNormal</c>'s <c>intensity</c>, to be undone.</summary>
    public const string Intensity = "intensity";

    /// <summary>The budget a node uses when its author has not said otherwise.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <c>UvSettings.SolverIterations</c>' 64, and the difference is the missing warm
    ///         start.</b> Conjugate gradient removes error fastest at the high frequencies and slowest
    ///         at the low ones, and the low ones are the whole of what a height field is — so a cold
    ///         solve over a grid this shape is still visibly tilted after 64 steps while a local–global
    ///         loop handed the previous iterate is finished. 256 is four times that, which is the
    ///         cheapest number that is not obviously too few.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is a default and not a claim of convergence, and no number here could be.</b>
    ///         The steps a Poisson system needs grow with the grid's diameter, so what is ample at 256
    ///         texels is thin at 4K — and there is deliberately no residual test that would notice,
    ///         because a residual test decides differently on different hardware and doc 42 § D5 traded
    ///         that away for a bake that is the same everywhere. So a graph baked large and looking
    ///         gently tilted wants a larger number here, and that is a knob rather than a bug.
    ///     </para>
    /// </remarks>
    public const float DefaultIterations = 256f;

    /// <summary>
    ///     The largest picture this will attempt, in texels, and the reason there is a ceiling at all.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A CPU op's cost is not the shape a plan's other costs have, and this one's is
    ///     memory.</b> The system has one unknown per texel and five stored entries per row, and
    ///     <c>SparseMatrixBuilder</c> holds triplets before it compacts them — so the assembly alone
    ///     is <c>5 · texels · 16</c> bytes, the compaction allocates two more copies of it, and the
    ///     incomplete Cholesky factor is another. Measured against the arithmetic: 1024² is about
    ///     180 MB and 4096² would be about 2.9 GB, which is not a slow bake but a dead process. The
    ///     ceiling is 2048², it is refused with the number in the message rather than attempted, and
    ///     lifting it means giving the solver a matrix-free grid operator rather than raising a
    ///     constant — <a href="https://github.com/Rikarin/Vixen/issues/757">#757</a>.
    /// </remarks>
    public const int MaxTexels = 2048 * 2048;

    /// <summary>How near zero a decoded <c>n.z</c> may get before the slope it divides is clamped.</summary>
    /// <remarks>
    ///     ⚠ <b>A relative epsilon would be the usual advice and is wrong here.</b> A decoded normal
    ///     is a unit vector, so <c>n.z</c> has one scale by construction and <c>1/64</c> is a slope of
    ///     64 — already far past anything a height field can mean. What this guards is not a rounding
    ///     error but a texel that is not a normal at all: an unwritten channel, a mask wired into the
    ///     port by mistake, or the <c>(0, 0, 0)</c> a zeroed texture reads as, each of which divides
    ///     by zero and puts an infinity into the right-hand side, from where it reaches every texel
    ///     in the picture through the solve.
    /// </remarks>
    const double MinimumZ = 1.0 / 64.0;

    /// <inheritdoc />
    public string Name => "Normal → Height";

    /// <inheritdoc />
    public void Run(in TextureCpuInvocation invocation) {
        var plan = invocation.Plan;
        var op = plan.Ops[invocation.Op];
        var output = invocation.Output;

        if (invocation.Inputs.Length != 1) {
            throw new InvalidOperationException(
                $"Op {invocation.Op} runs '{Name}' over {invocation.Inputs.Length} inputs, and a normal map is one."
            );
        }

        var normals = invocation.Inputs[0];
        var width = output.Width;
        var height = output.Height;

        if ((long)width * height > MaxTexels) {
            var message = string.Create(
                CultureInfo.InvariantCulture,
                $"Op {invocation.Op} runs '{Name}' over {width}×{height} texels, and the Poisson system that needs is more memory than a process should ask for — the ceiling is {MaxTexels} texels. Bake the height at a lower resolution and upscale, or see #757."
            );

            throw new InvalidOperationException(message);
        }

        var iterations = (int)MathF.Round(Number(plan, invocation.Op, op, Iterations, DefaultIterations));
        var intensity = Number(plan, invocation.Op, op, Intensity, 1f);

        // ⚠ Zero is the one value of `intensity` that is not invertible: `HeightToNormal` at zero
        // writes a flat map whatever the height was, so there is no height to recover and the
        // division below would be an infinity per texel rather than a flat answer.
        var scale = Math.Abs(intensity) < 1e-6f ? 0d : 1d / intensity;

        var (gradientX, gradientY) = Slopes(normals, width, height, scale);
        var solved = Solve(gradientX, gradientY, width, height, Math.Max(iterations, 0));

        Encode(solved, output);
    }

    /// <summary>One parameter of the op, resolved as a kernel's uniform member would be.</summary>
    /// <remarks>
    ///     <see cref="TexturePlan.Resolve" /> rather than <c>parameter.Value</c>, which is the whole
    ///     reason <see cref="TextureCpuInvocation" /> carries the plan and the index instead of the
    ///     numbers: doc 48 § D8's scaling then reaches both kinds of op through one expression.
    /// </remarks>
    static float Number(TexturePlan plan, int index, TextureOp op, string name, float fallback) =>
        op.Find(name) is { } parameter ? plan.Resolve(index, parameter) : fallback;

    /// <summary>
    ///     The height field's slope at every texel, in height units per texel step, from the encoded
    ///     normals.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two conversions, and both come straight out of <c>HeightToNormal.rvn</c>.</b> That
    ///         kernel writes <c>normalize(−s · intensity, 1)</c> where <c>s</c> is the slope per unit
    ///         of <em>UV</em>, so undoing it is <c>s = −n.xy / n.z / intensity</c>. Then a texel step
    ///         is <c>1/width</c> of a unit of <c>u</c>, which is the second division and the half of
    ///         this that makes the node resolution-independent in the same sense § D8 asks of a
    ///         radius: the same normal map read at two bake resolutions produces the same height,
    ///         rather than one twice as tall.
    ///     </para>
    /// </remarks>
    static (double[] X, double[] Y) Slopes(in TextureCpuImage normals, int width, int height, double scale) {
        var perU = scale / width;
        var perV = scale / height;

        var gradientX = new double[width * height];
        var gradientY = new double[width * height];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                // ⚠ Sampled at the *output's* grid and clamped into the source's, which is the rule
                // every kernel in this assembly already follows: a normal map that is not the size
                // of the height being solved is read correctly rather than off the end.
                var texel = Decode(
                    normals,
                    Math.Min(x, normals.Width - 1),
                    Math.Min(y, normals.Height - 1)
                );

                var z = Math.Max(texel.Z, MinimumZ);
                var at = (y * width) + x;

                gradientX[at] = -texel.X / z * perU;
                gradientY[at] = -texel.Y / z * perV;
            }
        }

        return (gradientX, gradientY);
    }

    /// <summary>One texel of a normal map, decoded to a vector.</summary>
    /// <remarks>
    ///     <c>2v − 1</c>, which is <c>Normals.Decode</c> and what <c>HeightToNormal</c> encoded
    ///     against. ⚠ The result is deliberately <em>not</em> renormalised: the length carries no
    ///     information the two slopes need — they are both divided by <c>z</c>, so a common factor
    ///     cancels — and a map whose texels are short because it was blended or filtered would
    ///     otherwise be silently rescaled into a different height.
    /// </remarks>
    static (double X, double Y, double Z) Decode(in TextureCpuImage image, int x, int y) {
        var stride = TextureFormats.BytesPerTexel(image.Format);
        var at = ((y * image.Width) + x) * stride;
        var bytes = image.Bytes;

        return image.Format switch {
            TextureFormat.Rgba8 => (
                (bytes[at] / 255d * 2d) - 1d,
                (bytes[at + 1] / 255d * 2d) - 1d,
                (bytes[at + 2] / 255d * 2d) - 1d
            ),
            TextureFormat.Rg8 => ((bytes[at] / 255d * 2d) - 1d, (bytes[at + 1] / 255d * 2d) - 1d, 1d),
            TextureFormat.R8 => ((bytes[at] / 255d * 2d) - 1d, -1d, 1d),
            TextureFormat.Rgba16Float => (
                (Half(bytes, at) * 2d) - 1d,
                (Half(bytes, at + 2) * 2d) - 1d,
                (Half(bytes, at + 4) * 2d) - 1d
            ),
            TextureFormat.R16Float => ((Half(bytes, at) * 2d) - 1d, -1d, 1d),
            _ => throw new ArgumentOutOfRangeException(nameof(image), image.Format, "No such texture format.")
        };
    }

    static double Half(byte[] bytes, int at) => (double)BitConverter.ToHalf(bytes.AsSpan(at, 2));

    /// <summary>Solves <c>∇²h = div g</c> over the grid, and returns a height field of mean zero.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Assembled as a least-squares fit over the grid's edges rather than written down as a
    ///         stencil</b>, because the two are the same matrix and only one of them states the
    ///         boundary condition by construction. The energy is
    ///         <c>Σ (h[q] − h[p] − g[p→q])²</c> over every horizontal and vertical neighbour pair;
    ///         its normal equations are the five-point Laplacian in the interior and the Neumann
    ///         boundary everywhere else, with no edge cases to write, because a texel on the border
    ///         simply has fewer edges.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One unknown is removed rather than the system being solved as it stands.</b> The
    ///         Neumann Laplacian is singular — adding a constant to every height changes nothing, and
    ///         that null space is exactly the constant the input does not contain — so a preconditioned
    ///         conjugate gradient over it has no defined answer to converge to and an incomplete
    ///         Cholesky of it is a factorization of a singular matrix. Deleting one unknown's row
    ///         *and* its column, rather than overwriting its row with an identity, is what keeps the
    ///         remainder symmetric; the result is positive-definite, and the removed texel's height is
    ///         zero by construction. The gauge is then moved to the mean afterwards, which is where
    ///         the choice of which texel to pin stops mattering.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The budget is spent, not tested.</b> There is no residual check and no early exit
    ///         here or in the solver, on purpose: doc 42 § D5's argument is that a floating-point
    ///         comparison decides differently on different platforms and a bake is supposed to be
    ///         byte-identical. So a budget of 8 gives a worse answer than a budget of 256, both give
    ///         the same answer twice, and <c>SolveReport.Residual</c> says where it got to and is read
    ///         by nobody.
    ///     </para>
    /// </remarks>
    static double[] Solve(double[] gradientX, double[] gradientY, int width, int height, int iterations) {
        var texels = width * height;

        if (texels <= 1) {
            return new double[texels];
        }

        // The pinned texel is the last one rather than the first, so that the unknown for texel `i`
        // is `i` for every `i` below it — an index map with no branch in the inner loops.
        var pinned = texels - 1;
        var unknowns = texels - 1;

        var builder = new SparseMatrixBuilder(unknowns, unknowns);
        var right = new double[unknowns];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var at = (y * width) + x;

                if (x + 1 < width) {
                    Edge(builder, right, at, at + 1, (gradientX[at] + gradientX[at + 1]) * 0.5d, pinned);
                }

                if (y + 1 < height) {
                    Edge(builder, right, at, at + width, (gradientY[at] + gradientY[at + width]) * 0.5d, pinned);
                }
            }
        }

        var solution = new double[unknowns];

        new ConjugateGradient(builder.Build()).Solve(right, solution, iterations);

        var heights = new double[texels];
        var total = 0d;

        for (var at = 0; at < unknowns; at++) {
            heights[at] = solution[at];
            total += solution[at];
        }

        // `heights[pinned]` is already zero, and it counts: the mean is over every texel of the
        // picture rather than over the unknowns, or the answer would depend on which one was pinned.
        var mean = total / texels;

        for (var at = 0; at < texels; at++) {
            heights[at] -= mean;
        }

        return heights;
    }

    /// <summary>Adds one neighbour pair's contribution to the normal equations.</summary>
    /// <remarks>
    ///     Both rows and both off-diagonals, because <c>(h[q] − h[p] − g)²</c> is one term of the
    ///     energy and appears in the derivative with respect to each of its two unknowns. ⚠ A pinned
    ///     unknown's column vanishes from the matrix and contributes nothing to the right-hand side
    ///     either — its height is zero, so the term it would have carried is <c>−1 · 0</c> — which is
    ///     why the two `if`s here are not paired with an adjustment.
    /// </remarks>
    static void Edge(SparseMatrixBuilder builder, double[] right, int from, int to, double gradient, int pinned) {
        if (from != pinned) {
            builder.Add(from, from, 1d);
            right[from] -= gradient;

            if (to != pinned) {
                builder.Add(from, to, -1d);
            }
        }

        if (to == pinned) {
            return;
        }

        builder.Add(to, to, 1d);
        right[to] += gradient;

        if (from != pinned) {
            builder.Add(to, from, -1d);
        }
    }

    /// <summary>Writes the solved heights into the output image's texels.</summary>
    /// <remarks>
    ///     ⚠ <b>Every texel, including the ones the solve left at zero.</b>
    ///     <see cref="ITextureCpuOperation.Run" />'s contract is that the buffer arrives zeroed and
    ///     zero is a plausible height, so a loop that skipped anything would produce a picture rather
    ///     than a failure.
    /// </remarks>
    static void Encode(double[] heights, in TextureCpuImage output) {
        var stride = TextureFormats.BytesPerTexel(output.Format);
        var bytes = output.Bytes;

        for (var at = 0; at < heights.Length; at++) {
            var value = (float)heights[at];
            var texel = at * stride;

            switch (output.Format) {
                case TextureFormat.R16Float:
                    BitConverter.TryWriteBytes(bytes.AsSpan(texel, 2), (Half)value);

                    break;

                case TextureFormat.Rgba16Float:
                    // ⚠ Grey rather than red-only, and alpha one. A height written into the red
                    // channel of an RGBA image and nothing else is black wherever it is negative and
                    // transparent everywhere, which reads as a broken solve rather than as a channel
                    // decision.
                    BitConverter.TryWriteBytes(bytes.AsSpan(texel, 2), (Half)value);
                    BitConverter.TryWriteBytes(bytes.AsSpan(texel + 2, 2), (Half)value);
                    BitConverter.TryWriteBytes(bytes.AsSpan(texel + 4, 2), (Half)value);
                    BitConverter.TryWriteBytes(bytes.AsSpan(texel + 6, 2), (Half)1f);

                    break;

                case TextureFormat.Rgba8:
                    var level = Byte(value);

                    bytes[texel] = level;
                    bytes[texel + 1] = level;
                    bytes[texel + 2] = level;
                    bytes[texel + 3] = 255;

                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(output),
                        output.Format,
                        "A height is written as a float or as grey levels, and that format is neither."
                    );
            }
        }
    }

    /// <summary>A mean-zero height as an eight-bit level, with zero at the middle of the range.</summary>
    /// <remarks>
    ///     ⚠ <b>Signed, because the answer is.</b> Clamping a mean-zero field into <c>[0, 1]</c>
    ///     would throw away every texel below the mean — half of the picture — and the half that
    ///     survived would look like a correct height map of something else.
    /// </remarks>
    static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round((value + 0.5f) * 255f), 0, 255);
}
