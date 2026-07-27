// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Assets;

/// <summary>Which importer claims a file.</summary>
/// <remarks>
///     <para>
///         <b>Told, never discovered.</b> An assembly scan looking for <c>[Importer]</c> would read
///         metadata that a trimmed publish has already deleted, and would make "which importers does
///         this build have" a question with a different answer in the editor and in the asset
///         compiler. Registration is a line of code where the importers are decided.
///     </para>
///     <para>
///         Two importers claiming one extension is an error naming both, rather than last-one-wins.
///         The alternative is an artist's PNG being imported as a cubemap because a plugin loaded in
///         a different order today.
///     </para>
/// </remarks>
public sealed class ImporterRegistry {
    readonly Dictionary<string, IAssetImporter> byExtension = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IAssetImporter> byName = new(StringComparer.Ordinal);

    IAssetImporter? fallback;

    /// <summary>How many importers are registered.</summary>
    public int Count => byName.Count;

    /// <summary>Everything registered.</summary>
    public IReadOnlyCollection<IAssetImporter> Importers => byName.Values;

    /// <summary>Registers an importer.</summary>
    /// <param name="importer">The importer.</param>
    /// <returns>This registry, for chaining.</returns>
    /// <exception cref="InvalidOperationException">Something already claims one of its extensions, or its name.</exception>
    public ImporterRegistry Add(IAssetImporter importer) {
        ArgumentNullException.ThrowIfNull(importer);

        if (!byName.TryAdd(importer.Name, importer)) {
            throw new InvalidOperationException(
                $"Both {byName[importer.Name].GetType().Name} and {importer.GetType().Name} are called "
                + $"'{importer.Name}'. The name comes from the settings type's [DataContract], so give one of "
                + "them an explicit alias."
            );
        }

        foreach (var extension in importer.Extensions) {
            if (!byExtension.TryAdd(extension, importer)) {
                throw new InvalidOperationException(
                    $"Both '{byExtension[extension].Name}' and '{importer.Name}' claim '{extension}'. One of them "
                    + "has to give it up; whichever loaded first is not a rule anyone can rely on."
                );
            }
        }

        return this;
    }

    /// <summary>Registers the importer that takes anything nothing else claimed.</summary>
    /// <param name="importer">The importer.</param>
    /// <returns>This registry, for chaining.</returns>
    /// <remarks>
    ///     Separate from <see cref="Add" /> because a fallback is a different thing from an importer
    ///     with a long extension list: it must not compete for extensions, and there can only be one.
    /// </remarks>
    public ImporterRegistry AddFallback(IAssetImporter importer) {
        ArgumentNullException.ThrowIfNull(importer);
        Add(importer);
        fallback = importer;
        return this;
    }

    /// <summary>Finds the importer for a file.</summary>
    /// <param name="path">The file's path or name.</param>
    /// <param name="importer">The importer.</param>
    /// <returns>Whether anything claims it, including the fallback.</returns>
    public bool TryGetForFile(string path, [NotNullWhen(true)] out IAssetImporter? importer) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var extension = Path.GetExtension(path);

        if (extension.Length > 0 && byExtension.TryGetValue(extension, out importer)) {
            return true;
        }

        importer = fallback;
        return importer is not null;
    }

    /// <summary>Finds an importer by name — the tag a <c>.meta</c> file carries.</summary>
    /// <param name="name">The name.</param>
    /// <param name="importer">The importer.</param>
    /// <returns>Whether this build has it.</returns>
    public bool TryGetByName(string name, [NotNullWhen(true)] out IAssetImporter? importer) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return byName.TryGetValue(name, out importer);
    }
}
