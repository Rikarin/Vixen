// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Graphics;

namespace Vixen.Shaders;

/// <summary>One compiled shader stage's bytecode.</summary>
/// <param name="Stage">Which stage this is.</param>
/// <param name="Bytecode">SPIR-V, as the device takes it.</param>
/// <param name="EntryPoint">The entry point's name in the module.</param>
public readonly record struct EffectStage(ShaderStage Stage, ImmutableArray<byte> Bytecode, string EntryPoint);

/// <summary>
///     One compiled variant of a shader: its bytecode, and what a host has to bind to run it.
/// </summary>
/// <remarks>
///     <para>
///         Immutable, and shared by every draw that resolves to the same <see cref="EffectKey" />.
///         An effect is expensive to produce and free to reuse, which is the whole reason the
///         cache exists.
///     </para>
///     <para>
///         It holds the <em>bytecode and layout</em>, not a pipeline. A pipeline also depends on the
///         vertex layout, the render pass and the blend and depth state, none of which the shader
///         knows — so one effect backs many pipelines, and keying pipelines by effect alone is a
///         cache that returns an object drawn with the wrong blend mode.
///     </para>
/// </remarks>
public sealed class Effect {
    /// <summary>The key this was compiled for.</summary>
    public required EffectKey Key { get; init; }

    /// <summary>The compiled stages.</summary>
    public required ImmutableArray<EffectStage> Stages { get; init; }

    /// <summary>The descriptor set layouts the pipeline layout is built from, in set order.</summary>
    public ImmutableArray<DescriptorSetLayoutHandle> SetLayouts { get; init; } = [];

    /// <summary>The pipeline layout every pipeline using this effect shares.</summary>
    public PipelineLayoutHandle Layout { get; init; }

    /// <summary>How many bytes the shader's uniform block needs.</summary>
    public int ConstantBufferSize { get; init; }

    /// <summary>The value parameters this variant actually has, for filling that block.</summary>
    public ImmutableArray<EffectParameter> Parameters { get; init; } = [];

    /// <summary>The permutation keys this variant's output depended on.</summary>
    /// <remarks>
    ///     Carried on the effect rather than looked up per draw, because it is what the *next* draw's
    ///     <see cref="EffectKey" /> is built from — and reading it off the effect the last draw
    ///     resolved to is what keeps the key and the shader in agreement.
    /// </remarks>
    public ImmutableArray<ParameterKey> UsedPermutationKeys { get; init; } = [];

    /// <inheritdoc />
    public override string ToString() => Key.ToString();
}

/// <summary>Where one parameter lives in an effect's constant buffer.</summary>
/// <param name="Key">The key a host sets it through.</param>
/// <param name="Offset">Byte offset within the block.</param>
/// <param name="Size">How many bytes it occupies.</param>
public readonly record struct EffectParameter(ParameterKey Key, int Offset, int Size);
