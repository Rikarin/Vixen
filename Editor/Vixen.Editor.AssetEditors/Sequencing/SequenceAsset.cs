// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Curves;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Sequencing;

/// <summary>What a sequencer track drives.</summary>
/// <remarks>
///     ⚠ <b>Five kinds and not "a property path".</b> A generic property track sounds more powerful
///     and is worse in the one way that matters: it cannot say what a key <i>means</i>, so a
///     sequencer built on one draws every track the same and cannot offer a camera cut, an audio
///     waveform or an event marker. These are the five things a cinematic is actually made of, and
///     a sixth is a new case here rather than a redesign.
/// </remarks>
public enum SequenceTrackKind {
    /// <summary>An entity's position, rotation and scale, keyed together.</summary>
    Transform,

    /// <summary>Which camera the sequence is looking through.</summary>
    Camera,

    /// <summary>A sound, played at a time.</summary>
    Audio,

    /// <summary>A named marker a game listens for.</summary>
    Event,

    /// <summary>Whether an entity is shown.</summary>
    Activation,

    /// <summary>What is held by whom, and from when.</summary>
    /// <remarks>
    ///     ⚠ <b>The sixth kind, and it is here for the authoring role rather than for playback.</b> A
    ///     clip being marked up needs to know a mug was in the character's hand from two seconds in:
    ///     "the hand is near the mug" and "the hand is holding the mug" are the same measurement and
    ///     different intents, and only the scene knows which. A track and not a field, for the reason
    ///     every other kind is one — what is attached changes over the sequence.
    /// </remarks>
    Attachment
}

/// <summary>One key of a sequencer track.</summary>
/// <remarks>
///     ⚠ <b>Ten numbers and a string, rather than a shape per track kind.</b> A transform key wants
///     ten; a camera cut wants a target and nothing else; an event wants a name. Giving each kind its
///     own record would make the file a polymorphic tree — the thing
///     <c>AnimationGraphAsset</c> refuses for the same reason — so the lanes are named by what reads
///     them and a kind that uses three leaves seven at rest.
/// </remarks>
[DataContract("SequenceKey")]
public sealed class SequenceKeyData {
    /// <summary>When it is, in seconds.</summary>
    public float Time { get; set; }

    /// <summary>The numbers it carries. A transform key uses all ten.</summary>
    public float[] Value { get; set; } = [];

    /// <summary>What it names — an event, or a camera.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>How the value is reached from the previous key.</summary>
    public TangentMode Mode { get; set; } = TangentMode.Auto;
}

/// <summary>One track of a sequence.</summary>
[DataContract("SequenceTrack")]
public sealed class SequenceTrackData {
    /// <summary>What the row says.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What it drives.</summary>
    public SequenceTrackKind Kind { get; set; }

    /// <summary>Which entity, by the scene's stable id.</summary>
    /// <remarks>
    ///     ⚠ <b>The stable id and not the entity handle or the name.</b> A handle is a slot in a world
    ///     and does not survive a reload; a name is a thing people change. <c>EntityId</c> is what a
    ///     <c>.vxscene</c> stores and what <c>SceneDocument.TryGetEntity</c> resolves, so a sequence
    ///     survives the scene being closed and reopened — which is most of what "a sequence is an
    ///     asset" means.
    /// </remarks>
    public EntityId Target { get; set; }

    /// <summary>The asset it plays, for an audio track.</summary>
    public AssetId Asset { get; set; }

    /// <summary>Whether it is evaluated.</summary>
    public bool Muted { get; set; }

    /// <summary>Which clip this actor is playing, by asset path, for the authoring role.</summary>
    /// <remarks>
    ///     ⚠ <b>A path and not an <see cref="AssetId" />, unlike <see cref="Asset" />.</b> A sequence
    ///     used as an authoring context is read by a tool alongside the clip being marked up, and a
    ///     path is what somebody can read in the file and correct by hand — which is the whole
    ///     difference between a reference a build resolves and a note a person maintains.
    /// </remarks>
    public string Clip { get; set; } = string.Empty;

    /// <summary>Its keys, in time order.</summary>
    public List<SequenceKeyData> Keys { get; set; } = [];
}

/// <summary>A whole <c>.vxseq</c> document.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20 calls this "the largest single missing authoring surface" and it is the one that
///         had nothing at all underneath it</b> — unlike the mixer, whose format existed, or the VFX
///         graph, whose compiler did. So the format is here, the evaluation is
///         <see cref="SequencePlayer" />, and both are deliberately small: a cinematic is tracks over
///         time, and everything else a mature sequencer grows (sub-sequences, blending between
///         shots, a recording mode) is a case added to a shape that already holds.
///     </para>
///     <para>
///         ⚠ <b>Times are seconds and the frame rate is the editor's snap</b>, for the reason
///         <c>AnimationClipAsset</c> gives: a file that stored frames would re-time itself when a
///         project changed its rate.
///     </para>
/// </remarks>
[DataContract("SequenceAsset")]
public sealed class SequenceAsset {
    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

    /// <summary>What a sequence is written as.</summary>
    public const string Extension = ".vxseq";

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the sequence is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>How long it runs, in seconds.</summary>
    public float Duration { get; set; } = 5f;

    /// <summary>What the timeline snaps to, in frames a second.</summary>
    public float FrameRate { get; set; } = 30f;

    /// <summary>Its tracks, in the order they are shown.</summary>
    public List<SequenceTrackData> Tracks { get; set; } = [];

    /// <summary>Which track is the character a clip is being marked up for, by name, or empty.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole of what makes a sequence an authoring context, and it is one string.</b>
    ///         A proposal engine measures proximity between the subject's effectors and everything
    ///         else in the scene; without knowing which actor is the subject, "everything else"
    ///         includes the subject, and every hand is in contact with its own arm.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Authoring-time only.</b> A sequence is not loaded by a build and nothing at runtime
    ///         may depend on this; it exists so the editor can work out what the animator meant, bake
    ///         that into a tag, and be discarded.
    ///     </para>
    /// </remarks>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Reads YAML into a sequence.</summary>
    /// <param name="yaml">The text.</param>
    /// <returns>The sequence.</returns>
    /// <exception cref="NotSupportedException">The file is from a newer editor.</exception>
    public static SequenceAsset FromYaml(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        if (yaml.Trim().Length == 0) {
            return new();
        }

        var sequence = YamlSerializer.Parse<SequenceAsset>(yaml);

        return sequence.Version <= Current
            ? sequence
            : throw new NotSupportedException(
                $"This sequence is version {sequence.Version} and this build reads {Current}."
            );
    }

    /// <summary>Writes it as YAML.</summary>
    /// <returns>The text.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(this);
}
