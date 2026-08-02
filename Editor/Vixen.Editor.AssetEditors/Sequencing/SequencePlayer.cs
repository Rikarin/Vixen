// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Curves;
using Vixen.Core.Mathematics;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Sequencing;

/// <summary>An event a sequence raised while it was being scrubbed or played.</summary>
/// <param name="Name">What the key names.</param>
/// <param name="Time">When it was, in seconds.</param>
/// <param name="Track">Which track it came from.</param>
public readonly record struct SequenceSignal(string Name, float Time, string Track);

/// <summary>Puts a scene into the state a sequence describes at a time.</summary>
/// <remarks>
///     <para>
///         <b>Scrubbing and playing are the same operation, which is the decision this class is
///         built around.</b> A player that stepped forward by a delta would give a different answer
///         when scrubbed backwards, and "drag the playhead and watch" is what a sequencer is *for*.
///         So <see cref="Apply" /> is a pure function of the time: every track is evaluated from its
///         own keys, nothing accumulates, and dragging left is exactly as correct as dragging right.
///     </para>
///     <para>
///         ⚠ <b>Events fire on the interval that was crossed, and that is the one thing a pure
///         evaluation cannot express.</b> An event is not a state, it is a moment — so
///         <see cref="Apply" /> takes the previous time as well and reports the events strictly
///         between the two. A scrub backwards reports nothing, deliberately: an editor that fired a
///         footstep because somebody dragged the playhead over it would make a scrub audibly
///         different from a play.
///     </para>
///     <para>
///         ⚠ <b>What it changes, it restores.</b> Entering a sequence takes a snapshot of every
///         entity a track names, and <see cref="Restore" /> puts them back — so scrubbing a cinematic
///         does not leave the level with its actors where the last frame of the shot put them. That is
///         doc 20's own "changes made in play mode are discarded, and the editor says so", applied to
///         the surface that would otherwise break it most quietly.
///     </para>
/// </remarks>
public sealed class SequencePlayer {
    readonly Dictionary<Entity, LocalTransform> taken = [];
    readonly Dictionary<Entity, bool> hidden = [];
    readonly List<SequenceSignal> signals = [];

    /// <summary>The sequence it plays.</summary>
    public SequenceAsset Sequence { get; }

    /// <summary>The scene it drives.</summary>
    public SceneDocument Scene { get; }

    /// <summary>Where the playhead is, in seconds.</summary>
    public float Time { get; private set; }

    /// <summary>Whether the scene is currently being driven.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Which camera the last <see cref="Apply" /> selected, or empty.</summary>
    public string Camera { get; private set; } = string.Empty;

    /// <summary>What the last <see cref="Apply" /> raised.</summary>
    public IReadOnlyList<SequenceSignal> Signals => signals;

    /// <summary>Starts a player over a sequence and a scene.</summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="scene">The scene it drives.</param>
    public SequencePlayer(SequenceAsset sequence, SceneDocument scene) {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(scene);

        Sequence = sequence;
        Scene = scene;
    }

    /// <summary>Takes the snapshot <see cref="Restore" /> puts back.</summary>
    /// <returns>How many entities were recorded.</returns>
    /// <remarks>
    ///     ⚠ <b>Only the entities the sequence names.</b> A whole-world snapshot is what play mode
    ///     takes and is the right thing there; a sequence touches a handful of actors, and copying a
    ///     level to scrub two seconds of it would make the playhead unusable.
    /// </remarks>
    public int Begin() {
        if (IsActive) {
            return taken.Count;
        }

        taken.Clear();
        hidden.Clear();

        foreach (var track in Sequence.Tracks) {
            if (!Scene.TryGetEntity(track.Target, out var entity) || !Scene.World.IsAlive(entity)) {
                continue;
            }

            if (Scene.World.Has<LocalTransform>(entity)) {
                taken[entity] = Scene.World.Get<LocalTransform>(entity);
            }

            hidden[entity] = Scene.IsHiddenDirectly(entity);
        }

        IsActive = true;
        return taken.Count;
    }

    /// <summary>Puts back everything <see cref="Begin" /> recorded.</summary>
    public void Restore() {
        if (!IsActive) {
            return;
        }

        foreach (var (entity, transform) in taken) {
            if (Scene.World.IsAlive(entity) && Scene.World.Has<LocalTransform>(entity)) {
                Scene.World.Set(entity, transform);
            }
        }

        foreach (var (entity, was) in hidden) {
            if (Scene.World.IsAlive(entity)) {
                Scene.SetHidden(entity, was);
            }
        }

        taken.Clear();
        hidden.Clear();

        IsActive = false;
        signals.Clear();
    }

    /// <summary>Puts the scene into the state the sequence describes at a time.</summary>
    /// <param name="time">Where the playhead is, in seconds.</param>
    /// <param name="previous">Where it was, for the events between; negative for a bare seek.</param>
    /// <returns>How many tracks were applied.</returns>
    public int Apply(float time, float previous = -1f) {
        signals.Clear();
        Camera = string.Empty;

        var clamped = Math.Clamp(time, 0f, Sequence.Duration);
        var applied = 0;

        Time = clamped;

        foreach (var track in Sequence.Tracks) {
            if (track.Muted || track.Keys.Count == 0) {
                continue;
            }

            switch (track.Kind) {
                case SequenceTrackKind.Transform when Resolve(track) is { } entity:
                    Scene.World.Set(entity, Transform(track, clamped));
                    applied++;

                    break;

                case SequenceTrackKind.Activation when Resolve(track) is { } entity:
                    Scene.SetHidden(entity, Held(track, clamped) is { } key && key.Value is [var shown, ..] && shown == 0f);
                    applied++;

                    break;

                case SequenceTrackKind.Camera:
                    if (Held(track, clamped) is { } cut) {
                        Camera = cut.Text;
                        applied++;
                    }

                    break;

                case SequenceTrackKind.Event:
                case SequenceTrackKind.Audio:
                    applied += Fire(track, clamped, previous);

                    break;

                default:
                    break;
            }
        }

        return applied;
    }

    Entity? Resolve(SequenceTrackData track) =>
        Scene.TryGetEntity(track.Target, out var entity) && Scene.World.IsAlive(entity) ? entity : null;

    /// <summary>The key at or before a time, or <see langword="null" /> before the first one.</summary>
    static SequenceKeyData? Held(SequenceTrackData track, float time) {
        SequenceKeyData? found = null;

        foreach (var key in track.Keys) {
            if (key.Time <= time) {
                found = key;
            }
        }

        return found;
    }

    int Fire(SequenceTrackData track, float time, float previous) {
        if (previous < 0f || time <= previous) {
            return 0;
        }

        var fired = 0;

        foreach (var key in track.Keys) {
            if (key.Time > previous && key.Time <= time) {
                signals.Add(new(key.Text.Length > 0 ? key.Text : track.Name, key.Time, track.Name));
                fired++;
            }
        }

        return fired;
    }

    /// <summary>What a transform track says at a time.</summary>
    /// <remarks>
    ///     ⚠ <b>The rotation is a slerp between the two keys' quaternions</b>, not a lerp of the ten
    ///     lanes. Interpolating four components independently and normalising is close for small
    ///     angles and visibly wrong for a camera that swings ninety degrees over a shot — which is a
    ///     thing every cinematic does.
    /// </remarks>
    public static LocalTransform Transform(SequenceTrackData track, float time) {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Keys.Count == 0) {
            return LocalTransform.Identity;
        }

        SequenceKeyData? before = null;
        SequenceKeyData? after = null;

        foreach (var key in track.Keys) {
            if (key.Time <= time) {
                before = key;
            } else {
                after = key;

                break;
            }
        }

        if (before is null) {
            return Read(track.Keys[0]);
        }

        if (after is null || after.Time - before.Time <= 1e-6f) {
            return Read(before);
        }

        // ⚠ Held rather than interpolated for a stepped key, which is what a camera cut inside a
        // transform track is. `TangentMode.Constant` is the control set's own name for it, so a curve
        // and a sequencer track mean the same thing by it.
        if (before.Mode == TangentMode.Constant) {
            return Read(before);
        }

        var t = (time - before.Time) / (after.Time - before.Time);
        var first = Read(before);
        var second = Read(after);

        return new() {
            Position = Vector3.Lerp(first.Position, second.Position, t),
            Rotation = Quaternion.Slerp(first.Rotation, second.Rotation, t),
            Scale = Vector3.Lerp(first.Scale, second.Scale, t)
        };
    }

    /// <summary>One key's ten lanes as a transform, filling anything it does not carry.</summary>
    public static LocalTransform Read(SequenceKeyData key) {
        ArgumentNullException.ThrowIfNull(key);

        var lanes = key.Value;

        return new() {
            Position = new(Lane(lanes, 0), Lane(lanes, 1), Lane(lanes, 2)),
            Rotation = lanes.Length >= 7
                ? Quaternion.Normalize(new(Lane(lanes, 3), Lane(lanes, 4), Lane(lanes, 5), Lane(lanes, 6)))
                : Quaternion.Identity,
            Scale = lanes.Length >= 10
                ? new(Lane(lanes, 7), Lane(lanes, 8), Lane(lanes, 9))
                : Vector3.One
        };
    }

    /// <summary>A transform written as a key's ten lanes.</summary>
    /// <param name="transform">The transform.</param>
    /// <returns>The lanes.</returns>
    public static float[] Write(LocalTransform transform) => [
        transform.Position.X, transform.Position.Y, transform.Position.Z,
        transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W,
        transform.Scale.X, transform.Scale.Y, transform.Scale.Z
    ];

    static float Lane(float[] lanes, int index) => index < lanes.Length ? lanes[index] : 0f;
}
