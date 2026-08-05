// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Progression;

/// <summary>What an award of experience did.</summary>
/// <param name="Levels">How many levels were gained.</param>
/// <param name="Level">What level they are now.</param>
/// <param name="Wasted">Experience that fell off the end at the maximum level.</param>
public readonly record struct ExperienceGain(int Levels, int Level, int Wasted);

/// <summary>One character's progression: the durable half of doc 28 § Progression.</summary>
/// <remarks>
///     <para>
///         <b>An <see cref="IRequirementContext" />, which is the whole point.</b> Doc 28's own
///         example is <c>requires: [ Level >= 80, HasTag(Profession.Smithing), … ]</c>, and this is
///         what makes that resolve: it supplies <c>Level</c>, <c>GearScore</c> and every profession's
///         and faction's number, plus the tags for every rank and tier reached.
///     </para>
///     <para>
///         ⚠ <b>The same string names a track's tag and its number.</b> <c>Profession.Smithing</c> is
///         both "has the profession" and "the skill in it", so a designer learns one name per track
///         rather than two — and a requirement cannot ask about a value whose tag does not exist.
///     </para>
///     <para>
///         ⚠ <b>Gear score is a number a game sets, not one this computes.</b> Averaging an item
///         level needs the inventory, and a progression library that depended on containers would
///         make a game with no items unable to have levels. Doc 28 lists it under Progression, and
///         this is the honest reading of that.
///     </para>
/// </remarks>
public sealed class ProgressionState : IRequirementContext {
    readonly Dictionary<uint, int> professions = [];
    readonly Dictionary<uint, int> reputations = [];
    readonly Dictionary<uint, TalentAllocation> talents = [];
    readonly Dictionary<uint, float> values = [];
    readonly GameplayTagSet tags = new();

    /// <summary>Makes a fresh character at level one.</summary>
    /// <param name="library">Where the curves, trees and tracks come from.</param>
    public ProgressionState(ProgressionLibrary library) {
        ArgumentNullException.ThrowIfNull(library);

        Library = library;
        Level = 1;
        Refresh();
    }

    /// <summary>Where the curves, trees and tracks come from.</summary>
    public ProgressionLibrary Library { get; }

    /// <summary>What level they are.</summary>
    public int Level { get; private set; }

    /// <summary>How much experience they have towards the next level.</summary>
    public int Experience { get; private set; }

    /// <summary>How much they have ever earned.</summary>
    public long TotalExperience { get; private set; }

    /// <summary>Whether they are as high as the curve goes.</summary>
    public bool IsMaximumLevel => Level >= Library.Curve.MaximumLevel;

    /// <summary>How much they need for the next level, or zero at the maximum.</summary>
    public int ToNextLevel => IsMaximumLevel ? 0 : Math.Max(0, Library.Curve.CostOf(Level) - Experience);

    /// <summary>Which specialisation they chose, or <see cref="DefId.None" />.</summary>
    public DefId Specialisation { get; private set; }

    /// <summary>How good their equipment is. A number a game computes and sets.</summary>
    public float GearScore { get; set; }

    /// <summary>Every tag their progression grants: rank tags, tier tags, talents, the specialisation.</summary>
    public GameplayTagSet Tags => tags;

    /// <inheritdoc />
    GameplayTagSet? IRequirementContext.Tags => tags;

    /// <summary>How many talent points they have to spend. A game's rule; set it.</summary>
    /// <remarks>
    ///     Not derived from the level, because "one point per level after ten" and "a point every
    ///     other level" and "points from quests" are all games that ship, and a derived number would
    ///     be one of them chosen for everybody.
    /// </remarks>
    public int TalentPoints { get; set; }

    /// <summary>Awards experience, levelling up as many times as it buys.</summary>
    /// <param name="experience">How much. Negative is ignored.</param>
    /// <returns>What it did.</returns>
    /// <remarks>
    ///     ⚠ <b>Experience past the maximum level is reported as wasted rather than banked.</b>
    ///     Banking it would mean a level cap raised in a patch instantly levelling everybody who had
    ///     been grinding at the old one, which is a decision a game makes deliberately or not at all.
    /// </remarks>
    public ExperienceGain Award(int experience) {
        if (experience <= 0) {
            return new(0, Level, 0);
        }

        if (IsMaximumLevel) {
            return new(0, Level, experience);
        }

        TotalExperience += experience;
        Experience += experience;

        var levels = 0;

        while (!IsMaximumLevel && Experience >= Library.Curve.CostOf(Level)) {
            Experience -= Library.Curve.CostOf(Level);
            Level++;
            levels++;
        }

        var wasted = 0;

        if (IsMaximumLevel && Experience > 0) {
            wasted = Experience;
            Experience = 0;
        }

        if (levels > 0) {
            Refresh();
        }

        return new(levels, Level, wasted);
    }

    /// <summary>Sets the level outright. What a boost, a template character and a test do.</summary>
    /// <param name="level">The level.</param>
    public void SetLevel(int level) {
        Level = Math.Clamp(level, 1, Library.Curve.MaximumLevel);
        Experience = 0;
        Refresh();
    }

    /// <summary>Puts a level back exactly as it was, with no checks and nothing zeroed.</summary>
    /// <param name="level">Their level.</param>
    /// <param name="experience">How far into it they are.</param>
    /// <param name="total">How much they have earned in all.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <see cref="SetLevel" />, which zeroes the experience.</b> That is right for a
    ///         boost and wrong for a load: restoring a character with <see cref="SetLevel" /> throws
    ///         away everything they had earned towards the next one, every single login. This is the
    ///         seam <c>Guild.Seat</c> and <c>HousePlot.Assign</c> are, one library over.
    ///     </para>
    ///     <para>
    ///         The level is still clamped to the curve, because a curve whose maximum a patch lowered
    ///         would otherwise leave characters above a cap that every other rule reads as impossible.
    ///         The experience is not, because it is spent by the next <see cref="Award" /> anyway.
    ///     </para>
    /// </remarks>
    public void Seat(int level, int experience, long total) {
        Level = Math.Clamp(level, 1, Library.Curve.MaximumLevel);
        Experience = Math.Max(0, experience);
        TotalExperience = Math.Max(0, total);
        Refresh();
    }

    /// <summary>Puts a profession's skill back with no checks. The authority's, like <see cref="Seat" />.</summary>
    /// <param name="profession">Which one.</param>
    /// <param name="skill">How much.</param>
    /// <remarks>
    ///     ⚠ <b>Not clamped to the track, and not dropped when this build has no such profession.</b>
    ///     Clamping on load makes a patch that lowers a cap destroy the difference for everybody the
    ///     first time they log in — and reverting the patch does not bring it back. The next
    ///     <see cref="Train" /> clamps, which is late enough to be reversible. Dropping an unknown
    ///     profession is the same mistake the profile container refuses to make about a section it
    ///     does not recognise.
    /// </remarks>
    public void SeatSkill(DefId profession, int skill) {
        if (profession.IsSome) {
            professions[profession.Value] = skill;
            Refresh();
        }
    }

    /// <summary>Puts a faction standing back with no checks. The authority's, like <see cref="Seat" />.</summary>
    /// <param name="faction">Which one.</param>
    /// <param name="standing">How much.</param>
    public void SeatStanding(DefId faction, int standing) {
        if (faction.IsSome) {
            reputations[faction.Value] = standing;
            Refresh();
        }
    }

    /// <summary>Puts a talent allocation back with no validation at all.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="allocation">What they had taken.</param>
    /// <remarks>
    ///     ⚠ <b>Deliberately not <see cref="Allocate" />, and this is the one where the difference
    ///     hurts most.</b> Re-validating a saved build against today's tree means a patch that moved a
    ///     node's prerequisite silently wipes every character who had taken it — on login, with no
    ///     refund and no message. A game that wants those characters respecced does it on purpose, as
    ///     a migration that also gives the points back.
    /// </remarks>
    public void SeatTalents(DefId tree, TalentAllocation allocation) {
        ArgumentNullException.ThrowIfNull(allocation);

        if (tree.IsSome) {
            talents[tree.Value] = allocation.Copy();
            Refresh();
        }
    }

    /// <summary>Puts a specialisation back without asking whether they would still qualify.</summary>
    /// <param name="specialisation">Which one.</param>
    /// <remarks>⚠ <see cref="SeatTalents" />' reason: a requirement a patch tightened is not a reason to un-specialise somebody.</remarks>
    public void SeatSpecialisation(DefId specialisation) {
        Specialisation = specialisation;
        Refresh();
    }

    /// <summary>Every profession they have any skill in, in id order.</summary>
    /// <remarks>Ordered, so two realms holding the same character write the same bytes.</remarks>
    public IEnumerable<KeyValuePair<DefId, int>> Skills =>
        professions.OrderBy(pair => pair.Key).Select(pair => new KeyValuePair<DefId, int>(new(pair.Key), pair.Value));

    /// <summary>Every faction they have any standing with, in id order.</summary>
    public IEnumerable<KeyValuePair<DefId, int>> Standings =>
        reputations.OrderBy(pair => pair.Key).Select(pair => new KeyValuePair<DefId, int>(new(pair.Key), pair.Value));

    /// <summary>Every tree they have taken anything in, in id order.</summary>
    public IEnumerable<KeyValuePair<DefId, TalentAllocation>> Allocations =>
        talents.Where(pair => pair.Value.Count > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => new KeyValuePair<DefId, TalentAllocation>(new(pair.Key), pair.Value));

    /// <summary>How much skill they have in a profession.</summary>
    /// <param name="profession">Which one.</param>
    /// <returns>The skill, or zero.</returns>
    public int SkillIn(DefId profession) => professions.GetValueOrDefault(profession.Value);

    /// <summary>Adds skill in a profession, clamped to its maximum.</summary>
    /// <param name="profession">Which one.</param>
    /// <param name="amount">How much.</param>
    /// <returns>The new skill, or zero when this build has no such profession.</returns>
    public int Train(DefId profession, int amount) {
        if (Library.FindProfession(profession) is not { } track) {
            return 0;
        }

        var skill = Math.Clamp(SkillIn(profession) + amount, track.Minimum, track.Maximum);
        professions[profession.Value] = skill;
        Refresh();

        return skill;
    }

    /// <summary>What standing they have with a faction.</summary>
    /// <param name="faction">Which one.</param>
    /// <returns>The standing, or zero.</returns>
    public int StandingWith(DefId faction) => reputations.GetValueOrDefault(faction.Value);

    /// <summary>Changes standing with a faction, clamped to its bounds.</summary>
    /// <param name="faction">Which one.</param>
    /// <param name="amount">How much. Negative angers them.</param>
    /// <returns>The new standing, or zero when this build has no such faction.</returns>
    public int Earn(DefId faction, int amount) {
        if (Library.FindReputation(faction) is not { } track) {
            return 0;
        }

        var standing = Math.Clamp(StandingWith(faction) + amount, track.Minimum, track.Maximum);
        reputations[faction.Value] = standing;
        Refresh();

        return standing;
    }

    /// <summary>Their allocation in a talent tree.</summary>
    /// <param name="tree">Which tree.</param>
    /// <returns>It, empty when they have taken nothing.</returns>
    public TalentAllocation AllocationIn(DefId tree) {
        if (!talents.TryGetValue(tree.Value, out var allocation)) {
            allocation = new();
            talents.Add(tree.Value, allocation);
        }

        return allocation;
    }

    /// <summary>Replaces an allocation, if it is legal.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="allocation">What they say they have taken.</param>
    /// <returns>The verdict. Nothing changes unless it is legal.</returns>
    /// <remarks>
    ///     <b>The server-side half of doc 28's "a client-built talent tree is a client-chosen power
    ///     level".</b> The whole allocation is validated from scratch against the points they
    ///     actually have, and rejected as a unit.
    /// </remarks>
    public TalentVerdict Allocate(DefId tree, TalentAllocation allocation) {
        ArgumentNullException.ThrowIfNull(allocation);

        if (Library.FindTree(tree) is not { } compiled) {
            return new(TalentRejection.UnknownNode, string.Empty, $"{tree} is not a talent tree this build knows.");
        }

        var verdict = compiled.Validate(allocation, TalentPoints);

        if (!verdict.IsLegal) {
            return verdict;
        }

        talents[tree.Value] = allocation.Copy();
        Refresh();

        return TalentVerdict.Legal;
    }

    /// <summary>Chooses a specialisation, if its requirements are met.</summary>
    /// <param name="specialisation">Which one.</param>
    /// <returns>Whether it was chosen.</returns>
    /// <remarks>
    ///     ⚠ <b>Evaluated against this state, which is already the requirement context.</b> So
    ///     "level 30 and honoured with Ebonhawke" is checked by the same code a vendor uses, and a
    ///     specialisation cannot ask a question a vendor could not.
    /// </remarks>
    public bool Specialise(DefId specialisation) {
        if (Library.FindSpecialisation(specialisation) is not { } compiled) {
            return false;
        }

        if (!compiled.Requirements.IsMetBy(this)) {
            return false;
        }

        Specialisation = specialisation;
        Refresh();

        return true;
    }

    /// <summary>Everything their progression does to their stats.</summary>
    /// <param name="source">What the modifiers are removable by.</param>
    /// <param name="into">Where to put them.</param>
    /// <returns>How many were produced.</returns>
    public int Modifiers(ModifierSource source, ICollection<Modifier> into) {
        ArgumentNullException.ThrowIfNull(into);

        var produced = 0;

        if (Specialisation.IsSome && Library.FindSpecialisation(Specialisation) is { } specialisation) {
            foreach (ref readonly var modifier in specialisation.Modifiers) {
                into.Add(modifier with { Source = source });
                produced++;
            }
        }

        foreach (var tree in Library.Trees) {
            if (talents.TryGetValue(tree.Id.Value, out var allocation)) {
                produced += tree.Modifiers(allocation, source, into);
            }
        }

        return produced;
    }

    /// <inheritdoc />
    public bool TryGetValue(AttributeId subject, out float value) {
        if (subject == LevelValue) {
            value = Level;

            return true;
        }

        if (subject == GearScoreValue) {
            value = GearScore;

            return true;
        }

        return values.TryGetValue(subject.Value, out value);
    }

    /// <summary>What a requirement calls the character's level.</summary>
    public static AttributeId LevelValue { get; } = AttributeId.From("Level");

    /// <summary>What a requirement calls how good their equipment is.</summary>
    public static AttributeId GearScoreValue { get; } = AttributeId.From("GearScore");

    /// <summary>
    ///     Recomputes the tags and the named values. Called by everything that changes the state, so a
    ///     caller never has to remember to.
    /// </summary>
    void Refresh() {
        tags.Clear();
        values.Clear();

        foreach (var track in Library.Professions) {
            var skill = SkillIn(track.Id);

            values[track.Value.Value] = skill;

            if (skill <= 0) {
                continue;
            }

            tags.Add(track.Tag);

            var rank = track.RankAt(skill);

            if (rank >= 0) {
                tags.Add(track.Ranks[rank].Tag);
            }
        }

        foreach (var track in Library.Reputations) {
            var standing = StandingWith(track.Id);

            values[track.Value.Value] = standing;
            tags.Add(track.Tag);

            var rank = track.RankAt(standing);

            if (rank >= 0) {
                tags.Add(track.Ranks[rank].Tag);
            }
        }

        foreach (var tree in Library.Trees) {
            if (talents.TryGetValue(tree.Id.Value, out var allocation)) {
                var granted = new List<GameplayTag>();
                tree.Tags(allocation, granted);

                foreach (var tag in granted) {
                    tags.Add(tag);
                }
            }
        }

        if (Specialisation.IsSome && Library.FindSpecialisation(Specialisation) is { Tag.IsSome: true } chosen) {
            tags.Add(chosen.Tag);
        }
    }
}
