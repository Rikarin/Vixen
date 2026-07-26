// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;

namespace Vixen.Raven.IR;

/// <summary>How a shader-level binding is presented to the pipeline.</summary>
public enum IrBindingKind {
    /// <summary>A constant-buffer / uniform entry.</summary>
    Uniform,
    Texture,
    Sampler
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
    ResourceSet set = ResourceSet.PerMaterial
) {
    public IrVariable Variable { get; } = variable;
    public IrBindingKind Kind { get; } = kind;
    public int Slot { get; } = slot;

    /// <summary>The descriptor set this binding belongs to.</summary>
    public ResourceSet Set { get; } = set;

    /// <summary>The pipeline semantic from <c>[Semantic("…")]</c>, if any.</summary>
    public string? Semantic { get; } = semantic;

    public string Name => Variable.Name;
    public IrType Type => Variable.Type;
}

/// <summary>One input or output of an entry point.</summary>
public sealed record IrStageIo(string Name, IrType Type, string? Semantic);

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
    IrStageIo? output,
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

    /// <summary>The stage output, or null when the entry point returns nothing.</summary>
    public IrStageIo? Output { get; } = output;

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

    internal void SetStreams(IReadOnlyList<IrStream> inputs, IReadOnlyList<IrStream> outputs) {
        StreamInputs = inputs;
        StreamOutputs = outputs;
    }
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
    internal void Add(IrStream stream) => streams.Add(stream);
    internal void Add(IrValueParameter parameter) => valueParameters.Add(parameter);
}
