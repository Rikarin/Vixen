// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What this assembly contributes to the language a <c>.vxml</c> is written in.</summary>
/// <remarks>
///     Written against <c>BuildContext</c> directly rather than against markup, because that is the
///     contract: a <c>.vxml</c> compiles to exactly these calls, and this assembly cannot reference
///     the markup compiler that would produce them.
/// </remarks>
public class MarkupTests {
    /// <summary>A control's tag builds the control, with the tag its own type answers to.</summary>
    [Fact]
    public void A_control_tag_builds_the_control() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Bar>(fixture.Document, fixture.Document.Root);

        var progress = Assert.IsType<ProgressBar>(component.Root.Children[0]);
        Assert.Equal("progress-bar", progress.Tag);
    }

    /// <summary>
    ///     <c>on:click</c> on a control is its <i>activation</i>, which is why the control library
    ///     has to be able to say what the name means. A button is pressed by Space and by Enter as
    ///     well as by a pointer, and a binding that only heard the tap would work for everyone who
    ///     tests with a mouse and for nobody who does not use one.
    /// </summary>
    [Fact]
    public void A_click_binding_on_a_control_hears_the_keyboard_as_well_as_the_pointer() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Pressable>(fixture.Document, fixture.Document.Root);
        var button = (Button) component.Root.Children[0];

        fixture.Update();
        fixture.Click(button);
        Assert.Equal(1, component.Clicks);

        fixture.Document.Focus(button);
        fixture.Type(InputKey.Enter);
        Assert.Equal(2, component.Clicks);
    }

    /// <summary>
    ///     And exactly once for a tap. The control raises its <c>ClickEvent</c> from inside its own
    ///     tap handler, so a runtime that subscribed to both would count one press twice.
    /// </summary>
    [Fact]
    public void A_click_binding_on_a_control_counts_one_press_once() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Pressable>(fixture.Document, fixture.Document.Root);

        fixture.Update();
        fixture.Click((Button) component.Root.Children[0]);

        Assert.Equal(1, component.Clicks);
    }

    /// <summary>
    ///     An element that is not a control raises no <c>ClickEvent</c>, so <c>on:click</c> on one
    ///     stays the tap it always was. The decision is per element, not per name.
    /// </summary>
    [Fact]
    public void A_click_binding_on_a_plain_element_is_still_the_tap() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Plain>(fixture.Document, fixture.Document.Root);

        component.Root.Children[0].Raise(new TapEvent { Count = 1 });

        Assert.Equal(1, component.Clicks);
    }

    /// <summary>
    ///     And by Space, by an access key and by <c>Activate()</c> — the four ways
    ///     <c>ButtonBase</c> lists. <c>Activate()</c> matters most of the four here, because it is
    ///     what every editor test presses a button with, so a binding that missed it would be a
    ///     binding no ported panel's test could see was broken.
    /// </summary>
    [Fact]
    public void A_click_binding_hears_space_an_access_key_and_a_programmatic_activation() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Pressable>(fixture.Document, fixture.Document.Root);
        var button = (Button) component.Root.Children[0];

        fixture.Update();
        fixture.Document.Focus(button);

        // Space activates on release, which is what the two calls are.
        fixture.Type(InputKey.Space);
        Assert.Equal(1, component.Clicks);

        // Alt and a letter, through the document, rather than an `AccessKeyEvent` raised by hand —
        // the document is what decides which element an access key names, and a test that skipped it
        // would pass against a button nothing could ever reach.
        button.AccessKey = 'S';
        fixture.KeyDown(InputKey.S, ModifierKeys.Alt);
        Assert.Equal(2, component.Clicks);

        button.Activate();
        Assert.Equal(3, component.Clicks);
    }

    /// <summary>
    ///     ⚠ <b>And on a control that raises no activation, which is most of them.</b>
    ///     <c>ButtonBase</c> and <c>ColorSwatch</c> are the only two types in the whole set that
    ///     raise a <c>ClickEvent</c>; <see cref="Card" />, <see cref="Panel" />, a row, a badge and
    ///     the thirty others do not. A runtime that bound <c>on:click</c> to the activation because
    ///     the element was a <c>Control</c> gave every one of those a handler that could never run —
    ///     silently, which is the failure this whole table exists to avoid.
    /// </summary>
    [Fact]
    public void A_click_binding_on_a_control_that_raises_no_activation_still_hears_the_tap() {
        using var fixture = new ControlFixture(css: "card { width: 200px; height: 100px; }");
        var component = BuildContext.Build<Boxed>(fixture.Document, fixture.Document.Root);

        fixture.Update();
        fixture.Click(component.Card);

        Assert.Equal(1, component.Clicks);
    }

    /// <summary>
    ///     ⚠ <b>And a container hears a button inside it exactly once.</b> Both halves are live at
    ///     the same time here: the activation bubbles up from the button and the tap that produced
    ///     it bubbles up behind it, so a runtime that counted both would report one press as two —
    ///     which is the reason the tap is asked whether the activation already told anybody.
    /// </summary>
    [Fact]
    public void A_click_binding_on_a_container_counts_a_button_inside_it_once() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Clickable>(fixture.Document, fixture.Document.Root);

        fixture.Update();
        fixture.Click(component.Button);

        Assert.Equal(1, component.Clicks);
    }

    /// <summary>
    ///     ⚠ <b>And a disabled button is not a click, neither on itself nor on what contains it.</b>
    ///     A disabled control raises no activation <i>and</i> does not mark the tap handled — it
    ///     returns before both — so the tap arrives at the container looking exactly like an
    ///     ordinary one.
    /// </summary>
    [Fact]
    public void A_disabled_button_is_not_a_click_for_itself_or_for_its_container() {
        using var fixture = new ControlFixture();
        var component = BuildContext.Build<Clickable>(fixture.Document, fixture.Document.Root);
        var pressable = BuildContext.Build<Pressable>(fixture.Document, fixture.Document.Root);

        component.Button.Disabled = true;
        ((Button) pressable.Root.Children[0]).Disabled = true;

        fixture.Update();
        fixture.Click(component.Button);
        fixture.Click(pressable.Root.Children[0]);

        Assert.Equal(0, component.Clicks);
        Assert.Equal(0, pressable.Clicks);
    }


    // ================================================================== change: and refs

    /// <summary>
    ///     ⚠ <b>The thing <c>on:</c> cannot do, on the controls that made it matter.</b> A drag moves
    ///     a real <see cref="Slider" /> and the panel is told the number — which no entry in the
    ///     <c>Subscribe</c> table could deliver, because its handlers take a routed
    ///     <c>UiEvent</c> and a value is not one.
    /// </summary>
    /// <remarks>
    ///     Dragged rather than assigned, for <c>RangeInteractionTests</c>' reason: assigning
    ///     <c>Value</c> tests the property, and what is on trial here is whether a binding hears the
    ///     control at all.
    /// </remarks>
    [Fact]
    public void A_change_binding_hears_a_real_drag_and_is_given_the_value() {
        using var fixture = new ControlFixture(css: "slider { width: 200px; height: 24px; }");
        var mixer = BuildContext.Build<Mixer>(fixture.Document, fixture.Document.Root);

        fixture.Update();
        fixture.Document.Effects.Flush();
        fixture.Update();

        Drag(fixture, mixer.Faders["sfx"], 0.75f);

        // Every step of the drag, not only the release — a panel that wrote once at the end would
        // be a mixer whose meters do not move while a fader does.
        Assert.True(mixer.Writes.Count > 1, $"expected the drag to report continuously, got {mixer.Writes.Count}");
        Assert.All(mixer.Writes, write => Assert.Equal("sfx", write.Bus));
        Assert.True(mixer.Writes[^1].Gain > 0.6f, $"expected the dragged value, got {mixer.Writes[^1].Gain}");
    }

    /// <summary>
    ///     ⚠ <b>What a per-iteration handle is for, and it is not reaching a row from outside.</b>
    ///     A mixer strip's fader handler has to read <i>its own</i> mute — <c>AudioMixerView</c>
    ///     lines 234-235 — and a loop body's <c>ref</c> is one member for every row, so the answer
    ///     would be whichever strip was built last.
    /// </summary>
    [Fact]
    public void A_rows_handler_reads_that_rows_other_control_and_not_the_last_ones() {
        using var fixture = new ControlFixture(css: "slider { width: 200px; height: 24px; }");
        var mixer = BuildContext.Build<Mixer>(fixture.Document, fixture.Document.Root);

        fixture.Update();
        fixture.Document.Effects.Flush();
        fixture.Update();

        // Only music is muted, so a handler that read the wrong row's toggle would say so.
        mixer.Mutes["music"].IsChecked = true;
        mixer.Writes.Clear();

        Drag(fixture, mixer.Faders["sfx"], 0.75f);
        Assert.NotEmpty(mixer.Writes);
        Assert.All(mixer.Writes, write => Assert.False(write.Muted, "the sfx strip read the music strip's mute"));

        mixer.Writes.Clear();
        Drag(fixture, mixer.Faders["music"], 0.25f);
        Assert.NotEmpty(mixer.Writes);
        Assert.All(mixer.Writes, write => Assert.True(write.Muted, "the music strip read another strip's mute"));
    }

    /// <summary>
    ///     ⚠ <b>A reorder keeps every row's element, which is what a list-valued handle could not
    ///     do.</b> The entry is filed under the identity <c>BuildContext.For</c> reconciled on, so
    ///     position is not what is being asked and moving a row cannot answer with its neighbour.
    /// </summary>
    [Fact]
    public void A_reordered_sequence_still_hands_back_each_rows_own_controls() {
        using var fixture = new ControlFixture(css: "slider { width: 200px; height: 24px; }");
        var mixer = BuildContext.Build<Mixer>(fixture.Document, fixture.Document.Root);

        fixture.Update();
        fixture.Document.Effects.Flush();

        var music = mixer.Faders["music"];
        var sfx = mixer.Faders["sfx"];

        mixer.Buses.Value = ["sfx", "music"];
        fixture.Document.Effects.Flush();

        Assert.Same(music, mixer.Faders["music"]);
        Assert.Same(sfx, mixer.Faders["sfx"]);
    }

    /// <summary>
    ///     And a row that leaves takes its entry with it — a handle that only gained would answer
    ///     for strips that had left the document, and hold them alive to do it.
    /// </summary>
    [Fact]
    public void A_row_that_leaves_the_sequence_takes_its_entry_with_it() {
        using var fixture = new ControlFixture(css: "slider { width: 200px; height: 24px; }");
        var mixer = BuildContext.Build<Mixer>(fixture.Document, fixture.Document.Root);

        fixture.Update();
        fixture.Document.Effects.Flush();
        Assert.Equal(2, mixer.Faders.Count);

        mixer.Buses.Value = ["music"];
        fixture.Document.Effects.Flush();

        Assert.Equal(1, mixer.Faders.Count);
        Assert.False(mixer.Faders.Contains("sfx"));
        Assert.False(mixer.Mutes.Contains("sfx"));
    }

    /// <summary>
    ///     ⚠ <b>The asymmetry a ported panel meets first, said out loud instead of answered with
    ///     null.</b> A sequence change is applied by an effect, so the handle is not filled on the
    ///     line that changed it — and the panel this replaces filled its member on that line. A
    ///     lookup that answered null would surface as a <c>NullReferenceException</c> somewhere else
    ///     entirely.
    /// </summary>
    [Fact]
    public void A_handle_read_before_the_flush_says_which_frame_it_is_waiting_for() {
        using var fixture = new ControlFixture(css: "slider { width: 200px; height: 24px; }");
        var mixer = BuildContext.Build<Mixer>(fixture.Document, fixture.Document.Root);

        var thrown = Assert.Throws<KeyNotFoundException>(() => mixer.Faders["music"]);
        Assert.Contains("flushed", thrown.Message, StringComparison.Ordinal);

        fixture.Document.Effects.Flush();

        // And once it has run, a key the loop never produced is a different mistake with a
        // different message — not the frame one, which would send a reader looking for a frame.
        Assert.DoesNotContain("flushed", Assert.Throws<KeyNotFoundException>(() => mixer.Faders["reverb"]).Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A forward binding's own write is not a change to report, and this is the one rule
    ///     <c>change:</c> does not share with <c>bind:</c>.</b> The subscription is made while the
    ///     panel builds and the forward binding first writes one flush later, so without the rule
    ///     every mixer would post an undo entry for a gain nobody touched, on open. The C# it
    ///     replaces cannot have that bug: there the value is assigned before the <c>+=</c>.
    /// </summary>
    [Fact]
    public void A_value_arriving_from_the_model_is_not_reported_as_a_change() {
        using var fixture = new ControlFixture(css: "slider { width: 200px; height: 24px; }");
        var mixer = BuildContext.Build<Mixer>(fixture.Document, fixture.Document.Root);

        mixer.Gain.Value = 0.4f;
        fixture.Document.Effects.Flush();

        Assert.Equal(0.4f, mixer.Faders["music"].Value);
        Assert.Empty(mixer.Writes);
    }

    /// <summary>Drags a slider's thumb to a fraction of its width, with a real pointer.</summary>
    static void Drag(ControlFixture fixture, Slider slider, float fraction) {
        var bounds = slider.Bounds;
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Press(bounds.X + (bounds.Width * 0.5f), y);
        fixture.MovePointer(bounds.X + (bounds.Width * fraction), y);
        fixture.Release(bounds.X + (bounds.Width * fraction), y);
    }

    /// <summary>What the panel wrote, so a test can say which row said it.</summary>
    readonly record struct Written(string Bus, float Gain, bool Muted);

    /// <summary>
    ///     <c>AudioMixerView</c>'s shape, reduced to the two things that made it unportable: a
    ///     value-change subscription, and a row whose handler reads its own sibling control.
    /// </summary>
    /// <remarks>
    ///     Written against <c>BuildContext</c> rather than as a <c>.vxml</c>, for this file's
    ///     reason: these are the calls
    ///     <c>&lt;Slider refs="@Faders" change:Value="…" /&gt;</c> compiles to, and this assembly
    ///     cannot reference the compiler that would produce them. That the compiler produces exactly
    ///     these is <c>Vixen.Ui.Markup.Tests</c>' question.
    /// </remarks>
    sealed class Mixer : Component {
        public Signal<string[]> Buses { get; } = new(["music", "sfx"]);

        public Signal<float> Gain { get; } = new(0f);

        public ElementRefs<Slider> Faders { get; } = new();

        public ElementRefs<ToggleButton> Mutes { get; } = new();

        public List<Written> Writes { get; } = [];

        protected override void Build(BuildContext ctx) =>
            ctx.For(
                null,
                () => (IEnumerable<string>) Buses.Value,
                static bus => bus,
                (row, parent, bus) => {
                    var strip = row.Element(parent, "strip");

                    var fader = row.Child<Slider>(strip);
                    row.Refs(Faders, fader);

                    var mute = row.Child<ToggleButton>(strip);
                    row.Refs(Mutes, mute);

                    // The forward binding, which is what makes the "not reported" rule load-bearing:
                    // it first writes one flush after this subscription exists.
                    row.Bind(() => fader.Value = Gain.Value);

                    row.Changed(fader, "Value", () => fader.Value, value => Writes.Add(new(bus, value, Mutes[bus].IsChecked)));
                    row.Changed(mute, "IsChecked", () => mute.IsChecked, on => Writes.Add(new(bus, Faders[bus].Value, on)));
                }
            );
    }

    sealed class Bar : Component {
        protected override void Build(BuildContext ctx) => ctx.Child<ProgressBar>(null);
    }

    sealed class Pressable : Component {
        public int Clicks { get; private set; }

        protected override void Build(BuildContext ctx) {
            var button = ctx.Child<Button>(null);
            button.Label = "Press";

            ctx.On(BuildContext.Host(button), "click", () => Clicks++);
        }
    }

    /// <summary>
    ///     One <c>on:click</c> on a bare <see cref="Card" /> — the shape a web author writes without
    ///     thinking about it, and the one that used to bind a handler nothing could ever raise.
    /// </summary>
    sealed class Boxed : Component {
        public int Clicks { get; private set; }

        public Card Card { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            Card = ctx.Child<Card>(null);
            ctx.On(BuildContext.Host(Card), "click", () => Clicks++);
        }
    }

    /// <summary>The same, with a button inside it, which is where one press can become two.</summary>
    sealed class Clickable : Component {
        public int Clicks { get; private set; }

        public Card Card { get; private set; } = null!;

        public Button Button { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            Card = ctx.Child<Card>(null);
            Button = ctx.Child<Button>(BuildContext.Inner(Card));
            Button.Label = "Press";

            ctx.On(BuildContext.Host(Card), "click", () => Clicks++);
        }
    }

    sealed class Plain : Component {
        public int Clicks { get; private set; }

        protected override void Build(BuildContext ctx) =>
            ctx.On(ctx.Element(null, "div"), "click", () => Clicks++);
    }


    // ================================================================== bind:, and what commits it

    /// <summary>
    ///     <b>Every change, which is what <c>bind:X</c> with no modifier means and has to keep
    ///     meaning.</b> A binding whose other end is a <c>Signal&lt;T&gt;</c> wants the keystroke —
    ///     a deferred one is a panel lagging its own field — and a control with no commit moment
    ///     would never write at all if the default were the other way round.
    /// </summary>
    [Fact]
    public void A_binding_with_no_commit_event_writes_on_every_change() {
        using var fixture = new ControlFixture();
        var form = BuildContext.Build<Form>(fixture.Document, fixture.Document.Root);

        fixture.Document.Effects.Flush();
        form.LiveBox.Value = "que";

        Assert.Equal("que", form.Live.Value);
    }

    /// <summary>
    ///     ⚠ <b>And a binding that names <c>submit</c> writes nothing until Enter.</b> This is the
    ///     whole of what the modifier buys: the value moves through the control the entire time and
    ///     reaches the model once, so a consumer that treats a write as a decision — an undo entry,
    ///     a query, a file save — gets one decision rather than one per keystroke.
    /// </summary>
    [Fact]
    public void A_binding_that_names_submit_writes_nothing_until_enter() {
        using var fixture = new ControlFixture();
        var form = BuildContext.Build<Form>(fixture.Document, fixture.Document.Root);

        fixture.Document.Effects.Flush();
        fixture.Document.Focus(form.SubmitBox);
        form.SubmitBox.Value = "que";

        Assert.Null(form.Committed.Value);

        fixture.Type(InputKey.Enter);

        Assert.Equal("que", form.Committed.Value);
    }

    /// <summary>
    ///     And <c>blur</c>, which is the other half of what "commit" means to anyone who has used a
    ///     form. ⚠ Both names were absent from the runtime's table until this landed, while the
    ///     binder's alias list had accepted <c>onfocus</c> and <c>onblur</c> all along — so they
    ///     bound and threw at compose, which is the same shape of failure <c>on:keydown</c> had.
    /// </summary>
    [Fact]
    public void A_binding_that_names_blur_writes_when_the_focus_leaves() {
        using var fixture = new ControlFixture();
        var form = BuildContext.Build<Form>(fixture.Document, fixture.Document.Root);

        fixture.Document.Effects.Flush();
        fixture.Document.Focus(form.BlurBox);
        form.BlurBox.Value = "hello";

        Assert.Null(form.Blurred.Value);

        fixture.Document.Focus(form.LiveBox);

        Assert.Equal("hello", form.Blurred.Value);
    }

    /// <summary>
    ///     ⚠ <b>The value is read at the event, so a field that reformats on commit hands over what
    ///     it settled on.</b> <c>NumericInput</c> only rereads its text in <c>OnSubmit</c>, so a
    ///     routed submission raised before that would have handed the model the number the field
    ///     held <i>before</i> anything was typed — nought — while the box read <c>7</c>.
    /// </summary>
    [Fact]
    public void A_commit_binding_is_given_what_the_field_settled_on_and_not_what_was_typed() {
        using var fixture = new ControlFixture();
        var form = BuildContext.Build<Form>(fixture.Document, fixture.Document.Root);

        fixture.Document.Effects.Flush();
        fixture.Document.Focus(form.NumberBox);
        form.NumberBox.Value = "007";
        fixture.Type(InputKey.Enter);

        Assert.Equal(7d, form.Number.Value);
    }

    /// <summary>
    ///     The forward leg is untouched by any of it: a value arriving from the model still reaches
    ///     the control, which is what stops a commit binding being a one-way write dressed up.
    /// </summary>
    [Fact]
    public void A_commit_binding_still_takes_the_value_the_model_hands_it() {
        using var fixture = new ControlFixture();
        var form = BuildContext.Build<Form>(fixture.Document, fixture.Document.Root);

        form.Committed.Value = "from the model";
        fixture.Document.Effects.Flush();

        Assert.Equal("from the model", form.SubmitBox.Value);
    }

    /// <summary>
    ///     ⚠ <b><c>TextField.Submitted</c> was unreachable from markup until it had a routed
    ///     half.</b> <c>on:</c> subscribes routed events, so a <c>.vxml</c> could hear a field's
    ///     keys and its focus and not the one keystroke a form is about; every panel that wanted it
    ///     held a <c>ref</c> and wired the C# event by hand.
    /// </summary>
    [Fact]
    public void On_submit_hears_the_field_being_finished_with() {
        using var fixture = new ControlFixture();
        var form = BuildContext.Build<Form>(fixture.Document, fixture.Document.Root);

        fixture.Document.Effects.Flush();
        fixture.Document.Focus(form.SubmitBox);

        Assert.Equal(0, form.Submissions);

        fixture.Type(InputKey.Enter);

        Assert.Equal(1, form.Submissions);
    }

    /// <summary>
    ///     ⚠ <b>Enter in a text area is a line break and submits nothing, and Ctrl-Enter submits.</b>
    ///     That rule lives in the control, which is exactly why the commit moment is an event the
    ///     control raises rather than a filtered <c>on:keydown</c> the binding would reconstruct —
    ///     a second copy of it would disagree with the first the moment either moved.
    /// </summary>
    [Fact]
    public void A_text_area_commits_on_ctrl_enter_and_not_on_the_line_break() {
        using var fixture = new ControlFixture();

        var area = fixture.Add<TextArea>();
        var submissions = 0;

        area.AddHandler<SubmitEvent>((_, _) => submissions++);
        fixture.Document.Focus(area);

        fixture.Type(InputKey.Enter);
        Assert.Equal(0, submissions);
        Assert.Equal("\n", area.Value);

        fixture.KeyDown(InputKey.Enter, ModifierKeys.Control);
        Assert.Equal(1, submissions);
    }

    /// <summary>
    ///     A name the runtime does not know is the same loud failure <c>on:</c> gives it, and for
    ///     the same reason: the markup compiler cannot check the table, so the runtime has to.
    /// </summary>
    [Fact]
    public void A_commit_event_the_runtime_does_not_know_says_so_at_compose() {
        using var fixture = new ControlFixture();

        var thrown = Assert.Throws<ArgumentException>(
            () => BuildContext.Build<Misspelt>(fixture.Document, fixture.Document.Root)
        );

        Assert.Contains("blurr", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The four bindings the commit rule has to tell apart, in one panel.</summary>
    /// <remarks>
    ///     Written against <c>BuildContext</c> rather than as a <c>.vxml</c>, for this file's
    ///     reason: these are the calls <c>&lt;TextBox bind:Value.submit="@…" /&gt;</c> compiles to,
    ///     and that the compiler produces exactly them is <c>Vixen.Ui.Markup.Tests</c>' question.
    /// </remarks>
    sealed class Form : Component {
        public Signal<string?> Live { get; } = new(null);

        public Signal<string?> Committed { get; } = new(null);

        public Signal<string?> Blurred { get; } = new(null);

        public Signal<double> Number { get; } = new(0d);

        public int Submissions { get; private set; }

        public TextBox LiveBox { get; private set; } = null!;

        public TextBox SubmitBox { get; private set; } = null!;

        public TextBox BlurBox { get; private set; } = null!;

        public NumericInput NumberBox { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            LiveBox = ctx.Child<TextBox>(null);
            ctx.TwoWay(LiveBox, "Value", () => Live.Value, value => Live.Value = value);

            SubmitBox = ctx.Child<TextBox>(null);
            ctx.TwoWay(SubmitBox, "Value", () => Committed.Value, value => Committed.Value = value, "submit");
            ctx.On(SubmitBox, "submit", () => Submissions++);

            BlurBox = ctx.Child<TextBox>(null);
            ctx.TwoWay(BlurBox, "Value", () => Blurred.Value, value => Blurred.Value = value, "blur");

            NumberBox = ctx.Child<NumericInput>(null);
            ctx.TwoWay(NumberBox, "Number", () => Number.Value, value => Number.Value = value, "submit");
        }
    }

    sealed class Misspelt : Component {
        readonly Signal<string?> name = new(null);

        protected override void Build(BuildContext ctx) {
            var box = ctx.Child<TextBox>(null);
            ctx.TwoWay(box, "Value", () => name.Value, value => name.Value = value, "blurr");
        }
    }
}
