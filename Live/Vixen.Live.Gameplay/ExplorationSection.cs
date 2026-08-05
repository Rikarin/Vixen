// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Exploration;

namespace Vixen.Live.Gameplay;

/// <summary>A character's discovered points and lifted fog, as a profile section.</summary>
/// <remarks>
///     <para>
///         <b>The largest section by a long way, and the one whose size is a design decision.</b> Fog
///         is a bitmap — one bit per cell — so a 256×256 map is a kilobyte and forty maps is forty
///         kilobytes. It is written on the same cadence as a level because it is the same kind of
///         thing: revealing a cell twice reveals it once.
///     </para>
///     <para>
///         ⚠ <b>A map's fog is written with the size it was written at, and refused on load if the
///         map has been resized.</b> A bitmap read into a grid of a different width is not wrong in a
///         way anybody notices — it is a character whose explored map has quietly become diagonal
///         stripes. Losing the fog for one map on the patch that resized it is the honest outcome;
///         <c>ExplorationRecord.RestoreFog</c> is what enforces it and this only records that it
///         happened.
///     </para>
///     <para>
///         ⚠ <b>The discovered points are seated silently.</b> <c>Discover</c> with a null context
///         skips the requirements and still raises <c>Found</c> and <c>Completed</c>, so a login
///         would toast every landmark the character has ever visited and play the map-complete
///         fanfare again. <c>ExplorationRecord.Seat</c> is the door for exactly that reason.
///     </para>
/// </remarks>
public sealed class ExplorationSection : IProfileSection {
    /// <summary>The format this reads and writes.</summary>
    public const int Version = 1;

    readonly ExplorationRecord record;
    readonly CheckpointPolicy? checkpoint;

    /// <summary>Makes one over a character's record.</summary>
    /// <param name="record">Theirs.</param>
    /// <param name="checkpoint">What to tell when something moves, or null for a test.</param>
    public ExplorationSection(ExplorationRecord record, CheckpointPolicy? checkpoint = null) {
        ArgumentNullException.ThrowIfNull(record);

        this.record = record;
        this.checkpoint = checkpoint;

        record.Found += (_, _) => this.checkpoint?.Touch();
    }

    /// <inheritdoc />
    public ProfileSectionId Id => ProfileSections.Exploration;

    /// <summary>How many maps could not be restored because they had been resized.</summary>
    /// <remarks>
    ///     ⚠ Not an error and not zero for ever: a patch that resizes a map costs everybody the fog on
    ///     it, once. What the number is for is telling that apart from a codec that has started
    ///     dropping fog every login.
    /// </remarks>
    public int Resized { get; private set; }

    /// <summary>Says something moved, so the next checkpoint writes.</summary>
    /// <remarks>What lifting fog calls; discovery is subscribed, revealing a cell is not an event.</remarks>
    public void Touch() => checkpoint?.Touch();

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Save() {
        var writer = new ProfileWriter(512);
        var maps = record.Library.Maps.ToArray();

        writer.Int32(Version);
        writer.Int32(maps.Length);

        foreach (var map in maps) {
            var points = record.PointsOn(map).ToArray();
            var fog = record.FogOf(map);

            writer.UInt32(map.Id.Value);
            writer.Int32(map.Columns);
            writer.Int32(map.Rows);
            writer.Int32(points.Length);

            foreach (var point in points) {
                writer.Int32(point);
            }

            writer.Int32(fog.Length);

            foreach (var word in fog) {
                writer.UInt64(word);
            }
        }

        return writer.Written();
    }

    /// <inheritdoc />
    public void Load(ReadOnlyMemory<byte> bytes) {
        var reader = new ProfileReader(bytes.Span);

        Resized = 0;

        if (bytes.Length == 0 || reader.Int32() != Version) {
            return;
        }

        for (var index = reader.Count(20); index > 0 && !reader.IsDone; index--) {
            var map = record.Library.Find(new(reader.UInt32()));
            var columns = reader.Int32();
            var rows = reader.Int32();

            // Read past whatever is here even when the map is gone from this build, so the maps
            // behind it are still reachable. The bytes stay in the profile either way.
            for (var point = reader.Count(4); point > 0 && !reader.IsDone; point--) {
                var found = reader.Int32();

                if (map is not null) {
                    record.Seat(map, found);
                }
            }

            var words = reader.Count(8);
            var fog = new ulong[words];

            for (var word = 0; word < words && !reader.IsDone; word++) {
                fog[word] = reader.UInt64();
            }

            // No fog at all is a character who has not been there, not a map that was resized.
            if (map is null || words == 0) {
                continue;
            }

            if (map.Columns != columns || map.Rows != rows || !record.RestoreFog(map, fog)) {
                Resized++;
            }
        }
    }
}
