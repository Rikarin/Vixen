// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>Something of a known size, put in the scene to judge everything else against.</summary>
/// <param name="Name">What it is: a person, a door, a corridor, a car.</param>
/// <param name="Size">How big, in metres — width, height and depth.</param>
/// <param name="Origin">Where its box sits relative to the point it is placed at.</param>
/// <remarks>
///     ⚠ <b><see cref="Origin" /> is a fraction of the size, not a distance.</b> A person and a door
///     stand on the floor, a corridor is a hole you are inside, and a car sits on its wheels — so what
///     "placed here" means differs per volume and is a property of the volume rather than of the
///     placement. Zero is centred; −0.5 in Y puts the bottom of the box on the point.
/// </remarks>
public readonly record struct ReferenceVolume(string Name, Vector3 Size, Vector3 Origin) {
    /// <summary>Where the box's centre goes when the volume is placed at a point.</summary>
    /// <param name="at">The point.</param>
    /// <returns>The centre, in world space.</returns>
    public Vector3 CentreAt(Vector3 at) => at + (Size * Origin);
}

/// <summary>The four sizes every level designer draws by hand on every project.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24 lists these under the group that separates a toolset a professional will use from
///         one they will try, and the reason is that a grey box has no scale.</b> A corridor is four
///         metres wide or eight and there is nothing in an empty scene to tell you which; a 1.8 m
///         capsule beside it answers in one glance and keeps answering while the corridor is dragged.
///     </para>
///     <para>
///         ⚠ <b>Drawn, not shipped.</b> They are lines in the viewport rather than entities in the
///         scene: nothing to select, nothing to save, nothing to accidentally leave in a level and
///         find in a build. That is the whole difference between this and the cube everybody scales to
///         1.8 and then forgets about.
///     </para>
///     <para>
///         The numbers are the ones the two reference engines' own documentation uses for their
///         character controllers and their door frames, which is what makes a block-out built against
///         them feel right the first time a character is dropped into it.
///     </para>
/// </remarks>
public static class ReferenceVolumes {
    /// <summary>A standing person: shoulder width, 1.8 m tall, standing on the point.</summary>
    public static ReferenceVolume Person { get; } =
        new("Person", new Vector3(0.6f, 1.8f, 0.4f), new Vector3(0f, 0.5f, 0f));

    /// <summary>A door: the one measurement a wall is judged against.</summary>
    public static ReferenceVolume Door { get; } =
        new("Door", new Vector3(0.9f, 2.1f, 0.15f), new Vector3(0f, 0.5f, 0f));

    /// <summary>Four metres of corridor, which is a hole you are inside rather than a thing on the floor.</summary>
    public static ReferenceVolume Corridor { get; } =
        new("Corridor", new Vector3(2.4f, 2.6f, 4f), new Vector3(0f, 0.5f, 0f));

    /// <summary>A car, for the scenes that have to admit vehicles exist.</summary>
    public static ReferenceVolume Vehicle { get; } =
        new("Vehicle", new Vector3(1.9f, 1.5f, 4.5f), new Vector3(0f, 0.5f, 0f));

    /// <summary>All four, in the order a menu lists them.</summary>
    public static IReadOnlyList<ReferenceVolume> All { get; } = [Person, Door, Corridor, Vehicle];

    /// <summary>The one with a name, or <see langword="null" />.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The volume.</returns>
    public static ReferenceVolume? Find(string name) {
        ArgumentNullException.ThrowIfNull(name);

        foreach (var volume in All) {
            if (string.Equals(volume.Name, name, StringComparison.OrdinalIgnoreCase)) {
                return volume;
            }
        }

        return null;
    }
}

/// <summary>The reference volumes a pane is showing, and where.</summary>
/// <remarks>
///     ⚠ <b>A list on the pane rather than entities in the document, which is the point.</b> See
///     <see cref="ReferenceVolumes" />: these exist to be looked at and then to go away, and anything
///     that made them savable would make "did somebody leave a reference cube in the level" a question
///     a build has to answer.
/// </remarks>
public sealed class ReferenceVolumeSet {
    readonly List<(ReferenceVolume Volume, Vector3 At)> placed = [];

    /// <summary>What is being shown, and where each one is.</summary>
    public IReadOnlyList<(ReferenceVolume Volume, Vector3 At)> Placed => placed;

    /// <summary>Whether there is anything to draw.</summary>
    public bool IsEmpty => placed.Count == 0;

    /// <summary>Puts one in the scene.</summary>
    /// <param name="volume">Which.</param>
    /// <param name="at">Where, in world space.</param>
    public void Add(ReferenceVolume volume, Vector3 at) => placed.Add((volume, at));

    /// <summary>Takes the last one back out.</summary>
    /// <returns>Whether there was one.</returns>
    public bool RemoveLast() {
        if (placed.Count == 0) {
            return false;
        }

        placed.RemoveAt(placed.Count - 1);
        return true;
    }

    /// <summary>Takes them all out.</summary>
    public void Clear() => placed.Clear();
}
