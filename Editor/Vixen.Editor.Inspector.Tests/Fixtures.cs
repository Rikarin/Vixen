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

/// <summary>A described object held by another, for the nested drawer.</summary>
/// <remarks>
///     ⚠ <b>A class, and not by choice.</b> <c>VXI0103</c> refuses <c>[Inspector]</c> on a member of
///     a value type — <c>InspectorMember&lt;TOwner, TValue&gt;</c> constrains the owner to a class —
///     so a struct is not describable and the nested drawer never sees one. Writing this as a
///     <c>struct</c> is a compile error, which is the test that the rule is still the rule.
/// </remarks>
public sealed class Bounds {
    /// <summary>The low corner.</summary>
    [Inspector]
    public Vector3 Low;

    /// <summary>The high corner.</summary>
    [Inspector]
    public Vector3 High;

    /// <summary>How much slack there is around it.</summary>
    [Inspector]
    [Range(0, 10)]
    public float Padding;
}

/// <summary>A described class, for the nested drawer's by-reference half.</summary>
public sealed class Attribution {
    /// <summary>Who made it.</summary>
    [Inspector]
    public string Author = "";

    /// <summary>Which year.</summary>
    [Inspector]
    public int Year = 2026;
}

/// <summary>A type whose members are the ones the composite drawers claim.</summary>
public sealed class Volume {
    /// <summary>A nested object, whose members become rows of their own.</summary>
    [Inspector]
    public Bounds Extent = new() { Low = new Vector3(-1f), High = new Vector3(1f), Padding = 0.5f };

    /// <summary>Another, to prove the drawer is about the shape rather than about one type.</summary>
    [Inspector]
    public Attribution Credit = new();

    /// <summary>A list of values.</summary>
    [Inspector]
    public List<float> Weights = [0.25f, 0.5f, 0.75f];

    /// <summary>An array, whose elements inherit the range on the member.</summary>
    [Inspector]
    [Range(0, 1)]
    public float[] Falloff = [0f, 1f];

    /// <summary>A list of assets, to prove the element rows get the attribute's drawer.</summary>
    [Inspector]
    [AssetPicker(typeof(TextureAsset))]
    public List<AssetId> Layers = [];
}

/// <summary>A type that contains itself, for the nested drawer's depth guard.</summary>
/// <remarks>
///     ⚠ A cycle is not exotic — a tree node, a linked settings block, a parent pointer. Without a
///     bound the rows would be built until the stack ran out, at the moment somebody selected one.
/// </remarks>
public sealed class Recursive {
    /// <summary>Itself.</summary>
    [Inspector]
    public Recursive? Inner;

    /// <summary>Something to look at.</summary>
    [Inspector]
    public int Depth;
}

/// <summary>A type with no parameterless constructor, so it offers no defaults.</summary>
/// <param name="size">How big.</param>
public sealed class Uncreatable(int size) {
    /// <summary>How big.</summary>
    [Inspector]
    public int Size = size;
}

/// <summary>A struct whose zero is a trap, publishing the engine's <c>Neutral</c> convention.</summary>
/// <remarks>
///     The shape of <c>ColorGrading</c>: a zeroed instance is a saturation of zero — a greyscale
///     image — so the value ticking an optional member's box supplies must be the declared neutral
///     rather than <c>default</c>.
/// </remarks>
public readonly record struct Grade {
    /// <summary>How much colour survives. 0 is greyscale, 1 leaves it alone.</summary>
    public float Saturation { get; init; }

    /// <summary>The grade that changes nothing.</summary>
    public static Grade Neutral => new() { Saturation = 1f };
}

/// <summary>A struct publishing <c>Default</c>, the convention's other name.</summary>
public readonly record struct Falloff {
    /// <summary>How far it reaches.</summary>
    public float Radius { get; init; }

    /// <summary>What a fresh one is.</summary>
    public static Falloff Default => new() { Radius = 2f };
}

/// <summary>A struct whose <c>Neutral</c> is a coincidence of naming, not the convention.</summary>
public readonly record struct Mislabeled {
    /// <summary>Something to zero.</summary>
    public float Value { get; init; }

    /// <summary>Not a <see cref="Mislabeled" />, so nothing should read it as one.</summary>
    public static string Neutral => "not a value of this type";
}

/// <summary>A type whose members are optional, for the checkbox drawer's fresh-value rules.</summary>
public sealed class GradedVolume {
    /// <summary>Null until somebody has an opinion; ticking must not supply the zero trap.</summary>
    [Inspector]
    public Grade? Grading;

    /// <summary>An optional whose type declares <c>Default</c> rather than <c>Neutral</c>.</summary>
    [Inspector]
    public Falloff? Shape;

    /// <summary>An optional whose type's <c>Neutral</c> is the wrong type, so the zero stands.</summary>
    [Inspector]
    public Mislabeled? Odd;

    /// <summary>A scalar optional, whose zero is the right fresh value.</summary>
    [Inspector]
    public float? Exposure;
}
