// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;

namespace Vixen.Editor.AssetEditors.Content;

/// <summary>The editable mirror of <see cref="AddressableGroup" />.</summary>
/// <remarks>
///     Member for member, on the terms <c>ImportSettingsDocument</c> sets out, and with the same
///     drift test behind it. What each policy <i>means</i> lives on the record; what is here is the
///     sentence a row shows when somebody hovers it.
/// </remarks>
[DataContract("AddressableGroupEdits")]
public sealed class AddressableGroupEdits {
    /// <summary>What the group is called. Bundles are named after it.</summary>
    [Inspector]
    [Tooltip("Assets name this in their sidecar, and folders inherit it downwards.")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether its bundles ship with the application or are downloaded.</summary>
    [Inspector]
    [Tooltip("Local ships inside the application. Remote is fetched from RemoteUrl.")]
    public ContentProvider LoadPath { get; set; } = ContentProvider.Local;

    /// <summary>How its assets are distributed across bundles.</summary>
    [Inspector]
    [Tooltip("The single most consequential setting here: a bundle is the unit of both download and residency.")]
    public BundlePacking Packing { get; set; } = BundlePacking.PackTogether;

    /// <summary>How its bundles' chunks are compressed.</summary>
    [Inspector]
    public CompressionMethod Compression { get; set; } = CompressionMethod.Lz4;

    /// <summary>What its bundle files are called.</summary>
    [Inspector]
    [Tooltip("FilenameHash is the only naming a CDN cache cannot serve a stale copy of.")]
    public BundleNaming BundleNaming { get; set; } = BundleNaming.FilenameHash;

    /// <summary>Whether it is built at all.</summary>
    [Inspector]
    [Tooltip("A group of work in progress is turned off rather than deleted.")]
    public bool IncludeInBuild { get; set; } = true;

    /// <summary>Whether a dedicated server's build ships it.</summary>
    /// <remarks>
    ///     ⚠ Drawn, round-tripped and saved rather than only readable in the file, for the reason
    ///     every other field here is: a policy the inspector could not see is one that opening the
    ///     group and pressing save would quietly reset — and the value it would reset to is "ship
    ///     everything", which reads as a server build that simply got bigger.
    /// </remarks>
    [Inspector]
    [Tooltip("Doc 17's server content profile. A realm never asks for it, so a server build leaves it out.")]
    public bool IncludeInServerBuild { get; set; } = true;

    /// <summary>Whether it may be replaced by a content update.</summary>
    [Inspector]
    [Tooltip("A group baked into the binary cannot be replaced by a download, six months after shipping.")]
    public UpdateRestriction UpdateRestriction { get; set; } = UpdateRestriction.CanChangePostRelease;

    /// <summary>Where its bundles are fetched from, for a remote group.</summary>
    [Inspector]
    [Tooltip("A prefix the bundle's file name is appended to. Empty for a local group.")]
    public string RemoteUrl { get; set; } = string.Empty;

    /// <summary>Labels every asset in the group carries, on top of its own.</summary>
    /// <remarks>
    ///     ⚠ Not <c>[Inspector]</c>: nothing draws a list of strings yet. It is mirrored anyway because
    ///     a member the mirror omits is a member <see cref="AddressableGroupDocument.ToGroup" /> writes
    ///     back as empty — opening a group and saving it would delete labels the author never saw.
    /// </remarks>
    public List<string> Labels { get; set; } = [];
}

/// <summary>One addressable group's policy, open for editing.</summary>
/// <remarks>
///     <para>
///         <b>A <c>.vxgroup</c> is one group and one file.</b> Unity keeps the group membership in the
///         group and gets a second identity system for it; doc 08 keeps membership in each asset's
///         sidecar and the <i>policy</i> here, which is what makes "which group is this asset in" a
///         question with one answer and one place to change it.
///     </para>
///     <para>
///         ⚠ <b>The file name and the group's name are not tied together</b>, and
///         <c>ProjectWorkspace</c> is what refuses two files defining one name. Renaming the group
///         here does not rename the file, because a file rename is a project-level operation with its
///         own undo entry on the global stack — and doing it as a side effect of typing in a field
///         would be a rename nobody asked for on every keystroke.
///     </para>
/// </remarks>
public sealed class AddressableGroupDocument : EditorDocument {
    /// <summary>What a group file is written as.</summary>
    public const string Extension = ".vxgroup";

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The policy, as an inspector edits it.</summary>
    public AddressableGroupEdits Policy { get; }

    /// <summary>Why the file did not read, or <see langword="null" /> if it did.</summary>
    public string? LoadError { get; }

    /// <summary>Opens a group.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public AddressableGroupDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        var text = AssetFile.Read(path);
        var group = new AddressableGroup();

        if (text.Trim().Length > 0) {
            try {
                group = YamlSerializer.Parse<AddressableGroup>(text);
            } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
                LoadError = exception.Message;
            }
        }

        Policy = new() {
            // A group whose file says nothing is named after its file, which is what an author who
            // just created `UiCore.vxgroup` means — and is what stops a new group being called "".
            Name = group.Name.Length > 0 ? group.Name : Path.GetFileNameWithoutExtension(path),
            LoadPath = group.LoadPath,
            Packing = group.Packing,
            Compression = group.Compression,
            BundleNaming = group.BundleNaming,
            IncludeInBuild = group.IncludeInBuild,
            IncludeInServerBuild = group.IncludeInServerBuild,
            UpdateRestriction = group.UpdateRestriction,
            RemoteUrl = group.RemoteUrl,
            Labels = [..group.Labels]
        };
    }

    /// <summary>The policy as the record the build reads.</summary>
    /// <returns>The group.</returns>
    public AddressableGroup ToGroup() => new() {
        Name = Policy.Name,
        LoadPath = Policy.LoadPath,
        Packing = Policy.Packing,
        Compression = Policy.Compression,
        BundleNaming = Policy.BundleNaming,
        IncludeInBuild = Policy.IncludeInBuild,
        IncludeInServerBuild = Policy.IncludeInServerBuild,
        UpdateRestriction = Policy.UpdateRestriction,
        RemoteUrl = Policy.RemoteUrl,
        Labels = [..Policy.Labels]
    };

    /// <summary>The file as this document would write it, without writing it.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(ToGroup());

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());
}
