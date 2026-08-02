// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage.Tests;

/// <summary>A surface to paint onto, with whatever shape a test needs.</summary>
/// <remarks>
///     ⚠ <b>Not a terrain, and that is the point of <see cref="IFoliageSurface" />.</b> Foliage
///     paints onto anything that can say what is under a point — a blockout wall, an imported cliff,
///     a rooftop — so the tests answer with arithmetic and this assembly never learns what a
///     heightfield is.
/// </remarks>
sealed class Ground(
    Func<Vector2, float>? height = null,
    Func<Vector2, Vector3>? normal = null,
    Func<Vector2, float>? weight = null,
    Func<Vector2, bool>? hit = null
) : IFoliageSurface {
    /// <summary>How many times it was asked.</summary>
    public int Samples { get; private set; }

    /// <inheritdoc />
    public FoliageSurface SampleAt(Vector2 position, string layer) {
        Samples++;

        if (hit is not null && !hit(position)) {
            return FoliageSurface.Missed;
        }

        return new(
            new(position.X, height?.Invoke(position) ?? 0f, position.Y),
            normal?.Invoke(position) ?? Vector3.UnitY,
            string.IsNullOrEmpty(layer) ? 1f : weight?.Invoke(position) ?? 1f,
            true
        );
    }

    /// <summary>Flat ground at height zero, facing up, painted everywhere.</summary>
    public static Ground Flat => new();

    /// <summary>Ground that slopes at a fixed angle.</summary>
    /// <param name="radians">How steep.</param>
    /// <returns>The surface.</returns>
    public static Ground Sloped(float radians) =>
        new(normal: _ => Vector3.Normalize(new(MathF.Sin(radians), MathF.Cos(radians), 0f)));

    /// <summary>Ground at a fixed height.</summary>
    /// <param name="metres">How high.</param>
    /// <returns>The surface.</returns>
    public static Ground At(float metres) => new(height: _ => metres);
}

/// <summary>The types the tests paint with.</summary>
static class Types {
    /// <summary>A tree: sparse, upright, and refusing steep ground.</summary>
    public static FoliageType Tree =>
        FoliageType.Of("Tree") with {
            Mesh = "Meshes/pine",
            Density = 0.05f,
            Radius = 3f,
            MinScale = 0.9f,
            MaxScale = 1.1f
        };

    /// <summary>A rock: dense, lying against the ground, and happy on a slope.</summary>
    public static FoliageType Rock =>
        FoliageType.Of("Rock") with {
            Mesh = "Meshes/rock",
            Density = 0.4f,
            Radius = 0.5f,
            AlignToNormal = 1f,
            MaxSlope = MathF.PI / 2f
        };
}
