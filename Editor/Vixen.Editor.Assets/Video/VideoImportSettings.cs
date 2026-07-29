// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Video;

/// <summary>How one video file is imported.</summary>
/// <remarks>
///     <para>
///         Shorter even than <c>AudioImportSettings</c>, and for a stronger reason: a video is not
///         transcoded here. The engine ships no encoder, re-encoding an hour of footage is not a
///         thing a build step should do on somebody's laptop, and every decision a transcode would
///         make — codec, bitrate, resolution — belongs to whatever exported the file.
///     </para>
///     <para>
///         So the import is a read of the header and a copy of the bytes, and the only setting is
///         the one the file cannot answer for itself.
///     </para>
/// </remarks>
[DataContract("VideoImporter")]
public sealed record VideoImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;

    /// <summary>
    ///     Whether to copy the container into the build, rather than only its metadata.
    /// </summary>
    /// <remarks>
    ///     On by default, which is what makes a video work with no further arrangement. Off is for a
    ///     title streaming its cutscenes from a CDN: the <c>VideoClip</c> still carries the size, the
    ///     duration and the codec — so a game can decide before the cutscene starts whether it can
    ///     play it — and the bytes are fetched by something else at run time.
    /// </remarks>
    public bool EmbedContainer { get; init; } = true;
}
