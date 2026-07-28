// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Vfx;

/// <summary>
///     The push-constant block <see cref="VfxShaderEmitter" /> declares, as the host writes it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is a struct and not a set of arguments.</b> The emitted shader opens with five
///         push constants in a fixed order, and a push-constant block is laid out by that order.
///         Something has to know it in order to fill the bytes, and the choice is between a host that
///         spells the offsets out by hand and a declaration sitting next to the emitter that writes
///         them. This is the second one: the field order here <i>is</i> the declaration order there,
///         and the two are one file apart rather than one assembly apart.
///     </para>
///     <para>
///         <b>It is checked rather than trusted.</b> The layout rule is std430's and the compiler
///         applies it, so agreement is a claim about two pieces of code and not something a comment
///         can secure. <c>VfxGpuLayoutTests</c> compiles the emitted source and compares the
///         reflected offsets to these ones, member by member — which is why the fields are ordinary
///         and public rather than packed cleverly.
///     </para>
///     <para>
///         Five scalars, each four bytes, each four-byte aligned: <see cref="Size" /> is twenty and
///         there is no padding anywhere in it. That is one reason to prefer scalars here over, say, a
///         <c>float4</c> of packed parameters — the packed one would be the version with a layout
///         rule worth getting wrong.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VfxShaderUniforms {
    /// <summary>How big the block is, in bytes, under std430.</summary>
    public const int Size = 20;

    /// <summary>The step, in seconds. Zero for an initializer dispatch.</summary>
    public float DeltaTime;

    /// <summary>The system instance's seed.</summary>
    public uint Seed;

    /// <summary>The first particle the dispatch touches.</summary>
    public int First;

    /// <summary>How many it touches.</summary>
    public int ParticleCount;

    /// <summary>How long the system has been running, in seconds.</summary>
    public float Time;
}
