// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Rendering;

/// <summary>
///     Samplers, created once per distinct description.
/// </summary>
/// <remarks>
///     <para>
///         The smallest cache in the renderer and the one with the widest reach. A sampler is pure
///         state — twelve fields, no memory, no contents — so two that describe the same filtering
///         <em>are</em> the same sampler, and every post pass in a frame wants
///         <see cref="SamplerDescription.LinearClamp" />. Vulkan puts a hard limit on how many a
///         device will create (4000 on some drivers, and 96 combined image samplers per stage on old
///         Mali), which turns "make one where you need one" from wasteful into a device-lost.
///     </para>
///     <para>
///         Keyed by the description itself, which works because it is a record struct: structural
///         equality over exactly the fields the driver compiles into the sampler, including
///         <see cref="SamplerDescription.Name" />. Two identical samplers under different names are
///         two entries, deliberately — the name is what a capture shows, and merging them would make
///         a RenderDoc frame name the wrong one.
///     </para>
/// </remarks>
public sealed class SamplerCache(IGraphicsDevice device) : IDisposable {
    readonly Dictionary<SamplerDescription, SamplerHandle> samplers = [];
    bool disposed;

    /// <summary>How many distinct samplers have been created.</summary>
    public int Count => samplers.Count;

    /// <summary>The sampler for a description, creating it the first time.</summary>
    public SamplerHandle GetOrCreate(in SamplerDescription description) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (samplers.TryGetValue(description, out var existing)) {
            return existing;
        }

        var created = device.CreateSampler(description);
        samplers[description] = created;
        return created;
    }

    /// <summary>Trilinear and clamped, which is what a full-screen pass reading a render target wants.</summary>
    /// <remarks>
    ///     Named rather than left to each caller because getting it wrong is quiet: a repeating
    ///     address mode on a post pass wraps the top row into the bottom, which shows only where an
    ///     effect taps outside the screen — a blur's edge, a temporal history's reprojection — and
    ///     looks like a bug in the effect.
    /// </remarks>
    public SamplerHandle LinearClamp => GetOrCreate(SamplerDescription.LinearClamp);

    /// <summary>Unfiltered and clamped, for a lookup where interpolation would be nonsense.</summary>
    public SamplerHandle PointClamp => GetOrCreate(SamplerDescription.PointClamp);

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var sampler in samplers.Values) {
            device.Destroy(sampler);
        }

        samplers.Clear();
    }
}
