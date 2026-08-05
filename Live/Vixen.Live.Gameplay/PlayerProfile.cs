// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;

namespace Vixen.Live.Gameplay;

/// <summary>What names one library's slice of a character's durable state.</summary>
/// <remarks>
///     A hash of a name, for <c>DefId</c>'s reason: no registry to keep, no number to allocate, and
///     two builds agree without being told. <c>"progression"</c>, <c>"quests"</c>, <c>"wardrobe"</c>.
/// </remarks>
/// <param name="Value">The hash.</param>
public readonly record struct ProfileSectionId(uint Value) {
    /// <summary>Nothing.</summary>
    public static ProfileSectionId None => default;

    /// <summary>Whether it names a section.</summary>
    public bool IsSome => Value != 0;

    /// <summary>Hashes a name.</summary>
    /// <param name="name">What the section is called.</param>
    /// <returns>Its id.</returns>
    public static ProfileSectionId From(string? name) {
        if (string.IsNullOrEmpty(name)) {
            return None;
        }

        // FNV-1a over UTF-8, the same construction DefId uses, so a section id and a definition id
        // are computed the same way and neither needs a table.
        var hash = 2166136261u;

        foreach (var character in name) {
            hash ^= character;
            hash *= 16777619u;
        }

        return new(hash == 0 ? 1u : hash);
    }

    /// <inheritdoc />
    public override string ToString() => IsSome ? $"section {Value:x8}" : "no section";
}

/// <summary>A profile whose bytes are not one.</summary>
/// <param name="message">What is wrong with them.</param>
public sealed class ProfileFormatException(string message) : Exception(message);

/// <summary>
///     One character's durable game state, as a bag of named slices — <c>PlayerRecord.Profile</c>'s
///     contents.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 27 keeps <c>Profile</c> opaque on purpose</b> — <em>"the game's own state. Opaque
///         here, and never queried by this layer"</em> — because its schema is doc 28's and the
///         game's. This is the thing that gives that blob a shape without giving the persistence
///         layer one.
///     </para>
///     <para>
///         ⚠ <b>An unknown section is preserved, never dropped, and that is the whole reason the
///         container knows nothing about types.</b> Doc 27 § Upgrades fragments a population by
///         version on purpose: during a rollout, an old realm and a new realm both write the same
///         character. If the old one dropped the section the new one had added, a player who zoned
///         the wrong way would lose it — silently, and only some of the time. A map of id to bytes
///         cannot make that mistake, and the codecs that know types sit above it.
///     </para>
///     <para>
///         ⚠ <b>Sections are written in id order.</b> Two realms that wrote the same state must
///         produce the same bytes, or every checkpoint looks like a change and the row is rewritten
///         on a cadence for ever.
///     </para>
/// </remarks>
public sealed class PlayerProfile {
    /// <summary>The four bytes every profile starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "VXPF"u8;

    /// <summary>The format this reads and writes.</summary>
    public const int Version = 1;

    readonly SortedDictionary<uint, ReadOnlyMemory<byte>> sections = [];

    /// <summary>How many slices it holds.</summary>
    public int Count => sections.Count;

    /// <summary>How many times it has changed since it was read.</summary>
    /// <remarks>What a checkpoint watches, so a character nobody touched is not rewritten.</remarks>
    public uint Revision { get; private set; }

    /// <summary>Every section it holds, in id order.</summary>
    public IEnumerable<ProfileSectionId> Sections => sections.Keys.Select(id => new ProfileSectionId(id));

    /// <summary>Reads one back.</summary>
    /// <param name="bytes">What <see cref="Write" /> wrote, or nothing for a fresh character.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="ProfileFormatException">The bytes are not a profile, or are truncated.</exception>
    public static PlayerProfile Read(ReadOnlyMemory<byte> bytes) {
        var profile = new PlayerProfile();

        if (bytes.Length == 0) {
            return profile;
        }

        var span = bytes.Span;

        if (span.Length < 12 || !span[..4].SequenceEqual(Magic)) {
            throw new ProfileFormatException("These bytes do not start with a profile header.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);

        if (version != Version) {
            throw new ProfileFormatException(
                $"This profile is version {version} and this build reads {Version}. A profile is "
                + "migrated by the build that introduced the change, never guessed at."
            );
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        var offset = 12;

        for (var index = 0; index < count; index++) {
            if (offset + 8 > span.Length) {
                throw new ProfileFormatException($"Section {index} of {count} runs past the end of the profile.");
            }

            var id = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(span[(offset + 4)..]);

            offset += 8;

            if (length < 0 || offset + length > span.Length) {
                throw new ProfileFormatException($"Section {id:x8} says it is {length} bytes and it is not.");
            }

            profile.sections[id] = bytes.Slice(offset, length);
            offset += length;
        }

        return profile;
    }

    /// <summary>Writes it.</summary>
    /// <returns>The bytes, for <c>PlayerRecord.Profile</c>.</returns>
    public ReadOnlyMemory<byte> Write() {
        var size = 12 + sections.Sum(section => 8 + section.Value.Length);
        var bytes = new byte[size];
        var span = bytes.AsSpan();

        Magic.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], sections.Count);

        var offset = 12;

        // SortedDictionary, so this is id order without a sort here — see the remarks on the type.
        foreach (var (id, section) in sections) {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], id);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 4)..], section.Length);
            section.Span.CopyTo(span[(offset + 8)..]);
            offset += 8 + section.Length;
        }

        return bytes;
    }

    /// <summary>Reads a slice.</summary>
    /// <param name="id">Which.</param>
    /// <param name="section">Its bytes.</param>
    /// <returns>Whether it is there.</returns>
    public bool TryGet(ProfileSectionId id, out ReadOnlyMemory<byte> section) =>
        sections.TryGetValue(id.Value, out section);

    /// <summary>Writes a slice.</summary>
    /// <param name="id">Which.</param>
    /// <param name="section">Its bytes. Empty removes it.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    ///     ⚠ <b>Writing the same bytes back is not a change.</b> A section that re-serialised
    ///     identically would otherwise move the revision, and a checkpoint on a revision that always
    ///     moves is a checkpoint on a cadence.
    /// </remarks>
    public bool Set(ProfileSectionId id, ReadOnlyMemory<byte> section) {
        if (!id.IsSome) {
            return false;
        }

        if (section.Length == 0) {
            return Remove(id);
        }

        if (sections.TryGetValue(id.Value, out var already) && already.Span.SequenceEqual(section.Span)) {
            return false;
        }

        sections[id.Value] = section;
        Revision++;

        return true;
    }

    /// <summary>Forgets a slice.</summary>
    /// <param name="id">Which.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(ProfileSectionId id) {
        if (!sections.Remove(id.Value)) {
            return false;
        }

        Revision++;

        return true;
    }
}

/// <summary>One library's slice of a character's durable state.</summary>
/// <remarks>
///     <para>
///         <b>The seam that keeps <c>Vixen.Live.Gameplay</c> from being a bundle.</b> Doc 28's whole
///         shape is that every library is declinable, so a game that took quests and declined
///         exploration should not carry an exploration codec. A section registers itself; a game that
///         never registers one never has it.
///     </para>
///     <para>
///         ⚠ <b>Whatever it does not register is still preserved</b> — see
///         <see cref="PlayerProfile" />. A section nothing in this build knows about is bytes that
///         travel through untouched.
///     </para>
/// </remarks>
public interface IProfileSection {
    /// <summary>What names it.</summary>
    ProfileSectionId Id { get; }

    /// <summary>Writes what it holds.</summary>
    /// <returns>The bytes, or empty to hold nothing.</returns>
    ReadOnlyMemory<byte> Save();

    /// <summary>Reads what it held.</summary>
    /// <param name="bytes">What <see cref="Save" /> wrote, or empty for a fresh character.</param>
    void Load(ReadOnlyMemory<byte> bytes);
}

/// <summary>What a game registers its sections with, and what loads and saves them all.</summary>
public sealed class ProfileBinder {
    readonly Dictionary<uint, IProfileSection> sections = [];

    /// <summary>How many are registered.</summary>
    public int Count => sections.Count;

    /// <summary>Registers one.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The binder, so registrations chain.</returns>
    /// <exception cref="InvalidOperationException">Two sections claim one id.</exception>
    public ProfileBinder Add(IProfileSection section) {
        ArgumentNullException.ThrowIfNull(section);

        if (!section.Id.IsSome) {
            throw new InvalidOperationException($"{section.GetType().Name} has no section id.");
        }

        // ⚠ Refused rather than last-wins. Two sections on one id is one of them silently reading the
        // other's bytes, which presents as a character whose quests are full of somebody's fog.
        if (!sections.TryAdd(section.Id.Value, section)) {
            throw new InvalidOperationException(
                $"{section.GetType().Name} and {sections[section.Id.Value].GetType().Name} both claim "
                + $"{section.Id}. Section names are hashed, so two that collide have to be renamed."
            );
        }

        return this;
    }

    /// <summary>Loads every registered section out of a profile.</summary>
    /// <param name="profile">The profile.</param>
    public void Load(PlayerProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        foreach (var section in sections.Values) {
            section.Load(profile.TryGet(section.Id, out var bytes) ? bytes : default);
        }
    }

    /// <summary>Writes every registered section into a profile.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>Whether anything changed.</returns>
    public bool Save(PlayerProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        var changed = false;

        foreach (var section in sections.Values) {
            changed |= profile.Set(section.Id, section.Save());
        }

        return changed;
    }
}

/// <summary>What a name looks like when it is written down once.</summary>
public static class ProfileSections {
    /// <summary>Levels, talents, professions, reputation.</summary>
    public static ProfileSectionId Progression { get; } = ProfileSectionId.From("progression");

    /// <summary>The journal and its objective counters.</summary>
    public static ProfileSectionId Quests { get; } = ProfileSectionId.From("quests");

    /// <summary>Points of interest and the fog bitmap.</summary>
    public static ProfileSectionId Exploration { get; } = ProfileSectionId.From("exploration");

    /// <summary>Transmog overrides, hidden slots and the worn title — per character, not per account.</summary>
    public static ProfileSectionId Wardrobe { get; } = ProfileSectionId.From("wardrobe");

    /// <summary>Runs of bad luck, per loot table.</summary>
    public static ProfileSectionId Pity { get; } = ProfileSectionId.From("pity");
}
