using Vixen.Raven.CodeGen.Glsl;
using Vixen.Raven.CodeGen.Spirv;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.CodeGen;

/// <summary>One generated translation unit — for most targets, one pipeline stage.</summary>
/// <param name="Code">
///     The readable form. For a source-level target that is the source itself; for a
///     binary one it is a listing of what <paramref name="Binary" /> contains.
/// </param>
/// <param name="Binary">
///     The bytes to write, for targets that are binary. Null for a source-level
///     target, where <paramref name="Code" /> is what gets written.
/// </param>
public sealed record GeneratedSource(string Name, ShaderStage Stage, string Code, byte[]? Binary = null) {
    /// <summary>True when this unit is written as bytes rather than as text.</summary>
    public bool IsBinary => Binary is not null;

    public override string ToString() => $"{Name} ({Stage})";
}

/// <summary>Conventional file-name suffixes for the pipeline stages.</summary>
public static class ShaderStages {
    public static string Suffix(ShaderStage stage) =>
        stage switch {
            ShaderStage.Vertex => "vert",
            ShaderStage.Pixel => "frag",
            ShaderStage.Geometry => "geom",
            ShaderStage.Compute => "comp",
            _ => "shader"
        };
}

/// <summary>
///     A code generator for one target language. Backends consume
///     <see cref="IrModule" /> and nothing else: they never see the bound tree or the
///     syntax tree, so a new target is a new implementation of this and nothing more.
/// </summary>
public interface ITargetBackend {
    /// <summary>Identifier used on the command line, e.g. <c>glsl</c>.</summary>
    string Name { get; }

    /// <summary>Extension for generated files, including the dot.</summary>
    string FileExtension { get; }

    /// <summary>
    ///     Generates one unit per entry point. A backend reports what it cannot
    ///     express through <paramref name="diagnostics" /> rather than emitting
    ///     something that does not compile.
    /// </summary>
    IReadOnlyList<GeneratedSource> Generate(IrModule irModule, DiagnosticBag diagnostics);
}

/// <summary>The backends this compiler knows about.</summary>
public static class TargetBackends {
    static readonly Dictionary<string, Func<ITargetBackend>> Factories = new(StringComparer.OrdinalIgnoreCase) {
        ["glsl"] = () => new GlslBackend(), ["spirv"] = () => new SpirvBackend()
    };

    /// <summary>Names accepted by <see cref="Create" />, in a stable order.</summary>
    public static IReadOnlyList<string> Names => Factories.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>Creates a backend by name, or null when there is no such target.</summary>
    public static ITargetBackend? Create(string name) =>
        Factories.TryGetValue(name, out var factory) ? factory() : null;
}
