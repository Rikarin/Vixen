// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Collections;

namespace Vixen.Live.Gameplay;

/// <summary>A character's transmog overrides, hidden slots and worn title, as a profile section.</summary>
/// <remarks>
///     <para>
///         <b>Per character, where the collection it draws on is per account.</b> A mount earned on
///         one character is owned by all of them and lives in <c>IAccountGrain</c>; which of them is
///         wearing the Tabard of the Ninth is one character's business. That split is why only the
///         wardrobe is here — the unlocks are not this section's to write, and storing them twice
///         would be two records of one fact that can disagree.
///     </para>
///     <para>
///         ⚠ <b>Slots are written as names and never as tag indices, and this is the same class of
///         bug as writing a gameplay <c>PlayerId</c> to the database.</b> A <c>GameplayTag</c> is an
///         index into a pre-order walk of the build's tag tree, so <em>adding one tag renumbers
///         every tag after it</em>. A wardrobe stored by index and read back on the next patch is a
///         character whose helm override has silently become their boots.
///     </para>
///     <para>
///         ⚠ <b>The appearances are <c>DefId</c>s and those are safe</b>, because a <c>DefId</c> is a
///         hash of an address rather than a position in a table — the same property that lets two
///         builds agree without being told.
///     </para>
///     <para>
///         ⚠ <b>Nothing is checked on the way in, and here that is the correct behaviour rather than
///         merely a safe one.</b> <c>Wardrobe.Resolve</c> and <c>Wardrobe.Worn</c> re-check the
///         unlock every time they are asked, so an appearance a patch has taken back stops showing on
///         its own — and if it is granted again the player's choice is still there. Checking at load
///         instead would throw that choice away for good.
///     </para>
/// </remarks>
public sealed class WardrobeSection : IProfileSection {
    /// <summary>The format this reads and writes.</summary>
    public const int Version = 1;

    readonly Wardrobe wardrobe;
    readonly GameplayTagTable tags;
    readonly CheckpointPolicy? checkpoint;

    /// <summary>Makes one over a character's wardrobe.</summary>
    /// <param name="wardrobe">Theirs.</param>
    /// <param name="tags">The build's tag table, for turning a slot into a name and back.</param>
    /// <param name="checkpoint">What to tell when something moves, or null for a test.</param>
    public WardrobeSection(Wardrobe wardrobe, GameplayTagTable tags, CheckpointPolicy? checkpoint = null) {
        ArgumentNullException.ThrowIfNull(wardrobe);
        ArgumentNullException.ThrowIfNull(tags);

        this.wardrobe = wardrobe;
        this.tags = tags;
        this.checkpoint = checkpoint;
    }

    /// <inheritdoc />
    public ProfileSectionId Id => ProfileSections.Wardrobe;

    /// <summary>How many stored slots this build has no tag for.</summary>
    /// <remarks>
    ///     ⚠ A slot renamed by a patch, which loses that override — and the number is how anybody
    ///     finds out. It should be zero; a rename is a content migration, not a load-time surprise.
    /// </remarks>
    public int UnknownSlots { get; private set; }

    /// <summary>Says something moved, so the next checkpoint writes.</summary>
    public void Touch() => checkpoint?.Touch();

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Save() {
        var writer = new ProfileWriter(96);
        var overrides = wardrobe.Overrides.ToArray();
        var hidden = wardrobe.Hidden.ToArray();

        writer.Int32(Version);
        writer.UInt32(wardrobe.Title.Value);
        writer.Int32(overrides.Length);

        foreach (var (slot, appearance) in overrides) {
            writer.Text(tags.NameOf(slot));
            writer.UInt32(appearance.Value);
        }

        writer.Int32(hidden.Length);

        foreach (var slot in hidden) {
            writer.Text(tags.NameOf(slot));
        }

        return writer.Written();
    }

    /// <inheritdoc />
    public void Load(ReadOnlyMemory<byte> bytes) {
        var reader = new ProfileReader(bytes.Span);

        UnknownSlots = 0;

        if (bytes.Length == 0 || reader.Int32() != Version) {
            return;
        }

        wardrobe.SeatTitle(new(reader.UInt32()));

        for (var index = reader.Count(8); index > 0 && !reader.IsDone; index--) {
            var slot = tags.Resolve(reader.Text());
            var appearance = new DefId(reader.UInt32());

            if (!slot.IsSome) {
                UnknownSlots++;

                continue;
            }

            wardrobe.Seat(slot, appearance);
        }

        for (var index = reader.Count(4); index > 0 && !reader.IsDone; index--) {
            var slot = tags.Resolve(reader.Text());

            if (slot.IsSome) {
                wardrobe.Hide(slot);
            } else {
                UnknownSlots++;
            }
        }
    }
}
