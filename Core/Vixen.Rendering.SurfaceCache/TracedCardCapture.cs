// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;

namespace Vixen.Rendering.SurfaceCache;

/// <summary>What a surface is made of, for the reference capture to ask.</summary>
/// <remarks>The runtime capture rasterises real materials; this is the fixture-and-reference seam,
///     the same role <c>IRadianceSource</c> plays for the tracers.</remarks>
public interface ISurfaceMaterial {
    /// <summary>What fraction of each channel a surface reflects.</summary>
    Vector3 Albedo(Vector3 position, Vector3 normal);

    /// <summary>What it emits, as radiance.</summary>
    Vector3 Emissive(Vector3 position, Vector3 normal);
}

/// <summary>Captures cards by marching a distance field — doc 19 § L4's capture, reference half.</summary>
/// <remarks>
///     One orthographic ray per texel, entering at the card's near plane and marching down the
///     card's axis: a hit stores depth, the field's gradient as the normal, and the material's
///     answers; a miss leaves the texel invalid. Deterministic, so a capture can be asserted
///     texel by texel — the rasterising capture is the device half and is compared against this,
///     the arrangement every capture in this engine has with its reference.
/// </remarks>
public sealed class TracedCardCapture(IDistanceField geometry, ISurfaceMaterial material) {
    readonly IDistanceField geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
    readonly ISurfaceMaterial material = material ?? throw new ArgumentNullException(nameof(material));

    /// <summary>How far off the near plane a capture ray starts, in world units.</summary>
    public float Bias { get; init; } = 0.01f;

    /// <summary>Fills one card's texels from the field.</summary>
    /// <param name="cache">The cache holding the card.</param>
    /// <param name="card">The card, by index.</param>
    /// <returns>How many texels captured a surface.</returns>
    /// <exception cref="ArgumentNullException">There is no cache.</exception>
    public int Capture(SurfaceCacheStore cache, int card) {
        ArgumentNullException.ThrowIfNull(cache);

        var (shape, _) = (cache.Cards[card].Card, cache.Cards[card].Origin);
        var direction = -shape.Direction;
        var (_, halfDepth) = shape.Extents;
        var trace = new DistanceFieldTraceSettings { MaxDistance = (halfDepth * 2f) + (Bias * 2f) };
        var captured = 0;

        for (var y = 0; y < shape.Resolution.Y; y++) {
            for (var x = 0; x < shape.Resolution.X; x++) {
                var texel = new Int2(x, y);
                var origin = shape.TexelOrigin(texel) - (direction * Bias);
                var hit = DistanceFieldTracer.Trace(geometry, origin, direction, trace);

                if (!hit.Hit) {
                    cache.Invalidate(card, texel);

                    continue;
                }

                var depth = Vector3.Dot(hit.Position - shape.TexelOrigin(texel), direction);

                cache.SetSurface(
                    card,
                    texel,
                    new(
                        material.Albedo(hit.Position, hit.Normal),
                        hit.Normal,
                        depth,
                        material.Emissive(hit.Position, hit.Normal)
                    )
                );

                captured++;
            }
        }

        return captured;
    }
}
