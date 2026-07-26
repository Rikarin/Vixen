// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Core.Mathematics;

namespace Vixen.Benchmarks.Math;

/// <summary>
///     The vectorised matrix paths against their scalar fallbacks, and against
///     <see cref="System.Numerics" /> as an outside reference.
/// </summary>
/// <remarks>
///     <para>
///         This exists because the SIMD code was written on the assumption that it is faster, and an
///         assumption is not a measurement. <c>Vector128.IsHardwareAccelerated</c> is a JIT constant,
///         so the two paths are reachable only because <c>Multiply</c> is split into
///         <c>MultiplyVectorized</c> and <c>MultiplyScalar</c> internally — without the split there
///         is no way to run the loser.
///     </para>
///     <para>
///         The <see cref="System.Numerics" /> column is the honesty check. The BCL's matrix multiply
///         is hand-tuned by people who do this full time; if ours is dramatically slower, the right
///         answer is to find out why rather than to keep our own version out of pride.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class MatrixBenchmarks {
    Matrix4x4 left;
    Matrix4x4 right;
    Vector4 vector;
    System.Numerics.Matrix4x4 bclLeft;
    System.Numerics.Matrix4x4 bclRight;

    [GlobalSetup]
    public void Setup() {
        left = Matrix4x4.Compose(
            new(1.5f, 2f, 0.5f),
            Quaternion.FromYawPitchRoll(0.3f, -0.7f, 1.1f),
            new(4f, -2f, 7f)
        );

        right = Matrix4x4.Compose(
            new(0.5f, 1f, 2f),
            Quaternion.FromYawPitchRoll(-1.2f, 0.4f, 0.9f),
            new(-3f, 5f, 1f)
        );

        vector = new(1f, 2f, 3f, 1f);
        bclLeft = left;
        bclRight = right;
    }

    [Benchmark(Baseline = true, Description = "Multiply — scalar")]
    public Matrix4x4 MultiplyScalar() => Matrix4x4.MultiplyScalar(left, right);

    [Benchmark(Description = "Multiply — Vector128")]
    public Matrix4x4 MultiplyVectorized() => Matrix4x4.MultiplyVectorized(left, right);

    [Benchmark(Description = "Multiply — System.Numerics")]
    public System.Numerics.Matrix4x4 MultiplyBcl() => bclLeft * bclRight;

    [Benchmark(Description = "TransformVector4 — scalar")]
    public Vector4 TransformScalar() => Matrix4x4.TransformVector4Scalar(vector, left);

    [Benchmark(Description = "TransformVector4 — Vector128")]
    public Vector4 TransformVectorized() => Matrix4x4.TransformVector4Vectorized(vector, left);

    [Benchmark(Description = "Invert")]
    public bool Invert() => Matrix4x4.Invert(left, out _);

    [Benchmark(Description = "Decompose")]
    public bool Decompose() => Matrix4x4.Decompose(left, out _, out _, out _);
}

/// <summary>
///     The bulk transform against transforming one point at a time, which is the comparison that
///     decides whether hoisting the matrix out of the loop was worth writing a second entry point.
/// </summary>
[MemoryDiagnoser]
public class BulkTransformBenchmarks {
    Matrix4x4 transform;
    Vector3[] source = [];
    Vector3[] destination = [];

    /// <summary>How many points to transform. Spans the sizes a cull actually sees.</summary>
    [Params(64, 1024, 16_384)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup() {
        transform = Matrix4x4.Compose(
            new(1.5f, 2f, 0.5f),
            Quaternion.FromYawPitchRoll(0.3f, -0.7f, 1.1f),
            new(4f, -2f, 7f)
        );

        source = new Vector3[Count];
        destination = new Vector3[Count];

        for (var i = 0; i < Count; i++) {
            source[i] = new(i * 0.1f, i * 0.2f, i * 0.3f);
        }
    }

    [Benchmark(Baseline = true, Description = "One at a time")]
    public void Scalar() {
        for (var i = 0; i < source.Length; i++) {
            destination[i] = Matrix4x4.TransformPosition(source[i], transform);
        }
    }

    [Benchmark(Description = "Bulk")]
    public void Bulk() => Matrix4x4.TransformPositions(source, transform, destination);
}
