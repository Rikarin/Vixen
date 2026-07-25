using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.CodeGen;

/// <summary>One generated translation unit — for most targets, one pipeline stage.</summary>
public sealed record GeneratedSource(string Name, ShaderStage Stage, string Code) {
    public override string ToString() => $"{Name} ({Stage})";
}

/// <summary>
/// A code generator for one target language. Backends consume
/// <see cref="IrModule"/> and nothing else: they never see the bound tree or the
/// syntax tree, so a new target is a new implementation of this and nothing more.
/// </summary>
public interface ITargetBackend {
    /// <summary>Identifier used on the command line, e.g. <c>glsl</c>.</summary>
    string Name { get; }

    /// <summary>Extension for generated files, including the dot.</summary>
    string FileExtension { get; }

    /// <summary>
    /// Generates one unit per entry point. A backend reports what it cannot
    /// express through <paramref name="diagnostics"/> rather than emitting
    /// something that does not compile.
    /// </summary>
    IReadOnlyList<GeneratedSource> Generate(IrModule module, DiagnosticBag diagnostics);
}

/// <summary>The backends this compiler knows about.</summary>
public static class TargetBackends {
    static readonly Dictionary<string, Func<ITargetBackend>> Factories = new(StringComparer.OrdinalIgnoreCase) {
        ["glsl"] = () => new Glsl.GlslBackend()
    };

    /// <summary>Names accepted by <see cref="Create"/>, in a stable order.</summary>
    public static IReadOnlyList<string> Names => Factories.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>Creates a backend by name, or null when there is no such target.</summary>
    public static ITargetBackend? Create(string name) =>
        Factories.TryGetValue(name, out var factory) ? factory() : null;
}
