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
/// One resource a shader expects from the host. Slots are assigned per kind, in
/// declaration order, so a host can bind against them deterministically.
/// </summary>
public sealed class IrBinding(IrVariable variable, IrBindingKind kind, int slot, string? semantic) {
    public IrVariable Variable { get; } = variable;
    public IrBindingKind Kind { get; } = kind;
    public int Slot { get; } = slot;

    /// <summary>The pipeline semantic from <c>[Semantic("…")]</c>, if any.</summary>
    public string? Semantic { get; } = semantic;

    public string Name => Variable.Name;
    public IrType Type => Variable.Type;
}

/// <summary>One input or output of an entry point.</summary>
public sealed record IrStageIo(string Name, IrType Type, string? Semantic);

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
/// A lowered shader: its bindings, the functions it is made of, and the entry
/// points a backend generates a pipeline stage for.
/// </summary>
public sealed class IrShader(string name) {
    readonly List<IrBinding> bindings = [];
    readonly List<IrEntryPoint> entryPoints = [];
    readonly List<IrFunction> functions = [];

    public string Name { get; } = name;

    public IReadOnlyList<IrBinding> Bindings => bindings;
    public IReadOnlyList<IrFunction> Functions => functions;
    public IReadOnlyList<IrEntryPoint> EntryPoints => entryPoints;

    /// <summary>Statements that initialize bindings with a declared default.</summary>
    public IrBlock Initializer { get; } = new();

    internal void Add(IrBinding binding) => bindings.Add(binding);
    internal void Add(IrFunction function) => functions.Add(function);
    internal void Add(IrEntryPoint entryPoint) => entryPoints.Add(entryPoint);

    public override string ToString() => Name;
}
