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
    /// <remarks>
    ///     <strong>One entry per value, which means one per array element.</strong> Raven's
    ///     reflection describes an array member once — <c>layers[].baseColor</c>, with an offset and
    ///     an array stride — because that is the shader's declaration. A parameter key holds one
    ///     value, so a provider building this expands that into <c>layers[0].baseColor</c>,
    ///     <c>layers[1].baseColor</c> and so on, each at <c>offset + index × stride</c>. Collapsing
    ///     them back into one entry would make every element after the first unreachable through the
    ///     parameter path, which is silent: a layered material would draw its first layer and nothing
    ///     else.
    /// </remarks>
    public ImmutableArray<EffectParameter> Parameters { get; init; } = [];

    /// <summary>Where each of its resources sits, by the shader's name for it.</summary>
    /// <remarks>
    ///     What lets a caller say "bind the scene colour to <c>source</c>" instead of "bind it to
    ///     binding 1". Empty for a provider that does not report it, in which case a caller has to
    ///     supply indices itself — which is what every caller did before this existed.
    /// </remarks>
    public ImmutableArray<EffectBinding> Bindings { get; init; } = [];

    /// <summary>Where a named resource sits, or null when this variant has no such name.</summary>
    /// <remarks>
    ///     Linear over a handful of entries rather than a dictionary: a shader has a few resources,
    ///     this is asked once per binding when a frame is built rather than per draw, and a dictionary
    ///     per effect would cost more to build than every lookup it ever serves.
    /// </remarks>
    public EffectBinding? BindingOf(string name) {
        foreach (var binding in Bindings) {
            if (string.Equals(binding.Name, name, StringComparison.Ordinal)) {
                return binding;
            }
        }

        return null;
    }

    /// <summary>
    ///     Whether this is something to draw with until the real variant arrives.
    /// </summary>
    /// <remarks>
    ///     Carried on the effect rather than known only to the thing that handed it out, because what
    ///     needs to know is downstream of that: a render feature resolves a variant once and keeps the
    ///     answer, and it has to be able to tell that the answer it kept is provisional. Without this
    ///     the placeholder is drawn forever — the compile finishes, the cache updates, and nothing
    ///     asks again.
    /// </remarks>
    public bool IsPlaceholder { get; init; }

    /// <summary>The permutation keys this variant's output depended on.</summary>
    /// <remarks>
    ///     Carried on the effect rather than looked up per draw, because it is what the *next* draw's
    ///     <see cref="EffectKey" /> is built from — and reading it off the effect the last draw
    ///     resolved to is what keeps the key and the shader in agreement.
    /// </remarks>
    public ImmutableArray<ParameterKey> UsedPermutationKeys { get; init; } = [];

    /// <summary>This effect again, marked as something to draw with until the real one arrives.</summary>
    /// <remarks>
    ///     <para>
    ///         A copy rather than a mutation, because the original is in the cache under its own key
    ///         and is a perfectly good effect — the placeholder shader resolved like any other. What
    ///         differs is only the role it is being put in, and a host sets that role once:
    ///     </para>
    ///     <code>effects.Placeholder = effects.Resolve(EffectKey.Of("Placeholder"))?.AsPlaceholder();</code>
    ///     <para>
    ///         Written out rather than a <c>with</c> expression, which would mean making this a record
    ///         — and a record's value equality would change what <c>(Effect, Stage)</c> means as a
    ///         dictionary key. The shader-module cache is keyed by exactly that and wants reference
    ///         identity.
    ///     </para>
    /// </remarks>
    public Effect AsPlaceholder() =>
        new() {
            Key = Key,
            Stages = Stages,
            SetLayouts = SetLayouts,
            Layout = Layout,
            ConstantBufferSize = ConstantBufferSize,
            Parameters = Parameters,
            Bindings = Bindings,
            UsedPermutationKeys = UsedPermutationKeys,
            IsPlaceholder = true
        };

    /// <inheritdoc />
    public override string ToString() => IsPlaceholder ? $"{Key} (placeholder)" : Key.ToString();
}

/// <summary>Where one of a shader's resources sits, by the name the shader gave it.</summary>
/// <param name="Name">The shader's own name for it — <c>source</c>, <c>sourceSampler</c>.</param>
/// <param name="Set">Which of the four conventional sets holds it.</param>
/// <param name="Binding">Its index within that set.</param>
/// <param name="Kind">What it binds.</param>
/// <remarks>
///     <para>
///         The half of the reflection that never reached the runtime. An effect carried its parameter
///         <em>offsets</em> from the day it was written and not its binding <em>indices</em>, so
///         everything that had to fill a descriptor set — a compositor node, a material, a per-view
///         block — was handed a number by whoever configured it.
///     </para>
///     <para>
///         That number is the shader's decision: Raven assigns it from declaration order within a
///         set, so adding a texture above another renumbers everything below it. Generated constants
///         fix it for code that can reference generated code, and fix nothing for a compositor
///         document naming a resource or a shader loaded from a bundle at run time. This is what
///         those need.
///     </para>
/// </remarks>
public readonly record struct EffectBinding(
    string Name,
    DescriptorSetSlot Set,
    uint Binding,
    DescriptorKind Kind
);

/// <summary>Where one parameter lives in an effect's constant buffer.</summary>
/// <param name="Key">The key a host sets it through.</param>
/// <param name="Offset">Byte offset within the block.</param>
/// <param name="Size">How many bytes it occupies.</param>
public readonly record struct EffectParameter(ParameterKey Key, int Offset, int Size);
