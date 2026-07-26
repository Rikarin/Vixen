// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Artefacts;

namespace Vixen.Raven;

/// <summary>
///     A compiled library a <see cref="Compilation" /> binds against — the shader equivalent of a
///     <c>.dll</c> reference.
/// </summary>
/// <remarks>
///     <para>
///         A thin handle rather than a hierarchy. There is one kind of reference, because there is
///         one artefact format: a <c>.rvnlib</c>, on disk or already in memory. An assembly of
///         source files is not a reference — it is more trees in the same compilation, which is
///         how the shader library was consumed before this existed and remains the right answer
///         when you have the source and want it recompiled.
///     </para>
///     <para>
///         Reading is eager, at construction, so a bad path or a corrupt artefact throws where the
///         caller can attribute it rather than surfacing later as an unresolved name.
///     </para>
/// </remarks>
public sealed class RavenReference {
    /// <summary>The library's declarations and IR.</summary>
    public CompiledLibrary Library { get; }

    /// <summary>Where the library was read from, or null when it was supplied in memory.</summary>
    public string? Path { get; }

    /// <summary>The library's name, which is what a duplicate reference is detected by.</summary>
    public string Name => Library.Name;

    RavenReference(CompiledLibrary library, string? path) {
        Library = library;
        Path = path;
    }

    /// <summary>Reads a reference from a <c>.rvnlib</c> on disk.</summary>
    /// <exception cref="InvalidDataException">The file is not a readable <c>.rvnlib</c>.</exception>
    public static RavenReference FromFile(string path) {
        ArgumentNullException.ThrowIfNull(path);
        return new(CompiledLibraryReader.ReadFile(path), path);
    }

    /// <summary>Wraps a library already in memory — what the tests and a hot-reload host use.</summary>
    public static RavenReference FromLibrary(CompiledLibrary library) {
        ArgumentNullException.ThrowIfNull(library);
        return new(library, null);
    }

    public override string ToString() => Path ?? Name;
}
