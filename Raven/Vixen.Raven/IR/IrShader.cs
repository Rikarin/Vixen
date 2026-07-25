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
    IrStageIo? output
) {
    public ShaderStage Stage { get; } = stage;
    public IrFunction Function { get; } = function;
    public IReadOnlyList<IrStageIo> Inputs { get; } = inputs;

    /// <summary>The stage output, or null when the entry point returns nothing.</summary>
    public IrStageIo? Output { get; } = output;
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
    readonly List<IrValueParameter> valueParameters = [];

    public string Name { get; } = name;

    public IReadOnlyList<IrBinding> Bindings => bindings;
    public IReadOnlyList<IrFunction> Functions => functions;
    public IReadOnlyList<IrEntryPoint> EntryPoints => entryPoints;

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
    internal void Add(IrValueParameter parameter) => valueParameters.Add(parameter);
}
