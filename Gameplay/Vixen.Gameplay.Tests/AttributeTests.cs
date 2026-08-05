// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Tests;

public class AttributeLayoutTests {
    [Fact]
    public void ADuplicateStatIsRefused() {
        var builder = new AttributeLayoutBuilder().Add("Power");

        var error = Assert.Throws<InvalidOperationException>(() => builder.Add("Power"));

        Assert.Contains("twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoundPairThatNoValueSatisfiesIsRefused() {
        var builder = new AttributeLayoutBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.Add("Health", minimum: 10f, maximum: 5f));
    }

    [Fact]
    public void AStatTheLayoutDoesNotDeclareHasNoSlot() {
        var layout = new AttributeLayoutBuilder().Add("Power").Build();

        Assert.Equal(-1, layout.SlotOf(AttributeId.From("Precision")));
        Assert.False(layout.Declares(AttributeId.From("Precision")));
        Assert.True(layout.Declares(AttributeId.From("Power")));
    }
}

public class AttributeSetTests {
    static readonly AttributeId Power = AttributeId.From("Power");
    static readonly AttributeId Health = AttributeId.From("Health");
    static readonly AttributeId Crit = AttributeId.From("CritChance");

    static AttributeLayout Layout() =>
        new AttributeLayoutBuilder()
            .Add("Power", 100f)
            .Add("Health", 1000f, 0f)
            .Add("CritChance", 0.05f, 0f, 1f)
            .Build();

    static AttributeSet Set() => new(Layout());

    [Fact]
    public void AFreshSetHoldsTheDeclaredDefaults() {
        var set = Set();

        Assert.Equal(100f, set.ValueOf(Power));
        Assert.Equal(1000f, set.ValueOf(Health));
        Assert.Equal(0.05f, set.ValueOf(Crit));
    }

    [Fact]
    public void TheEvaluationOrderIsBaseThenFlatThenAdditiveThenMultiplicative() {
        var set = Set();

        set.Add(new(Power, ModifierOp.Add, 20f, ModifierSource.From(new(1), 1)));
        set.Add(new(Power, ModifierOp.AddPercent, 0.5f, ModifierSource.From(new(2), 1)));
        set.Add(new(Power, ModifierOp.MultiplyPercent, 0.1f, ModifierSource.From(new(3), 1)));

        // (100 + 20) × 1.5 × 1.1
        Assert.Equal(198f, set.ValueOf(Power), 4);
    }

    [Fact]
    public void TwoAdditivePercentagesSumAndTwoMultiplicativeOnesCompose() {
        var additive = Set();
        additive.Add(new(Power, ModifierOp.AddPercent, 0.5f, ModifierSource.From(new(1), 1)));
        additive.Add(new(Power, ModifierOp.AddPercent, 0.5f, ModifierSource.From(new(2), 1)));

        var multiplicative = Set();
        multiplicative.Add(new(Power, ModifierOp.MultiplyPercent, 0.5f, ModifierSource.From(new(1), 1)));
        multiplicative.Add(new(Power, ModifierOp.MultiplyPercent, 0.5f, ModifierSource.From(new(2), 1)));

        Assert.Equal(200f, additive.ValueOf(Power), 4);
        Assert.Equal(225f, multiplicative.ValueOf(Power), 4);
    }

    [Fact]
    public void RemoveBySourceIsExactAndLeavesNoResidue() {
        var set = Set();
        var buff = ModifierSource.From(new(7), 1);

        var before = set.ValueOf(Power);

        for (var cycle = 0; cycle < 1000; cycle++) {
            set.Add(new(Power, ModifierOp.AddPercent, 0.15f, buff));
            set.Add(new(Power, ModifierOp.Add, 37.3f, buff));
            _ = set.ValueOf(Power);
            Assert.Equal(2, set.RemoveBySource(buff));
            Assert.Equal(before, set.ValueOf(Power));
        }

        Assert.Empty(set.Modifiers);
    }

    [Fact]
    public void TheResultDoesNotDependOnTheOrderTheModifiersArrivedIn() {
        var modifiers = new Modifier[] {
            new(Power, ModifierOp.Add, 13.7f, ModifierSource.From(new(1), 1)),
            new(Power, ModifierOp.AddPercent, 0.17f, ModifierSource.From(new(2), 1)),
            new(Power, ModifierOp.Add, 5.3f, ModifierSource.From(new(3), 1)),
            new(Power, ModifierOp.MultiplyPercent, 0.23f, ModifierSource.From(new(4), 1)),
            new(Power, ModifierOp.AddPercent, 0.31f, ModifierSource.From(new(5), 1)),
            new(Power, ModifierOp.MultiplyPercent, 0.07f, ModifierSource.From(new(6), 1))
        };

        var random = new GameplayRandom(981);
        var expected = float.NaN;

        for (var attempt = 0; attempt < 200; attempt++) {
            var shuffled = (Modifier[])modifiers.Clone();

            for (var index = shuffled.Length - 1; index > 0; index--) {
                var swap = random.NextInt(index + 1);
                (shuffled[index], shuffled[swap]) = (shuffled[swap], shuffled[index]);
            }

            var set = Set();
            set.AddRange(shuffled);

            var value = set.ValueOf(Power);

            if (float.IsNaN(expected)) {
                expected = value;
            }

            // Bit-identical, not approximately equal. Two hosts that disagree in the last bit are two
            // hosts whose prediction reports a mismatch every frame.
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(expected),
                BitConverter.SingleToUInt32Bits(value)
            );
        }
    }

    [Fact]
    public void RemovingOneOfTwoStacksLeavesExactlyTheOther() {
        var set = Set();
        var first = ModifierSource.From(new(9), 1);
        var second = ModifierSource.From(new(9), 2);

        set.Add(new(Power, ModifierOp.AddPercent, 0.2f, first));
        var withOne = set.ValueOf(Power);

        set.Add(new(Power, ModifierOp.AddPercent, 0.2f, second));
        Assert.Equal(140f, set.ValueOf(Power), 4);

        set.RemoveBySource(second);
        Assert.Equal(withOne, set.ValueOf(Power));
    }

    [Fact]
    public void AnUnownedModifierIsNotRemovedByAnEffectExpiring() {
        var set = Set();

        set.Add(new(Power, ModifierOp.Add, 50f, ModifierSource.None));

        Assert.Equal(0, set.RemoveBySource(ModifierSource.None));
        Assert.Equal(150f, set.ValueOf(Power));

        set.ClearModifiers();
        Assert.Equal(100f, set.ValueOf(Power));
    }

    [Fact]
    public void TheClampAndTheRoundingAreOnTheStat() {
        var layout = new AttributeLayoutBuilder()
            .Add("CritChance", 0.05f, 0f, 1f)
            .Add("Damage", 10.4f, 0f, float.PositiveInfinity, AttributeRounding.Nearest)
            .Build();

        var set = new AttributeSet(layout);

        set.Add(new(Crit, ModifierOp.AddPercent, 100f, ModifierSource.From(new(1), 1)));
        Assert.Equal(1f, set.ValueOf(Crit));

        set.Add(new(Crit, ModifierOp.Add, -100f, ModifierSource.From(new(2), 1)));
        Assert.Equal(0f, set.ValueOf(Crit));

        Assert.Equal(10f, set.ValueOf(AttributeId.From("Damage")));
    }

    [Fact]
    public void AModifierOnAStatTheLayoutDoesNotHaveIsDroppedAndCounted() {
        var set = Set();

        Assert.False(set.Add(new(AttributeId.From("DodgeChance"), ModifierOp.Add, 1f, ModifierSource.From(new(1), 1))));
        Assert.Equal(1, set.DroppedModifiers);
        Assert.Empty(set.Modifiers);
    }

    [Fact]
    public void RecomputationIsBatchedAndOnlyTouchesWhatChanged() {
        var set = Set();

        Assert.Equal(3, set.Recompute());
        Assert.Equal(0, set.Recompute());

        set.Add(new(Power, ModifierOp.Add, 1f, ModifierSource.From(new(1), 1)));
        Assert.Equal(1, set.Recompute());
    }

    [Fact]
    public void AStatWhoseModifiersChangedButWhoseValueDidNotHasNotChanged() {
        var set = Set();
        set.Recompute();
        set.ClearChanges();

        // Saturated at the ceiling already; a second helping of crit changes nothing anybody has to
        // be told about.
        set.Add(new(Crit, ModifierOp.Add, 5f, ModifierSource.From(new(1), 1)));
        set.Recompute();
        Assert.True(set.HasChanged(Crit));

        set.ClearChanges();
        set.Add(new(Crit, ModifierOp.Add, 5f, ModifierSource.From(new(2), 1)));
        set.Recompute();

        Assert.False(set.HasChanged(Crit));
        Assert.False(set.HasChanges);
    }

    [Fact]
    public void SettingABaseMarksTheStatAndNothingElse() {
        var set = Set();
        set.Recompute();
        set.ClearChanges();

        Assert.True(set.SetBase(Health, 1500f));
        set.Recompute();

        Assert.True(set.HasChanged(Health));
        Assert.False(set.HasChanged(Power));
        Assert.Equal(1500f, set.ValueOf(Health));
    }

    [Fact]
    public void AStatTheLayoutDoesNotHaveReadsAsZeroAndCannotBeSet() {
        var set = Set();

        Assert.Equal(0f, set.ValueOf(AttributeId.From("Precision")));
        Assert.False(set.SetBase(AttributeId.From("Precision"), 100f));
        Assert.False(set.HasChanged(AttributeId.From("Precision")));
    }
}
