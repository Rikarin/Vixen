// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Spatial;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Audio.Assets;

/// <summary>A room, as a file declares it.</summary>
/// <remarks>
///     <para>
///         <b>What a designer needs a programmer for, moved into content</b> — the same argument as
///         <see cref="AudioParameterAsset" />. "The cellar is boomier" should be an edit to this and
///         not a change to a scene or a rebuild.
///     </para>
///     <para>
///         <b>There is no position on it, and that is the point.</b> One of these describes a *kind*
///         of room; where the rooms are is a matter for the entities carrying it. Twenty cathedral
///         entities share one asset and are twenty different cathedrals, which is what makes changing
///         all of them one edit.
///     </para>
///     <para>
///         <b>The parameter it drives is a name and not a reverb.</b> A zone pushes a number into a
///         named mixer parameter; what that number does — an aux send opening, a wet level, a filter
///         closing — is a curve in an <see cref="AudioParameterAsset" />. Two indirections rather than
///         one, and the reason is that "which room am I in" and "what does that room sound like" are
///         edited by the same person at different times.
///     </para>
/// </remarks>
[DataContract("AudioZone")]
public sealed record AudioZoneAsset {
    /// <summary>What it is called, for a tool to list.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Which mixer parameter it drives while the listener is inside it.</summary>
    public string Parameter { get; init; } = string.Empty;

    /// <summary>Sphere or box.</summary>
    /// <remarks>A box for a corridor, where a sphere reaching the far end would also reach through the ceiling.</remarks>
    public AudioZoneShape Shape { get; init; }

    /// <summary>For a sphere, its radius in X. For a box, half its size on each axis.</summary>
    public Vector3 Extent { get; init; } = new(5f, 5f, 5f);

    /// <summary>How far in from the edge it reaches full strength, in world units.</summary>
    /// <remarks>Zero is a hard edge, which a sealed door occasionally wants and a doorway never does.</remarks>
    public float Blend { get; init; } = 1f;

    /// <summary>How strong it is well inside.</summary>
    public float Strength { get; init; } = 1f;

    /// <summary>Which zone wins where two overlap. Higher takes it.</summary>
    /// <remarks>A cupboard inside a cathedral should sound like a cupboard, so give the cupboard the higher number.</remarks>
    public int Priority { get; init; }

    /// <summary>The zone this describes.</summary>
    /// <returns>The zone, with no position — whatever places it decides that.</returns>
    public AudioReverbZone ToZone() => new() {
        Parameter = Parameter,
        Shape = Shape,
        Extent = Extent,
        Blend = Blend,
        Strength = Strength,
        Priority = Priority
    };
}
