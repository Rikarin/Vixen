// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Graphics;

namespace Vixen.Shaders;

/// <summary>
///     The CLR type one shader value is written as.
/// </summary>
/// <remarks>
///     <para>
///         Stored rather than derived, because the thing that reads an <see cref="EffectData" /> has
///         no shader source and no compiler — it has bytes and a name, and it still has to intern a
///         <see cref="ParameterKey{T}" /> of the right type. A key is interned by name and carries a
///         <see cref="ParameterKey.ValueType" />, and the same name interned twice with two types
///         throws; so this has to agree, exactly, with what
///         <c>Vixen.Shaders.Generators</c> emits for the same parameter.
///     </para>
///     <para>
///         That agreement is load-bearing and it is also self-enforcing: the generated
///         <c>…Keys.Exposure</c> and the key this creates for <c>"Tonemap.exposure"</c> are the same
///         object or the interning table throws naming both. There is no third outcome where a value
///         quietly goes to the wrong offset.
///     </para>
/// </remarks>
public enum ShaderValueKind : byte {
    /// <summary>A type the engine has no C# equivalent for — a struct, or an odd matrix shape.</summary>
    /// <remarks>
    ///     Not an error. The generator skips such a parameter too, so it is a value the host writes
    ///     through its leaves or not at all, and an effect carrying one is still perfectly usable.
    /// </remarks>
    Unknown = 0,

    /// <summary><c>bool</c>.</summary>
    Bool = 1,

    /// <summary><c>int</c>.</summary>
    Int = 2,

    /// <summary><c>int2</c>.</summary>
    Int2 = 3,

    /// <summary><c>int3</c>.</summary>
    Int3 = 4,

    /// <summary><c>int4</c>.</summary>
    Int4 = 5,

    /// <summary><c>uint</c>.</summary>
    UInt = 6,

    /// <summary><c>float</c>.</summary>
    Float = 7,

    /// <summary><c>float2</c>.</summary>
    Float2 = 8,

    /// <summary><c>float3</c>.</summary>
    Float3 = 9,

    /// <summary><c>float4</c>.</summary>
    Float4 = 10,

    /// <summary><c>float3x3</c>.</summary>
    Matrix3x3 = 11,

    /// <summary><c>float4x4</c>.</summary>
    Matrix4x4 = 12,

    /// <summary><c>double</c>.</summary>
    Double = 13
}

/// <summary>One permutation value, as the text that names a variant.</summary>
/// <param name="Name">The permutation key.</param>
/// <param name="Value">Its value, in <see cref="EffectKey" />'s normal form.</param>
/// <param name="Kind">Its type — <c>bool</c>, <c>int</c> or <c>uint</c>, which is all Raven admits.</param>
/// <param name="DefaultValue">What the shader declared, for interning the key.</param>
/// <remarks>
///     The type and the default ride along with the value because
///     <see cref="Effect.UsedPermutationKeys" /> holds typed <see cref="PermutationKey{T}" />
///     objects, and a name alone cannot produce one. It is also why there is no separate list of
///     used keys on <see cref="EffectData" />: a variant's permutation values <em>are</em> the keys
///     it read, and two lists that have to agree would eventually not.
/// </remarks>
[DataContract("EffectPermutationValue")]
public sealed record EffectPermutationValue(
    string Name = "",
    string Value = "",
    ShaderValueKind Kind = ShaderValueKind.Bool,
    string DefaultValue = ""
);

/// <summary>One filled <c>compose</c> slot.</summary>
/// <param name="Slot">The slot, qualified as the compiler qualifies it.</param>
/// <param name="Shader">The shader filling it.</param>
[DataContract("EffectComposeBinding")]
public sealed record EffectComposeBinding(string Slot = "", string Shader = "");

/// <summary>One stage's compiled module.</summary>
/// <param name="Stage">Which stage.</param>
/// <param name="Bytecode">The module, as the device takes it.</param>
/// <param name="EntryPoint">Its entry point.</param>
[DataContract("EffectStageData")]
public sealed record EffectStageData(ShaderStage Stage = ShaderStage.None, byte[]? Bytecode = null, string EntryPoint = "main") {
    /// <summary>The module, never null.</summary>
    public byte[] Bytecode { get; init; } = Bytecode ?? [];
}

/// <summary>One resource the shader binds, with everything a set layout needs.</summary>
/// <param name="Name">The shader's own name for it.</param>
/// <param name="Set">Which of the four conventional sets holds it.</param>
/// <param name="Binding">Its index within that set.</param>
/// <param name="Kind">What it binds.</param>
/// <param name="Stages">Which stages reference it.</param>
/// <param name="Count">Array length; 1 for a single resource, 0 for unbounded.</param>
/// <param name="Size">
///     How many bytes a block binding is; 0 for a resource. Per binding rather than per effect
///     because a shader that marks its sets has a block in each of them.
/// </param>
/// <remarks>
///     One record for two jobs: <see cref="Effect.Bindings" />, which answers "where does
///     <c>source</c> go", and the <see cref="DescriptorSetLayoutDescription" /> the device wants.
///     They were separate in the compiler's reflection and separating them here too would leave two
///     lists that have to agree and no reason they would.
/// </remarks>
[DataContract("EffectBindingData")]
public sealed record EffectBindingData(
    string Name = "",
    DescriptorSetSlot Set = DescriptorSetSlot.PerMaterial,
    uint Binding = 0,
    DescriptorKind Kind = DescriptorKind.UniformBuffer,
    ShaderStage Stages = ShaderStage.None,
    int Count = 1,
    int Size = 0
);

/// <summary>
///     One push-constant range: what a shader is handed per draw without a descriptor.
/// </summary>
/// <param name="Stages">Which stages read it.</param>
/// <param name="Offset">Where the range starts, in bytes.</param>
/// <param name="Size">How many bytes it is.</param>
/// <param name="Members">What is inside it, so a caller can find one by name.</param>
/// <remarks>
///     <para>
///         Carried because a pipeline layout has to declare it, and a layout that does not is one a
///         push is silently dropped against — or refused outright by the validation layers. It went
///         missing for a while, and what it costs is not obscure: <c>ForwardPlus.rvn</c> pushes the
///         <em>world matrix</em>, so every object in the frame is drawn at the origin.
///     </para>
///     <para>
///         <strong>The members are here, and this comment used to say they were not.</strong> The
///         claim was that a caller building the bytes reads the generated constants — but nothing is
///         generated for a push-constant block, so the only offset a host had was the one it assumed.
///         That held while the block was one matrix at zero. It stopped holding the moment a second
///         member existed, and the failure would have been silent: a push at the wrong offset within
///         a declared range is accepted by every layer there is.
///     </para>
/// </remarks>
[DataContract("EffectPushConstantData")]
public sealed record EffectPushConstantData(
    ShaderStage Stages = ShaderStage.None,
    int Offset = 0,
    int Size = 0,
    EffectPushConstantMember[]? Members = null
);

/// <summary>One member of a push-constant block, and where in it the member sits.</summary>
/// <param name="Name">The name the shader gave it, unqualified.</param>
/// <param name="Offset">Where it starts within the block, in bytes.</param>
/// <param name="Size">How many bytes it occupies.</param>
[DataContract("EffectPushConstantMember")]
public sealed record EffectPushConstantMember(string Name = "", int Offset = 0, int Size = 0);

/// <summary>
///     One vertex attribute the shader reads, and the location it reads it at.
/// </summary>
/// <param name="Name">The parameter's name in the shader's vertex entry point.</param>
/// <param name="Location">Which attribute location it is bound to.</param>
/// <param name="Kind">Its type, which decides the format a buffer has to supply.</param>
/// <remarks>
///     <para>
///         Carried because the locations are <em>not</em> what a host would guess. A shader's
///         <c>stream</c> variables take locations first, so <c>ForwardPlus</c>'s four vertex inputs
///         start at five — and a pipeline built against zero-based attributes is refused outright,
///         which is the one merciful failure in this family.
///     </para>
///     <para>
///         The <em>layout</em> is still the mesh's: a stride, an offset and which buffer each
///         attribute comes out of are facts about how vertices were packed, and no shader knows them.
///         What the shader knows is which name it wants at which location, which is this.
///     </para>
/// </remarks>
[DataContract("EffectVertexInputData")]
public sealed record EffectVertexInputData(
    string Name = "",
    int Location = 0,
    ShaderValueKind Kind = ShaderValueKind.Unknown
);

/// <summary>Where one value sits in the constant buffer, and what type it is.</summary>
/// <param name="Name">The dotted name the shader's reflection gave it.</param>
/// <param name="Kind">Its CLR type, for interning the key.</param>
/// <param name="Offset">Byte offset within the block.</param>
/// <param name="Size">Bytes occupied.</param>
/// <param name="Set">Which set's block that offset is into.</param>
[DataContract("EffectParameterData")]
public sealed record EffectParameterData(
    string Name = "",
    ShaderValueKind Kind = ShaderValueKind.Unknown,
    int Offset = 0,
    int Size = 0,
    DescriptorSetSlot Set = DescriptorSetSlot.PerMaterial
);

/// <summary>
///     One compiled variant of a shader, in a form that outlives the process that compiled it and
///     needs nothing from the compiler to read.
/// </summary>
/// <remarks>
///     <para>
///         <strong>This is what makes "zero runtime shader compilation" a structural claim rather
///         than a policy.</strong> Raven's own <c>.rvnfx</c> already holds bytecode and reflection —
///         but <c>CompiledEffectReader</c> lives in the compiler assembly, so a runtime that read one
///         would link the parser, the lowerer and both backends. Every tier below the in-memory
///         dictionary reads <em>this</em> instead: the disk cache, the baked bundle and the answer
///         that comes back over TCP are all the same record, and translating a <c>.rvnfx</c> into it
///         happens once, on the build side, in the one place that is allowed to know both.
///     </para>
///     <para>
///         It is device-independent on purpose. Descriptor set layouts and a pipeline layout are
///         handles owned by a device that did not exist when this was baked, so what is stored is the
///         <em>description</em> and <see cref="EffectLoader" /> creates the handles. Baking handles
///         would make a bundle valid for exactly one run of one process.
///     </para>
///     <para>
///         <see cref="SourceHash" /> and <see cref="Target" /> are carried but not part of the
///         identity: two artefacts for the same <see cref="EffectKey" /> from different sources are
///         the same variant, and the hash is what says whether one of them is stale.
///     </para>
/// </remarks>
[DataContract("EffectData")]
public sealed record EffectData {
    /// <summary>The shader this is a variant of.</summary>
    public string ShaderName { get; init; } = string.Empty;

    /// <summary>The permutation values that selected it — only the keys it read.</summary>
    public EffectPermutationValue[] Permutations { get; init; } = [];

    /// <summary>What filled its <c>compose</c> slots.</summary>
    public EffectComposeBinding[] Composition { get; init; } = [];

    /// <summary>The backend that produced the modules, as Raven's <c>TargetBackends</c> names it.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>A hash of the source it was compiled from, for detecting a stale artefact.</summary>
    public string SourceHash { get; init; } = string.Empty;

    /// <summary>The compiled stages.</summary>
    public EffectStageData[] Stages { get; init; } = [];

    /// <summary>Every resource it binds.</summary>
    public EffectBindingData[] Bindings { get; init; } = [];

    /// <summary>How many bytes its uniform block needs.</summary>
    public int ConstantBufferSize { get; init; }

    /// <summary>Every value in that block, one entry per value.</summary>
    public EffectParameterData[] Parameters { get; init; } = [];

    /// <summary>The push-constant ranges its pipeline layout has to declare.</summary>
    public EffectPushConstantData[] PushConstants { get; init; } = [];

    /// <summary>The vertex attributes its vertex stage reads, with the locations it reads them at.</summary>
    public EffectVertexInputData[] VertexInputs { get; init; } = [];

    /// <summary>The key that selects this variant.</summary>
    /// <remarks>
    ///     Rebuilt rather than stored, so a record whose permutation list was edited cannot end up
    ///     filed under the key it used to have. The normal form is <see cref="EffectKey" />'s, which
    ///     is what makes a key built here and a key built from a
    ///     <see cref="ParameterCollection" /> at draw time compare equal.
    /// </remarks>
    public EffectKey ToKey() =>
        EffectKey.Of(
            ShaderName,
            Permutations.Select(value => new KeyValuePair<string, string>(value.Name, value.Value)),
            ShaderComposition.Of(Composition.Select(slot => new KeyValuePair<string, string>(slot.Slot, slot.Shader)))
        );
}

/// <summary>A set of pre-compiled variants: what a content build bakes and a shipping build loads.</summary>
/// <remarks>
///     <para>
///         Deliberately a plain list rather than a dictionary. It is serialised into a content chunk,
///         and a dictionary would put the key's normal form into the file twice — once in the record
///         and once as the entry's name — with no way to tell which one a reader should believe.
///         <see cref="EffectStore" /> indexes it on load, where there is exactly one answer.
///     </para>
/// </remarks>
[DataContract("EffectBundle")]
public sealed record EffectBundle {
    /// <summary>What the build was told to produce.</summary>
    public EffectData[] Effects { get; init; } = [];
}
