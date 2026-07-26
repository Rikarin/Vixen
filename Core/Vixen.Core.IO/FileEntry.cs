// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.IO;

/// <summary>What a provider knows about one file or directory without opening it.</summary>
/// <param name="Path">The full virtual path.</param>
/// <param name="Length">The size in bytes. Zero for a directory.</param>
/// <param name="LastWriteUtc">When it last changed, or <see cref="DateTimeOffset.MinValue" /> if the provider cannot say.</param>
/// <param name="IsDirectory">Whether it is a directory.</param>
/// <remarks>
///     Deliberately four fields. Permissions, owners, and attributes are things some providers cannot
///     answer at all — a file inside an APK has no mode bits — and an interface that promises them
///     would be an interface most implementations lie about.
/// </remarks>
public readonly record struct FileEntry(VirtualPath Path, long Length, DateTimeOffset LastWriteUtc, bool IsDirectory);
