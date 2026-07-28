// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     Turns named values into the writes one of a shader's descriptor sets wants.
/// </summary>
/// <remarks>
///     <para>
///         One place, because there are now two callers and the rule they share is the whole point:
///         a caller says <em>what</em> a resource is called and <see cref="Effect.Bindings" /> says
///         where it goes. A material names <c>albedo</c>; a frame names <c>environment</c>; neither
///         writes down a binding index, which is the shader's to assign and to renumber when a
///         texture is added above it.
///     </para>
///     <para>
///         <strong>The name is qualified by the shader.</strong> That is how the generator interns
///         it — <c>ForwardPlusKeys.Environment</c> is <c>"ForwardPlus.environment"</c> — while the
///         shader's own name for the binding is bare. Bridging those two is the entire job, and it
///         is one line that would otherwise be written once per caller.
///     </para>
/// </remarks>
public static class EffectSetWriter {
    /// <summary>
    ///     Fills <paramref name="writes" /> for one set, and answers whether every binding was
    ///     satisfied.
    /// </summary>
    /// <param name="effect">The variant whose plan says where each name goes.</param>
    /// <param name="slot">Which set to write.</param>
    /// <param name="parameters">Where the handles come from, by the generator's qualified names.</param>
    /// <param name="block">The uniform block's buffer, or null when the set has none.</param>
    /// <param name="writes">Cleared and filled.</param>
    /// <returns>
    ///     False when a binding had nothing to fill it. A partly-written set is a validation error on
    ///     one backend and a sampled black texture on another, and neither says which name was
    ///     missing — so the caller's business is to bind nothing rather than to bind that.
    /// </returns>
    public static bool TryWrite(
        Effect effect,
        DescriptorSetSlot slot,
        ParameterCollection parameters,
        EffectConstants? block,
        IList<DescriptorWrite> writes
    ) {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(writes);

        writes.Clear();
        var wanted = 0;

        foreach (var binding in effect.Bindings) {
            if (binding.Set != slot) {
                continue;
            }

            wanted++;

            if (Write(binding, effect.Key.ShaderName, parameters, block) is { } write) {
                writes.Add(write);
            }
        }

        return wanted > 0 && writes.Count == wanted;
    }

    /// <summary>One binding, as the write that fills it — or null when nothing can.</summary>
    static DescriptorWrite? Write(
        EffectBinding binding,
        string shaderName,
        ParameterCollection parameters,
        EffectConstants? block
    ) {
        // The uniform block, which belongs to the set rather than being a value somebody set.
        if (binding.Kind is DescriptorKind.UniformBuffer or DescriptorKind.DynamicUniformBuffer) {
            return block is { Size: > 0 } filled
                ? DescriptorWrite.Uniform(binding.Binding, filled.Buffer, filled.Offset, filled.Size)
                : null;
        }

        if (!ParameterKeys.TryGet($"{shaderName}.{binding.Name}", out var key) || !parameters.Has(key)) {
            return null;
        }

        return binding.Kind switch {
            DescriptorKind.SampledTexture or DescriptorKind.StorageTexture =>
                key is ParameterKey<TextureViewHandle> texture
                    ? DescriptorWrite.Texture(binding.Binding, parameters.Get(texture))
                    : null,
            DescriptorKind.Sampler =>
                key is ParameterKey<SamplerHandle> sampler
                    ? DescriptorWrite.SamplerAt(binding.Binding, parameters.Get(sampler))
                    : null,
            DescriptorKind.StorageBuffer or DescriptorKind.DynamicStorageBuffer =>
                key is ParameterKey<BufferHandle> buffer
                    ? DescriptorWrite.Storage(binding.Binding, parameters.Get(buffer))
                    : null,
            _ => null
        };
    }
}
