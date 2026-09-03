// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
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

/// <summary>
///     Conventional file-name suffixes for the pipeline stages.
/// </summary>
/// <remarks>
///     Named for what it is rather than <c>ShaderStages</c>, which is the flags enum in
///     <c>Vixen.Raven.Reflection</c>. Two types of that name in one assembly would make any file
///     importing both ambiguous.
/// </remarks>
public static class ShaderStageNames {
    public static string Suffix(ShaderStage stage) =>
        stage switch {
            ShaderStage.Vertex => "vert",
            ShaderStage.Fragment => "frag",
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
/// <remarks>
///     <para>
///         Two are built in, and a third can be added from outside. That asymmetry is
///         ADR-012's: SPIR-V is the canonical output and Vulkan GLSL is emitted beside it, but
///         every <em>other</em> dialect — ESSL, HLSL, MSL, WGSL — is SPIRV-Cross's job, and
///         SPIRV-Cross is a native library. <c>Vixen.Raven</c> is a shipped package; a reference
///         to the project that hosts it would put those binaries in the restore graph of anything
///         that consumes the compiler as a library, which is the shape docs/plan/01 already
///         declined once for shaderc.
///     </para>
///     <para>
///         So the table is open rather than closed, and <c>Vixen.Raven.Transpile</c> pushes
///         <c>essl</c> into it from <c>Vixen.Raven.Cli</c>'s entry point. ⚠ <see cref="Names" />
///         is therefore a property of the <em>process</em> and not of this assembly — a caller
///         that builds a menu from it must register first, which is why the CLI registers before
///         it builds its command rather than inside the handler.
///     </para>
/// </remarks>
public static class TargetBackends {
    static readonly Dictionary<string, Func<ITargetBackend>> Factories = new(StringComparer.OrdinalIgnoreCase) {
        ["glsl"] = () => new GlslBackend(), ["spirv"] = () => new SpirvBackend()
    };

    /// <summary>Names accepted by <see cref="Create" />, in a stable order.</summary>
    public static IReadOnlyList<string> Names => Factories.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>Creates a backend by name, or null when there is no such target.</summary>
    public static ITargetBackend? Create(string name) =>
        Factories.TryGetValue(name, out var factory) ? factory() : null;

    /// <summary>Adds a backend this assembly cannot reference.</summary>
    /// <param name="name">The name <see cref="Create" /> will answer to.</param>
    /// <param name="factory">Makes one. Called per compilation, not once.</param>
    /// <remarks>
    ///     Last registration wins, and registering the same name twice is deliberately not an
    ///     error: a host that registers on every compile rather than once at startup should work,
    ///     and a duplicate-name throw would make the difference between the two an exception in
    ///     production rather than at the desk. ⚠ It follows that a name registered here can
    ///     <em>replace</em> <c>glsl</c> or <c>spirv</c>; nothing needs that today, and nothing
    ///     stops it either, because forbidding it would need a list of reserved names that would
    ///     itself go stale.
    /// </remarks>
    public static void Register(string name, Func<ITargetBackend> factory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        Factories[name] = factory;
    }
}
