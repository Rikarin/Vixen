// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Yaml.Meta;

/// <summary>Reading and writing <c>.meta</c> sidecars.</summary>
/// <remarks>
///     Migration happens on the node tree between parsing and binding, which is the only place it
///     can: a document old enough to need migrating does not fit the current type, so there is
///     nothing to bind it to yet.
/// </remarks>
public static class AssetMetaFile {
    /// <summary>The extension a sidecar has.</summary>
    public const string Extension = ".meta";

    /// <summary>Reads a sidecar.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="chain">The migrations to apply, or <see langword="null" /> for the engine's.</param>
    /// <param name="onUnknownKey">Told about every key that matched no member.</param>
    /// <returns>The sidecar.</returns>
    /// <exception cref="YamlParseException">It is not YAML.</exception>
    /// <exception cref="MetaVersionException">It was written by a newer editor.</exception>
    /// <exception cref="YamlBindingException">It does not fit the schema.</exception>
    public static AssetMeta Read(string text, MetaMigrationChain? chain = null, Action<string>? onUnknownKey = null) {
        ArgumentNullException.ThrowIfNull(text);

        if (YamlReader.Read(text) is not YamlMapping root) {
            throw new YamlBindingException(string.Empty, "A .meta file is a mapping at the top level.");
        }

        (chain ?? MetaMigrationChain.Current).Apply(root);

        return YamlSerializer.Deserialize<AssetMeta>(
            root,
            YamlSerializerOptions.Default with { OnUnknownKey = onUnknownKey }
        );
    }

    /// <summary>Writes a sidecar.</summary>
    /// <param name="meta">The sidecar.</param>
    /// <returns>The YAML, ending in a newline.</returns>
    public static string Write(AssetMeta meta) {
        ArgumentNullException.ThrowIfNull(meta);
        return YamlSerializer.ToYaml(meta);
    }

    /// <summary>Reads a sidecar from disk.</summary>
    /// <param name="path">Where it is.</param>
    /// <param name="chain">The migrations to apply, or <see langword="null" /> for the engine's.</param>
    /// <param name="onUnknownKey">Told about every key that matched no member.</param>
    /// <returns>The sidecar.</returns>
    public static AssetMeta ReadFile(
        string path,
        MetaMigrationChain? chain = null,
        Action<string>? onUnknownKey = null
    ) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Read(File.ReadAllText(path), chain, onUnknownKey);
    }

    /// <summary>Writes a sidecar to disk.</summary>
    /// <param name="path">Where to put it.</param>
    /// <param name="meta">The sidecar.</param>
    /// <remarks>
    ///     LF line endings on every platform, matching what <c>.gitattributes</c> declares for these
    ///     files. A Windows checkout that wrote CRLF would make every sidecar it touched a whole-file
    ///     diff.
    /// </remarks>
    public static void WriteFile(string path, AssetMeta meta) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, Write(meta));
    }

    /// <summary>Where an asset's sidecar lives.</summary>
    /// <param name="assetPath">The asset.</param>
    /// <returns>Its sidecar's path.</returns>
    /// <remarks>
    ///     Appended rather than substituted — <c>hero.png</c> becomes <c>hero.png.meta</c>, not
    ///     <c>hero.meta</c> — so <c>hero.png</c> and <c>hero.fbx</c> in one folder do not share one
    ///     sidecar. Unity's rule, and it is right.
    /// </remarks>
    public static string PathFor(string assetPath) {
        ArgumentException.ThrowIfNullOrEmpty(assetPath);
        return assetPath + Extension;
    }
}
