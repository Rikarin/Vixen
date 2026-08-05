// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Serialization.Storage;

namespace Vixen.Editor.Assets.Content;

/// <summary>How a group's assets are distributed across bundles.</summary>
/// <remarks>
///     The single most consequential setting in a content build. A bundle is the unit of download and
///     the unit of residency: everything in one arrives together and stays together, so packing
///     decides both what a patch costs and how much memory one asset drags in behind it.
/// </remarks>
public enum BundlePacking {
    /// <summary>
    ///     One bundle for the group. Fewest requests and the best compression, and a change to one
    ///     asset re-downloads all of them.
    /// </summary>
    PackTogether,

    /// <summary>
    ///     One bundle per asset. A patch ships only what changed, at the cost of a request each and
    ///     compression that has nothing to find patterns across.
    /// </summary>
    PackSeparately,

    /// <summary>
    ///     One bundle per label. The middle answer, and usually the right one: things labelled
    ///     together are things loaded together.
    /// </summary>
    PackTogetherByLabel
}

/// <summary>What a bundle's file is called.</summary>
public enum BundleNaming {
    /// <summary>The group's name. Readable, and unsafe behind a CDN that caches by URL.</summary>
    Filename,

    /// <summary>
    ///     The name with the content hash appended. Two builds that produced different bytes have
    ///     different URLs, so a cache can never serve the old one — which is the only way to make a
    ///     content update land reliably.
    /// </summary>
    FilenameHash
}

/// <summary>Whether a group may change after the application has shipped.</summary>
/// <remarks>
///     Unity's content-update semantics, and the non-obvious piece people discover they need six
///     months after shipping. A group baked into the application binary cannot be replaced by a
///     download, so an asset in one that changes has to be moved somewhere that can be — see
///     <see cref="ContentBuilder" />.
/// </remarks>
public enum UpdateRestriction {
    /// <summary>It may be replaced by a content update.</summary>
    CanChangePostRelease,

    /// <summary>It shipped inside the application and is never redownloaded.</summary>
    CannotChangePostRelease
}

/// <summary>An addressable group: a policy for a set of assets.</summary>
/// <remarks>
///     Written as a <c>.vxgroup</c> file next to the assets it governs, and bound through the same
///     YAML machinery as a <c>.meta</c> file — so the format is the API and an artist can read it.
/// </remarks>
[DataContract("AddressableGroup")]
public sealed record AddressableGroup {
    /// <summary>What a group file is called.</summary>
    /// <remarks>
    ///     Spelled once, because two places read it: <c>ProjectWorkspace.Groups</c> collects them and
    ///     <c>BuildPlanner</c> keeps them <i>out</i> of a build. A <c>.vxgroup</c> is the build's own
    ///     configuration and lives under <c>Assets/</c> because that is where the things it governs
    ///     are — packing it would ship a project's packing policy to its players.
    /// </remarks>
    public const string Extension = ".vxgroup";

    /// <summary>What the group is called. Bundles are named after it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Whether its bundles ship with the application or are downloaded.</summary>
    public ContentProvider LoadPath { get; init; } = ContentProvider.Local;

    /// <summary>How its assets are distributed across bundles.</summary>
    public BundlePacking Packing { get; init; } = BundlePacking.PackTogether;

    /// <summary>How its bundles' chunks are compressed.</summary>
    public CompressionMethod Compression { get; init; } = CompressionMethod.Lz4;

    /// <summary>What its bundle files are called.</summary>
    public BundleNaming BundleNaming { get; init; } = BundleNaming.FilenameHash;

    /// <summary>Whether it is built at all. A group of work in progress is turned off rather than deleted.</summary>
    public bool IncludeInBuild { get; init; } = true;

    /// <summary>Whether it may be replaced by a content update.</summary>
    public UpdateRestriction UpdateRestriction { get; init; } = UpdateRestriction.CanChangePostRelease;

    /// <summary>Where its bundles are fetched from, for a remote group.</summary>
    /// <remarks>
    ///     A prefix the bundle's file name is appended to. Empty for a local group, where the
    ///     application already knows where its own files are.
    /// </remarks>
    public string RemoteUrl { get; init; } = string.Empty;

    /// <summary>Labels every asset in the group carries, on top of its own.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is not the folder inheritance <see cref="BuildPlanner" /> refuses, and the
    ///         difference is what makes it safe.</b> That note is about a folder's labels reaching its
    ///         descendants, and its objection is that a label is a query — <em>"the thing you most
    ///         want to be able to say 'all of these except that one' about"</em> — and inherited
    ///         labels cannot be removed from one file. A group is joined explicitly, per asset, so
    ///         "except that one" is answered by putting that one in another group, which is already
    ///         how packing and load path are decided.
    ///     </para>
    ///     <para>
    ///         <b>What it is for.</b> "Everything in this group is a gameplay definition" is the whole
    ///         of how a runtime finds its content, and the alternative is the same line in five
    ///         hundred sidecars.
    ///     </para>
    /// </remarks>
    public List<string> Labels { get; init; } = [];
}
