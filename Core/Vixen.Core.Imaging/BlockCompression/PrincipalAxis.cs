// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>The direction a block's colours vary along most.</summary>
/// <remarks>
///     <para>
///         Every format here stores a line and sixteen positions along it, so the one decision an
///         encoder makes that matters is which line. Power iteration on the colours' covariance gives
///         it in a few multiplies, and a bounding box — the other common answer — is visibly worse on
///         anything running diagonally through colour space, which is most skin, most foliage and
///         every sunset.
///     </para>
///     <para>
///         <b>The starting vector is not (1, 1, 1).</b> That is the obvious choice and it is wrong in
///         a way that is invisible until it isn't: a block whose colours run along (1, 0, −1) — red
///         rising exactly as blue falls, which is a sunset, a specular falloff and half of every
///         gradient — is <i>orthogonal</i> to it, so the first multiply returns the zero vector, the
///         iteration stops, and the axis is left pointing at the one direction the data has no
///         extent in. Both endpoints then land on the same texel and the whole block decodes as a
///         single flat colour. Starting from the covariance row with the largest norm cannot do that:
///         it is a vector the data itself produced.
///     </para>
/// </remarks>
static class PrincipalAxis {
    /// <summary>How many times to iterate. It converges long before this on real blocks.</summary>
    const int Iterations = 8;

    /// <summary>Finds the dominant eigenvector of a covariance matrix.</summary>
    /// <param name="covariance">The matrix, row-major, <paramref name="dimensions" /> squared.</param>
    /// <param name="dimensions">How many channels.</param>
    /// <param name="axis">The direction, normalised. All ones if the block has no extent at all.</param>
    public static void Find(ReadOnlySpan<float> covariance, int dimensions, Span<float> axis) {
        Span<float> next = stackalloc float[dimensions];
        var bestRow = -1;
        var bestNorm = 0f;

        for (var row = 0; row < dimensions; row++) {
            var norm = 0f;

            for (var column = 0; column < dimensions; column++) {
                var value = covariance[(row * dimensions) + column];
                norm += value * value;
            }

            if (norm > bestNorm) {
                bestNorm = norm;
                bestRow = row;
            }
        }

        if (bestRow < 0) {
            // Every colour in the block is the same one. Any direction will do; both endpoints are
            // going to land on the same texel whatever this says.
            axis.Fill(1f);
            return;
        }

        covariance.Slice(bestRow * dimensions, dimensions).CopyTo(axis);
        Normalise(axis);

        for (var iteration = 0; iteration < Iterations; iteration++) {
            next.Clear();

            for (var row = 0; row < dimensions; row++) {
                for (var column = 0; column < dimensions; column++) {
                    next[row] += covariance[(row * dimensions) + column] * axis[column];
                }
            }

            if (!Normalise(next)) {
                break;
            }

            next.CopyTo(axis);
        }
    }

    static bool Normalise(Span<float> vector) {
        var length = 0f;

        foreach (var value in vector) {
            length += value * value;
        }

        length = MathF.Sqrt(length);

        if (length < 1e-6f) {
            return false;
        }

        for (var index = 0; index < vector.Length; index++) {
            vector[index] /= length;
        }

        return true;
    }
}
