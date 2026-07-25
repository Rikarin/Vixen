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
    /// <param name="set">
    ///     The descriptor set these bindings belong to. Raven has no per-binding set syntax yet,
    ///     so a shader lands in one set; the four-set convention lives in docs/plan/05.
    /// </param>
    public static RavenReflection Describe(
        IrShader shader,
        IEnumerable<string>? usedPermutationKeys = null,
        int set = 0
    ) {
        ArgumentNullException.ThrowIfNull(shader);

        var stages = shader.EntryPoints.Select(e => e.Stage).Distinct().Order().ToImmutableArray();
        var stageFlags = shader.EntryPoints.Aggregate(ShaderStages.None, (flags, e) => flags | Flag(e.Stage));

        var bindings = BuildBindings(shader, stageFlags);
        var sets = bindings.IsEmpty
            ? ImmutableArray<DescriptorSetInfo>.Empty
            : [new DescriptorSetInfo(set, bindings)];

        return new() {
            Sets = sets,
            VertexInputs = BuildVertexInputs(shader),
            Outputs = BuildOutputs(shader),
            Parameters = BuildParameters(sets),
            RequiredCapabilities = [.. IrCapabilities.Of(shader)],
            UsedPermutationKeys = [.. (usedPermutationKeys ?? []).Order(StringComparer.Ordinal)],
            Stages = stages
        };
    }

    /// <summary>Describes every shader in a module, keyed by name.</summary>
    public static ImmutableDictionary<string, RavenReflection> Describe(
        IrModule module,
        IEnumerable<string>? usedPermutationKeys = null,
        int set = 0
    ) {
        ArgumentNullException.ThrowIfNull(module);

        var keys = usedPermutationKeys?.ToArray() ?? [];
        return module.Shaders.ToImmutableDictionary(s => s.Name, s => Describe(s, keys, set), StringComparer.Ordinal);
    }

    static ImmutableArray<BindingInfo> BuildBindings(IrShader shader, ShaderStages stages) {
        // A shader's loose uniforms are gathered into one block, exactly as the backends
        // emit them, so the reported offsets are the offsets that were generated.
        var uniforms = shader.Bindings.Where(b => b.Kind == IrBindingKind.Uniform).ToArray();
        var resources = shader.Bindings.Where(b => b.Kind != IrBindingKind.Uniform).ToArray();

        var result = ImmutableArray.CreateBuilder<BindingInfo>();
        var index = 0;

        if (uniforms.Length > 0) {
            var (offsets, size) = ShaderLayout.Members([.. uniforms.Select(u => u.Type)]);
            var members = ImmutableArray.CreateBuilder<MemberInfo>();

            for (var i = 0; i < uniforms.Length; i++) {
                Flatten(uniforms[i].Name, uniforms[i].Type, offsets[i], LayoutRule.Std140, members);
            }

            result.Add(
                new BindingInfo(
                    index++,
                    shader.Name + "Uniforms",
                    DescriptorType.UniformBuffer,
                    1,
                    stages,
                    members.ToImmutable()
                ) { Size = size }
            );
        }

        foreach (var resource in resources) {
            var (type, count) = Describe(resource.Type, resource.Kind);
            result.Add(new(index++, resource.Name, type, count, stages, []));
        }

        return result.ToImmutable();
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

    static ImmutableArray<VertexInputInfo> BuildVertexInputs(IrShader shader) {
        if (shader.EntryPoints.FirstOrDefault(e => e.Stage == ShaderStage.Vertex) is not { } vertex) {
            return [];
        }

        return [
            .. vertex.Inputs.Select((io, i) => new VertexInputInfo(i, io.Name, ShaderDataType.From(io.Type), io.Semantic))
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

    static ShaderStages Flag(ShaderStage stage) =>
        stage switch {
            ShaderStage.Vertex => ShaderStages.Vertex,
            ShaderStage.Pixel => ShaderStages.Pixel,
            ShaderStage.Geometry => ShaderStages.Geometry,
            ShaderStage.Compute => ShaderStages.Compute,
            _ => ShaderStages.None
        };
}
