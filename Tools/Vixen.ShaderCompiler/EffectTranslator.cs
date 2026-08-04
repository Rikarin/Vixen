// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Raven.Artefacts;
using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Shaders;
using RavenStage = Vixen.Raven.Symbols.ShaderStage;

namespace Vixen.ShaderCompiler;

/// <summary>
///     Turns what Raven compiled into what the engine loads.
/// </summary>
/// <remarks>
///     <para>
///         The only place in the repository where the compiler's vocabulary and the runtime's meet,
///         and it exists so that they never have to anywhere else. The stages are spelled the same
///         on both sides — <c>Fragment</c>, deliberately, so that this stays a widening from a plain
///         enum to a flags one and never a translation. What genuinely differs is the rest: Raven
///         describes a member of a struct array once with a stride and the engine wants a key per
///         element; Raven's parameter names are bare and the engine's are qualified by shader. Every
///         one of those is a rename, and every rename is somewhere a mistake could be made once and
///         then be permanent in a bundle.
///     </para>
///     <para>
///         <strong>The naming has to match the generator, not merely resemble it.</strong>
///         <c>Vixen.Shaders.Generators</c> emits <c>ParameterKeys.New&lt;float&gt;
///         ("Lighting.exposure")</c> from the same reflection at build time; this produces the key a
///         loaded effect writes through. They are interned by name, so agreeing means they are the
///         same object and disagreeing means two keys for one offset — a value set through the
///         generated one landing nowhere.
///     </para>
/// </remarks>
public static class EffectTranslator {
    /// <summary>Translates one compiled artefact.</summary>
    /// <param name="effect">What Raven produced.</param>
    /// <param name="composition">
    ///     What filled the shader's <c>compose</c> slots. Not in the artefact: Raven resolves a
    ///     composition away at compile time, so a finished module has no record of having had one —
    ///     which is exactly why the caller has to carry it into the key.
    /// </param>
    public static EffectData Translate(CompiledEffect effect, ShaderComposition composition = default) {
        ArgumentNullException.ThrowIfNull(effect);

        var reflection = effect.Reflection;
        var block = BlockOf(reflection);

        return new() {
            ShaderName = effect.Name,
            Target = effect.Target,
            SourceHash = effect.SourceHash,
            Permutations = [.. Permutations(effect)],
            Composition = [.. composition.Slots.Select(slot => new EffectComposeBinding(slot.Key, slot.Value))],
            Stages = [.. effect.Modules.Select(module => new EffectStageData(Stage(module.Stage), [.. module.Bytes], module.Name))],
            Bindings = [.. Bindings(reflection)],
            ConstantBufferSize = block?.Binding.Size ?? 0,
            Parameters = [.. Parameters(effect.Name, reflection)],
            VertexInputs = [
                .. reflection.VertexInputs.Select(input => new EffectVertexInputData(input.Name, input.Location, Kind(input.Type)))
            ],
            PushConstants = [
                .. reflection.PushConstants.Select(
                    range => new EffectPushConstantData(
                        Stages(range.Stages),
                        range.Offset,
                        range.Size,
                        [
                            .. range.Members.Select(
                                member => new EffectPushConstantMember(member.Name, member.Offset, member.Size)
                            )
                        ]
                    )
                )
            ]
        };
    }

    /// <summary>The permutation values that named this variant, qualified as the engine names them.</summary>
    /// <remarks>
    ///     Taken from <c>PermutationKey</c>, which Raven has already filtered down to the keys the
    ///     compilation read. The declared type and default come from <c>Permutations</c>, which lists
    ///     every key the shader declares — the two lists are different on purpose, and taking the
    ///     values from the wrong one is how a variant ends up keyed on a flag it never branched on.
    /// </remarks>
    static IEnumerable<EffectPermutationValue> Permutations(CompiledEffect effect) {
        foreach (var (name, value) in effect.PermutationKey) {
            var declared = effect.Reflection.Permutations.FirstOrDefault(permutation => permutation.Name == name);

            yield return new(
                Qualified(effect.Name, name),
                // "default" is Raven's way of saying nobody supplied one, which the engine spells as
                // the declared value — a key has to hold what the variant was actually built with or
                // two spellings of one variant get two cache entries.
                value == "default" ? declared?.DefaultValue ?? value : value,
                Kind(declared?.Type),
                declared?.DefaultValue ?? string.Empty
            );
        }
    }

    static IEnumerable<EffectBindingData> Bindings(RavenReflection reflection) {
        foreach (var set in reflection.Sets) {
            foreach (var binding in set.Bindings) {
                yield return new(
                    binding.Name,
                    (DescriptorSetSlot)set.Set,
                    (uint)binding.Binding,
                    Kind(binding.Type),
                    Stages(binding.Stages),
                    binding.Count,
                    binding.Size
                );
            }
        }
    }

    /// <summary>
    ///     Every value of the uniform block, one entry per value.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only the block's own parameters, matched on set <em>and</em> binding: a storage buffer
    ///         in the same set has its own member offsets, and filtering on the index alone mixes the
    ///         two into one list of offsets into different buffers.
    ///     </para>
    ///     <para>
    ///         <strong>A struct array becomes one entry per element.</strong> Raven describes
    ///         <c>lights[].color</c> once, with an offset and a stride, because that is the
    ///         declaration; a parameter key holds one value. Leaving it collapsed would make every
    ///         element after the first unreachable by name — a layered material drawing its first
    ///         layer and nothing else, with no error.
    ///     </para>
    /// </remarks>
    static IEnumerable<EffectParameterData> Parameters(string shaderName, RavenReflection reflection) {
        foreach (var block in Blocks(reflection)) {
            foreach (var parameter in Parameters(shaderName, reflection, block)) {
                yield return parameter;
            }
        }
    }

    /// <summary>Every uniform block the shader has — one per set, for a shader that marks its sets.</summary>
    /// <remarks>
    ///     Was one, because a shader that marks nothing has one. A pass that says which set each
    ///     binding is in has up to four, and a translation that kept only the first would leave three
    ///     sets' worth of values with no offsets at all.
    /// </remarks>
    static IEnumerable<UniformBlock> Blocks(RavenReflection reflection) {
        foreach (var set in reflection.Sets) {
            foreach (var binding in set.Bindings) {
                if (binding.Type == DescriptorType.UniformBuffer && binding.Members.Length > 0) {
                    yield return new(set.Set, binding);
                }
            }
        }
    }

    static IEnumerable<EffectParameterData> Parameters(string shaderName, RavenReflection reflection, UniformBlock block) {
        var slot = (DescriptorSetSlot)block.Set;

        foreach (var parameter in reflection.Parameters) {
            if (parameter.Set != block.Set || parameter.Binding != block.Binding.Binding) {
                continue;
            }

            var kind = Kind(parameter.Type);
            var marker = parameter.Name.IndexOf("[]", StringComparison.Ordinal);

            if (marker < 0) {
                yield return new(Qualified(shaderName, parameter.Name), kind, parameter.Offset, parameter.Size, slot);
                continue;
            }

            var count = ElementCount(block.Binding, parameter.Name[..marker]);

            for (var index = 0; index < count; index++) {
                var name = $"{parameter.Name[..marker]}[{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}]{parameter.Name[(marker + 2)..]}";

                yield return new(
                    Qualified(shaderName, name),
                    kind,
                    parameter.Offset + (index * parameter.ArrayStride),
                    parameter.Size,
                    slot
                );
            }
        }
    }

    /// <summary>
    ///     How many elements the array a flattened leaf belongs to has.
    /// </summary>
    /// <remarks>
    ///     Read off the block's members rather than the leaf, because the leaf does not have it: the
    ///     type of <c>lights[].color</c> is a <c>float3</c>, and the length belongs to <c>lights</c>.
    ///     A runtime-sized array reports zero and expands to nothing, which is correct — there is no
    ///     count to write keys for.
    /// </remarks>
    static int ElementCount(BindingInfo binding, string arrayName) {
        foreach (var member in binding.Members) {
            if (string.Equals(member.Name, arrayName, StringComparison.Ordinal)) {
                return member.Type.ArrayLength ?? 0;
            }
        }

        return 0;
    }

    /// <summary>The one block a shader's loose uniforms are gathered into, if it has any.</summary>
    /// <remarks>
    ///     The same rule the generator uses, deliberately: Raven puts every loose uniform into a
    ///     single block, so "the constant buffer" is well defined, and the two have to pick the same
    ///     one or the offsets the engine writes at are offsets into a buffer nobody bound.
    /// </remarks>
    static UniformBlock? BlockOf(RavenReflection reflection) {
        foreach (var set in reflection.Sets) {
            foreach (var binding in set.Bindings) {
                if (binding.Type == DescriptorType.UniformBuffer && binding.Members.Length > 0) {
                    return new(set.Set, binding);
                }
            }
        }

        return null;
    }

    /// <summary>The engine's name for a parameter: the shader's, qualified by the shader.</summary>
    /// <remarks>
    ///     Two shaders both declaring <c>exposure</c> is the ordinary case, and an interning table is
    ///     global. Unqualified, the second one to load would find a key of the first one's type and
    ///     either throw or — worse, if the types happened to agree — write at the first one's offset.
    /// </remarks>
    static string Qualified(string shaderName, string name) => $"{shaderName}.{name}";

    static Vixen.Graphics.ShaderStage Stage(RavenStage stage) =>
        stage switch {
            RavenStage.Vertex => Vixen.Graphics.ShaderStage.Vertex,
            RavenStage.Fragment => Vixen.Graphics.ShaderStage.Fragment,
            RavenStage.Geometry => Vixen.Graphics.ShaderStage.Geometry,
            RavenStage.Compute => Vixen.Graphics.ShaderStage.Compute,
            _ => Vixen.Graphics.ShaderStage.None
        };

    static Vixen.Graphics.ShaderStage Stages(ShaderStages stages) {
        var result = Vixen.Graphics.ShaderStage.None;

        if (stages.HasFlag(ShaderStages.Vertex)) {
            result |= Vixen.Graphics.ShaderStage.Vertex;
        }

        if (stages.HasFlag(ShaderStages.Fragment)) {
            result |= Vixen.Graphics.ShaderStage.Fragment;
        }

        if (stages.HasFlag(ShaderStages.Geometry)) {
            result |= Vixen.Graphics.ShaderStage.Geometry;
        }

        if (stages.HasFlag(ShaderStages.Compute)) {
            result |= Vixen.Graphics.ShaderStage.Compute;
        }

        return result;
    }

    static DescriptorKind Kind(DescriptorType type) =>
        type switch {
            DescriptorType.UniformBuffer => DescriptorKind.UniformBuffer,
            DescriptorType.StorageBuffer => DescriptorKind.StorageBuffer,
            DescriptorType.SampledTexture => DescriptorKind.SampledTexture,
            DescriptorType.Sampler => DescriptorKind.Sampler,
            DescriptorType.StorageImage => DescriptorKind.StorageTexture,
            // Spelled out rather than left to the fallback below, which would silently make the
            // scene's hierarchy a uniform buffer — a layout the driver refuses at best.
            DescriptorType.AccelerationStructure => DescriptorKind.AccelerationStructure,
            _ => DescriptorKind.UniformBuffer
        };

    /// <summary>The CLR type a value has, by the rule the binding generator uses.</summary>
    /// <remarks>
    ///     Shapes the engine has no type for — a struct, a <c>mat2</c>, an <c>int</c> vector the
    ///     mathematics library does not carry — come back as
    ///     <see cref="ShaderValueKind.Unknown" /> and the loader skips them, exactly as the generator
    ///     skips emitting a key for them. Inventing a type here would put a name in the interning
    ///     table that the generated code for the same shader does not agree with.
    /// </remarks>
    static ShaderValueKind Kind(ShaderDataType? type) {
        if (type is null || type.IsStruct) {
            return ShaderValueKind.Unknown;
        }

        if (type.IsMatrix) {
            return (type.Rows, type.Columns) switch {
                (4, 4) => ShaderValueKind.Matrix4x4,
                (3, 3) => ShaderValueKind.Matrix3x3,
                _ => ShaderValueKind.Unknown
            };
        }

        return type.Scalar switch {
            IrTypeKind.Float => type.Rows switch {
                1 => ShaderValueKind.Float,
                2 => ShaderValueKind.Float2,
                3 => ShaderValueKind.Float3,
                4 => ShaderValueKind.Float4,
                _ => ShaderValueKind.Unknown
            },
            IrTypeKind.Int => type.Rows switch {
                1 => ShaderValueKind.Int,
                2 => ShaderValueKind.Int2,
                3 => ShaderValueKind.Int3,
                4 => ShaderValueKind.Int4,
                _ => ShaderValueKind.Unknown
            },
            IrTypeKind.UInt => type.Rows == 1 ? ShaderValueKind.UInt : ShaderValueKind.Unknown,
            IrTypeKind.Bool => type.Rows == 1 ? ShaderValueKind.Bool : ShaderValueKind.Unknown,
            IrTypeKind.Double => type.Rows == 1 ? ShaderValueKind.Double : ShaderValueKind.Unknown,
            _ => ShaderValueKind.Unknown
        };
    }

    /// <summary>A uniform block and the set it sits in, which together identify its parameters.</summary>
    sealed record UniformBlock(int Set, BindingInfo Binding);
}
