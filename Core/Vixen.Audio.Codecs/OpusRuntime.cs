// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Concentus;

namespace Vixen.Audio.Codecs;

/// <summary>Pins Opus to its managed implementation, before anything can ask for the other one.</summary>
/// <remarks>
///     <para>
///         <b>Concentus will P/Invoke a system libopus if it finds one, and that is not a behaviour a
///         game engine can have.</b> Its <c>AttemptToUseNativeLibrary</c> defaults to on, so whether
///         the codec runs managed or native depends on whether the machine happens to have libopus
///         installed — which a developer's Mac with Homebrew does and a player's does not. That is a
///         difference in behaviour that no test on the developer's machine can see.
///     </para>
///     <para>
///         <b>It is not hypothetical.</b> Against Homebrew's libopus on macOS the interop mismatches:
///         the encoder ignores its bitrate and emits maximum-length packets — twenty times the
///         bandwidth — property reads come back as zero, and some calls fault the process outright.
///         A shipped game would have had the bug and not the crash, which is worse.
///     </para>
///     <para>
///         <b>Managed is the position anyway.</b> It is the same reasoning
///         <see cref="VorbisStreamDecoder" /> is written down with: a native decoder means a binary
///         per RID and a resolver, the browser target cannot P/Invoke at all, and the decode is a
///         fraction of a per-cent of a core. Choosing it explicitly costs nothing that was not
///         already being given up, and buys the same answer on every machine.
///     </para>
///     <para>
///         Called from every codec's constructor rather than run as a module initializer, which is
///         discouraged in a library for good reasons: a consumer should not have a type's behaviour
///         changed underneath it by merely referencing an assembly. Three explicit calls are cheaper
///         to understand than one invisible one.
///     </para>
/// </remarks>
static class OpusRuntime {
    static OpusRuntime() => OpusCodecFactory.AttemptToUseNativeLibrary = false;

    /// <summary>Makes sure the choice above has been made. Call it before creating any Opus codec.</summary>
    /// <remarks>
    ///     Empty on purpose. Calling a static method is what the runtime guarantees will run the
    ///     static constructor first, and the static constructor is the whole of the work.
    /// </remarks>
    internal static void Ensure() { }
}
