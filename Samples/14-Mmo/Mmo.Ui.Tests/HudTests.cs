// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Samples.Mmo.Ui.Tests;

/// <summary>The HUD, mounted into a real document and driven by frames.</summary>
/// <remarks>
///     <para>
///         <b>No GPU, no window, no sleep.</b> <c>Vixen.Ui.Testing</c> counts waiting in frames
///         because the test owns the loop, which is what makes an interface suite something CI runs
///         on every push instead of nightly.
///     </para>
///     <para>
///         ⚠ <b>Text content is a child element, not the parent's own text.</b> The emitter writes
///         <c>ctx.Text(parent, …)</c> for every run of content, and that makes a <c>&lt;text&gt;</c>
///         element under it — so an assertion has to be <c>frame-name text</c> and not
///         <c>frame-name</c>. It is also why colouring text works from the parent at all: <c>color</c>
///         inherits, and the child is what draws.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is about the document, not about the model.</b> Asserting that
///         a signal holds what it was assigned is asserting that <c>=</c> works; the question worth
///         a test is whether the *element* followed it, because that is the half a component can get
///         wrong.
///     </para>
/// </remarks>
public sealed class HudTests : IDisposable {
    readonly UiTest ui = UiTest.Create();
    readonly HudModel model = new();
    readonly Hud hud = new();

    public HudTests() {
        hud.Model = model;

        ui.Load(MmoStyles.Css);
        BuildContext.BuildInto(hud, ui.Document, ui.Document.Root);
    }

    public void Dispose() => ui.Dispose();

    [Fact]
    public void TheHudDrawsThePlayer() {
        model.Player.Name.Value = "Bruna";
        model.Player.Level.Value = 42;

        ui.Frame();

        ui.Get("frame-name").ShouldExist();
        ui.Get("frame-name text").ShouldHaveText("Bruna");
        ui.Get("frame-level text").ShouldHaveText("42");
    }

    /// <summary>
    ///     ⚠ <b>The property the whole composition model rests on.</b> <c>Build</c> ran once, in the
    ///     constructor. Nothing re-rendered, nothing diffed a tree — an effect assigned exactly the
    ///     property it was written for, and it did so because a signal changed.
    /// </summary>
    [Fact]
    public void ChangingASignalMovesTheElementWithoutRebuilding() {
        model.Player.Name.Value = "Bruna";
        ui.Frame();

        model.Player.Name.Value = "Halvard";
        ui.Frame();

        ui.Get("frame-name text").ShouldHaveText("Halvard");
    }

    [Fact]
    public void ATargetFrameAppearsWhenSomethingIsSelectedAndGoesAgain() {
        model.Player.Name.Value = "Bruna";
        ui.Frame();

        ui.Get("frame-name").ShouldHaveCount(1);

        var boar = new UnitModel();

        boar.Name.Value = "Tuskfather";
        boar.IsElite.Value = true;
        model.Target.Value = boar;
        ui.Frame();

        ui.Get("frame-name").ShouldHaveCount(2);
        ui.Get("elite-mark").ShouldExist();

        model.Target.Value = null;
        ui.Frame();

        ui.Get("frame-name").ShouldHaveCount(1);
    }

    /// <summary>
    ///     ⚠ <b>A cast bar exists only while something is being cast.</b> One that is always there
    ///     and usually empty is one players stop looking at — and it is two more rectangles a frame
    ///     for every nameplate in a world that can have forty of them.
    /// </summary>
    [Fact]
    public void TheCastBarIsAbsentUntilSomethingIsBeingCast() {
        ui.Frame();

        Assert.Equal(0, ui.Get("cast-row").Count);

        model.Player.Casting.Value = "Emberlance";
        model.Player.CastProgress.Value = 0.4f;
        ui.Frame();

        ui.Get("cast-name text").ShouldHaveText("Emberlance");
    }

    /// <summary>
    ///     ⚠ <b>A replicated struct arrives before the one that would have filled it in.</b> Health
    ///     over a zero maximum is <c>NaN</c>, and a bar whose fraction is <c>NaN</c> lays out as a
    ///     panel the width of the screen — exactly once, at the moment a player targets something.
    /// </summary>
    [Fact]
    public void AVitalsStructThatHasNotArrivedYetDrawsAnEmptyBarRatherThanANaN() {
        var fresh = new UnitModel();

        fresh.Health.Value = 900;
        fresh.MaximumHealth.Value = 0;

        Assert.Equal(0f, fresh.HealthFraction);
        Assert.False(float.IsNaN(fresh.HealthFraction));
    }

    [Fact]
    public void ASlotOnCooldownIsMarkedAndOneThatCannotBeUsedIsMarkedDifferently() {
        var ready = new ActionSlot();
        var cooling = new ActionSlot();
        var refused = new ActionSlot();

        ready.Name.Value = "Cleave";
        cooling.Name.Value = "Rally";
        cooling.Cooldown.Value = 0.7f;
        refused.Name.Value = "Warcry";
        refused.Usable.Value = false;

        model.Bar.Slots.Add(ready);
        model.Bar.Slots.Add(cooling);
        model.Bar.Slots.Add(refused);
        ui.Frame();

        ui.Get("slot-cell").ShouldHaveCount(3);
        ui.Get(".on-cooldown").ShouldHaveCount(1);
        ui.Get(".unusable").ShouldHaveCount(1);
    }

    /// <summary>
    ///     ⚠ <b>Two refusals, told two different ways.</b> "You cannot afford it" is a number a
    ///     player can change today; "you are not Honoured enough" is a reason. Greying both out
    ///     identically is the interface deciding they are the same failure.
    /// </summary>
    [Fact]
    public void AVendorShowsAPriceForOneRefusalAndAReasonForTheOther() {
        var poor = new VendorRow();
        var locked = new VendorRow();

        poor.Name.Value = "Barrowbane Hauberk";
        poor.Price.Value = 4_000;
        poor.Currency.Value = "g";
        poor.Affordable.Value = false;

        locked.Name.Value = "Company Cuirass";
        locked.Locked.Value = true;
        locked.LockReason.Value = "Honoured with the Ashfen Company";

        model.Vendor.Open.Value = true;
        model.Vendor.Title.Value = "Ashfen Quartermaster";
        model.Vendor.Stock.Add(poor);
        model.Vendor.Stock.Add(locked);
        ui.Frame();

        ui.Get("vendor-title text").ShouldHaveText("Ashfen Quartermaster");
        ui.Get("vendor-price").ShouldHaveCount(1);
        ui.Get("vendor-lock").ShouldHaveCount(1);
        ui.Get("vendor-lock text").ShouldContainText("Honoured");

        Assert.False(poor.CanBuy);
        Assert.False(locked.CanBuy);
    }

    /// <summary>
    ///     ⚠ <b>The window closes locally and the item is awarded elsewhere.</b> A client that
    ///     decided the winner would disagree with the realm every time two people need at once,
    ///     which is most of the times a roll matters.
    /// </summary>
    [Fact]
    public void ALootRollClosesOnTheChoiceAndAwardsNothing() {
        model.Loot.Open.Value = true;
        model.Loot.Item.Value = "Hollowmoor Signet";
        model.Loot.RarityClass.Value = "text-storied";
        model.Loot.Seconds.Value = 30;
        ui.Frame();

        ui.Get("roll-item text").ShouldHaveText("Hollowmoor Signet");

        model.Loot.Decide(LootRollModel.Choice.Need);
        ui.Frame();

        Assert.Equal(LootRollModel.Choice.Need, model.Loot.Chosen.Value);
        Assert.False(model.Loot.Open.Value);
        Assert.Equal(0, ui.Get("roll-item").Count);
    }

    [Fact]
    public void AFinishedObjectiveIsColouredRatherThanRemoved() {
        var quest = new QuestEntry();
        var kill = new ObjectiveEntry();
        var gather = new ObjectiveEntry();

        quest.Title.Value = "The Broken Fence of Greenmarch";
        quest.Stage.Value = "Thin out the boars";
        kill.Text.Value = "Boars felled";
        kill.Have.Value = 5;
        kill.Need.Value = 5;
        gather.Text.Value = "Gathered";
        gather.Have.Value = 1;
        gather.Need.Value = 3;

        quest.Objectives.Add(kill);
        quest.Objectives.Add(gather);
        model.Quests.Tracked.Add(quest);
        ui.Frame();

        // Both are still on screen, which is the assertion: the done one did not vanish under the
        // cursor at the moment the player was reading it.
        ui.Get("objective-row").ShouldHaveCount(2);
        ui.Get(".text-done").ShouldHaveCount(2);
    }

    [Fact]
    public void AnEmptyTrackerSaysSoRatherThanBeingAbsent() {
        ui.Frame();

        ui.Get("tracker-empty").ShouldExist();
        ui.Get("tracker-empty text").ShouldContainText("Nothing tracked");
    }

    [Fact]
    public void ABagSlotWithOneOfSomethingShowsNoNumber() {
        var single = new BagSlot();
        var stack = new BagSlot();

        single.Name.Value = "Greenmarch Ore";
        single.Count.Value = 1;
        stack.Name.Value = "Ashfen Herb";
        stack.Count.Value = 12;
        stack.RarityClass.Value = "border-fine";

        model.Bags.Slots.Add(single);
        model.Bags.Slots.Add(stack);
        model.Bags.Free.Value = 14;
        ui.Frame();

        ui.Get("bag-slot").ShouldHaveCount(2);
        // ⚠ `First`, because `@Bags.Free.Value free` is *two* text children — one per run of
        // content — and a selector that matches both is asked about the first.
        ui.Get("bag-free text").First().ShouldHaveText("14");
        // The single shows nothing and the stack shows its count, which is the whole rule.
        ui.Get("slot-count text").ShouldHaveCount(2);
        ui.Get("slot-count text").First().ShouldHaveText(string.Empty);
        ui.Get("slot-count text").Last().ShouldHaveText("12");
    }

    /// <summary>
    ///     ⚠ <b>A chat log is the other unbounded set in a client.</b> Same shape as the realm's
    ///     idempotency-key horizon and much smaller stakes: dropping the oldest line loses a joke
    ///     rather than an item.
    /// </summary>
    [Fact]
    public void TheChatLogIsBounded() {
        var chat = new ChatModel { Capacity = 4 };

        for (var index = 0; index < 20; index++) {
            chat.Say(new() { Channel = "Say", Speaker = "Bruna", Text = $"line {index}" });
        }

        Assert.Equal(4, chat.Lines.Count);
        Assert.Equal("line 19", chat.Lines[^1].Text);
    }
}
