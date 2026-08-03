// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>What a type looks like, wherever the editor draws one.</summary>
/// <param name="Target">The type — a component, a behaviour, or the class an asset deserialises to.</param>
/// <param name="Art">Its picture.</param>
/// <param name="Order">
///     Which of two declarations for one type wins; the higher one, and the later one on a tie.
/// </param>
/// <remarks>
///     <para>
///         <b>Doc 36 § D6.</b> A contribution rather than a switch, so the outliner's row for a
///         plugin's component and the inspector's header for it draw the same picture without either
///         panel knowing the plugin exists.
///     </para>
///     <para>
///         ⚠ <b>A registration and not an attribute, and the reason is in <see cref="Icon" />.</b>
///         Doc 36 spells this <c>[EditorIcon("Icons/thing.svg")]</c> — but there is no SVG path parser
///         in this repository and its absence is a decision: an icon set is compiled content, so
///         turning <c>"M12 2L2 22h20z"</c> into segments belongs to an asset pipeline rather than to
///         every application at start-up. An attribute naming a file nothing can read would be an
///         attribute that looks like a mechanism, which is what P2 declined to ship for the same
///         reason. A type declares its icon by registering it, which a module initializer, a plugin's
///         <c>Activate</c> and a project's own script can all do.
///     </para>
/// </remarks>
public sealed record TypeIcon(Type Target, IconArt Art, int Order = 0);

/// <summary>What a kind of file looks like in the Project panel.</summary>
/// <param name="Kind">An importer tag — <c>TextureImporter</c> — or an extension, with its dot.</param>
/// <param name="Art">Its picture.</param>
/// <param name="Order">Which of two declarations for one kind wins.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Two ways to name a file kind, because an asset has one before it has the other.</b>
///         The importer tag is what the sidecar records and what the browser's type filter offers, so
///         it is the better key — but a file the database has not indexed has no tag at all, and an
///         asset type a plugin contributed through <c>NewAssetKind</c> may have no importer in the
///         first place. The extension is what is left, and it is what the Create ▸ entry already
///         names.
///     </para>
///     <para>
///         The tag is tried first: two plugins whose asset types share an extension are told apart by
///         which importer claimed the file, and nothing else could tell them apart at all.
///     </para>
/// </remarks>
public sealed record AssetIcon(string Kind, IconArt Art, int Order = 0);

/// <summary>Finding the picture for a type or for a file.</summary>
/// <remarks>
///     ⚠ <b>Over a list rather than over the registry, which keeps this assembly where it is.</b>
///     <c>IEditorRegistry</c> lives in <c>Vixen.Editor.Core</c> with the documents and the command
///     stack, and the shell deliberately knows about neither — so a caller passes
///     <c>registry.All&lt;TypeIcon&gt;()</c> and the matching rule stays in one place all the same.
/// </remarks>
public static class EditorArt {
    /// <summary>The picture declared for a type, or for the nearest base type that has one.</summary>
    /// <param name="icons">Everything contributed.</param>
    /// <param name="type">The type.</param>
    /// <returns>Its art, or <see langword="null" /> if nothing claims it.</returns>
    /// <remarks>
    ///     ⚠ <b>The base walk is what makes a behaviour's icon declarable once.</b> Every
    ///     <c>Behavior</c> subclass in a project is a distinct type, and an author who wanted them all
    ///     to look like a script would otherwise register one icon per class. A type that declares its
    ///     own still wins, because the walk starts at the type itself.
    /// </remarks>
    public static IconArt? Of(IReadOnlyList<TypeIcon> icons, Type? type) {
        ArgumentNullException.ThrowIfNull(icons);

        for (var current = type; current is not null; current = current.BaseType) {
            if (Best(icons, current) is { } art) {
                return art;
            }
        }

        return null;
    }

    /// <summary>The picture for a file, by what claimed it and then by what it is called.</summary>
    /// <param name="icons">Everything contributed.</param>
    /// <param name="importer">The importer tag the sidecar records, or nothing.</param>
    /// <param name="name">The file's name or path, for its extension. May be empty.</param>
    /// <returns>Its art, or <see langword="null" /> if nothing claims it.</returns>
    public static IconArt? Of(IReadOnlyList<AssetIcon> icons, string? importer, string? name) {
        ArgumentNullException.ThrowIfNull(icons);

        if (!string.IsNullOrEmpty(importer) && Best(icons, importer) is { } claimed) {
            return claimed;
        }

        var extension = string.IsNullOrEmpty(name) ? string.Empty : Path.GetExtension(name);

        // Lowercased, because a file called `Thing.VXTERRAIN` is the same kind of thing and an icon
        // set that had to enumerate the spellings would miss one.
        return string.IsNullOrEmpty(extension) ? null : Best(icons, extension.ToLowerInvariant());
    }

    static IconArt? Best(IReadOnlyList<TypeIcon> icons, Type type) {
        IconArt? found = null;
        var order = int.MinValue;

        foreach (var icon in icons) {
            if (icon.Target == type && icon.Order >= order) {
                found = icon.Art;
                order = icon.Order;
            }
        }

        return found;
    }

    static IconArt? Best(IReadOnlyList<AssetIcon> icons, string kind) {
        IconArt? found = null;
        var order = int.MinValue;

        foreach (var icon in icons) {
            if (icon.Order >= order && string.Equals(icon.Kind, kind, StringComparison.OrdinalIgnoreCase)) {
                found = icon.Art;
                order = icon.Order;
            }
        }

        return found;
    }
}
