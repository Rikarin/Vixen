// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.HotReload.Tests;

/// <summary>The three channels, and what each one is allowed to lose.</summary>
public class HotReloadTests {
    // ------------------------------------------------------------ Styles

    /// <summary>
    ///     The channel that is genuinely free: no element is rebuilt, so nothing has anything to
    ///     preserve.
    /// </summary>
    [Fact]
    public void A_style_reload_changes_the_look_without_touching_a_single_element() {
        using var document = new UiDocument(200f, 200f);
        var sheet = document.Load("box { width: 10px; }");

        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);
        document.Update();

        var box = component.Root.Children[0];
        Assert.Equal(10f, box.Width);

        var report = host.ReloadStyles(sheet, "box { width: 40px; }");
        document.Update();

        Assert.True(report.Succeeded);
        Assert.Same(box, component.Root.Children[0]);
        Assert.Equal(40f, box.Width);
    }

    /// <summary>
    ///     ⚠ A rule that is deleted has to stop applying. Loading the new text on top of the old
    ///     would leave the old rule underneath, still winning wherever the new one says nothing —
    ///     which is an overlay, not a reload.
    /// </summary>
    [Fact]
    public void A_rule_deleted_from_a_stylesheet_stops_applying() {
        using var document = new UiDocument(200f, 200f);
        var sheet = document.Load("box { width: 10px; height: 30px; }");

        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);
        document.Update();

        Assert.Equal(30f, component.Root.Children[0].Height);

        host.ReloadStyles(sheet, "box { width: 10px; }");
        document.Update();

        // 200 rather than 0: with no height of its own the box stretches to its parent, which is
        // the default a `height` declaration was overriding. The declaration is gone.
        Assert.Equal(200f, component.Root.Children[0].Height);
    }

    [Fact]
    public void A_stylesheet_that_does_not_load_puts_the_previous_one_back() {
        using var document = new UiDocument(200f, 200f);
        var sheet = document.Load("box { width: 10px; }");

        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);
        document.Update();

        var report = host.ReloadStyles(sheet, "box:nonsense-pseudo { width: 99px; }");
        document.Update();

        Assert.False(report.Succeeded);
        Assert.NotEmpty(report.Errors);
        Assert.Equal(10f, component.Root.Children[0].Width);
    }

    // ------------------------------------------------------------ Markup

    [Fact]
    public void A_markup_reload_rebuilds_the_elements_and_keeps_the_component() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var before = component.Root.Children[0];
        var report = host.ReloadComponents();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Components);
        Assert.True(before.IsRemoved);
        Assert.NotSame(before, component.Root.Children[0]);
    }

    /// <summary>
    ///     Most of what "state was preserved" means: the component object survives a rebuild, so
    ///     its signals do, and every effect the new build registers reads the value that was there.
    /// </summary>
    [Fact]
    public void Signal_values_survive_a_markup_reload_without_being_carried()  {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Counting>(document.Root);

        component.Count.Value = 12;
        EffectScheduler.Default.Flush();
        Assert.Equal("12", component.Root.Children[0].Text);

        host.ReloadComponents();
        EffectScheduler.Default.Flush();

        Assert.Equal(12, component.Count.Value);
        Assert.Equal("12", component.Root.Children[0].Text);
    }

    [Fact]
    public void The_effects_of_the_previous_build_do_not_survive_it() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Counting>(document.Root);

        EffectScheduler.Default.Flush();
        host.ReloadComponents();
        EffectScheduler.Default.Flush();

        var runs = component.Runs;
        component.Count.Value = 5;
        EffectScheduler.Default.Flush();

        // One effect ran, not two: the old build's is disposed rather than left reading the signal.
        Assert.Equal(runs + 1, component.Runs);
    }

    [Fact]
    public void The_focus_goes_back_to_the_same_place_in_the_rebuilt_tree() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Focusable>(document.Root);

        var field = component.Root.Children[1];
        Assert.True(document.Focus(field));

        var report = host.ReloadComponents();

        Assert.True(report.FocusRestored);
        Assert.NotSame(field, document.Focused);
        Assert.Same(component.Root.Children[1], document.Focused);
    }

    /// <summary>
    ///     ⚠ Clear-then-build has no snapshot to fall back to, so a <c>Build</c> that throws leaves
    ///     an empty component. Reported rather than swallowed, and said out loud rather than
    ///     described as "the previous UI is kept".
    /// </summary>
    [Fact]
    public void A_build_that_throws_is_reported_and_leaves_the_component_empty() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        component.Broken = true;
        var report = host.ReloadComponents();

        Assert.False(report.Succeeded);
        Assert.Contains("deliberate", Assert.Single(report.Errors), StringComparison.Ordinal);
        Assert.Empty(component.Root.Children);
    }

    // ------------------------------------------------------------ Replacement

    /// <summary>
    ///     The case <see cref="HotReloadStateAttribute" /> exists for: a new instance, which starts
    ///     from its own field initialisers unless something carries the old values across.
    /// </summary>
    [Fact]
    public void A_replaced_component_carries_the_state_it_marked_and_nothing_else() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Carrying>(document.Root);

        component.Kept.Value = 9;
        component.Dropped = "changed";

        var report = host.Replace(component, static () => new Carrying());
        var replacement = (Carrying)Assert.Single(host.Components);

        Assert.True(report.Succeeded);
        Assert.NotSame(component, replacement);
        Assert.Equal(9, replacement.Kept.Value);
        Assert.Equal("initial", replacement.Dropped);
    }

    [Fact]
    public void A_replaced_component_lands_where_the_old_one_was() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);

        var first = host.Mount<Boxes>(document.Root);
        _ = host.Mount<Boxes>(document.Root);

        host.Replace(first, static () => new Boxes());
        var replacement = host.Components[0];

        Assert.Equal(0, replacement.Root.IndexInParent);
        Assert.Equal(2, document.Root.Children.Count);
    }

    /// <summary>
    ///     The point of a reload is that the type changed, so a carried value may no longer fit
    ///     where it came from. Checking turns a cast exception somewhere in the framework into a
    ///     value that quietly did not come across.
    /// </summary>
    [Fact]
    public void State_whose_type_no_longer_fits_is_left_behind_rather_than_thrown_at_the_field() {
        var captured = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Note"] = 42 };
        var component = new Carrying();

        HotReloadHost.Restore(component, captured);

        Assert.Equal("kept", component.Note);
    }

    [Fact]
    public void State_that_still_fits_is_carried_into_the_field_it_came_from() {
        var captured = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Note"] = "carried" };
        var component = new Carrying();

        HotReloadHost.Restore(component, captured);

        Assert.Equal("carried", component.Note);
    }

    /// <summary>
    ///     ⚠ A slot list that survived a rebuild would still name the elements the previous build
    ///     made, so a caller's children would be projected into an element no longer in the
    ///     document — where they would be invisible and unreachable.
    /// </summary>
    [Fact]
    public void A_rebuilt_component_declares_its_slots_again_rather_than_keeping_the_old_ones() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Projecting>(document.Root);

        var slot = component.Content;
        Assert.Equal("slot", slot.Tag);

        host.ReloadComponents();

        Assert.True(slot.IsRemoved);
        Assert.NotSame(slot, component.Content);
        Assert.False(component.Content.IsRemoved);

        // And the case that actually needs the list cleared rather than overwritten: a slot the new
        // build does not declare at all. Projection has to fall back to the root, not to an element
        // that is no longer in the document.
        component.Slotted = false;
        host.ReloadComponents();

        Assert.Same(component.Root, component.Content);
    }

    [Fact]
    public void Replacing_a_component_this_host_never_mounted_is_refused() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);

        Assert.Throws<ArgumentException>(() => host.Replace(new Boxes(), static () => new Boxes()));
    }

    // ------------------------------------------------------------ Registration

    [Fact]
    public void A_registered_host_reloads_when_the_runtime_updates_the_application() {
        using var document = new UiDocument(200f, 200f);
        var host = new HotReloadHost(document);
        var component = host.Mount<Boxes>(document.Root);

        var reports = new List<ReloadReport>();
        host.Reloaded += reports.Add;

        MetadataUpdate.Register(host);

        try {
            MetadataUpdate.UpdateApplication(null);
        } finally {
            Assert.True(MetadataUpdate.Unregister(host));
        }

        Assert.Equal(ReloadChannel.Markup, Assert.Single(reports).Channel);
        Assert.Single(component.Root.Children);
    }

    // ------------------------------------------------------------ Fixtures

    sealed class Boxes : Component {
        public bool Broken { get; set; }

        protected override void Build(BuildContext ctx) {
            if (Broken) {
                throw new InvalidOperationException("a deliberate failure");
            }

            ctx.Element(null, "box");
        }
    }

    sealed class Counting : Component {
        public Signal<int> Count { get; } = new(0);
        public int Runs { get; private set; }

        protected override void Build(BuildContext ctx) {
            var label = ctx.Element(null, "label");
            ctx.Bind(() => {
                Runs++;
                label.Text = Count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            });
        }
    }

    sealed class Focusable : Component {
        protected override void Build(BuildContext ctx) {
            ctx.Element(null, "first").Focusable = true;
            ctx.Element(null, "second").Focusable = true;
        }
    }

    sealed class Carrying : Component {
        [HotReloadState] public Signal<int> Kept { get; } = new(0);

        [HotReloadState] public string Note { get; set; } = "kept";

        public string Dropped { get; set; } = "initial";

        protected override void Build(BuildContext ctx) => ctx.Element(null, "box");
    }

    sealed class Projecting : Component {
        public bool Slotted { get; set; } = true;

        protected override void Build(BuildContext ctx) {
            if (Slotted) {
                ctx.Slot(null, BuildContext.DefaultSlot);
            } else {
                ctx.Element(null, "plain");
            }
        }
    }
}
