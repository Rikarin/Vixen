// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;

namespace Vixen.Editor.Assets;

/// <summary>An importer read a file it never said it depended on.</summary>
/// <remarks>
///     <para>
///         This is the single most valuable check in the pipeline, and it is worth understanding why
///         it is an <i>exception</i> rather than a warning. Incrementality is decided entirely by the
///         cache key, and the cache key is built from the dependencies an importer declared. An
///         importer that quietly reads a palette file, a shared configuration, or a sibling texture
///         without declaring it produces an artefact that is <b>correct today and stale for ever</b>:
///         the file it read can change and nothing will re-run it.
///     </para>
///     <para>
///         That failure does not show up as a crash. It shows up as an artist changing a file,
///         rebuilding, and getting the old result — once, on one machine, in a way nobody can
///         reproduce. Catching it at the moment of the read, with the path in the message, turns a
///         week of that into a line of code.
///     </para>
/// </remarks>
public sealed class UnregisteredReadException(VirtualPath path, string importer)
    : Exception(
        $"'{importer}' read '{path}' without declaring it. Call DependsOnFile before reading it, or the "
        + "artefact this import produces will never be rebuilt when that file changes — and nothing will "
        + "say so."
    ) {
    /// <summary>What it read.</summary>
    public VirtualPath Path { get; } = path;

    /// <summary>Which importer read it.</summary>
    public string Importer { get; } = importer;
}
