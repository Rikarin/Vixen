// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     The vector types. The layout assertions matter as much as the arithmetic: these structs are
///     memcpy'd into GPU buffers and reinterpreted through <c>Unsafe.As</c>, so their size and field
///     order are part of the contract rather than an implementation detail.
/// </summary>
public class VectorTests {
    [Fact]
    public void The_layouts_are_exactly_what_a_gpu_buffer_expects() {
        Assert.Equal(8, Marshal.SizeOf<Vector2>());
        Assert.Equal(12, Marshal.SizeOf<Vector3>());
        Assert.Equal(16, Marshal.SizeOf<Vector4>());
        Assert.Equal(16, Marshal.SizeOf<Quaternion>());
        Assert.Equal(36, Marshal.SizeOf<Matrix3x3>());
        Assert.Equal(64, Marshal.SizeOf<Matrix4x4>());
        Assert.Equal(8, Marshal.SizeOf<Int2>());
        Assert.Equal(12, Marshal.SizeOf<Int3>());
        Assert.Equal(16, Marshal.SizeOf<Int4>());
    }

    [Fact]
    public void Components_come_back_in_declaration_order() {
        var vector = new Vector4(1f, 2f, 3f, 4f);

        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, vector.AsSpan().ToArray());
        Assert.Equal(1f, vector[0]);
        Assert.Equal(4f, vector[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => vector[4]);

        Span<float> copied = stackalloc float[4];
        vector.CopyTo(copied);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, copied.ToArray());
    }

    [Fact]
    public void Arithmetic_is_component_wise() {
        var a = new Vector3(1f, 2f, 3f);
        var b = new Vector3(10f, 20f, 30f);

        Assert.Equal(new(11f, 22f, 33f), a + b);
        Assert.Equal(new(-9f, -18f, -27f), a - b);
        Assert.Equal(new(10f, 40f, 90f), a * b);
        Assert.Equal(new(2f, 4f, 6f), a * 2f);
        Assert.Equal(new(2f, 4f, 6f), 2f * a);
        Assert.Equal(new(-1f, -2f, -3f), -a);
        Assert.True(Vector3.NearEqual(new(0.5f, 1f, 1.5f), a / 2f));
    }

    [Fact]
    public void Length_and_distance_agree_with_their_squared_forms() {
        var vector = new Vector3(3f, 4f, 0f);

        Assert.Equal(5f, vector.Length(), 5);
        Assert.Equal(25f, vector.LengthSquared(), 4);
        Assert.Equal(5f, Vector3.Distance(Vector3.Zero, vector), 5);
        Assert.Equal(25f, Vector3.DistanceSquared(Vector3.Zero, vector), 4);
    }

    [Fact]
    public void A_degenerate_vector_normalises_to_zero_rather_than_to_NaN() {
        // The alternative is a NaN that spreads silently through every later comparison.
        Assert.Equal(Vector3.Zero, Vector3.Normalize(Vector3.Zero));
        Assert.Equal(Vector2.Zero, Vector2.Normalize(Vector2.Zero));
        Assert.Equal(Vector4.Zero, Vector4.Normalize(Vector4.Zero));
        Assert.False(Vector3.Normalize(Vector3.Zero).IsNaN);
    }

    [Fact]
    public void Reflect_bounces_a_direction_off_a_surface() {
        // Straight down onto a floor comes straight back up.
        var reflected = Vector3.Reflect(Vector3.Down, Vector3.Up);
        Assert.True(Vector3.NearEqual(Vector3.Up, reflected));

        // A 45° incidence leaves at 45°.
        var diagonal = Vector3.Normalize(new(1f, -1f, 0f));
        Assert.True(Vector3.NearEqual(Vector3.Normalize(new(1f, 1f, 0f)), Vector3.Reflect(diagonal, Vector3.Up)));
    }

    [Fact]
    public void Project_takes_the_component_along_a_direction() {
        Assert.True(Vector3.NearEqual(new(3f, 0f, 0f), Vector3.Project(new(3f, 4f, 0f), Vector3.UnitX)));
        Assert.Equal(Vector3.Zero, Vector3.Project(new(3f, 4f, 0f), Vector3.Zero));
    }

    [Fact]
    public void Min_Max_Clamp_and_Abs_work_per_component() {
        var a = new Vector3(1f, 5f, -3f);
        var b = new Vector3(4f, 2f, 0f);

        Assert.Equal(new(1f, 2f, -3f), Vector3.Min(a, b));
        Assert.Equal(new(4f, 5f, 0f), Vector3.Max(a, b));
        Assert.Equal(new(1f, 5f, 3f), Vector3.Abs(a));
        Assert.Equal(new(1f, 3f, 0f), Vector3.Clamp(a, Vector3.Zero, new(3f, 3f, 3f)));
    }

    [Fact]
    public void The_two_dimensional_cross_product_is_the_signed_area() {
        Assert.Equal(1f, Vector2.Cross(Vector2.UnitX, Vector2.UnitY), 5);
        Assert.Equal(-1f, Vector2.Cross(Vector2.UnitY, Vector2.UnitX), 5);
        Assert.Equal(0f, Vector2.Cross(Vector2.UnitX, Vector2.UnitX), 5);
    }

    [Fact]
    public void Swizzles_and_widening_constructors_line_up() {
        var v4 = new Vector4(1f, 2f, 3f, 4f);

        Assert.Equal(new(1f, 2f, 3f), v4.Xyz);
        Assert.Equal(new(1f, 2f), v4.Xy);
        Assert.Equal(v4, new Vector4(v4.Xyz, 4f));
        Assert.Equal(new(1f, 2f, 3f), new Vector3(new Vector2(1f, 2f), 3f));
    }

    [Fact]
    public void Conversion_to_the_bcl_vectors_preserves_every_component() {
        var vixen = new Vector3(1.5f, -2.5f, 3.5f);
        System.Numerics.Vector3 bcl = vixen;
        Vector3 roundTripped = bcl;

        Assert.Equal(vixen, roundTripped);
        Assert.Equal(vixen.X, bcl.X);
        Assert.Equal(vixen.Z, bcl.Z);
    }

    [Fact]
    public void Formatting_is_invariant_and_writes_into_a_caller_buffer() {
        var vector = new Vector3(1.5f, -2.25f, 0f);
        Span<char> buffer = stackalloc char[64];

        Assert.Equal("(1.5, -2.25, 0)", vector.ToString());
        Assert.True(vector.TryFormat(buffer, out var written));
        Assert.Equal("(1.5, -2.25, 0)", new(buffer[..written]));
        Assert.Equal("(1.50, -2.25, 0.00)", vector.ToString("F2", null));

        // A buffer that cannot hold the result is reported, not overrun.
        Assert.False(vector.TryFormat(stackalloc char[4], out var truncated));
        Assert.Equal(0, truncated);
    }

    [Fact]
    public void Integer_vectors_do_integer_arithmetic() {
        var a = new Int3(7, -3, 2);
        var b = new Int3(2, 2, 2);

        Assert.Equal(new(9, -1, 4), a + b);
        Assert.Equal(new(14, -6, 4), a * 2);

        // Truncating toward zero, as C# division does — not floored.
        Assert.Equal(new(3, -1, 1), a / b);

        Assert.Equal(42L, new Int3(2, 3, 7).Volume);
        Assert.Equal(12L, new Int2(3, 4).Area);
    }

    [Fact]
    public void Integer_vectors_widen_to_float_ones() {
        Vector3 widened = new Int3(1, 2, 3);
        Assert.Equal(new(1f, 2f, 3f), widened);

        Vector2 widened2 = new Int2(4, 5);
        Assert.Equal(new(4f, 5f), widened2);
    }

    [Fact]
    public void Equality_and_hashing_agree_across_the_vector_types() {
        Assert.Equal(new Vector2(1f, 2f), new Vector2(1f, 2f));
        Assert.Equal(new Vector2(1f, 2f).GetHashCode(), new Vector2(1f, 2f).GetHashCode());
        Assert.NotEqual(new Vector2(1f, 2f), new Vector2(2f, 1f));

        Assert.Equal(new Int4(1, 2, 3, 4), new Int4(1, 2, 3, 4));
        Assert.Equal(new Int4(1, 2, 3, 4).GetHashCode(), new Int4(1, 2, 3, 4).GetHashCode());

        Assert.True(new Vector4(1f, 2f, 3f, 4f).Equals((object)new Vector4(1f, 2f, 3f, 4f)));
        Assert.False(new Vector4(1f, 2f, 3f, 4f).Equals("not a vector"));
    }

    [Fact]
    public void Vector4_reinterprets_to_and_from_a_simd_register() {
        var vector = new Vector4(1f, 2f, 3f, 4f);
        var register = vector.AsVector128();

        Assert.Equal(1f, register[0]);
        Assert.Equal(4f, register[3]);
        Assert.Equal(vector, Vector4.FromVector128(register));
    }

    [Fact]
    public void Deconstruction_gives_the_components_back() {
        var (x, y, z, w) = new Vector4(1f, 2f, 3f, 4f);
        Assert.Equal((1f, 2f, 3f, 4f), (x, y, z, w));

        var (ix, iy) = new Int2(6, 7);
        Assert.Equal((6, 7), (ix, iy));
    }
}
