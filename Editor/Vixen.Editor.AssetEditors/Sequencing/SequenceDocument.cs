// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;

namespace Vixen.Editor.AssetEditors.Sequencing;

/// <summary>A cinematic, open for editing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The scene it drives is supplied, not opened.</b> A sequence is authored <i>against</i>
///         a level that is already open — which is why doc 20's exit criterion is "a cinematic can be
///         authored, scrubbed and played" rather than "a sequence file can be edited". A document
///         that opened its own scene would put a second world beside the one the viewport is drawing
///         and the playhead would move actors nobody could see.
///     </para>
///     <para>
///         ⚠ <b>A sequence with no scene is still editable.</b> Opening one from the browser before
///         the level it belongs to gives a document whose tracks can be renamed, reordered and
///         retimed, and whose <see cref="Player" /> is null — scrubbing says there is nothing to
///         drive rather than doing nothing.
///     </para>
/// </remarks>
public sealed class SequenceDocument : EditorDocument {
    /// <summary>What a sequence is written as.</summary>
    public const string Extension = SequenceAsset.Extension;

    SequencePlayer? player;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The sequence.</summary>
    public SequenceAsset Sequence { get; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>What drives the scene, or <see langword="null" /> until one is attached.</summary>
    public SequencePlayer? Player => player;

    /// <summary>Raised after anything changes the sequence.</summary>
    public event Action<SequenceDocument>? Changed;

    /// <summary>Opens a sequence.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public SequenceDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        try {
            Sequence = SequenceAsset.FromYaml(AssetFile.Read(path));
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            Sequence = new();
            LoadError = exception.Message;
        }

        if (Sequence.Name.Length == 0) {
            Sequence.Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>Points the sequence at a scene, so the playhead has something to move.</summary>
    /// <param name="scene">The scene, or <see langword="null" /> to detach.</param>
    /// <remarks>
    ///     ⚠ <b>Detaching restores first.</b> Closing the scene tab while scrubbed halfway through a
    ///     shot would otherwise save a level with its actors wherever frame 47 left them.
    /// </remarks>
    public void Attach(SceneDocument? scene) {
        player?.Restore();
        player = scene is null ? null : new SequencePlayer(Sequence, scene);
    }

    /// <summary>Adds a track, undoably.</summary>
    /// <param name="track">The track.</param>
    public void AddTrack(SequenceTrackData track) {
        ArgumentNullException.ThrowIfNull(track);

        Run("Add Track", () => Sequence.Tracks.Add(track), () => Sequence.Tracks.Remove(track));
    }

    /// <summary>Removes a track, undoably.</summary>
    /// <param name="track">The track.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveTrack(SequenceTrackData track) {
        ArgumentNullException.ThrowIfNull(track);

        var index = Sequence.Tracks.IndexOf(track);

        if (index < 0) {
            return false;
        }

        Run("Remove Track", () => Sequence.Tracks.RemoveAt(index), () => Sequence.Tracks.Insert(index, track));

        return true;
    }

    /// <summary>Adds a key to a track, undoably.</summary>
    /// <param name="track">The track.</param>
    /// <param name="key">The key.</param>
    /// <remarks>
    ///     ⚠ <b>A key at a time another key already occupies replaces it.</b> Pressing the key button
    ///     twice at one frame is how somebody re-records a pose, and a second key at the same time is
    ///     a track whose value there is whichever the sort happened to put last.
    /// </remarks>
    public void AddKey(SequenceTrackData track, SequenceKeyData key) {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(key);

        var previous = track.Keys.Find(entry => Math.Abs(entry.Time - key.Time) <= Epsilon);
        var before = previous is null ? -1 : track.Keys.IndexOf(previous);

        Run(
            "Add Key",
            () => {
                if (previous is not null) {
                    track.Keys.Remove(previous);
                }

                track.Keys.Add(key);
                track.Keys.Sort(static (left, right) => left.Time.CompareTo(right.Time));
            },
            () => {
                track.Keys.Remove(key);

                if (previous is not null) {
                    track.Keys.Insert(Math.Clamp(before, 0, track.Keys.Count), previous);
                }
            }
        );
    }

    /// <summary>Removes a key, undoably.</summary>
    /// <param name="track">The track it is on.</param>
    /// <param name="key">The key.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveKey(SequenceTrackData track, SequenceKeyData key) {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(key);

        var index = track.Keys.IndexOf(key);

        if (index < 0) {
            return false;
        }

        Run("Remove Key", () => track.Keys.RemoveAt(index), () => track.Keys.Insert(index, key));

        return true;
    }

    /// <summary>Moves a key, undoably.</summary>
    /// <param name="track">The track it is on.</param>
    /// <param name="key">The key.</param>
    /// <param name="time">Where to.</param>
    public void MoveKey(SequenceTrackData track, SequenceKeyData key, float time) {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(key);

        var previous = key.Time;

        Run(
            "Move Key",
            () => {
                key.Time = time;
                track.Keys.Sort(static (left, right) => left.Time.CompareTo(right.Time));
            },
            () => {
                key.Time = previous;
                track.Keys.Sort(static (left, right) => left.Time.CompareTo(right.Time));
            }
        );
    }

    /// <summary>Changes how long the sequence runs, undoably.</summary>
    /// <param name="duration">The new length, in seconds.</param>
    public void SetDuration(float duration) {
        var clamped = Math.Max(duration, MinimumDuration);
        var previous = Sequence.Duration;

        if (Math.Abs(clamped - previous) < 1e-6f) {
            return;
        }

        Run("Set Sequence Length", () => Sequence.Duration = clamped, () => Sequence.Duration = previous);
    }

    /// <summary>Turns a track on or off, undoably.</summary>
    /// <param name="track">The track.</param>
    /// <param name="muted">Whether it is skipped.</param>
    public void SetMuted(SequenceTrackData track, bool muted) {
        ArgumentNullException.ThrowIfNull(track);

        var previous = track.Muted;

        if (previous == muted) {
            return;
        }

        Run("Mute Track", () => track.Muted = muted, () => track.Muted = previous);
    }

    /// <summary>The shortest a sequence may be, in seconds.</summary>
    public const float MinimumDuration = 1f / 240f;

    /// <summary>How close two keys have to be before one replaces the other.</summary>
    public const float Epsilon = 1f / 480f;

    /// <summary>The sequence as it would be written, without writing it.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => Sequence.ToYaml();

    void Run(string name, Action apply, Action revert) {
        Stack.Execute(
            new DelegateCommand(
                name,
                _ => {
                    apply();
                    Changed?.Invoke(this);
                },
                _ => {
                    revert();
                    Changed?.Invoke(this);
                }
            )
        );
    }

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());

    /// <inheritdoc />
    protected override void OnClosed() {
        base.OnClosed();

        // ⚠ The scene goes back to what it was, however the tab closed. A sequence document is the
        // one thing in this assembly that changes a *different* document's state, so it is the one
        // that has to put it back.
        player?.Restore();
        player = null;
    }
}
