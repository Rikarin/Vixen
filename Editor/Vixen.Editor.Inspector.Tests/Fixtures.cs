// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Inspector.Tests;

/// <summary>How rough a surface is, for the enum drawer.</summary>
public enum SurfaceKind {
    /// <summary>Smooth.</summary>
    Smooth,

    /// <summary>Rough.</summary>
    Rough,

    /// <summary>Neither.</summary>
    Mixed
}

/// <summary>What a channel is used for, for the flags drawer.</summary>
[Flags]
public enum ChannelMask {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Red.</summary>
    Red = 1,

    /// <summary>Green.</summary>
    Green = 2,

    /// <summary>Blue.</summary>
    Blue = 4,

    /// <summary>All three.</summary>
    Colour = Red | Green | Blue
}

/// <summary>A texture, only as something an asset picker can be typed by.</summary>
public sealed class TextureAsset;

/// <summary>
///     The fixture doc 11's testing table asks for: a type with every attribute combination.
/// </summary>
/// <remarks>
///     Written to be read as a specification of what the generator understands. Every member here is
///     asserted on somewhere in this assembly, so a member that stops being described is a failing
///     test rather than an editor that quietly loses a row.
/// </remarks>
public sealed class WaterMaterial {
    /// <summary>A bounded float, which is a slider.</summary>
    [Inspector]
    [Range(0, 1, Step = 0.05)]
    public float Roughness = 0.2f;

    /// <summary>An HDR colour with no alpha band.</summary>
    [Inspector]
    [ColorUsage(hdr: true, ShowAlpha = false)]
    public Color4 Tint = Color4.White;

    /// <summary>An asset reference filtered by type.</summary>
    [Inspector]
    [AssetPicker(typeof(TextureAsset))]
    public AssetId NormalMap;

    /// <summary>A curve, and the member that starts the second section.</summary>
    [Inspector]
    [Header("Waves")]
    [Curve(Maximum = 2f)]
    public AnimationCurve Amplitude = AnimationCurve.Linear();

    /// <summary>Only shown while <see cref="UseFoam" /> is on.</summary>
    [Inspector]
    [ShowIf(nameof(UseFoam))]
    public float FoamWidth = 0.1f;

    /// <summary>Only shown while <see cref="UseFoam" /> is off.</summary>
    [Inspector]
    [HideIf(nameof(UseFoam))]
    public float DryWidth = 0.3f;

    /// <summary>The flag the two above are conditioned on.</summary>
    [Inspector]
    public bool UseFoam = true;

    /// <summary>A struct member, edited in place through the generated <c>ref</c> accessor.</summary>
    [Inspector]
    public Vector3 Flow = new(1f, 0f, 0f);

    /// <summary>A rotation, shown as three angles.</summary>
    [Inspector]
    public Quaternion Facing = Quaternion.Identity;

    /// <summary>An enum, which is a dropdown.</summary>
    [Inspector]
    public SurfaceKind Kind = SurfaceKind.Smooth;

    /// <summary>A flags enum, which is a multi-select.</summary>
    [Inspector]
    public ChannelMask Channels = ChannelMask.Colour;

    /// <summary>A member with a name of its own and an explicit place.</summary>
    [Inspector(Name = "Sea Level", Order = -1)]
    public float Level;

    /// <summary>A property, which gets accessors rather than a reference.</summary>
    [Inspector]
    [Tooltip("How much of the sky the surface gives back.")]
    public float Reflectivity { get; set; } = 0.5f;

    /// <summary>A computed member, which is shown and not written.</summary>
    [Inspector]
    public float Sharpness => 1f - Roughness;

    /// <summary>A member the inspector is told not to write.</summary>
    [Inspector]
    [InspectorReadOnly]
    public int Version = 3;

    /// <summary>A long string.</summary>
    [Inspector]
    [Multiline(Lines = 6)]
    public string Notes = "";
}

/// <summary>A base, to prove that a derived type shows what it inherits.</summary>
public class Emitter {
    /// <summary>Declared by the base.</summary>
    [Inspector]
    public float Rate = 10f;
}

/// <summary>A leaf, whose rows are the base's and then its own.</summary>
public sealed class SparkEmitter : Emitter {
    /// <summary>Declared by the leaf.</summary>
    [Inspector]
    public float Spread = 0.25f;
}

/// <summary>A type with no parameterless constructor, so it offers no defaults.</summary>
/// <param name="size">How big.</param>
public sealed class Uncreatable(int size) {
    /// <summary>How big.</summary>
    [Inspector]
    public int Size = size;
}
