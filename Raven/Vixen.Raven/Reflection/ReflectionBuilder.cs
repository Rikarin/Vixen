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
            PushConstants = BuildPushConstants(shader, stageFlags),
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

            // Narrowed to what *this* shader declares. The list handed in is the compilation's, and a
            // compilation is more than one shader — so a key another shader branched on would
            // otherwise be reported as having affected this output, which is what the field says it
            // means and is what an effect cache key hashes. Describing a whole library at once made
            // that visible: a two-permutation post effect reported forty.
            UsedPermutationKeys = [
                .. (usedPermutationKeys ?? [])
                    .Where(key => shader.Permutations.Any(p => string.Equals(p.Name, key, StringComparison.Ordinal)))
                    .Order(StringComparer.Ordinal)
            ],
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
    ///     Describes the push-constant range, if the shader has one.
    /// </summary>
    /// <remarks>
    ///     One range, not one per marked field, because that is what a pipeline layout takes.
    ///     Offset 0: Raven emits a shader's whole block and does not sub-range it per stage, so a
    ///     host that pushes the block pushes all of it. Std430, which is what both backends laid it
    ///     out with — the offsets here are the ones that were generated, not a second computation
    ///     of them.
    /// </remarks>
    static ImmutableArray<PushConstantInfo> BuildPushConstants(IrShader shader, ShaderStages stages) {
        if (BindingPlan.PushConstants(shader) is not { IsEmpty: false } constants) {
            return [];
        }

        var (offsets, size) = ShaderLayout.Members([.. constants.Select(c => c.Type)], LayoutRule.Std430);
        var members = ImmutableArray.CreateBuilder<MemberInfo>();

        for (var i = 0; i < constants.Length; i++) {
            Flatten(constants[i].Name, constants[i].Type, offsets[i], LayoutRule.Std430, members);
        }

        return [
            new PushConstantInfo(
                BindingPlan.PushConstantBlockName(shader),
                0,
                size,
                stages,
                members.ToImmutable()
            )
        ];
    }

    /// <summary>
    ///     Describes a set's uniform block. The loose uniforms are gathered into one block
    ///     exactly as the backends emit them, so the reported offsets are the offsets that
    ///     were generated.
    /// </summary>
    static BindingInfo Describe(PlannedBinding planned, ShaderStages stages) {
        var uniforms = planned.Members;

        // A record is packed std430 and read as an element of a buffer; a block is std140 and bound
        // as itself. Reported with the rule it was *emitted* with rather than the one a uniform block
        // usually has, because the host writes bytes at these offsets and both backends laid the
        // record out the other way.
        var rule = planned.IsRecord ? LayoutRule.Std430 : LayoutRule.Std140;
        var (offsets, size) = ShaderLayout.Members([.. uniforms.Select(u => u.Type)], rule);
        var members = ImmutableArray.CreateBuilder<MemberInfo>();

        for (var i = 0; i < uniforms.Length; i++) {
            var top = members.Count;
            Flatten(uniforms[i].Name, uniforms[i].Type, offsets[i], rule, members);

            // The declared default belongs to the member the author wrote, which is the first entry
            // Flatten adds — its leaves are fields of a struct type and their defaults, if any,
            // belong to that struct rather than to this block.
            if (uniforms[i].DefaultValue is { } declared) {
                members[top] = members[top] with { DefaultValue = Format(declared) };
            }
        }

        // The kind the host has to create a layout from. A record is a storage buffer — declaring it
        // as a uniform buffer would build a descriptor of the wrong type against a shader that reads
        // a BufferBlock, which no API checks and which reads as a frame lit by whatever those bytes
        // meant. The count is zero for the same reason every storage buffer's is: the array inside it
        // is runtime-sized, and how many records there are is the host's to decide.
        return planned.IsRecord
            ? new BindingInfo(
                planned.Binding,
                planned.Name,
                DescriptorType.StorageBuffer,
                0,
                stages,
                members.ToImmutable()
            ) { Size = size }
            : new BindingInfo(
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
                IrStorageImageType => DescriptorType.StorageImage,
                _ => kind switch {
                    IrBindingKind.Sampler => DescriptorType.Sampler,
                    IrBindingKind.StorageImage => DescriptorType.StorageImage,
                    _ => DescriptorType.SampledTexture
                }
            },
            count
        );
    }

    /// <summary>
    ///     Walks a member into the flat list, descending through structs so a nested value has
    ///     its own absolute offset and the host never has to add anything up.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An <em>array</em> of structs is descended into once, under a <c>name[].field</c> path,
    ///         and the leaf offsets are element zero's. One entry per field rather than one per
    ///         element: a <c>PunctualLight[64]</c> would otherwise contribute 512 entries saying the
    ///         same eight things at a fixed spacing, and the spacing is already reported as the
    ///         array's <c>ArrayStride</c>. Element <em>i</em>'s field is
    ///         <c>member.Offset + i * array.ArrayStride</c>.
    ///     </para>
    ///     <para>
    ///         The alternative — reporting the array and leaving its element opaque — is what this
    ///         did until the C# binding generator needed it, and it made a shader's <em>most</em>
    ///         important parameter the one thing the reflection could not describe: a light list is a
    ///         struct array in a uniform block, and so is every other per-instance table.
    ///     </para>
    /// </remarks>
    static void Flatten(
        string path,
        IrType type,
        int offset,
        LayoutRule rule,
        ImmutableArray<MemberInfo>.Builder members,
        int enclosingStride = 0
    ) {
        // A leaf inside an array element reports the *element's* stride, which is what gets a reader
        // from this field to the same field of the next element — the only spacing that means
        // anything for a leaf, whose own array stride is zero.
        var stride = type is IrArrayType array ? ShaderLayout.ArrayStride(array, rule) : enclosingStride;

        members.Add(
            new(
                path,
                ShaderDataType.From(type),
                offset,
                ShaderLayout.Size(type, rule),
                stride,
                type is IrMatrixType matrix ? ShaderLayout.MatrixStride(matrix, rule) : 0
            )
        );

        switch (type) {
            case IrStructType structType:
                Fields(path, structType, offset, rule, members, enclosingStride);
                break;

            // Element zero's layout, which every element repeats at `ArrayStride` intervals.
            case IrArrayType { Element: IrStructType element }:
                Fields($"{path}[]", element, offset, rule, members, stride);
                break;
        }
    }

    static void Fields(
        string path,
        IrStructType structType,
        int offset,
        LayoutRule rule,
        ImmutableArray<MemberInfo>.Builder members,
        int enclosingStride
    ) {
        var (offsets, _) = ShaderLayout.Members([.. structType.Fields.Select(f => f.Type)], rule);

        for (var i = 0; i < structType.Fields.Count; i++) {
            Flatten(
                $"{path}.{structType.Fields[i].Name}",
                structType.Fields[i].Type,
                offset + offsets[i],
                rule,
                members,
                enclosingStride
            );
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

        var locations = StreamPlan.InputLocations(shader, vertex);

        return [
            .. vertex.StreamInputs.Select(stream => new VertexInputInfo(
                    StreamPlan.LocationOf(shader, stream),
                    stream.Name,
                    ShaderDataType.From(stream.Type),
                    null
                )
            ),

            // A built-in has no location and nothing for the host to bind, so it is absent rather
            // than listed with a placeholder: this list *is* the vertex layout.
            .. vertex.Inputs
                .Select((io, i) => (Io: io, Location: locations[i]))
                .Where(entry => entry.Location is not null)
                .Select(entry => new VertexInputInfo(
                        entry.Location!.Value,
                        entry.Io.Name,
                        ShaderDataType.From(entry.Io.Type),
                        entry.Io.Semantic
                    )
                )
        ];
    }

    /// <summary>
    ///     The fragment stage's render targets, one entry per location.
    /// </summary>
    /// <remarks>
    ///     Location is the index, which is the render-target index — so a shader returning a
    ///     G-buffer struct reports one output per member in declaration order, and the host builds
    ///     its colour-attachment list straight off this.
    /// </remarks>
    static ImmutableArray<FragmentOutputInfo> BuildOutputs(IrShader shader) {
        if (shader.EntryPoints.FirstOrDefault(e => e.Stage == ShaderStage.Fragment) is not { } fragment) {
            return [];
        }

        return [
            .. fragment.Outputs.Select(
                (output, location) => new FragmentOutputInfo(
                    location,
                    output.Name,
                    ShaderDataType.From(output.Type),
                    output.Semantic
                )
            )
        ];
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
                            member.MatrixStride,
                            member.DefaultValue
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
            ShaderStage.Fragment => ShaderStages.Fragment,
            ShaderStage.Geometry => ShaderStages.Geometry,
            ShaderStage.Compute => ShaderStages.Compute,
            _ => ShaderStages.None
        };
}
