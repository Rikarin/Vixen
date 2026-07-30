// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;

namespace Vixen.Rendering.SurfaceCache;

/// <summary>Direct light on the cache, and the bounce over the cards — doc 19 § L4's radiosity.</summary>
/// <remarks>
///     <para>
///         <b>Two passes with one convention.</b> <see cref="Light" /> evaluates the sun on every
///         valid texel — cosine, shadow ray through the field, over π — and writes it as direct
///         incident irradiance. <see cref="Gather" /> shoots cosine-weighted rays from every texel:
///         a ray that hits a cached surface brings back its <i>outgoing</i> radiance, one that hits
///         an uncached one brings back black (the honest reading, and the same answer the tracers
///         give), and one that escapes brings back the sky — which is how skylight reaches the
///         cards at all, with no ambient term to double-count it.
///     </para>
///     <para>
///         <b>Each gather reads the previous gather</b> — the store double-buffers — so pass
///         <i>n</i>'s output carries light that bounced <i>n</i> times. Iterating to a fixed point
///         is what turns one bounce into the infinite-bounce look: the series converges because
///         albedo is below one, geometrically, and the Cornell test measures the limit against a
///         path tracer rather than trusting the argument.
///     </para>
///     <para>
///         The rays are deterministic — Hammersley over the hemisphere, the exit-criteria tests'
///         own sampler — because a reference that changes between runs referees nothing, and
///         because two gathers of one cache must agree to the bit for any dispatch to be compared
///         against this.
///     </para>
/// </remarks>
public sealed class CardRadiosity(IDistanceField geometry) {
    readonly IDistanceField geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));

    /// <summary>How many rays each texel's gather casts.</summary>
    public int Rays { get; set; } = 32;

    /// <summary>How far a gather ray looks before deciding it escaped.</summary>
    public float MaxDistance { get; set; } = 100f;

    /// <summary>How far off its surface a ray starts, in world units.</summary>
    public float Bias { get; set; } = 0.01f;

    /// <summary>What an escaping ray sees, by direction. Null is black — a closed scene.</summary>
    public Func<Vector3, Vector3>? Sky { get; set; }

    /// <summary>Evaluates the sun on every valid texel of every card.</summary>
    /// <param name="cache">The cache.</param>
    /// <param name="towardSun">From the surface toward the sun, normalised.</param>
    /// <param name="sunIrradiance">The sun's irradiance on a perpendicular surface.</param>
    /// <exception cref="ArgumentNullException">There is no cache.</exception>
    public void Light(SurfaceCacheStore cache, Vector3 towardSun, Vector3 sunIrradiance) {
        ArgumentNullException.ThrowIfNull(cache);

        var trace = new DistanceFieldTraceSettings { MaxDistance = MaxDistance };

        for (var card = 0; card < cache.Cards.Count; card++) {
            var resolution = cache.Cards[card].Card.Resolution;

            for (var y = 0; y < resolution.Y; y++) {
                for (var x = 0; x < resolution.X; x++) {
                    var texel = new Int2(x, y);

                    if (!cache.IsValid(card, texel)) {
                        continue;
                    }

                    var surface = cache.Surface(card, texel);
                    var cosine = Vector3.Dot(surface.Normal, towardSun);

                    if (cosine <= 0f) {
                        cache.SetDirect(card, texel, Vector3.Zero);

                        continue;
                    }

                    var position = Position(cache, card, texel, surface);
                    var shadow = DistanceFieldTracer.Trace(geometry, position + (surface.Normal * Bias), towardSun, trace);

                    cache.SetDirect(
                        card,
                        texel,
                        shadow.Hit ? Vector3.Zero : sunIrradiance * (cosine / MathF.PI)
                    );
                }
            }
        }
    }

    /// <summary>One bounce: every texel gathers what the others radiated last pass.</summary>
    /// <param name="cache">The cache. Swapped on the way out — the new gather is live.</param>
    /// <returns>The largest change any texel saw, per channel maximum — the convergence measure.</returns>
    /// <exception cref="ArgumentNullException">There is no cache.</exception>
    public float Gather(SurfaceCacheStore cache) {
        ArgumentNullException.ThrowIfNull(cache);

        var trace = new DistanceFieldTraceSettings { MaxDistance = MaxDistance };
        var largest = 0f;

        for (var card = 0; card < cache.Cards.Count; card++) {
            var resolution = cache.Cards[card].Card.Resolution;

            for (var y = 0; y < resolution.Y; y++) {
                for (var x = 0; x < resolution.X; x++) {
                    var texel = new Int2(x, y);

                    if (!cache.IsValid(card, texel)) {
                        continue;
                    }

                    var surface = cache.Surface(card, texel);
                    var position = Position(cache, card, texel, surface);
                    var origin = position + (surface.Normal * Bias);
                    var tangent = Tangent(surface.Normal);
                    var bitangent = Vector3.Cross(surface.Normal, tangent);
                    var sum = Vector3.Zero;

                    for (var ray = 0; ray < Rays; ray++) {
                        var u = (ray + 0.5f) / Rays;
                        var v = RadicalInverse(ray);
                        var r = MathF.Sqrt(u);
                        var phi = 2f * MathF.PI * v;
                        var direction = (tangent * (r * MathF.Cos(phi)))
                            + (bitangent * (r * MathF.Sin(phi)))
                            + (surface.Normal * MathF.Sqrt(1f - u));

                        var hit = DistanceFieldTracer.Trace(geometry, origin, direction, trace);

                        if (hit.Hit) {
                            if (cache.TryRadiance(hit.Position, hit.Normal, out var radiance)) {
                                sum += radiance;
                            }
                        } else if (Sky is { } sky) {
                            sum += sky(direction);
                        }
                    }

                    // Cosine importance folds the lobe into the distribution: the mean IS E/π.
                    var next = sum / Rays;
                    var was = cache.Gathered(card, texel);
                    var change = Vector3.Max(next - was, was - next);

                    largest = MathF.Max(largest, MathF.Max(change.X, MathF.Max(change.Y, change.Z)));
                    cache.SetGatheredNext(card, texel, next);
                }
            }
        }

        cache.SwapGathered();

        return largest;
    }

    /// <summary>A texel's world position, from its card and its stored depth.</summary>
    static Vector3 Position(SurfaceCacheStore cache, int card, Int2 texel, in SurfaceTexel surface) {
        var shape = cache.Cards[card].Card;

        return shape.TexelOrigin(texel) - (shape.Direction * surface.Depth);
    }

    static Vector3 Tangent(Vector3 normal) =>
        Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Y) < 0.99f ? new(0f, 1f, 0f) : new(1f, 0f, 0f), normal));

    /// <summary>Van der Corput base two — the Hammersley set's second coordinate.</summary>
    static float RadicalInverse(int index) {
        var bits = (uint)index;

        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);

        return bits * 2.3283064365386963e-10f;
    }
}
