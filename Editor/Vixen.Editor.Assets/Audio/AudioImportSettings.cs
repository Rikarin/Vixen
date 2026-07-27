// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Audio;

/// <summary>Which sample format a clip ships in.</summary>
public enum AudioFormatChoice {
    /// <summary>Whatever the file held. 16-bit stays 16-bit; a float or 24-bit source keeps its headroom.</summary>
    Automatic,

    /// <summary>Signed 16-bit. Half the memory, and below the noise floor of most sound effects.</summary>
    Int16,

    /// <summary>32-bit float. What a source recorded with headroom to spare was written at.</summary>
    Float32
}

/// <summary>How one audio file is imported.</summary>
/// <remarks>
///     Short, and deliberately. The settings that belong here are the ones a file cannot answer for
///     itself; sample rate and channel count are in the file and changing either is resampling, which
///     is a signal-processing decision an authoring tool makes better than a build step.
/// </remarks>
[DataContract("AudioImporter")]
public sealed record AudioImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;

    /// <summary>Which sample format to ship in.</summary>
    public AudioFormatChoice Format { get; init; } = AudioFormatChoice.Automatic;

    /// <summary>
    ///     Whether to mix the channels down to one.
    /// </summary>
    /// <remarks>
    ///     The setting that earns its place. <b>A stereo clip cannot be positioned in the world</b> —
    ///     it already says which ear it is in, so panning it does nothing and the sound stays in the
    ///     listener's head wherever its emitter is. Every 3D sound has to be mono, and the source an
    ///     artist delivers usually is not. Doing it here rather than asking for a second export keeps
    ///     one file in the project.
    /// </remarks>
    public bool ForceMono { get; init; }
}
