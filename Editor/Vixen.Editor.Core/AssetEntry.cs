// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.Core;

/// <summary>What the database knows about one asset without opening its importer's settings.</summary>
/// <param name="Guid">Its identity.</param>
/// <param name="Path">Where it is, project-relative with forward slashes.</param>
/// <param name="ImporterTag">Which importer claims it, or <see langword="null" /> if it says none yet.</param>
/// <param name="MetaVersion">Which envelope schema version its sidecar was written against.</param>
/// <param name="IsFolder">Whether it is a folder rather than a file.</param>
/// <remarks>
///     Deliberately only the envelope. The whole point of the index is that a project's worth of
///     sidecars can be read without parsing a project's worth of importer settings; an entry that
///     carried the settings would make that impossible.
/// </remarks>
public readonly record struct AssetEntry(
    AssetId Guid,
    string Path,
    string? ImporterTag,
    int MetaVersion,
    bool IsFolder
) {
    /// <summary>The file name, with its extension.</summary>
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Something the database found that a person should know about.</summary>
public enum AssetIssueKind {
    /// <summary>A file with no sidecar. One was created with a fresh GUID.</summary>
    MetaCreated,

    /// <summary>A sidecar whose asset has gone. It was moved to <c>Library/OrphanMeta/</c>.</summary>
    MetaOrphaned,

    /// <summary>Two assets claiming one GUID. One of them was re-GUIDed.</summary>
    DuplicateGuid,

    /// <summary>A sidecar that could not be read at all.</summary>
    MetaUnreadable
}

/// <summary>One thing the database found.</summary>
/// <param name="Kind">What kind of thing.</param>
/// <param name="Path">Which asset it concerns, project-relative.</param>
/// <param name="Message">What happened, in a sentence a person can act on.</param>
public sealed record AssetIssue(AssetIssueKind Kind, string Path, string Message);

/// <summary>What a scan did.</summary>
/// <param name="Assets">How many assets are in the index.</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Issues">Everything worth telling somebody about.</param>
public sealed record ScanReport(int Assets, TimeSpan Elapsed, IReadOnlyList<AssetIssue> Issues);

/// <summary>How a scan should behave when it finds something wrong.</summary>
/// <param name="CreateMissingMeta">Whether a file with no sidecar gets one.</param>
/// <param name="QuarantineOrphanMeta">Whether a sidecar with no asset is moved aside.</param>
/// <param name="ResolveDuplicateGuids">Whether one of two assets claiming a GUID is re-GUIDed.</param>
/// <remarks>
///     All three default to repairing, because that is what opening a project should do. They exist
///     because inspection is a real use — a build server asking "is this project clean?" wants the
///     answer, not a working tree with edits in it.
/// </remarks>
public sealed record ScanOptions(
    bool CreateMissingMeta = true,
    bool QuarantineOrphanMeta = true,
    bool ResolveDuplicateGuids = true
) {
    /// <summary>Repair everything. What opening a project does.</summary>
    public static ScanOptions Default { get; } = new();

    /// <summary>Change nothing on disk. What a check does.</summary>
    public static ScanOptions ReadOnly { get; } = new(false, false, false);
}
