// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;

namespace Vixen.Raven.IR;

/// <summary>How a shader-level binding is presented to the pipeline.</summary>
public enum IrBindingKind {
    /// <summary>A constant-buffer / uniform entry.</summary>
    Uniform,
    Texture,
    Sampler,

    /// <summary>
    ///     A storage buffer: a block of its own, laid out std430, holding one runtime-sized array.
    /// </summary>
    /// <remarks>
    ///     This is the one position where an <see cref="IrArrayType" /> with no length is legal, and
    ///     that is exactly the spec's rule — an unsized array may only be a storage block's last
    ///     member. Everywhere else it stays <c>RVN4001</c>, so the IR now expresses precisely what
    ///     the targets allow rather than a superset of it.
    /// </remarks>
    StorageBuffer,

    /// <summary>A storage image: a texture the shader stores into, with an explicit texel format.</summary>
    StorageImage,

    /// <summary>
    ///     A push constant: a member of the one small block the host writes into the command buffer.
    /// </summary>
    /// <remarks>
    ///     It has no descriptor, so <see cref="IrBinding.Set" /> and the <c>(set, binding)</c> pair
    ///     say nothing about it — which is why it is a kind here rather than a fifth
    ///     <see cref="ResourceSet" />. Laid out std430, matching what both targets give a
    ///     push-constant block by default.
    /// </remarks>
    PushConstant
}

/// <summary>
///     One resource a shader expects from the host. Slots are assigned per kind, in
///     declaration order, so a host can bind against them deterministically.
/// </summary>
/// <remarks>
///     <see cref="Slot" /> is not the descriptor binding index. It numbers each kind
///     separately, which is what the IR verifier checks for duplicates and what the IR dump
///     prints; the <c>(set, binding)</c> pair a backend emits is assigned by
///     <c>Vixen.Raven.Reflection.BindingPlan</c>, once, for every consumer.
/// </remarks>
public sealed class IrBinding(
    IrVariable variable,
    IrBindingKind kind,
    int slot,
    string? semantic,
    ResourceSet set = ResourceSet.PerMaterial,
    string? name = null,
    bool writable = false,
    object? defaultValue = null,
    bool shared = false,
    bool materialIndex = false
) {
    public IrVariable Variable { get; } = variable;
    public IrBindingKind Kind { get; } = kind;
    public int Slot { get; } = slot;

    /// <summary>
    ///     Whether the shader may store into this binding. Only a <c>RWBuffer</c> or a storage image
    ///     ever can.
    /// </summary>
    /// <remarks>
    ///     A flag rather than a second <see cref="IrBindingKind" /> because both forms are one Vulkan
    ///     descriptor type; the difference is an access decoration — <c>NonWritable</c> in SPIR-V,
    ///     <c>readonly</c> in GLSL. Worth carrying rather than dropping: a driver may hoist a load out
    ///     of a loop from a read-only buffer and may not from a writable one.
    /// </remarks>
    public bool IsWritable { get; } = writable;

    /// <summary>The descriptor set this binding belongs to.</summary>
    public ResourceSet Set { get; } = set;

    /// <summary>
    ///     The initialiser the author wrote — <c>var exposure: float = 1f</c> — or null.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Carried because it is the host's default, not the shader's. A uniform's initialiser
    ///         never runs on the GPU: the block arrives already filled, so what the author wrote is a
    ///         statement about what a host should put there when it has nothing of its own to say.
    ///         Dropping it in lowering made <c>exposure = 1f</c> reach the engine as zero, which is a
    ///         black frame produced by a parameter nobody touched.
    ///     </para>
    ///     <para>
    ///         Literals only, which is what <c>FieldSymbol.DeclaredValue</c> gives a non-const field.
    ///         A computed initialiser has no value here and none in the reflection, rather than a
    ///         wrong one.
    ///     </para>
    /// </remarks>
    public object? DefaultValue { get; } = defaultValue;

    /// <summary>The pipeline semantic from <c>[Semantic("…")]</c>, if any.</summary>
    public string? Semantic { get; } = semantic;

    /// <summary>
    ///     The host-visible name: the variable's, unless this binding was contributed by a shader
    ///     filling a <c>compose</c> slot, in which case it is qualified by that shader.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Qualified because features are authored independently and collide: three of the
    ///         features in <c>Material/MaterialSurface.rvn</c> declare a <c>strength</c>. Two
    ///         reflection entries with one name is a host writing the wrong offset, or a generated
    ///         binding with two properties of the same name — neither of which announces itself.
    ///     </para>
    ///     <para>
    ///         Every contributed binding is qualified, not only the ones that happen to clash. A name
    ///         that changed depending on what else the material composed would be worse than a long
    ///         one: a host binding by name would break when an unrelated feature was added.
    ///     </para>
    ///     <para>
    ///         Distinct from the identifier a backend emits, which is derived from the variable and
    ///         uniquified per translation unit — a <c>.</c> is not a GLSL identifier. The two were
    ///         already free to differ; this is the first thing that makes them.
    ///     </para>
    /// </remarks>
    public string Name { get; } = name ?? variable.Name;

    /// <summary>
    ///     Whether this is one resource for the whole compilation rather than a contribution from
    ///     each feature that declared it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The exception to <see cref="Name" />'s rule, and it is narrow on purpose. A composed
    ///         contribution is qualified because a feature's parameter belongs to the feature; a
    ///         shared binding is not, because it does not belong to the feature at all — it belongs
    ///         to the frame, and the feature is naming something it expects to already be there.
    ///     </para>
    ///     <para>
    ///         Several bindings may carry this and be the same resource. They are collapsed by
    ///         <c>BindingPlan</c>, which gives one <c>(set, binding)</c> pair to all of them and
    ///         lists the rest as aliases — so every feature's own variable resolves to one
    ///         declaration in both backends, and the reflection reports one binding rather than
    ///         however many features mentioned it.
    ///     </para>
    /// </remarks>
    public bool IsShared { get; } = shared;

    /// <summary>
    ///     Whether this is the index of the material record a draw reads.
    /// </summary>
    /// <remarks>
    ///     Its presence is what makes the per-material block a record. Carried on the binding rather
    ///     than on the shader because it is a field the author marked, and because the emitters need
    ///     the <em>variable</em> — the index is loaded and used as the first subscript of every
    ///     per-material access, so knowing that a shader is indexed is not enough.
    /// </remarks>
    public bool IsMaterialIndex { get; } = materialIndex;

    public IrType Type => Variable.Type;
}

/// <summary>One input or output of an entry point.</summary>
/// <param name="Name">The interface variable's name.</param>
/// <param name="Type">Its type, which <see cref="Reflection.StageInterface" /> has to accept.</param>
/// <param name="Semantic">The pipeline semantic, if any.</param>
/// <param name="Member">
///     For an output, the index of the returned struct's member this output takes its value from;
///     null when the output <em>is</em> the returned value.
/// </param>
/// <remarks>
///     <paramref name="Member" /> is what multiple render targets are made of. A stage interface
///     variable gets one location and therefore has to be one scalar or vector, so a fragment stage
///     writing four targets returns a struct and the entry-point wrapper takes it apart — one
///     extract, one store, per target. Recording the index here rather than re-deriving it in each
///     backend keeps the two from disagreeing about which member is which target.
/// </remarks>
public sealed record IrStageIo(string Name, IrType Type, string? Semantic, int? Member = null);

/// <summary>
///     An interstage value the shader declares with <c>stream</c>: written by one stage, read by
///     the next.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately not an <see cref="IrBinding" />. A binding is host-visible state with a
///         descriptor; a stream is per-invocation and lives in the pipeline's own interface, so
///         nothing about it reaches <c>BindingPlan</c> or the descriptor sets.
///     </para>
///     <para>
///         The variable it carries is a module-scope global, which is exactly how both targets model
///         a stage interface — a SPIR-V <c>Input</c>/<c>Output</c> variable and a GLSL
///         <c>in</c>/<c>out</c> are both module scope. So a read lowers to an ordinary load and a
///         write to an ordinary store, and it is the <em>direction</em> that the backend resolves
///         per stage. Which direction a stage needs is not declared: see
///         <see cref="IrEntryPoint.StreamInputs" />.
///     </para>
/// </remarks>
public sealed class IrStream(IrVariable variable) {
    public IrVariable Variable { get; } = variable;

    public string Name => Variable.Name;
    public IrType Type => Variable.Type;

    public override string ToString() => Name;
}

/// <summary>
///     Storage one workgroup shares, declared with <c>groupshared</c>.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately not an <see cref="IrBinding" />, and for the same reason
///         <see cref="IrStream" /> is not: there is no descriptor, no <c>(set, binding)</c> and
///         nothing the host writes. It is not a local either — a local is one copy per invocation
///         and this is one per workgroup, which is the entire difference and the entire point.
///     </para>
///     <para>
///         The variable it carries is a module-scope global, which is what both targets model this
///         as: a SPIR-V <c>OpVariable</c> in the <c>Workgroup</c> storage class and a GLSL
///         <c>shared</c> declaration are both module scope. So a read lowers to an ordinary load
///         and a write to an ordinary store, and the storage class is the backend's to spell.
///     </para>
/// </remarks>
public sealed class IrSharedVariable(IrVariable variable) {
    public IrVariable Variable { get; } = variable;

    public string Name => Variable.Name;
    public IrType Type => Variable.Type;

    public override string ToString() => Name;
}

/// <summary>
///     A <c>[Permutation]</c> key the shader declares, with the default it falls back to.
/// </summary>
/// <remarks>
///     <para>
///         Recorded even though the key itself is gone by this point — folded into a constant, its
///         dead branch eliminated. That is the point: nothing downstream could otherwise answer
///         "what can this shader be varied by?", which is what a host needs to enumerate variants
///         and what the C# key generator emits a <c>PermutationKey</c> for.
///     </para>
///     <para>
///         Deliberately not the same thing as <c>Compilation.UsedPermutationKeys</c>. That is what
///         this variant <em>read</em>, and it is the cache key. This is what the shader
///         <em>declares</em>, and it is the same for every variant — which it has to be, or the
///         generated C# API would change shape depending on which variant happened to compile.
///     </para>
/// </remarks>
/// <param name="Name">The declared name.</param>
/// <param name="Type">Its type: bool, int or uint.</param>
/// <param name="DefaultValue">The declared default, which every permutation key has (RVN2063).</param>
public sealed record IrPermutation(string Name, IrType Type, object? DefaultValue);

/// <summary>
///     A <c>val</c> type parameter the shader declares — <c>shader Blur&lt;val TapCount: int&gt;</c>.
/// </summary>
/// <remarks>
///     No value here, and that is the distinguishing fact rather than an omission: a value parameter
///     has no default (RVN2082 makes compiling without one an error), so it is a <em>required</em>
///     compile-time argument. What this variant was given is per-variant data, and
///     <c>CompiledEffect.PermutationKey</c> already carries it.
/// </remarks>
/// <param name="Name">The declared name.</param>
/// <param name="Type">Its type: bool, int or uint.</param>
public sealed record IrValueParameter(string Name, IrType Type);

/// <summary>A stage entry point and the interface it presents.</summary>
public sealed class IrEntryPoint(
    ShaderStage stage,
    IrFunction function,
    IReadOnlyList<IrStageIo> inputs,
    IReadOnlyList<IrStageIo> outputs,
    WorkgroupSize? workgroupSize = null
) {
    public ShaderStage Stage { get; } = stage;
    public IrFunction Function { get; } = function;
    public IReadOnlyList<IrStageIo> Inputs { get; } = inputs;

    /// <summary>
    ///     The workgroup size, on a <see cref="ShaderStage.Compute" /> stage and nowhere else.
    /// </summary>
    /// <remarks>
    ///     Carried on the entry point rather than the shader because it belongs to the stage: two
    ///     compute entry points in one shader are two dispatches with their own sizes, the same way
    ///     each has its own signature. Both targets need it — GLSL's <c>local_size_x</c> layout and
    ///     SPIR-V's <c>LocalSize</c> execution mode — so it has to survive lowering rather than
    ///     being read back off the symbol by each backend.
    /// </remarks>
    public WorkgroupSize? WorkgroupSize { get; } = workgroupSize;

    /// <summary>
    ///     The stage's outputs, in location order. Empty when the entry point returns nothing.
    /// </summary>
    /// <remarks>
    ///     A list rather than one value because a fragment stage may write several render targets.
    ///     Every other stage has exactly one output or none: a vertex stage's varyings are
    ///     <c>stream</c>s, which is a different mechanism and a better one, and a compute stage has
    ///     no pipeline interface at all.
    /// </remarks>
    public IReadOnlyList<IrStageIo> Outputs { get; } = outputs;

    /// <summary>The single stage output, or null when there is not exactly one.</summary>
    /// <remarks>
    ///     For the places that genuinely mean "the one output" — a vertex position, a stage whose
    ///     result goes straight into an interface variable with no extract in between.
    /// </remarks>
    public IrStageIo? Output => Outputs.Count == 1 ? Outputs[0] : null;

    /// <summary>
    ///     The shader's streams this stage reads, in the shader's declaration order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Derived rather than declared: a stream this stage's reachable code loads is an input,
    ///         one it stores is an output, and one it does both to is both. That is what makes the
    ///         feature worth having — a helper deep in the call graph can contribute an interstage
    ///         value without any signature between it and the entry point changing.
    ///     </para>
    ///     <para>
    ///         Reachability, not shader membership, decides what "this stage's code" means — the same
    ///         reason the backends use it, since a <c>compose</c>d implementation's functions live in
    ///         another <see cref="IrShader" />.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<IrStream> StreamInputs { get; private set; } = [];

    /// <summary>The shader's streams this stage writes, in the shader's declaration order.</summary>
    public IReadOnlyList<IrStream> StreamOutputs { get; private set; } = [];

    /// <summary>
    ///     The shader's <c>groupshared</c> variables this stage's reachable code touches, in the
    ///     shader's declaration order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Derived rather than declared, the way <see cref="StreamInputs" /> is, and for a
    ///         sharper reason than tidiness: workgroup memory is a budget. A device guarantees
    ///         16 KB, and a backend that emitted every shader-level declaration into every stage's
    ///         unit would spend another entry point's tile against this one's limit — a pipeline
    ///         that fails to create, for storage the stage never reads.
    ///     </para>
    ///     <para>
    ///         Reachability rather than shader membership decides what "this stage's code" is, for
    ///         the reason the streams give: a <c>compose</c>d implementation's functions live in
    ///         another <see cref="IrShader" />.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<IrSharedVariable> SharedVariables { get; private set; } = [];

    internal void SetStreams(IReadOnlyList<IrStream> inputs, IReadOnlyList<IrStream> outputs) {
        StreamInputs = inputs;
        StreamOutputs = outputs;
    }

    internal void SetSharedVariables(IReadOnlyList<IrSharedVariable> shared) => SharedVariables = shared;
}

/// <summary>
///     A lowered shader: its bindings, the functions it is made of, and the entry
///     points a backend generates a pipeline stage for.
/// </summary>
public sealed class IrShader(string name) {
    readonly List<IrBinding> bindings = [];
    readonly List<IrEntryPoint> entryPoints = [];
    readonly List<IrFunction> functions = [];
    readonly List<IrPermutation> permutations = [];
    readonly List<IrSharedVariable> sharedVariables = [];
    readonly List<IrStream> streams = [];
    readonly List<IrValueParameter> valueParameters = [];

    public string Name { get; } = name;

    public IReadOnlyList<IrBinding> Bindings => bindings;
    public IReadOnlyList<IrFunction> Functions => functions;
    public IReadOnlyList<IrEntryPoint> EntryPoints => entryPoints;

    /// <summary>
    ///     The interstage values this shader declares, in declaration order.
    /// </summary>
    /// <remarks>
    ///     The order is load-bearing rather than presentational: it is what
    ///     <c>Vixen.Raven.Reflection.StreamPlan</c> turns into locations, and a stream's location
    ///     has to be a property of the shader for the writing stage and the reading stage to agree.
    /// </remarks>
    public IReadOnlyList<IrStream> Streams => streams;

    /// <summary>The <c>groupshared</c> variables this shader declares, in declaration order.</summary>
    public IReadOnlyList<IrSharedVariable> SharedVariables => sharedVariables;

    /// <summary>The <c>[Permutation]</c> keys this shader declares, in declaration order.</summary>
    public IReadOnlyList<IrPermutation> Permutations => permutations;

    /// <summary>The <c>val</c> type parameters this shader declares, in declaration order.</summary>
    public IReadOnlyList<IrValueParameter> ValueParameters => valueParameters;

    /// <summary>Statements that initialize bindings with a declared default.</summary>
    public IrBlock Initializer { get; } = new();

    public override string ToString() => Name;

    internal void Add(IrBinding binding) => bindings.Add(binding);
    internal void Add(IrFunction function) => functions.Add(function);
    internal void Add(IrEntryPoint entryPoint) => entryPoints.Add(entryPoint);
    internal void Add(IrPermutation permutation) => permutations.Add(permutation);
    internal void Add(IrSharedVariable shared) => sharedVariables.Add(shared);
    internal void Add(IrStream stream) => streams.Add(stream);
    internal void Add(IrValueParameter parameter) => valueParameters.Add(parameter);
}
