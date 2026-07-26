// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Reflection;

/// <summary>
///     Reads a lowered shader into the <see cref="RavenReflection" /> the engine binds against.
/// </summary>
/// <remarks>
///     Built from the IR, never by parsing emitted GLSL or SPIR-V back. That is what makes the
///     numbers identical on every backend: the SPIR-V decorations and these offsets come from
///     one <see cref="ShaderLayout" />, so there is nothing for two backends to disagree about.
///     It also means a value behind a false permutation is already gone, so the reported
///     interface is the one this variant actually has.
/// </remarks>
public static class ReflectionBuilder {
    /// <summary>
    ///     Describes one shader.
    /// </summary>
    /// <param name="shader">The lowered shader.</param>
    /// <param name="usedPermutationKeys">
    ///     Keys the compilation consulted — <c>Compilation.UsedPermutationKeys</c>. Passed in
    ///     rather than read from the IR because it is a fact about how this variant was
    ///     produced, not about its contents.
    /// </param>
    public static RavenReflection Describe(
        IrShader shader,
        IEnumerable<string>? usedPermutationKeys = null
    ) {
        ArgumentNullException.ThrowIfNull(shader);

        var stages = shader.EntryPoints.Select(e => e.Stage).Distinct().Order().ToImmutableArray();
        var stageFlags = shader.EntryPoints.Aggregate(ShaderStages.None, (flags, e) => flags | Flag(e.Stage));

        var sets = BuildSets(shader, stageFlags);

        return new() {
            Sets = sets,
            VertexInputs = BuildVertexInputs(shader),
            Outputs = BuildOutputs(shader),
            Parameters = BuildParameters(sets),
            Permutations = [
                .. shader.Permutations.Select(p => new PermutationInfo(
                    p.Name,
                    ShaderDataType.From(p.Type),
                    Format(p.DefaultValue)
                ))
            ],
            ValueParameters = [
                .. shader.ValueParameters.Select(p => new ValueParameterInfo(p.Name, ShaderDataType.From(p.Type)))
            ],
            RequiredCapabilities = [.. IrCapabilities.Of(shader)],
            UsedPermutationKeys = [.. (usedPermutationKeys ?? []).Order(StringComparer.Ordinal)],
            Stages = stages
        };
    }

    /// <summary>Describes every shader in a module, keyed by name.</summary>
    public static ImmutableDictionary<string, RavenReflection> Describe(
        IrModule module,
        IEnumerable<string>? usedPermutationKeys = null
    ) {
        ArgumentNullException.ThrowIfNull(module);

        var keys = usedPermutationKeys?.ToArray() ?? [];
        return module.Shaders.ToImmutableDictionary(s => s.Name, s => Describe(s, keys), StringComparer.Ordinal);
    }

    /// <summary>
    ///     Reads the descriptor sets off <see cref="BindingPlan" />, so the set and binding
    ///     indices reported here are the ones the backends decorated.
    /// </summary>
    static ImmutableArray<DescriptorSetInfo> BuildSets(IrShader shader, ShaderStages stages) {
        var sets = ImmutableArray.CreateBuilder<DescriptorSetInfo>();

        foreach (var group in BindingPlan.Of(shader).GroupBy(b => b.Set)) {
            var bindings = ImmutableArray.CreateBuilder<BindingInfo>();

            foreach (var planned in group) {
                bindings.Add(
                    planned switch {
                        { Kind: IrBindingKind.StorageBuffer, Resource: { } buffer } =>
                            DescribeBuffer(planned, buffer, stages),
                        { Resource: { } resource } => Describe(planned, resource, stages),
                        _ => Describe(planned, stages)
                    }
                );
            }

            sets.Add(new((int)group.Key, bindings.ToImmutable()));
        }

        return sets.ToImmutable();
    }

    /// <summary>
    ///     Describes a set's uniform block. The loose uniforms are gathered into one block
    ///     exactly as the backends emit them, so the reported offsets are the offsets that
    ///     were generated.
    /// </summary>
    static BindingInfo Describe(PlannedBinding planned, ShaderStages stages) {
        var uniforms = planned.Members;
        var (offsets, size) = ShaderLayout.Members([.. uniforms.Select(u => u.Type)]);
        var members = ImmutableArray.CreateBuilder<MemberInfo>();

        for (var i = 0; i < uniforms.Length; i++) {
            Flatten(uniforms[i].Name, uniforms[i].Type, offsets[i], LayoutRule.Std140, members);
        }

        return new BindingInfo(
            planned.Binding,
            planned.Name,
            DescriptorType.UniformBuffer,
            1,
            stages,
            members.ToImmutable()
        ) { Size = size };
    }

    /// <summary>
    ///     Describes a storage buffer: the element's std430 layout, and a count of 0.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The offsets are the <em>element's</em>, relative to the start of one element, because
    ///         that is what the host needs: it writes an array of these and the stride is what gets it
    ///         from one to the next. Reporting them relative to the block would be reporting element
    ///         zero's, which is the same numbers with a misleading name.
    ///     </para>
    ///     <para>
    ///         <c>Count</c> is 0, which is this schema's spelling for "the host decides" — the same
    ///         convention <see cref="BindingInfo.Count" /> already documents for a runtime-sized array.
    ///         <c>Size</c> is one element's stride rather than the block's, which has none.
    ///     </para>
    /// </remarks>
    static BindingInfo DescribeBuffer(PlannedBinding planned, IrBinding buffer, ShaderStages stages) {
        var members = ImmutableArray.CreateBuilder<MemberInfo>();
        var stride = 0;

        if (buffer.Type is IrArrayType array) {
            stride = ShaderLayout.ArrayStride(array, LayoutRule.Std430);
            Flatten(buffer.Name, array.Element, 0, LayoutRule.Std430, members);
        }

        return new BindingInfo(
            planned.Binding,
            buffer.Name,
            DescriptorType.StorageBuffer,
            0,
            stages,
            members.ToImmutable()
        ) { Size = stride, IsWritable = buffer.IsWritable };
    }

    static BindingInfo Describe(PlannedBinding planned, IrBinding resource, ShaderStages stages) {
        var (type, count) = Describe(resource.Type, resource.Kind);
        return new(planned.Binding, resource.Name, type, count, stages, []);
    }

    static (DescriptorType Type, int Count) Describe(IrType type, IrBindingKind kind) {
        // An array of resources is one binding with a count, not several bindings.
        var count = 1;
        if (type is IrArrayType array) {
            count = array.Length ?? 0;
            type = array.Element;
        }

        return (
            type switch {
                IrSamplerType => DescriptorType.Sampler,
                IrTextureType => DescriptorType.SampledTexture,
                _ => kind == IrBindingKind.Sampler ? DescriptorType.Sampler : DescriptorType.SampledTexture
            },
            count
        );
    }

    /// <summary>
    ///     Walks a member into the flat list, descending through structs so a nested value has
    ///     its own absolute offset and the host never has to add anything up.
    /// </summary>
    static void Flatten(
        string path,
        IrType type,
        int offset,
        LayoutRule rule,
        ImmutableArray<MemberInfo>.Builder members
    ) {
        members.Add(
            new(
                path,
                ShaderDataType.From(type),
                offset,
                ShaderLayout.Size(type, rule),
                type is IrArrayType array ? ShaderLayout.ArrayStride(array, rule) : 0,
                type is IrMatrixType matrix ? ShaderLayout.MatrixStride(matrix, rule) : 0
            )
        );

        // Only a struct is descended into. An array of structs would need one entry per
        // element, which is what ArrayStride is for instead.
        if (type is not IrStructType structType) {
            return;
        }

        var (offsets, _) = ShaderLayout.Members([.. structType.Fields.Select(f => f.Type)], rule);
        for (var i = 0; i < structType.Fields.Count; i++) {
            Flatten($"{path}.{structType.Fields[i].Name}", structType.Fields[i].Type, offset + offsets[i], rule, members);
        }
    }

    /// <summary>
    ///     The vertex stage's attributes: the streams it reads, then its own parameters.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A stream the vertex stage reads is a vertex attribute — there is no earlier stage for
    ///         it to have come from — so it belongs here alongside the entry point's parameters, and
    ///         the engine builds one vertex layout from the list.
    ///     </para>
    ///     <para>
    ///         Locations come from <see cref="StreamPlan" />, which puts the streams first. That is
    ///         why the parameters start at <see cref="StreamPlan.ParameterBase" /> rather than at 0:
    ///         a stream's location has to be the same number in the stage that writes it and the
    ///         stage that reads it, and only a shader-wide rule can give both the same answer.
    ///     </para>
    /// </remarks>
    static ImmutableArray<VertexInputInfo> BuildVertexInputs(IrShader shader) {
        if (shader.EntryPoints.FirstOrDefault(e => e.Stage == ShaderStage.Vertex) is not { } vertex) {
            return [];
        }

        var parameterBase = StreamPlan.ParameterBase(shader);

        return [
            .. vertex.StreamInputs.Select(stream => new VertexInputInfo(
                    StreamPlan.LocationOf(shader, stream),
                    stream.Name,
                    ShaderDataType.From(stream.Type),
                    null
                )
            ),
            .. vertex.Inputs.Select((io, i) => new VertexInputInfo(
                    parameterBase + i,
                    io.Name,
                    ShaderDataType.From(io.Type),
                    io.Semantic
                )
            )
        ];
    }

    static ImmutableArray<FragmentOutputInfo> BuildOutputs(IrShader shader) {
        if (shader.EntryPoints.FirstOrDefault(e => e.Stage == ShaderStage.Pixel) is not { Output: { } output }) {
            return [];
        }

        return [new FragmentOutputInfo(0, output.Name, ShaderDataType.From(output.Type), output.Semantic)];
    }

    /// <summary>
    ///     Flattens the block members of every binding into the engine-facing list. Structs
    ///     contribute their leaves; the struct entry itself is not writable on its own.
    /// </summary>
    static ImmutableArray<ParameterInfo> BuildParameters(ImmutableArray<DescriptorSetInfo> sets) {
        var result = ImmutableArray.CreateBuilder<ParameterInfo>();

        foreach (var set in sets) {
            foreach (var binding in set.Bindings) {
                foreach (var member in binding.Members) {
                    if (member.Type.IsStruct) {
                        continue;
                    }

                    result.Add(
                        new(
                            member.Name,
                            member.Type,
                            set.Set,
                            binding.Binding,
                            member.Offset,
                            member.Size,
                            member.ArrayStride,
                            member.MatrixStride
                        )
                    );
                }
            }
        }

        return result.ToImmutable();
    }

    /// <summary>
    ///     Renders a declared default as text, in the spelling a host supplies it in — so
    ///     <c>--define UseDetail=true</c> and the reported default read the same way round.
    /// </summary>
    static string Format(object? value) =>
        value switch {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };

    static ShaderStages Flag(ShaderStage stage) =>
        stage switch {
            ShaderStage.Vertex => ShaderStages.Vertex,
            ShaderStage.Pixel => ShaderStages.Pixel,
            ShaderStage.Geometry => ShaderStages.Geometry,
            ShaderStage.Compute => ShaderStages.Compute,
            _ => ShaderStages.None
        };
}
