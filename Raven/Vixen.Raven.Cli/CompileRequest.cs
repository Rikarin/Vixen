// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Cli;

/// <summary>Everything <see cref="CompileDriver" /> needs, with no console in it.</summary>
public sealed record CompileRequest {
    /// <summary>Source files. They become one compilation, so they can see each other.</summary>
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>
    ///     Where to write. A path with an extension is a file, and then the shader
    ///     must produce exactly one unit; anything else is a directory.
    /// </summary>
    public required string Output { get; init; }

    /// <summary>Backend name, as <see cref="CodeGen.TargetBackends" /> knows it.</summary>
    public string Target { get; init; } = "glsl";

    /// <summary>
    ///     Values for the shader's <c>[Permutation]</c> keys, as <c>Name=Value</c> strings.
    ///     <c>Name</c> alone means <c>Name=true</c>. Keys not named here take the default in
    ///     the source.
    /// </summary>
    public IReadOnlyList<string> Defines { get; init; } = [];

    /// <summary>
    ///     Which shader fills each <c>compose</c> slot, as <c>slot=Shader</c> strings. The
    ///     slot may be qualified by shader: <c>Lit.diffuse=Lambert</c>.
    /// </summary>
    public IReadOnlyList<string> Composes { get; init; } = [];

    /// <summary>Also write the IR dump next to the generated sources.</summary>
    public bool EmitIr { get; init; }

    /// <summary>For a binary target, also write the readable listing beside the bytes.</summary>
    public bool EmitListing { get; init; }

    /// <summary>
    ///     Print the target features the module requires, so a host can see what it will be
    ///     asked to support.
    /// </summary>
    public bool ShowCapabilities { get; init; }

    /// <summary>
    ///     Also write the reflection as JSON — the descriptor sets, member offsets and flattened
    ///     parameter list the engine binds against.
    /// </summary>
    public bool EmitReflection { get; init; }

    /// <summary>
    ///     Also write a <c>.rvnfx</c> per shader — the compiled effect the runtime loads instead
    ///     of compiling: every stage's module, the reflection, the permutation key and a source
    ///     hash.
    /// </summary>
    public bool EmitEffect { get; init; }

    /// <summary>Name every file as it is written.</summary>
    public bool Verbose { get; init; }

    /// <summary>Colour the diagnostics.</summary>
    public bool UseColor { get; init; }
}
