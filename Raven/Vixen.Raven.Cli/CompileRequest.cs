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

    /// <summary>Also write the IR dump next to the generated sources.</summary>
    public bool EmitIr { get; init; }

    /// <summary>For a binary target, also write the readable listing beside the bytes.</summary>
    public bool EmitListing { get; init; }

    /// <summary>Name every file as it is written.</summary>
    public bool Verbose { get; init; }

    /// <summary>Colour the diagnostics.</summary>
    public bool UseColor { get; init; }
}
