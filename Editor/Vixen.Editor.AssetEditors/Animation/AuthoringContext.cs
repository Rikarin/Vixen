// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;
using Vixen.Editor.AssetEditors.Sequencing;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>One actor in the scene a clip was marked up against.</summary>
/// <param name="Name">What the track calls it.</param>
/// <param name="Clip">Which clip it was playing, by asset path, or empty.</param>
/// <param name="Where">Where it was at the moment being looked at, in world space.</param>
/// <param name="Held">Whether the subject was holding it, and by which socket.</param>
public readonly record struct AuthoringActor(string Name, string Clip, BoneTransform Where, string Held);

/// <summary>What a clip was authored against, as the proposal pass needs it.</summary>
/// <remarks>
///     <para>
///         <b>The sequencer's second job.</b> Assisted authoring measures proximity between the
///         subject's effectors and the things around it, and a clip on its own carries none of that:
///         which actors were in the scene, where they were, and what was in whose hand. This reads a
///         <c>.vxseq</c> and answers those three questions at a moment.
///     </para>
///     <para>
///         ⚠ <b>It is discarded, and that is the design rather than a limitation.</b> What comes out
///         of the proposal pass is a surface coordinate that resolves from the live game alone. The
///         scene exists so the editor can work out what the animator meant; a constraint that needed
///         it at runtime would be a bug, and <c>AnimationClipAsset.ToContent</c> drops the reference
///         for exactly that reason.
///     </para>
///     <para>
///         ⚠ <b>A prop becomes a proxy shape on the subject's rig, and there is nowhere else to put
///         it.</b> <see cref="ConstraintProposals" /> measures against a <see cref="ProxyShapeSet" />,
///         whose shapes hang off joints — so a prop lying on a table is a shape on the subject's root
///         with a world-space offset, and a prop in the subject's hand is a shape on that hand's
///         joint. Both are true statements about where the thing was, which is all the proximity pass
///         needs.
///     </para>
/// </remarks>
public sealed class AuthoringContext {
    AuthoringContext(SequenceAsset sequence, SequenceTrackData? subject, IReadOnlyList<SequenceTrackData> others) {
        Sequence = sequence;
        Subject = subject;
        Others = others;
    }

    /// <summary>The scene.</summary>
    public SequenceAsset Sequence { get; }

    /// <summary>The track the clip being marked up belongs to, or <see langword="null" />.</summary>
    public SequenceTrackData? Subject { get; }

    /// <summary>Every other transform track, which is what the subject might be touching.</summary>
    public IReadOnlyList<SequenceTrackData> Others { get; }

    /// <summary>Whether the scene says enough to be worth measuring against.</summary>
    public bool IsUsable => Subject is not null && Others.Count > 0;

    /// <summary>Reads a sequence as an authoring context.</summary>
    /// <param name="sequence">The scene.</param>
    /// <returns>The context.</returns>
    /// <remarks>
    ///     ⚠ <b>A scene with no named subject is not usable, and saying so beats guessing.</b> Without
    ///     one, "everything else in the scene" includes the character itself and every hand is in
    ///     contact with its own arm.
    /// </remarks>
    public static AuthoringContext From(SequenceAsset sequence) {
        ArgumentNullException.ThrowIfNull(sequence);

        var subject = sequence.Subject.Length == 0
            ? null
            : sequence.Tracks.Find(
                track => track.Kind == SequenceTrackKind.Transform
                    && string.Equals(track.Name, sequence.Subject, StringComparison.Ordinal)
            );

        List<SequenceTrackData> others = [];

        foreach (var track in sequence.Tracks) {
            if (track.Kind == SequenceTrackKind.Transform && !ReferenceEquals(track, subject)) {
                others.Add(track);
            }
        }

        return new(sequence, subject, others);
    }

    /// <summary>Who was where, and what the subject was holding, at a moment.</summary>
    /// <param name="time">When, in seconds.</param>
    /// <returns>The actors, subject excluded.</returns>
    public IReadOnlyList<AuthoringActor> At(float time) {
        List<AuthoringActor> actors = [];

        foreach (var track in Others) {
            actors.Add(new(track.Name, track.Clip, Sample(track, time), Socket(track.Name, time)));
        }

        return actors;
    }

    /// <summary>The subject's own place at a moment.</summary>
    /// <param name="time">When, in seconds.</param>
    /// <returns>The transform.</returns>
    public BoneTransform SubjectAt(float time) =>
        Subject is { } track ? Sample(track, time) : BoneTransform.Identity;

    /// <summary>
    ///     The subject's shapes plus one for every other actor, placed where the scene says it was.
    /// </summary>
    /// <param name="body">The subject's own shapes.</param>
    /// <param name="rig">The subject's rig, for resolving the joint an attachment names.</param>
    /// <param name="time">When, in seconds.</param>
    /// <param name="size">How big to make an actor with no shapes of its own, in metres.</param>
    /// <returns>The combined set, ready for <see cref="ConstraintProposals.Find" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Everything is brought into the subject's model space.</b> A proxy shape's offset is
    ///     from its joint, and the joints are the subject's — so a prop recorded in world space has to
    ///     have the subject's own placement taken back off it, or every proposal would be measured
    ///     against a prop as far from the character as the character is from the origin.
    /// </remarks>
    public ProxyShapeSet Augment(ProxyShapeSet body, Skeleton rig, float time, float size = 0.08f) {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(rig);

        List<ProxyShape> shapes = [.. body.Shapes];

        var subject = SubjectAt(time);
        var inverse = Invert(subject);

        foreach (var actor in At(time)) {
            var joint = actor.Held.Length > 0 ? rig.IndexOf(actor.Held) : 0;

            if (joint < 0) {
                // A socket this rig does not have: the prop is still in the scene, so it is placed
                // against the root rather than dropped. A dropped prop is a proposal that silently
                // never happens, which is the failure the whole authoring role exists to prevent.
                joint = 0;
            }

            var local = BoneTransform.Concatenate(actor.Where, inverse);

            shapes.Add(
                new() {
                    Name = Symbol.Intern(actor.Name),
                    Kind = ShapeKind.Sphere,
                    Joint = joint,
                    Offset = joint == 0
                        ? local
                        : new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One),
                    Dimensions = ShapeParams.Sphere(size),
                    Tags = FacetSet.Of(("source", "scene"))
                }
            );
        }

        return ProxyShapeSet.Of($"{body.Name}+scene", null, [.. shapes]);
    }

    /// <summary>Which socket, if any, the subject was holding an actor by at a moment.</summary>
    string Socket(string actor, float time) {
        var held = string.Empty;

        foreach (var track in Sequence.Tracks) {
            if (track.Kind != SequenceTrackKind.Attachment
                || !string.Equals(track.Name, actor, StringComparison.Ordinal)) {
                continue;
            }

            // ⚠ The last key at or before the moment, and not the nearest. An attachment is a state
            // that starts and stops, not a value between two keys — reading the nearest would put a
            // mug in a hand for the half second before it was picked up.
            foreach (var key in track.Keys) {
                if (key.Time <= time) {
                    held = key.Text;
                }
            }
        }

        return held;
    }

    static BoneTransform Sample(SequenceTrackData track, float time) {
        if (track.Keys.Count == 0) {
            return BoneTransform.Identity;
        }

        var chosen = track.Keys[0];

        foreach (var key in track.Keys) {
            if (key.Time <= time) {
                chosen = key;
            }
        }

        return Read(chosen);
    }

    static BoneTransform Read(SequenceKeyData key) {
        var value = key.Value;

        return new(
            value.Length >= 3 ? new Vector3(value[0], value[1], value[2]) : Vector3.Zero,
            value.Length >= 7 ? Quaternion.Normalize(new(value[3], value[4], value[5], value[6])) : Quaternion.Identity,
            value.Length >= 10 ? new Vector3(value[7], value[8], value[9]) : Vector3.One
        );
    }

    static BoneTransform Invert(in BoneTransform transform) {
        var rotation = Quaternion.Conjugate(transform.Rotation);
        var scale = new Vector3(
            transform.Scale.X == 0f ? 0f : 1f / transform.Scale.X,
            transform.Scale.Y == 0f ? 0f : 1f / transform.Scale.Y,
            transform.Scale.Z == 0f ? 0f : 1f / transform.Scale.Z
        );

        return new(Quaternion.Transform(-transform.Translation * scale, rotation), rotation, scale);
    }
}
