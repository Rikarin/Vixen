// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.IrradianceFields;

namespace Vixen.Rendering.SurfaceCache;

/// <summary>What a ray that hits a cached surface sees — the answer § L4 exists to give.</summary>
/// <remarks>
///     <para>
///         Every tracer below this — the irradiance field's fillers, the screen probes' gather —
///         has returned <i>nothing</i> from a hit since the day it was written, each one noting
///         that a surface's own radiance is the surface cache and the surface cache is § L4. This
///         is that seam filled: a hit inside a resident card answers with the card's outgoing
///         radiance — direct light, emissive and every bounce the radiosity has folded in — and the
///         probes above inherit multi-bounce light without changing a line.
///     </para>
///     <para>
///         <b>The fallback answers what the cache cannot.</b> An uncached hit gives whatever the
///         wrapped source gave — black, for every fixture that predates the cache — so composing
///         this over an existing scene changes exactly the hits the cache covers and nothing else.
///         The sky passes straight through, because the cache is a statement about surfaces.
///     </para>
/// </remarks>
public sealed class SurfaceCacheRadiance(SurfaceCacheStore cache, IRadianceSource fallback) : IRadianceSource {
    readonly SurfaceCacheStore cache = cache ?? throw new ArgumentNullException(nameof(cache));
    readonly IRadianceSource fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

    /// <inheritdoc />
    public Vector3 Sky(Vector3 direction) => fallback.Sky(direction);

    /// <inheritdoc />
    public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) =>
        cache.TryRadiance(position, normal, out var radiance)
            ? radiance
            : fallback.Surface(position, normal, direction);
}
