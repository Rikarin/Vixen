// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Reflection;

/// <summary>What a descriptor binding holds, in the RHI's terms.</summary>
public enum DescriptorType {
    /// <summary>A constant buffer, laid out <c>std140</c>.</summary>
    UniformBuffer,

    /// <summary>A read-write buffer, laid out <c>std430</c>.</summary>
    StorageBuffer,

    /// <summary>A sampled texture.</summary>
    SampledTexture,

    /// <summary>A standalone sampler.</summary>
    Sampler
}

/// <summary>Which stages reference a binding. Flags, because one binding often serves several.</summary>
[Flags]
public enum ShaderStages {
    /// <summary>No stage.</summary>
    None = 0,

    /// <summary>The vertex stage.</summary>
    Vertex = 1 << 0,

    /// <summary>The pixel/fragment stage.</summary>
    Pixel = 1 << 1,

    /// <summary>The geometry stage.</summary>
    Geometry = 1 << 2,

    /// <summary>The compute stage.</summary>
    Compute = 1 << 3
}

/// <summary>The shape of a value, flat enough for a generator or an RHI to switch on.</summary>
/// <param name="Scalar">Component type. <c>Void</c> for an opaque resource.</param>
/// <param name="Rows">
///     Vector lanes, or matrix rows. 1 for a scalar. A matrix is stored column-major, so this
///     is the length of one column.
/// </param>
/// <param name="Columns">Matrix columns. 1 for a scalar or vector.</param>
/// <param name="ArrayLength">Array length; 0 for a runtime-sized array; null when not an array.</param>
/// <param name="StructName">Name of the struct this describes, when it is one.</param>
public sealed record ShaderDataType(
    IrTypeKind Scalar,
    int Rows = 1,
    int Columns = 1,
    int? ArrayLength = null,
    string? StructName = null
) {
    /// <summary>Whether this describes a matrix rather than a scalar or vector.</summary>
    public bool IsMatrix => Columns > 1;

    /// <summary>Whether this describes an array.</summary>
    public bool IsArray => ArrayLength is not null;

    /// <summary>Whether this describes a struct.</summary>
    public bool IsStruct => StructName is not null;

    /// <summary>Reads an IR type into the engine-facing shape.</summary>
    public static ShaderDataType From(IrType type) {
        ArgumentNullException.ThrowIfNull(type);

        return type switch {
            IrScalarType scalar => new(scalar.Kind),
            IrVectorType vector => new(vector.Component.Kind, vector.Size),
            IrMatrixType matrix => new(matrix.Component.Kind, matrix.Rows, matrix.Columns),
            IrArrayType array => From(array.Element) with { ArrayLength = array.Length ?? 0 },
            IrStructType structType => new(IrTypeKind.Struct, StructName: structType.Name),
            IrTextureType or IrSamplerType => new(IrTypeKind.Void),
            _ => new(IrTypeKind.Void)
        };
    }
}

/// <summary>
///     One member of a uniform or storage block, with everything the host needs to write it
///     without asking the driver.
/// </summary>
/// <param name="Name">Dotted path from the block root, so a nested member is unambiguous.</param>
/// <param name="Type">The member's shape.</param>
/// <param name="Offset">Byte offset from the start of the block.</param>
/// <param name="Size">Bytes the member occupies. A <c>float3</c> is 12 even though it aligns to 16.</param>
/// <param name="ArrayStride">Bytes between array elements, or 0 when not an array.</param>
/// <param name="MatrixStride">Bytes between matrix columns, or 0 when not a matrix.</param>
public sealed record MemberInfo(
    string Name,
    ShaderDataType Type,
    int Offset,
    int Size,
    int ArrayStride,
    int MatrixStride
);

/// <summary>One binding within a descriptor set.</summary>
/// <param name="Binding">Binding index within the set.</param>
/// <param name="Name">Declared name.</param>
/// <param name="Type">What the binding holds.</param>
/// <param name="Count">Array length; 1 for a single resource, 0 for a runtime-sized array.</param>
/// <param name="Stages">Which stages reference it.</param>
/// <param name="Members">Block members, in declaration order. Empty for an opaque resource.</param>
public sealed record BindingInfo(
    int Binding,
    string Name,
    DescriptorType Type,
    int Count,
    ShaderStages Stages,
    ImmutableArray<MemberInfo> Members
) {
    /// <summary>Total size of the block, or 0 for an opaque resource.</summary>
    public int Size { get; init; }
}

/// <summary>The bindings of one descriptor set.</summary>
/// <param name="Set">Set index. See the four-set convention in docs/plan/05.</param>
/// <param name="Bindings">Bindings, ordered by index.</param>
public sealed record DescriptorSetInfo(int Set, ImmutableArray<BindingInfo> Bindings);

/// <summary>One vertex-stage input.</summary>
/// <param name="Location">Attribute location.</param>
/// <param name="Name">Parameter name.</param>
/// <param name="Type">The attribute's shape.</param>
/// <param name="Semantic">The <c>[Semantic("…")]</c> name, if any.</param>
public sealed record VertexInputInfo(int Location, string Name, ShaderDataType Type, string? Semantic);

/// <summary>One fragment-stage output.</summary>
/// <param name="Location">Render-target index.</param>
/// <param name="Name">Output name.</param>
/// <param name="Type">The output's shape.</param>
/// <param name="Semantic">The <c>[Semantic("…")]</c> name, if any.</param>
public sealed record FragmentOutputInfo(int Location, string Name, ShaderDataType Type, string? Semantic);

/// <summary>One push-constant range.</summary>
/// <param name="Name">Declared name.</param>
/// <param name="Offset">Byte offset within the push-constant block.</param>
/// <param name="Size">Bytes occupied.</param>
/// <param name="Stages">Which stages reference it.</param>
/// <param name="Members">Members of the range.</param>
public sealed record PushConstantInfo(
    string Name,
    int Offset,
    int Size,
    ShaderStages Stages,
    ImmutableArray<MemberInfo> Members
);

/// <summary>One specialisation constant.</summary>
/// <param name="Id">The constant's specialisation id.</param>
/// <param name="Name">Declared name.</param>
/// <param name="Type">The constant's shape.</param>
/// <param name="DefaultValue">
///     The value compiled in, which a host may override, rendered as text. See
///     <see cref="PermutationInfo.DefaultValue" /> for why it is text.
/// </param>
public sealed record SpecConstantInfo(int Id, string Name, ShaderDataType Type, string? DefaultValue);

/// <summary>
///     One <c>[Permutation]</c> key the shader declares: something a host may vary to get a
///     different compiled variant.
/// </summary>
/// <remarks>
///     <para>
///         Not a <see cref="SpecConstantInfo" />, and the difference is the whole design. A
///         specialisation constant is overridable when the pipeline is created, so the module has to
///         keep both branches; a permutation key is resolved when the shader is <em>compiled</em>,
///         which is what lets the dead branch disappear. Raven has no spec constants for exactly
///         that reason.
///     </para>
///     <para>
///         This is also not <see cref="RavenReflection.UsedPermutationKeys" />. That is what the
///         variant read and it is the cache key; this is what the shader declares and it is the same
///         for every variant — which it must be, or generated C# would change shape depending on
///         which variant happened to compile.
///     </para>
/// </remarks>
/// <param name="Name">The declared name, as a host supplies it.</param>
/// <param name="Type">Its shape: <c>bool</c>, <c>int</c> or <c>uint</c>.</param>
/// <param name="DefaultValue">
///     The declared default, which every permutation key has. Text rather than <c>object</c> so it
///     survives the JSON header of a <c>.rvnfx</c> unchanged — a boxed value comes back as a
///     <c>JsonElement</c> and stops comparing equal. <paramref name="Type" /> says how to read it,
///     and it matches how <c>CompiledEffect.PermutationKey</c> already stores values.
/// </param>
public sealed record PermutationInfo(string Name, ShaderDataType Type, string DefaultValue);

/// <summary>
///     One <c>val</c> type parameter the shader declares —
///     <c>shader Blur&lt;val TapCount: int&gt;</c>.
/// </summary>
/// <remarks>
///     There is no default, and that is the point rather than a gap: a value parameter is part of
///     the shader's signature, so a host <em>must</em> supply one (RVN2082). A generator emits it as
///     a required argument, not as a key with a fallback. The value a given variant was built with is
///     in <c>CompiledEffect.PermutationKey</c>.
/// </remarks>
/// <param name="Name">The declared name.</param>
/// <param name="Type">Its shape: <c>bool</c>, <c>int</c> or <c>uint</c>.</param>
public sealed record ValueParameterInfo(string Name, ShaderDataType Type);

/// <summary>
///     One writable value, flattened out of the descriptor sets.
/// </summary>
/// <remarks>
///     The engine-facing list. A material sets <c>Material.tint</c> without knowing which set or
///     block it lives in, and writes it at <paramref name="Offset" /> into the buffer bound at
///     (<paramref name="Set" />, <paramref name="Binding" />) — no reflection lookup at draw time.
/// </remarks>
/// <param name="Name">Dotted path, unique across the whole interface.</param>
/// <param name="Type">The value's shape.</param>
/// <param name="Set">Descriptor set holding it.</param>
/// <param name="Binding">Binding within that set.</param>
/// <param name="Offset">Byte offset within the block.</param>
/// <param name="Size">Bytes occupied.</param>
/// <param name="ArrayStride">Bytes between array elements, or 0.</param>
/// <param name="MatrixStride">Bytes between matrix columns, or 0.</param>
public sealed record ParameterInfo(
    string Name,
    ShaderDataType Type,
    int Set,
    int Binding,
    int Offset,
    int Size,
    int ArrayStride,
    int MatrixStride
);

/// <summary>
///     Everything the engine needs to bind and drive one compiled shader, without asking a
///     driver anything at runtime.
/// </summary>
/// <remarks>
///     <para>
///         This is what <c>Vixen.Shaders.Generators</c> turns into C# and what the RHI turns
///         into descriptor set layouts. It comes from the semantic and lowering phases, never
///         from parsing back emitted GLSL or SPIR-V — so it is the same on every backend by
///         construction rather than by agreement.
///     </para>
///     <para>
///         Offsets and strides are explicit on every member for the same reason: the engine
///         writes constant buffers by generated offset, and a number that came from one
///         backend's reflection is a number the other backend might not share.
///     </para>
/// </remarks>
public sealed record RavenReflection {
    /// <summary>Descriptor sets, ordered by set index.</summary>
    public ImmutableArray<DescriptorSetInfo> Sets { get; init; } = [];

    /// <summary>Vertex inputs, ordered by location. Empty unless there is a vertex stage.</summary>
    public ImmutableArray<VertexInputInfo> VertexInputs { get; init; } = [];

    /// <summary>Fragment outputs, ordered by location.</summary>
    public ImmutableArray<FragmentOutputInfo> Outputs { get; init; } = [];

    /// <summary>
    ///     Push-constant ranges. Always empty for now: Raven has no syntax for push constants,
    ///     and reporting a guess would be worse than reporting none.
    /// </summary>
    public ImmutableArray<PushConstantInfo> PushConstants { get; init; } = [];

    /// <summary>
    ///     Specialisation constants. Always empty, and always will be while permutation keys are
    ///     resolved at compile time: see <see cref="Permutations" />, which is what Raven has
    ///     instead.
    /// </summary>
    public ImmutableArray<SpecConstantInfo> SpecConstants { get; init; } = [];

    /// <summary>
    ///     The <c>[Permutation]</c> keys this shader declares, with their defaults — what a host may
    ///     vary to get a different variant.
    /// </summary>
    /// <remarks>
    ///     The same for every variant of a shader, unlike <see cref="UsedPermutationKeys" />. A host
    ///     enumerating variants and a generator emitting a <c>PermutationKey</c> both need this one;
    ///     using the other would make the answer depend on which variant it happened to be handed.
    /// </remarks>
    public ImmutableArray<PermutationInfo> Permutations { get; init; } = [];

    /// <summary>
    ///     The <c>val</c> type parameters this shader declares. Required rather than defaulted —
    ///     see <see cref="ValueParameterInfo" />.
    /// </summary>
    public ImmutableArray<ValueParameterInfo> ValueParameters { get; init; } = [];

    /// <summary>Every writable value, flattened out of <see cref="Sets" />.</summary>
    public ImmutableArray<ParameterInfo> Parameters { get; init; } = [];

    /// <summary>Target features this shader needs. See <see cref="IrCapability" />.</summary>
    public ImmutableArray<string> RequiredCapabilities { get; init; } = [];

    /// <summary>
    ///     The permutation keys that actually affected this output, sorted. The effect cache
    ///     key hashes these, not every key that was passed in.
    /// </summary>
    public ImmutableArray<string> UsedPermutationKeys { get; init; } = [];

    /// <summary>The stages this shader provides.</summary>
    public ImmutableArray<ShaderStage> Stages { get; init; } = [];
}
