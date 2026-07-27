// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Reactive.Tests;

/// <summary>
///     Writable, until the thing it is derived from moves. The tests are the two halves of that
///     sentence, and the interaction between them.
/// </summary>
public class LinkedSignalTests {
    [Fact]
    public void It_starts_out_derived_from_its_source() {
        var selection = new Signal<string>("cube");
        var tab = new LinkedSignal<string, int>(() => selection.Value, static (_, _) => 0);

        Assert.Equal(0, tab.Value);
    }

    [Fact]
    public void A_write_sticks_until_the_source_changes() {
        var selection = new Signal<string>("cube");
        var tab = new LinkedSignal<string, int>(() => selection.Value, static (_, _) => 0);

        tab.Value = 3;

        Assert.Equal(3, tab.Value);

        selection.Value = "light";

        Assert.Equal(0, tab.Value);
    }

    [Fact]
    public void An_unrelated_invalidation_does_not_discard_a_write() {
        var selection = new Signal<string>("cube");
        var unrelated = new Signal<int>(0);
        var tab = new LinkedSignal<string, int>(
            () => {
                _ = unrelated.Value;
                return selection.Value;
            },
            static (_, _) => 0
        );

        tab.Value = 3;
        unrelated.Value = 1;

        // The source function re-ran, because something it read changed. What it returned did not,
        // so the write stands.
        Assert.Equal(3, tab.Value);
    }

    [Fact]
    public void The_computation_is_given_the_value_it_is_replacing() {
        // "Keep the selected row if it survived the refresh" is the case that needs both the new
        // source and the old value, and is why this is not just a computed with a setter.
        var rows = new Signal<int[]>([1, 2, 3]);
        var selected = new LinkedSignal<int[], int>(
            () => rows.Value,
            static (available, previous) => Array.IndexOf(available, previous) >= 0 ? previous : available[0]
        );

        selected.Value = 2;

        Assert.Equal(2, selected.Value);

        rows.Value = [2, 3, 4];

        Assert.Equal(2, selected.Value);

        rows.Value = [7, 8];

        Assert.Equal(7, selected.Value);
    }

    [Fact]
    public void A_reset_wakes_whatever_is_watching() {
        var scheduler = new EffectScheduler();
        var selection = new Signal<string>("cube");
        var tab = new LinkedSignal<string, int>(() => selection.Value, static (_, _) => 0);
        var seen = -1;

        using var effect = new Effect(() => seen = tab.Value, scheduler);
        scheduler.Flush();

        Assert.Equal(0, seen);

        tab.Value = 2;
        scheduler.Flush();

        Assert.Equal(2, seen);

        selection.Value = "light";
        scheduler.Flush();

        Assert.Equal(0, seen);
    }

    [Fact]
    public void A_reset_that_lands_on_the_same_value_does_not_wake_anything() {
        var scheduler = new EffectScheduler();
        var selection = new Signal<string>("cube");
        var tab = new LinkedSignal<string, int>(() => selection.Value, static (_, _) => 0);
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                _ = tab.Value;
            },
            scheduler
        );

        scheduler.Flush();

        Assert.Equal(1, runs);

        selection.Value = "light";
        scheduler.Flush();

        // The source moved and the value did not, so the equality short-circuit applies here too.
        Assert.Equal(1, runs);
    }
}
