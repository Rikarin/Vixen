// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.IO.Watch;

/// <summary>What happened to a file.</summary>
public enum FileChangeKind {
    /// <summary>It did not exist and now does.</summary>
    Created,

    /// <summary>Its contents changed.</summary>
    Changed,

    /// <summary>It existed and now does not.</summary>
    Deleted,

    /// <summary>It moved. <see cref="FileChange.OldPath" /> says from where.</summary>
    Renamed
}

/// <summary>One settled change to one path.</summary>
/// <param name="Path">What changed.</param>
/// <param name="Kind">How.</param>
/// <param name="OldPath">Where it came from, for <see cref="FileChangeKind.Renamed" />; otherwise the default.</param>
public readonly record struct FileChange(VirtualPath Path, FileChangeKind Kind, VirtualPath OldPath = default);
