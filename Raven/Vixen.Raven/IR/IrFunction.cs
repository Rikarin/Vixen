namespace Vixen.Raven.IR;

/// <summary>
///     A function in the IR: a signature, its local storage, and a structured body.
///     The function owns value numbering, so <c>%0</c> means the same thing
///     throughout one function and nothing outside it.
/// </summary>
public sealed class IrFunction(string name, IrType returnType) {
    readonly List<IrVariable> locals = [];
    readonly List<IrVariable> parameters = [];

    public string Name { get; } = name;
    public IrType ReturnType { get; } = returnType;

    public IReadOnlyList<IrVariable> Parameters => parameters;
    public IReadOnlyList<IrVariable> Locals => locals;

    /// <summary>The function body. Empty for a declaration with no body.</summary>
    public IrBlock Body { get; } = new();

    /// <summary>How many values this function defines; also the next free id.</summary>
    public int ValueCount { get; private set; }

    public override string ToString() =>
        $"{Name}({string.Join(", ", parameters.Select(p => p.Type.Name))}) : {ReturnType.Name}";

    /// <summary>Allocates the next SSA value of the given type.</summary>
    internal IrValue NewValue(IrType type) => new(ValueCount++, type);

    internal IrVariable AddParameter(string name, IrType type) {
        var variable = new IrVariable(name, type, IrVariableKind.Parameter);
        parameters.Add(variable);
        return variable;
    }

    internal IrVariable AddLocal(string name, IrType type) {
        // Locals share one namespace per function; disambiguate shadowed names
        // so the dump and the backends stay unambiguous.
        var unique = name;
        var suffix = 1;
        while (locals.Any(l => l.Name == unique) || parameters.Any(p => p.Name == unique)) {
            unique = $"{name}#{suffix++}";
        }

        var variable = new IrVariable(unique, type, IrVariableKind.Local);
        locals.Add(variable);
        return variable;
    }
}
