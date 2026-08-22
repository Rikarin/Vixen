// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Reactive;

namespace Vixen.Samples.Mmo.Ui;

/// <summary>What the HUD is looking at. Signals all the way down, so nothing polls.</summary>
/// <remarks>
///     <para>
///         <b>Every panel binds to this and nothing binds to the network.</b> A component that read
///         a replicated struct directly would be a component that cannot be tested without a session
///         — and, worse, one whose refresh policy is "whenever a packet arrives" rather than
///         "whenever the value changed".
///     </para>
///     <para>
///         ⚠ <b>The models hold signals; the components hold none.</b> That is
///         <c>Vixen.Editor.Ui</c>'s <c>TaskCenter</c> lesson: when the model is signal-backed the
///         view needs no subscription, no revision counter and no handler that can outlive the panel.
///         <c>Build</c> runs once and an effect assigns exactly the property it was written for.
///     </para>
/// </remarks>
public sealed class HudModel {
    /// <summary>The player's own frame.</summary>
    public UnitModel Player { get; } = new();

    /// <summary>What they have selected, or nothing.</summary>
    public Signal<UnitModel?> Target { get; } = new(null);

    /// <summary>Whether anything is selected.</summary>
    public bool HasTarget => Target.Value is not null;

    /// <summary>The target, or an empty frame's worth of nobody.</summary>
    /// <remarks>
    ///     ⚠ <b>Here rather than as a pattern in the markup, and that is a fact about the emitter.</b>
    ///     A <c>@if</c> becomes an effect and its body becomes a separate build, so a variable
    ///     declared by <c>is { } target</c> in the test is not in scope inside the branch — the C#
    ///     compiler says so at the right character of the <c>.vxml</c>, which is the bargain VXML
    ///     makes by emitting expressions under a <c>#line</c> instead of typechecking them itself.
    /// </remarks>
    public UnitModel TargetOrNobody => Target.Value ?? Nobody;

    static UnitModel Nobody { get; } = new();

    /// <summary>The bar along the bottom.</summary>
    public ActionBarModel Bar { get; } = new();

    /// <summary>The quests down the right-hand side.</summary>
    public QuestTrackerModel Quests { get; } = new();

    /// <summary>The bags.</summary>
    public BagModel Bags { get; } = new();

    /// <summary>The vendor window, when one is open.</summary>
    public VendorModel Vendor { get; } = new();

    /// <summary>The chat log.</summary>
    public ChatModel Chat { get; } = new();

    /// <summary>The loot roll, when one is up.</summary>
    public LootRollModel Loot { get; } = new();
}

/// <summary>A unit frame's worth of somebody: the player, their target, a party member.</summary>
public sealed class UnitModel {
    /// <summary>What to call them.</summary>
    public Signal<string> Name { get; } = new("");

    /// <summary>Their level.</summary>
    public Signal<int> Level { get; } = new(1);

    /// <summary>What they have.</summary>
    public Signal<int> Health { get; } = new(1);

    /// <summary>What they could have. Never zero — see <see cref="HealthFraction" />.</summary>
    public Signal<int> MaximumHealth { get; } = new(1);

    /// <summary>Rage, mana or focus.</summary>
    public Signal<int> Resource { get; } = new(0);

    /// <summary>The same, at full.</summary>
    public Signal<int> MaximumResource { get; } = new(1);

    /// <summary>Which colour the resource bar draws in — a whole class name, never a fragment.</summary>
    /// <remarks>
    ///     ⚠ <b>A text colour, and that is not a mistake.</b> <c>ProgressBar</c>'s fill falls back to
    ///     the element's foreground when no <c>--fill-color</c> is set, so <c>text-mana</c> is how a
    ///     utility class colours a bar — and it means the four resource colours ride the same family
    ///     as everything else rather than needing a custom property per bar.
    /// </remarks>
    public Signal<string> ResourceClass { get; } = new("text-mana");

    /// <summary>Whether they are worth being careful about.</summary>
    public Signal<bool> IsElite { get; } = new(false);

    /// <summary>What they are casting, or nothing.</summary>
    public Signal<string> Casting { get; } = new("");

    /// <summary>How far through the cast, nought to one.</summary>
    public Signal<float> CastProgress { get; } = new(0f);

    /// <summary>How full the health bar is, nought to one.</summary>
    /// <remarks>
    ///     ⚠ <b>Guarded against a zero maximum rather than trusting the wire.</b> A replicated struct
    ///     arrives before the one that would have filled it in at least once per session, and
    ///     <c>Health / 0</c> is a bar whose width is <c>NaN</c> — which lays out as a panel the width
    ///     of the screen exactly once, at the moment a player targets something.
    /// </remarks>
    public float HealthFraction => Fraction(Health.Value, MaximumHealth.Value);

    /// <summary>How full the resource bar is.</summary>
    public float ResourceFraction => Fraction(Resource.Value, MaximumResource.Value);

    /// <summary>Whether a cast bar should be showing.</summary>
    public bool IsCasting => Casting.Value.Length > 0;

    static float Fraction(int value, int maximum) =>
        maximum <= 0 ? 0f : Math.Clamp(value / (float)maximum, 0f, 1f);
}

/// <summary>One button on the action bar.</summary>
public sealed class ActionSlot {
    /// <summary>What the key is called, for the corner of the button.</summary>
    public Signal<string> Binding { get; } = new("");

    /// <summary>What the ability is called.</summary>
    public Signal<string> Name { get; } = new("");

    /// <summary>What it costs.</summary>
    public Signal<int> Cost { get; } = new(0);

    /// <summary>How much of the cooldown is left, nought to one. Nought is ready.</summary>
    public Signal<float> Cooldown { get; } = new(0f);

    /// <summary>Whether the player can afford it and has learned it.</summary>
    public Signal<bool> Usable { get; } = new(true);

    /// <summary>Why not, when they cannot.</summary>
    public Signal<string> Refusal { get; } = new("");

    /// <summary>Whether it is off cooldown.</summary>
    public bool IsReady => Cooldown.Value <= 0f;
}

/// <summary>The bar along the bottom.</summary>
public sealed class ActionBarModel {
    /// <summary>The slots, in order.</summary>
    public CollectionSignal<ActionSlot> Slots { get; } = new();
}

/// <summary>One objective inside a tracked quest.</summary>
public sealed class ObjectiveEntry {
    /// <summary>What to do.</summary>
    public Signal<string> Text { get; } = new("");

    /// <summary>How many are done.</summary>
    public Signal<int> Have { get; } = new(0);

    /// <summary>How many are needed.</summary>
    public Signal<int> Need { get; } = new(1);

    /// <summary>Whether it is finished.</summary>
    public bool IsDone => Have.Value >= Need.Value;
}

/// <summary>A quest as the tracker shows it.</summary>
public sealed class QuestEntry {
    /// <summary>Its name.</summary>
    public Signal<string> Title { get; } = new("");

    /// <summary>Which stage the player is on.</summary>
    public Signal<string> Stage { get; } = new("");

    /// <summary>What is left to do.</summary>
    public CollectionSignal<ObjectiveEntry> Objectives { get; } = new();

    /// <summary>Whether every objective is done and it can be handed in.</summary>
    public Signal<bool> IsReady { get; } = new(false);
}

/// <summary>The quests down the right-hand side.</summary>
public sealed class QuestTrackerModel {
    /// <summary>What is being tracked.</summary>
    public CollectionSignal<QuestEntry> Tracked { get; } = new();
}

/// <summary>One square of a bag.</summary>
public sealed class BagSlot {
    /// <summary>What is in it, or nothing.</summary>
    public Signal<string> Name { get; } = new("");

    /// <summary>How many.</summary>
    public Signal<int> Count { get; } = new(0);

    /// <summary>The whole border class for the rarity — <c>border-storied</c>, not <c>storied</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Whole, because the scanner cannot see a name that is assembled.</b> These five are
    ///     <c>VixenStyleSafelist</c> items in <c>Mmo.Ui.csproj</c> for the same reason, and between
    ///     the two of them the rule is: never put a fragment of a class name in a signal.
    /// </remarks>
    public Signal<string> RarityClass { get; } = new("border-common");

    /// <summary>Whether there is anything in it.</summary>
    public bool IsEmpty => Name.Value.Length == 0;
}

/// <summary>The bags.</summary>
public sealed class BagModel {
    /// <summary>Every square, in order.</summary>
    public CollectionSignal<BagSlot> Slots { get; } = new();

    /// <summary>How many are empty.</summary>
    public Signal<int> Free { get; } = new(0);
}

/// <summary>One line of a vendor's stock.</summary>
public sealed class VendorRow {
    /// <summary>What is for sale.</summary>
    public Signal<string> Name { get; } = new("");

    /// <summary>What it costs.</summary>
    public Signal<long> Price { get; } = new(0);

    /// <summary>What it costs it in.</summary>
    public Signal<string> Currency { get; } = new("");

    /// <summary>Its rarity's text class.</summary>
    public Signal<string> RarityClass { get; } = new("text-common");

    /// <summary>Whether the player has enough.</summary>
    public Signal<bool> Affordable { get; } = new(true);

    /// <summary>Whether a requirement stands in the way.</summary>
    public Signal<bool> Locked { get; } = new(false);

    /// <summary>What requirement, in a sentence.</summary>
    public Signal<string> LockReason { get; } = new("");

    /// <summary>Whether the buy button does anything.</summary>
    /// <remarks>
    ///     ⚠ <b>Locked and unaffordable are shown differently and refused the same.</b> A player who
    ///     cannot pay is told a number and a player who is not honoured enough is told a
    ///     <em>reason</em> — greying both out identically is the interface deciding that the two
    ///     failures are the same, and one of them is a thing the player can do something about today.
    /// </remarks>
    public bool CanBuy => Affordable.Value && !Locked.Value;
}

/// <summary>A vendor window.</summary>
public sealed class VendorModel {
    /// <summary>Whether one is open.</summary>
    public Signal<bool> Open { get; } = new(false);

    /// <summary>Whose shop.</summary>
    public Signal<string> Title { get; } = new("");

    /// <summary>What is on the shelves.</summary>
    public CollectionSignal<VendorRow> Stock { get; } = new();

    /// <summary>What the player has to spend.</summary>
    public Signal<long> Purse { get; } = new(0);
}

/// <summary>One line of chat.</summary>
public sealed class ChatEntry {
    /// <summary>Which channel it came in on.</summary>
    public string Channel { get; init; } = "";

    /// <summary>Who said it.</summary>
    public string Speaker { get; init; } = "";

    /// <summary>What they said.</summary>
    public string Text { get; init; } = "";

    /// <summary>The whole text class for the channel's colour.</summary>
    public string ChannelClass { get; init; } = "text-ink-100";
}

/// <summary>The chat log.</summary>
public sealed class ChatModel {
    /// <summary>What has been said, oldest first.</summary>
    public CollectionSignal<ChatEntry> Lines { get; } = new();

    /// <summary>How many lines are kept.</summary>
    /// <remarks>
    ///     ⚠ <b>A chat log is the other unbounded set in a client.</b> Doc 27's realm keeps an
    ///     idempotency-key set that needs a horizon; a client keeps every line anybody has ever said
    ///     near it. Same shape, same answer, much smaller stakes — dropping the oldest line loses a
    ///     joke rather than an item.
    /// </remarks>
    public int Capacity { get; init; } = 128;

    /// <summary>What is typed and not yet sent.</summary>
    public Signal<string> Draft { get; } = new("");

    /// <summary>Adds a line, dropping the oldest if the log is full.</summary>
    /// <param name="entry">What was said.</param>
    public void Say(ChatEntry entry) {
        Lines.Add(entry);

        while (Lines.Count > Capacity) {
            Lines.RemoveAt(0);
        }
    }
}

/// <summary>A loot roll, and the three things a player can do about it.</summary>
public sealed class LootRollModel {
    /// <summary>What a player chose.</summary>
    public enum Choice {
        /// <summary>Nothing yet.</summary>
        Undecided,

        /// <summary>They want it.</summary>
        Need,

        /// <summary>They will take it if nobody needs it.</summary>
        Greed,

        /// <summary>They are out.</summary>
        Pass
    }

    /// <summary>Whether a roll is up.</summary>
    public Signal<bool> Open { get; } = new(false);

    /// <summary>What dropped.</summary>
    public Signal<string> Item { get; } = new("");

    /// <summary>Its rarity's text class.</summary>
    public Signal<string> RarityClass { get; } = new("text-common");

    /// <summary>How long is left.</summary>
    public Signal<int> Seconds { get; } = new(0);

    /// <summary>What this player has said.</summary>
    public Signal<Choice> Chosen { get; } = new(Choice.Undecided);

    /// <summary>Records a choice and closes the window.</summary>
    /// <param name="choice">Which.</param>
    /// <remarks>
    ///     ⚠ <b>Closing is local and the roll is not.</b> The window goes away because this player
    ///     has answered; who wins is the realm's, arrives later, and is a chat line. An interface
    ///     that awarded the item here would be one that disagrees with the server whenever two
    ///     people need at once.
    /// </remarks>
    public void Decide(Choice choice) {
        Chosen.Value = choice;
        Open.Value = false;
    }
}
