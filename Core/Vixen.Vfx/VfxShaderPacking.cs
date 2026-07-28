// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Vfx;

/// <summary>
///     Moves one attribute between <see cref="ParticleBuffer" /> and the bytes a storage buffer holds.
/// </summary>
/// <remarks>
///     <para>
///         <b>Here rather than in the host, because the layout is decided here.</b>
///         <see cref="VfxShaderEmitter" /> chose to declare a three-component attribute as
///         <c>float4</c> — std430 gives a <c>vec3</c> array a stride of sixteen either way, so
///         declaring the padding is cheaper than pretending it is not there. That decision has a
///         consequence for whoever fills the buffer, and the consequence belongs next to the
///         decision. A renderer that wrote a packed <c>Vector3[]</c> straight into the buffer would
///         read every particle after the first from the wrong offset, and nothing would say so.
///     </para>
///     <para>
///         <b>Both directions, because the interesting test needs both.</b> Uploading is what a
///         spawn does; downloading is what the CPU/GPU agreement test does, and it is the only way to
///         ask the two backends whether they agree. It is a stall by construction — see
///         <c>VfxGpuSimulation</c>, which says when that is worth paying.
///     </para>
/// </remarks>
public static class VfxShaderPacking {
    /// <summary>How many bytes a run of particles occupies in one binding's buffer.</summary>
    public static int Size(in VfxShaderBinding binding, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return binding.Stride * count;
    }

    /// <summary>Writes the first <paramref name="count" /> particles of one attribute into bytes.</summary>
    /// <param name="particles">Where the attribute lives.</param>
    /// <param name="binding">Which attribute, and what its stride is.</param>
    /// <param name="count">How many particles, from the start of the buffer.</param>
    /// <param name="destination">At least <see cref="Size" /> bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="particles" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="destination" /> is too small, or the attribute has no storage in
    ///     <paramref name="particles" />.
    /// </exception>
    public static void Pack(ParticleBuffer particles, in VfxShaderBinding binding, int count, Span<byte> destination) {
        ArgumentNullException.ThrowIfNull(particles);
        Check(binding, count, destination.Length);

        if (binding.Slot >= 0) {
            PackLanes(particles.Custom(binding.Slot), particles.Lanes(binding.Slot), count, destination);

            return;
        }

        switch (binding.Attribute) {
            case VfxAttribute.Position: {
                PackPadded(particles.Position, count, destination);

                break;
            }

            case VfxAttribute.Velocity: {
                PackPadded(particles.Velocity, count, destination);

                break;
            }

            case VfxAttribute.Colour: {
                Copy<Vector4>(particles.Colour, count, destination);

                break;
            }

            case VfxAttribute.Size: {
                Copy<float>(particles.Size, count, destination);

                break;
            }

            case VfxAttribute.Lifetime: {
                Copy<float>(particles.Lifetime, count, destination);

                break;
            }

            case VfxAttribute.Age: {
                Copy<float>(particles.Age, count, destination);

                break;
            }

            case VfxAttribute.Rotation: {
                Copy<float>(particles.Rotation, count, destination);

                break;
            }

            case VfxAttribute.AngularVelocity: {
                Copy<float>(particles.AngularVelocity, count, destination);

                break;
            }

            case VfxAttribute.Identifier: {
                Copy<uint>(particles.Identifier, count, destination);

                break;
            }

            default: {
                throw new ArgumentException(
                    $"{binding.Attribute} is not an attribute a shader binds one buffer for.",
                    nameof(binding)
                );
            }
        }
    }

    /// <summary>Reads bytes back into the first <paramref name="count" /> particles of one attribute.</summary>
    /// <param name="source">At least <see cref="Size" /> bytes, as the shader left them.</param>
    /// <param name="binding">Which attribute, and what its stride is.</param>
    /// <param name="count">How many particles, from the start of the buffer.</param>
    /// <param name="particles">Where to put them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="particles" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="source" /> is too small, or the attribute has no storage in
    ///     <paramref name="particles" />.
    /// </exception>
    public static void Unpack(ReadOnlySpan<byte> source, in VfxShaderBinding binding, int count, ParticleBuffer particles) {
        ArgumentNullException.ThrowIfNull(particles);
        Check(binding, count, source.Length);

        if (binding.Slot >= 0) {
            UnpackLanes(source, particles.Lanes(binding.Slot), count, particles.Custom(binding.Slot));

            return;
        }

        switch (binding.Attribute) {
            case VfxAttribute.Position: {
                UnpackPadded(source, count, particles.Position);

                break;
            }

            case VfxAttribute.Velocity: {
                UnpackPadded(source, count, particles.Velocity);

                break;
            }

            case VfxAttribute.Colour: {
                Copy<Vector4>(source, count, particles.Colour);

                break;
            }

            case VfxAttribute.Size: {
                Copy<float>(source, count, particles.Size);

                break;
            }

            case VfxAttribute.Lifetime: {
                Copy<float>(source, count, particles.Lifetime);

                break;
            }

            case VfxAttribute.Age: {
                Copy<float>(source, count, particles.Age);

                break;
            }

            case VfxAttribute.Rotation: {
                Copy<float>(source, count, particles.Rotation);

                break;
            }

            case VfxAttribute.AngularVelocity: {
                Copy<float>(source, count, particles.AngularVelocity);

                break;
            }

            case VfxAttribute.Identifier: {
                Copy<uint>(source, count, particles.Identifier);

                break;
            }

            default: {
                throw new ArgumentException(
                    $"{binding.Attribute} is not an attribute a shader binds one buffer for.",
                    nameof(binding)
                );
            }
        }
    }

    static void Check(in VfxShaderBinding binding, int count, int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var needed = Size(binding, count);

        if (length < needed) {
            throw new ArgumentException(
                $"{count} particles of '{binding.Name}' need {needed} bytes and there are {length}.",
                nameof(count)
            );
        }
    }

    /// <summary>An attribute whose CPU form is its GPU form: one memcpy, both ways.</summary>
    static void Copy<T>(Span<T> attribute, int count, Span<byte> destination) where T : unmanaged {
        Storage(attribute, count).CopyTo(MemoryMarshal.Cast<byte, T>(destination));
    }

    static void Copy<T>(ReadOnlySpan<byte> source, int count, Span<T> attribute) where T : unmanaged {
        MemoryMarshal.Cast<byte, T>(source)[..count].CopyTo(Storage(attribute, count));
    }

    /// <summary>Three components on this side, four on the other. The fourth is written, as zero.</summary>
    /// <remarks>
    ///     Written rather than skipped so that the buffer holds no uninitialized bytes — a shader that
    ///     reads <c>.w</c> of a position would otherwise read whatever the allocator left, which is
    ///     reproducible on one driver and not on the next.
    /// </remarks>
    static void PackPadded(Span<Vector3> attribute, int count, Span<byte> destination) {
        var padded = MemoryMarshal.Cast<byte, Vector4>(destination);
        var source = Storage(attribute, count);

        for (var index = 0; index < count; index++) {
            padded[index] = new(source[index], 0f);
        }
    }

    static void UnpackPadded(ReadOnlySpan<byte> source, int count, Span<Vector3> attribute) {
        var padded = MemoryMarshal.Cast<byte, Vector4>(source);
        var destination = Storage(attribute, count);

        for (var index = 0; index < count; index++) {
            destination[index] = new(padded[index].X, padded[index].Y, padded[index].Z);
        }
    }

    /// <summary>
    ///     A custom attribute, whose CPU storage is a flat run of lanes.
    /// </summary>
    /// <remarks>
    ///     One and four lanes pack straight across — four floats on this side are a <c>float4</c> on
    ///     that one. Three do not, for the reason the whole file exists.
    /// </remarks>
    static void PackLanes(Span<float> attribute, int lanes, int count, Span<byte> destination) {
        if (lanes == 3) {
            var padded = MemoryMarshal.Cast<byte, Vector4>(destination);

            for (var index = 0; index < count; index++) {
                var lane = index * 3;

                padded[index] = new(attribute[lane], attribute[lane + 1], attribute[lane + 2], 0f);
            }

            return;
        }

        attribute[..(count * lanes)].CopyTo(MemoryMarshal.Cast<byte, float>(destination));
    }

    static void UnpackLanes(ReadOnlySpan<byte> source, int lanes, int count, Span<float> attribute) {
        if (lanes == 3) {
            var padded = MemoryMarshal.Cast<byte, Vector4>(source);

            for (var index = 0; index < count; index++) {
                var lane = index * 3;

                attribute[lane] = padded[index].X;
                attribute[lane + 1] = padded[index].Y;
                attribute[lane + 2] = padded[index].Z;
            }

            return;
        }

        MemoryMarshal.Cast<byte, float>(source)[..(count * lanes)].CopyTo(attribute);
    }

    /// <summary>The first <paramref name="count" /> of an attribute, or a stated failure.</summary>
    /// <remarks>
    ///     An attribute a graph does not declare has an empty span rather than a null one — see
    ///     <see cref="ParticleBuffer" /> — so slicing it would throw an index message naming neither
    ///     the attribute nor the graph. The binding list only ever names attributes the graph has, so
    ///     reaching this is a host binding a shader against the wrong buffer.
    /// </remarks>
    static Span<T> Storage<T>(Span<T> attribute, int count) {
        if (attribute.Length < count) {
            throw new ArgumentException(
                $"The particle buffer holds {attribute.Length} of this attribute and {count} were asked for. "
                + "The shader was emitted from a different graph than the buffer was allocated for.",
                nameof(count)
            );
        }

        return attribute[..count];
    }
}
