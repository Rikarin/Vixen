// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Reflection;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets;

/// <summary>Which file extensions an importer claims.</summary>
/// <param name="extensions">The extensions, with their leading dots.</param>
/// <remarks>
///     On the importer rather than in a table, so that adding an importer is adding a class and
///     nothing else. The registry still has to be told the instance — nothing scans assemblies here,
///     because a scan reads metadata that a trimmed publish has already deleted.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ImporterAttribute(params string[] extensions) : Attribute {
    /// <summary>The extensions it claims, lowercase and with their leading dots.</summary>
    public IReadOnlyList<string> Extensions { get; } =
        [.. (extensions ?? []).Select(extension => extension.ToLowerInvariant())];
}

/// <summary>Anything that turns a source file into artefacts.</summary>
public interface IAssetImporter {
    /// <summary>
    ///     What it is called in a <c>.meta</c> file and in the cache key — its settings type's
    ///     <c>[DataContract]</c> name.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Its own version. <b>Bumping it invalidates every artefact it has ever produced</b>, which
    ///     is the whole mechanism for "I fixed the mip filter, re-import everything".
    /// </summary>
    int Version { get; }

    /// <summary>What its settings are.</summary>
    Type SettingsType { get; }

    /// <summary>Which extensions it claims.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Makes a settings object with every default in place.</summary>
    /// <returns>The settings.</returns>
    IImportSettings CreateSettings();

    /// <summary>Imports one asset.</summary>
    /// <param name="context">Everything it is allowed to read, and everything it must declare.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What it produced.</returns>
    ValueTask<ImportResult> ImportAsync(ImportContext context, CancellationToken cancellationToken = default);
}

/// <summary>An importer for one settings type.</summary>
/// <typeparam name="TSettings">Its settings.</typeparam>
/// <remarks>
///     <para>
///         <b>The importer is generic and the context is not</b>, which is a deviation from
///         [08](../../docs/plan/08-asset-pipeline-and-addressables.md)'s
///         <c>ImportContext&lt;TSettings&gt;</c> and is not a simplification. Building an
///         <c>ImportContext&lt;T&gt;</c> for a settings type the pipeline only knows at run time
///         needs <c>MakeGenericType</c>, which NativeAOT does not have. The importer's own type
///         parameter costs nothing — it is closed at the <c>class TextureImporter :
///         AssetImporter&lt;TextureImportSettings&gt;</c> declaration — so the settings arrive as a
///         typed parameter and everything stays statically bound.
///     </para>
///     <para>
///         The name comes from the settings type's contract alias, so one attribute defines the
///         <c>.meta</c> tag, the settings type, the serializer and the cache key with nothing to keep
///         in sync.
///     </para>
/// </remarks>
public abstract class AssetImporter<TSettings> : IAssetImporter
    where TSettings : class, IImportSettings, new() {
    /// <inheritdoc />
    public abstract int Version { get; }

    /// <inheritdoc />
    public Type SettingsType => typeof(TSettings);

    /// <inheritdoc />
    public string Name =>
        TypeRegistry.TryGet<TSettings>(out var descriptor)
            ? descriptor.Alias
            : throw new InvalidOperationException(
                $"{typeof(TSettings).Name} has no descriptor, so this importer has no name. Give it "
                + "[DataContract], which is also what makes it the tag in a .meta file."
            );

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions =>
        (Attribute.GetCustomAttribute(GetType(), typeof(ImporterAttribute)) as ImporterAttribute)?.Extensions
        ?? throw new InvalidOperationException(
            $"{GetType().Name} has no [Importer] attribute, so nothing knows which files it claims."
        );

    /// <inheritdoc />
    public IImportSettings CreateSettings() => new TSettings();

    /// <inheritdoc />
    public ValueTask<ImportResult> ImportAsync(ImportContext context, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(context);

        return context.Settings is TSettings settings
            ? ImportAsync(context, settings, cancellationToken)
            : throw new ArgumentException(
                $"{Name} was handed {context.Settings.GetType().Name} rather than {typeof(TSettings).Name}.",
                nameof(context)
            );
    }

    /// <summary>Imports one asset.</summary>
    /// <param name="context">Everything it is allowed to read, and everything it must declare.</param>
    /// <param name="settings">How this asset is configured, with per-target overrides already applied.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What it produced.</returns>
    protected abstract ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        TSettings settings,
        CancellationToken cancellationToken
    );
}
