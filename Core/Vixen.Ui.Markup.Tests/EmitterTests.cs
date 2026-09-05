// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vixen.Input;
using Vixen.Ui.Composition;
using Vixen.Ui.Markup.Binding;
using Vixen.Ui.Markup.Emit;
using Vixen.Ui.Reactive;
using Xunit;
using Binder = Vixen.Ui.Markup.Binding.Binder;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Vixen.Ui.Markup.Tests;

/// <summary>
///     The generated C#, and the mapping that makes an error in it land on the markup.
/// </summary>
/// <remarks>
///     These tests do not read the output and check it looks right. They hand it to Roslyn and then
///     to a <see cref="UiDocument" />, because the only claims worth making about a code generator
///     are that it compiles, that it runs, and that when it does not the message points at what the
///     author wrote — and none of those is a question about text.
/// </remarks>
public class EmitterTests {
    const string Path = "Counter.vxml";

    const string Counter = """
                           @component Counter
                           @using System.Linq

                           @code {
                               private int _count;
                               private string _kind = "warning";
                               private void Increment() => _count++;
                               private System.Collections.Generic.IEnumerable<int> Steps => Enumerable.Range(0, 3);
                           }

                           <div class="row">
                               <span class="count @_kind">Clicked @_count times</span>
                               <Label Title="Hello" Step="@_count" class="lead" />

                               @if (_count > 10) {
                                   <Callout Kind="@_kind">That's a lot.</Callout>
                               } else if (_count > 0) {
                                   <em>Some.</em>
                               } else {
                                   <em>None.</em>
                               }

                               @for (var i in Steps) {
                                   <button key="@i" on:click.stop="@Increment">+@i</button>
                               }

                               @switch (_count) {
                                   case 0: <i>zero</i>
                                   default: <i>more</i>
                               }

                               <slot name="footer" />
                           </div>

                           <style scoped>.row { display: flex; }</style>
                           """;

    [Fact]
    public void The_generated_code_for_a_whole_component_compiles() =>
        Assert.Empty(Errors(Compile(Emit(Counter))));

    /// <summary>
    ///     ⚠ <b>An aliased <c>@using</c> reaches the generated file whole.</b> Lexed as a name and
    ///     nothing else, the <c>= A.B.C</c> was dropped and the emitter wrote <c>using Knob;</c> —
    ///     a namespace nobody declared, so what the author saw was <c>CS0246</c> against a type
    ///     that is right there, on a generated line they never wrote. The alias is the one shape of
    ///     import with no workaround, because a name that needs disambiguating cannot be spelled
    ///     any other way.
    /// </summary>
    [Fact]
    public void A_using_alias_survives_into_the_generated_file() {
        const string Elsewhere = """
                                 namespace Far.Away { public class Knob { public int Turns => 3; } }
                                 """;

        const string Source = """
                              @component Greeter
                              @using Knob = Far.Away.Knob

                              @code {
                                  private readonly Knob _knob = new();
                                  private int Turns => _knob.Turns;
                              }

                              <div>@Turns</div>
                              """;

        Assert.Empty(Errors(Compile(Emit(Source), Elsewhere)));
    }

    /// <summary>
    ///     ⚠ <b><c>@using static</c> is the same defect one directive-shape over.</b> The directive
    ///     lexed exactly one name, so <c>static</c> <i>was</i> the name: the emitter wrote
    ///     <c>using static;</c> — <c>CS1001: Identifier expected</c> against generated code — and
    ///     <c> System.Math</c> became a text node in the markup, silently, so the author also got
    ///     <c>CS0103</c> about a method that is right there.
    /// </summary>
    [Fact]
    public void A_static_import_survives_into_the_generated_file() {
        const string Source = """
                              @component Greeter
                              @using static System.Math

                              @code {
                                  private double X => Sqrt(4);
                              }

                              <div>@X</div>
                              """;

        Assert.Empty(Errors(Compile(Emit(Source))));
    }

    /// <summary>
    ///     The claim the whole design rests on. The binder resolves no types at all; it can afford
    ///     not to because a wrong expression is reported by Roslyn, against the characters in the
    ///     <c>.vxml</c> rather than against generated code the author has never seen.
    /// </summary>
    [Fact]
    public void A_type_error_in_an_interpolation_is_reported_at_the_expression_in_the_vxml() {
        const string Source = """
                              @component Counter
                              @code { private int _count; }
                              <div class="@_count.Nope" />
                              """;

        var error = Assert.Single(Errors(Compile(Emit(Source))));
        var span = error.Location.GetMappedLineSpan();

        Assert.Equal(Path, span.Path);
        Assert.Equal(2, span.StartLinePosition.Line);

        // Column 20 is `Nope`, not column 13 where the expression starts. That is the mapping
        // working rather than approximating: Roslyn squiggles the member it could not find, and
        // the span directive carries that through to the exact word in the markup.
        Assert.Equal(20, span.StartLinePosition.Character);
    }

    [Fact]
    public void An_unknown_component_is_reported_at_the_tag_name() {
        const string Source = """
                              @component Counter
                              <div>
                                  <Nope />
                              </div>
                              """;

        var error = Assert.Single(Errors(Compile(Emit(Source))));
        var span = error.Location.GetMappedLineSpan();

        Assert.Equal(Path, span.Path);
        Assert.Equal(2, span.StartLinePosition.Line);
        Assert.Equal(5, span.StartLinePosition.Character);
    }

    /// <summary>
    ///     ⚠ <b>Twice, and both at the attribute's name.</b> A quoted value is not necessarily a
    ///     string — <c>Variant="Subtle"</c> is an enum — so the literal goes through
    ///     <c>Literals.Of</c>, whose first argument is the property itself and exists only to be
    ///     inferred from. C# infers nothing from what an expression is assigned to, so the property
    ///     has to be named on both sides of the statement, and a name that does not exist is wrong
    ///     on both. What matters is that every one of them lands on the word the author wrote.
    /// </summary>
    [Fact]
    public void An_unknown_parameter_is_reported_at_the_attribute_name() {
        const string Source = """
                              @component Counter
                              <Label Missing="x" />
                              """;

        var errors = Errors(Compile(Emit(Source)));
        Assert.NotEmpty(errors);

        foreach (var span in errors.Select(error => error.Location.GetMappedLineSpan())) {
            Assert.Equal(Path, span.Path);
            Assert.Equal(1, span.StartLinePosition.Line);
            Assert.Equal(7, span.StartLinePosition.Character);
        }
    }

    /// <summary>
    ///     A quoted value becomes whatever the property it is assigned to is: an enum by member
    ///     name, a number, a flag, or the text itself. Which one is C#'s decision, made from the
    ///     property's type at the use site — the binder resolves no types and is not told.
    /// </summary>
    [Fact]
    public void A_quoted_value_becomes_the_type_the_property_wants() {
        const string Source = """
                              @component Greeter
                              <Dial Mode="Fast" Ratio="0.25" Steps="3" Loud="true" Caption="left" />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        var dial = component.Root.Children.Single();
        var type = dial.GetType();

        Assert.Equal("dial", dial.Tag);
        Assert.Equal("Fast", type.GetProperty("Mode")!.GetValue(dial)!.ToString());
        Assert.Equal(0.25f, type.GetProperty("Ratio")!.GetValue(dial));
        Assert.Equal(3, type.GetProperty("Steps")!.GetValue(dial));
        Assert.Equal(true, type.GetProperty("Loud")!.GetValue(dial));
        Assert.Equal("left", type.GetProperty("Caption")!.GetValue(dial));
    }

    /// <summary>
    ///     ⚠ <b>A component's parameters are assigned before its <c>Build</c> runs, and for years
    ///     they were assigned after it.</b> <c>Child&lt;T&gt;</c> constructs, mounts — which runs
    ///     <c>Build</c> — and returns, so every effect the child had made had already read every
    ///     parameter once at its default. A plain C# property assigned afterwards notifies nobody, so
    ///     <c>&lt;Label Title="Hello" /&gt;</c> drew the empty string for ever and nothing said so;
    ///     signal-backing every prop was the only escape, by convention, with nothing enforcing it.
    /// </summary>
    /// <remarks>
    ///     <c>Label</c> reads <c>Title</c> exactly once, in <c>Build</c>, which is what makes this an
    ///     oracle rather than an observation: the text it produced <i>is</i> the value the property
    ///     held at the moment the child was built, and nothing later can change it.
    /// </remarks>
    [Fact]
    public void A_component_is_built_with_its_parameters_rather_than_after_them() {
        const string Source = """
                              @component Greeter
                              <Label Title="Hello" />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        var label = component.Root.Children.Single();

        Assert.Equal("label", label.Tag);
        Assert.Equal("Hello", label.Children.Single().Text);
    }

    /// <summary>
    ///     The same for a dynamic parameter, whose assignment is an effect rather than a statement —
    ///     so this pins that <c>Bind</c>'s <i>first</i> run also lands before the build, and not
    ///     merely the literal case.
    /// </summary>
    [Fact]
    public void A_bound_parameter_reaches_the_child_before_it_builds_too() {
        const string Source = """
                              @component Greeter
                              @code { private string _greeting = "Hi"; }
                              <Label Title="@_greeting" />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        Assert.Equal("Hi", component.Root.Children.Single().Children.Single().Text);
    }

    /// <summary>
    ///     ⚠ <b>And a capitalised tag that is a <i>control</i> takes the same emitted pair</b>, which
    ///     is the half that could break silently: the markup compiler cannot tell a
    ///     <c>Component</c> from a <c>UiElement</c> and deliberately does not try, so
    ///     <c>Create</c>/<c>Compose</c> has to be legal C# for both and <c>Compose</c> has to do
    ///     nothing at all to an element.
    /// </summary>
    [Fact]
    public void A_control_tag_survives_the_same_split() {
        const string Source = """
                              @component Greeter
                              <Dial Mode="Fast" Steps="3" class="lead" />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        var dial = component.Root.Children.Single();

        Assert.Equal("dial", dial.Tag);
        Assert.Equal(3, dial.GetType().GetProperty("Steps")!.GetValue(dial));
        Assert.True(dial.HasClass("lead"));
    }

    /// <summary>
    ///     ⚠ <b>And a component tag with no parameter keeps emitting the call it always did.</b> The
    ///     split costs a statement and a second pass over the attributes, so it is taken only where
    ///     there is something to assign — which is a minority of the component tags in any file.
    /// </summary>
    [Fact]
    public void A_tag_with_nothing_to_assign_is_not_split() {
        const string Source = """
                              @component Greeter
                              <Label Title="Hello" />
                              <Callout />
                              """;

        var generated = Emit(Source);

        Assert.Equal(1, Occurrences(generated, ".Create<"));
        Assert.Equal(1, Occurrences(generated, ".Compose("));
        Assert.Equal(1, Occurrences(generated, ".Child<"));
    }

    /// <summary>
    ///     ⚠ <b>And a misspelt member is a run-time failure rather than a compile-time one</b>,
    ///     which is the price of the shorthand — so the message says what the value could have been.
    /// </summary>
    [Fact]
    public void A_quoted_value_that_is_not_a_member_says_what_the_members_are() {
        const string Source = """
                              @component Greeter
                              <Dial Mode="Fastt" />
                              """;

        var thrown = Assert.Throws<TargetInvocationException>(() => Run(Source));
        var cause = Assert.IsType<ArgumentException>(thrown.InnerException);

        Assert.Contains("'Fastt' is not a DialMode", cause.Message, StringComparison.Ordinal);
        Assert.Contains("Slow, Fast", cause.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b><c>slot-header</c> moves the subscription onto the control's part.</b>
    ///     <c>slot="header"</c> has given markup a spelling for writing <i>children</i> into a
    ///     part since it landed, and there was none for putting a <i>handler</i> on one — <c>on:</c>
    ///     is an attribute on a tag and a part is not a tag. `ComponentsView` stood eleven lines of
    ///     walking up from <c>args.Source</c> in for it. What the emitter writes is the call the
    ///     hand-written panel made, <c>fold.Header.AddHandler&lt;DragEvent&gt;(…)</c>, and not a
    ///     filter over the source that agrees with it most of the time.
    /// </summary>
    [Fact]
    public void A_slot_modifier_subscribes_to_the_controls_part_rather_than_to_the_control() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui

                              @code {
                                  public System.Collections.Generic.List<string> Seen { get; } = [];
                              }

                              <Foldout on:click.slot-header='@((UiEvent e) => Seen.Add("header"))' />
                              """;

        var (component, instance, document) = Run(Source);

        using var owned = document;
        var foldout = component.Root.Children.Single();
        var header = foldout.Children.Single();
        var seen = (System.Collections.IEnumerable)Property(instance, "Seen");

        Assert.Equal("foldout-header", header.Tag);

        // The part hears it.
        header.Raise(new TapEvent { Count = 1 });
        Assert.Equal(["header"], seen.Cast<string>());

        // ⚠ And the control does not, which is the half a subscription on the control would pass
        // anyway: an event raised on the foldout itself never reaches a handler on a child of it,
        // so this is the assertion that fails when the modifier is ignored.
        foldout.Raise(new TapEvent { Count = 1 });
        Assert.Equal(["header"], seen.Cast<string>());
    }

    /// <summary>A control that publishes no such part says so, naming both.</summary>
    [Fact]
    public void A_slot_modifier_naming_a_part_the_control_does_not_have_says_which() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui

                              <Foldout on:click.slot-footer="@((UiEvent e) => { })" />
                              """;

        var thrown = Assert.Throws<TargetInvocationException>(() => Run(Source));
        var cause = Assert.IsType<InvalidOperationException>(thrown.InnerException);

        Assert.Contains("no slot named 'footer'", cause.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tag_directive_names_the_components_host_element() {
        const string Source = """
                              @component Greeter
                              @tag task-center
                              <div />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        Assert.Equal("task-center", component.Root.Tag);
    }

    [Fact]
    public void An_error_in_a_code_block_is_reported_at_the_line_that_wrote_it() {
        const string Source = """
                              @component Counter
                              @code {
                                  private int _n = "not an int";
                              }
                              <div />
                              """;

        var error = Assert.Single(Errors(Compile(Emit(Source))));
        var span = error.Location.GetMappedLineSpan();

        Assert.Equal(Path, span.Path);
        Assert.Equal(2, span.StartLinePosition.Line);
    }

    /// <summary>
    ///     An <c>@if</c> chain and an <c>@switch</c> emit the same runtime primitive: one selector
    ///     that says which arm is live, one builder that constructs it.
    /// </summary>
    [Fact]
    public void Control_flow_emits_one_switch_primitive_for_both_shapes() {
        var emitted = Emit(Counter);

        Assert.Equal(2, Occurrences(emitted, ".Switch("));
        Assert.Contains(".For(", emitted, StringComparison.Ordinal);
        Assert.Contains(".Slot(", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_static_attribute_is_a_plain_call_and_a_dynamic_one_is_an_effect() {
        var emitted = Emit(Counter);

        Assert.Contains(".Attribute(n0, \"class\", \"row\");", emitted, StringComparison.Ordinal);
        Assert.Contains(".Bind(n1, \"class\", () => string.Concat(", emitted, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A binding's dots ride to the runtime as event names, where an event's dots are eaten
    ///     as filters.</b> They are trailing arguments in both cases and mean opposite things: on an
    ///     <c>on:</c> they qualify a subscription this side understands, and on a <c>bind:</c> they
    ///     say <i>when</i> the write-back happens, which only the runtime's table can resolve. A
    ///     binding with none of them keeps the call it always emitted.
    /// </summary>
    [Fact]
    public void A_bindings_commit_events_are_emitted_as_names_and_no_events_emits_the_old_call() {
        var emitted = Emit(
            """
            @component Form
            @using Vixen.Ui.Reactive

            @code {
                private readonly Signal<string?> _query = new(null);
                private readonly Signal<string?> _live = new(null);
            }

            <search-box bind:Value.submit.blur="@_query.Value" />
            <search-box bind:Value="@_live.Value" />
            """
        );

        Assert.Contains("= __v, \"submit\", \"blur\");", emitted, StringComparison.Ordinal);
        Assert.Contains("= __v);", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scoped_style_block_reaches_the_generated_class() {
        var emitted = Emit(Counter);

        Assert.Contains("protected override string? Style => \".row { display: flex; }\";", emitted, StringComparison.Ordinal);
        Assert.Contains("protected override bool StyleIsScoped => true;", emitted, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Without a key the loop falls back to the item's own identity, not to its index. An index
    ///     would make every element after an insertion compare unequal, which is the failure
    ///     VXML2004 warns about — a fallback that quietly did it would make the warning a lie.
    /// </summary>
    [Fact]
    public void A_keyless_loop_falls_back_to_identity_rather_than_to_the_index() {
        var emitted = Emit("@component Counter\n@for (var i in xs) { <p>@i</p> }");
        Assert.Contains("static i => i!,", emitted, StringComparison.Ordinal);
    }

    // ================================================================== Running it

    const string Greeter = """
                           @component Greeter
                           @using Vixen.Ui.Reactive

                           @code {
                               public Signal<int> Count { get; } = new(0);
                               public Signal<string[]> Items { get; } = new([]);
                           }

                           <div class="root">
                               <span>Count: @Count.Value</span>

                               @if (Count.Value > 0) {
                                   <em>positive</em>
                               }

                               @for (var item in Items.Value) {
                                   <li key="@item">@item</li>
                               }
                           </div>
                           """;

    /// <summary>
    ///     The end of the chain: markup to a syntax tree to a component model to C# to IL to an
    ///     element tree that reacts to a signal. Everything before this proves a stage; this proves
    ///     they compose.
    /// </summary>
    [Fact]
    public void A_compiled_component_builds_a_tree_and_follows_its_signals() {
        var (component, instance, document) = Run(Greeter);

        using var owned = document;
        var root = component.Root.Children.Single();
        var span = root.Children[0];

        document.Effects.Flush();
        Assert.Equal("div", root.Tag);
        Assert.True(root.HasClass("root"));
        Assert.Equal(["Count: ", "0"], span.Children.Select(child => child.Text));

        // A signal write reaches exactly the effect that reads it.
        Count(instance).Value = 3;
        document.Effects.Flush();
        Assert.Equal(["Count: ", "3"], span.Children.Select(child => child.Text));

        // ...and the branch it gates appears, in its place among the siblings.
        Assert.Equal(["span", "em"], root.Children.Select(child => child.Tag));

        Count(instance).Value = 0;
        document.Effects.Flush();
        Assert.Equal(["span"], root.Children.Select(child => child.Tag));
    }

    [Fact]
    public void A_compiled_loop_keeps_the_elements_of_the_items_that_survive() {
        var (component, instance, document) = Run(Greeter);

        using var owned = document;
        var root = component.Root.Children.Single();

        Items(instance).Value = ["a", "b"];
        document.Effects.Flush();

        var b = root.Children.Single(child => child.Tag == "li" && Text(child) == "b");

        Items(instance).Value = ["b", "a"];
        document.Effects.Flush();

        Assert.Same(b, root.Children.Single(child => child.Tag == "li" && Text(child) == "b"));
        Assert.Equal(["b", "a"], root.Children.Where(child => child.Tag == "li").Select(Text));
    }

    /// <summary>
    ///     ⚠ <b><c>class</c> is a set, and it is not the element's whole set.</b> A control gives
    ///     itself <c>variant-default</c> and <c>size-md</c> in <c>OnCreated</c>, before any markup
    ///     attribute is applied, and a <c>class</c> that replaced everything it found deleted both
    ///     — so <c>&lt;Button class="history-entry" Variant="Subtle" /&gt;</c> got its variant back
    ///     from the assignment that followed and silently lost its size. The scope class of a
    ///     <c>&lt;style scoped&gt;</c> went the same way.
    /// </summary>
    [Fact]
    public void A_class_on_a_control_tag_leaves_the_classes_the_control_gave_itself() {
        const string Source = """
                              @component Greeter
                              <Gauge class="history-entry" />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        var gauge = component.Root.Children.Single();

        Assert.True(gauge.HasClass("history-entry"));
        Assert.True(gauge.HasClass("size-md"));
        Assert.True(gauge.HasClass("variant-default"));
    }

    /// <summary>
    ///     And it still replaces what it wrote, which is the reason it does not simply append:
    ///     <c>class="tile @Kind"</c> whose expression changes must not leave the old value behind.
    /// </summary>
    [Fact]
    public void A_class_that_changes_takes_back_only_what_it_wrote() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive

                              @code { public Signal<string> Kind { get; } = new("warm"); }

                              <Gauge class="tile @Kind.Value" />
                              """;

        var (component, instance, document) = Run(Source);

        using var owned = document;
        var gauge = component.Root.Children.Single();

        document.Effects.Flush();
        Assert.True(gauge.HasClass("warm"));

        ((Signal<string>)Property(instance, "Kind")).Value = "cold";
        document.Effects.Flush();

        Assert.False(gauge.HasClass("warm"));
        Assert.True(gauge.HasClass("cold"));
        Assert.True(gauge.HasClass("tile"));
        Assert.True(gauge.HasClass("size-md"));
    }

    /// <summary>
    ///     ⚠ <b><c>style</c> is a cascade origin and not an attribute.</b> Before this it reached
    ///     <c>StyleTree.SetAttribute</c>, so <c>style="width: 42%"</c> put a string in the selector
    ///     engine's arena where <c>[style]</c> could match it and nothing else could read it. The
    ///     element came out however wide the stylesheet said, with no diagnostic — the panel that hit
    ///     it moved onto a <c>ProgressBar</c> instead.
    /// </summary>
    [Fact]
    public void An_inline_style_reaches_the_cascade_rather_than_the_selector_arena() {
        const string Source = """
                              @component Greeter
                              <div style="width: 42px; height: 7px" />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        var box = component.Root.Children.Single();

        document.Update();

        Assert.Equal("42px", box.GetStyle("width"));
        Assert.Equal(42f, box.Width, 0.001f);
        Assert.Equal(7f, box.Height, 0.001f);

        // And it is not also data to match on, which is what it used to be and only that.
        Assert.Null(box.Attribute("style"));
    }

    /// <summary>A bound value is the whole point: a splitter's ratio is not a rule anyone can write.</summary>
    [Fact]
    public void An_inline_style_follows_a_signal() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive

                              @code { public Signal<int> Wide { get; } = new(20); }

                              <div style="width: @(Wide.Value)px" />
                              """;

        var (component, instance, document) = Run(Source);

        using var owned = document;
        var box = component.Root.Children.Single();

        document.Update();
        Assert.Equal(20f, box.Width, 0.001f);

        ((Signal<int>)Property(instance, "Wide")).Value = 61;
        document.Update();

        Assert.Equal(61f, box.Width, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b>It takes back the properties it wrote and no others</b>, which is
    ///     <c>class</c>'s rule and matters more here. A control writes inline declarations from its
    ///     own code — a <c>DataGrid</c> row's <c>top</c>, a <c>Selects</c> popup's
    ///     <c>min-width</c> — and an attribute that owned the element's whole inline set would
    ///     silently unposition every one of them.
    /// </summary>
    [Fact]
    public void An_inline_style_that_changes_leaves_what_the_control_wrote_itself() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive

                              @code { public Signal<string> Shape { get; } = new("width: 20px"); }

                              <Marker style="@Shape.Value" />
                              """;

        var (component, instance, document) = Run(Source);

        using var owned = document;
        var marker = component.Root.Children.Single();

        document.Update();
        Assert.Equal("20px", marker.GetStyle("width"));
        Assert.Equal("5px", marker.GetStyle("top"));

        ((Signal<string>)Property(instance, "Shape")).Value = "height: 9px";
        document.Update();

        // The property the attribute stopped naming is gone…
        Assert.Null(marker.GetStyle("width"));
        Assert.Equal("9px", marker.GetStyle("height"));

        // …and the one the control wrote is not the attribute's to take.
        Assert.Equal("5px", marker.GetStyle("top"));
    }

    /// <summary>
    ///     A shorthand is expanded by the same parser a rule body goes through, which is the whole
    ///     reason the attribute is handed to ExCSS rather than split on <c>;</c> and <c>:</c>.
    /// </summary>
    [Fact]
    public void An_inline_shorthand_becomes_the_longhands_the_layout_reads() {
        const string Source = """
                              @component Greeter
                              <div style="padding: 4px 8px" />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        var box = component.Root.Children.Single();

        document.Update();

        Assert.Equal("4px", box.GetStyle("padding-top"));
        Assert.Equal("8px", box.GetStyle("padding-right"));
        Assert.Equal("4px", box.GetStyle("padding-bottom"));
        Assert.Equal("8px", box.GetStyle("padding-left"));
    }

    /// <summary>
    ///     ⚠ <b>A brace is refused rather than escaped.</b> The declarations are parsed by wrapping
    ///     them in a throwaway rule, so a value carrying one would otherwise close the wrapper and
    ///     load rules against the whole document.
    /// </summary>
    [Fact]
    public void An_inline_style_carrying_a_brace_is_refused_and_writes_nothing() {
        const string Source = """
                              @component Greeter
                              <div style="} div { width: 300px" />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        var box = component.Root.Children.Single();

        document.Update();

        Assert.False(box.HasInlineStyle);
        Assert.Contains(
            document.Styles.Loader.Diagnostics,
            diagnostic => diagnostic.Reason.Contains("brace", StringComparison.Ordinal)
        );
    }

    // ================================================================== tag, and use

    /// <summary>
    ///     ⚠ <b>What <c>@tag</c> could not say, because <c>@tag</c> is a header.</b> A control's
    ///     element name comes from its type, so <c>Part&lt;ScrollView&gt;("add-component-list")</c> —
    ///     a control under the tag a stylesheet names — had no markup spelling and no way to be
    ///     subclassed into one, <c>ScrollView</c> being sealed. The runtime always took the tag:
    ///     <c>UiDocument.Adopt</c> only falls back to <see cref="UiElement.TagName" />.
    /// </summary>
    [Fact]
    public void A_tag_attribute_renames_what_a_capitalised_tag_creates() {
        const string Source = """
                              @component Greeter
                              <Gauge tag="add-component-list" />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        var gauge = component.Root.Children.Single();

        Assert.Equal("add-component-list", gauge.Tag);

        // And it is still the control it was. A rename is not a downgrade to a plain element: the
        // classes it gave itself in `OnCreated` are the evidence, because those run before any
        // markup attribute is applied and a wrong `Child<T>` overload would have skipped them.
        Assert.True(gauge.HasClass("variant-default"));
        Assert.True(gauge.HasClass("size-md"));
    }

    /// <summary>
    ///     The other half: a component's host element, which is what makes "the same part under
    ///     another name" sayable. <c>WaterFacts</c> is a second type for the want of this.
    /// </summary>
    [Fact]
    public void A_tag_attribute_renames_a_components_host_element_too() {
        const string Source = """
                              @component Greeter
                              <Callout tag="water-facts" />
                              """;

        var (component, _, document) = Run(Source);

        using var owned = document;
        var host = component.Root.Children.Single();

        Assert.Equal("water-facts", host.Tag);

        // What the component built is still inside it, which is the thing a wrong host would break.
        Assert.Equal(["callout-body"], host.Children.Select(child => child.Tag));
    }

    /// <summary>
    ///     ⚠ <b>Computed, and read exactly once.</b> Wave 6 found two panels choosing a tag from the
    ///     data — <c>query-row-selected</c>, <c>agent-row-live</c> — and had to smuggle the flag into
    ///     the <c>key</c> because a tag could not be written at all. It can now, and the key is still
    ///     what decides: a tag is interned into the style node when the element is made, so a
    ///     surviving row keeps the tag it was born with and only a changed key makes a new one.
    /// </summary>
    [Fact]
    public void A_computed_tag_is_read_when_the_element_is_made_and_never_again() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive

                              @code {
                                  public Signal<string> Flavour { get; } = new("live");
                              }

                              <div>
                                  @for (var row in new[] { 1 }) {
                                      <Gauge key="@(row, Flavour.Value)" tag="@("row-" + Flavour.Value)" />
                                  }
                              </div>
                              """;

        var (component, instance, document) = Run(Source);

        using var owned = document;
        var root = component.Root.Children.Single();

        document.Effects.Flush();
        Assert.Equal(["row-live"], root.Children.Select(child => child.Tag));

        // The key carries the flavour, so the row is a different row and gets a new element.
        ((Signal<string>)Property(instance, "Flavour")).Value = "stale";
        document.Effects.Flush();
        Assert.Equal(["row-stale"], root.Children.Select(child => child.Tag));
    }

    /// <summary>
    ///     ⚠ <b>The ledger's sixth shape, closed without unsealing anything.</b> A control fed by a
    ///     <i>method</i> has no property for a parameter to assign, and the sanctioned escape —
    ///     a four-line subclass exposing the call as a property — is what <c>sealed</c> refuses.
    ///     <c>use</c> is that subclass without the type: an <c>Action&lt;T&gt;</c> run as an effect,
    ///     so the control is re-fed whenever what the expression read changes.
    /// </summary>
    [Fact]
    public void A_sealed_control_fed_by_a_method_is_reachable_from_markup() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive

                              @code {
                                  public Signal<string> Subject { get; } = new("transform");
                              }

                              <Roster use="@(view => view.Inspect(Subject.Value, 2))" />
                              """;

        var (component, instance, document) = Run(Source);

        using var owned = document;
        var roster = component.Root.Children.Single();
        var inspections = roster.GetType().GetProperty("Inspections")!;

        document.Effects.Flush();
        Assert.Equal("roster", roster.Tag);
        Assert.Equal("transform:2", roster.Text);
        Assert.Equal(1, inspections.GetValue(roster));

        // ⚠ The half that makes it worth having. A one-shot initialiser would leave the panel
        // showing the first thing it was ever pointed at, which is the defect `Restate` exists to
        // paper over everywhere this pattern is written by hand.
        ((Signal<string>)Property(instance, "Subject")).Value = "renderer";
        document.Effects.Flush();

        Assert.Equal("renderer:2", roster.Text);
        Assert.Equal(2, inspections.GetValue(roster));
    }

    /// <summary>
    ///     ⚠ <b>And shape 5 falls out of it.</b> An interpolation is a <c>text</c> <i>child</i> and
    ///     an attribute on an intrinsic tag goes to the selector arena, so an element's own
    ///     <c>Text</c> had no markup spelling — <c>&lt;fact-name Text="@Name" /&gt;</c> silently does
    ///     nothing and <c>&lt;fact-name&gt;@Name&lt;/fact-name&gt;</c> adds a box. Nine subclasses
    ///     were written in one panel for this. A <c>use</c> writes the property itself, and the
    ///     element has no children.
    /// </summary>
    [Fact]
    public void A_use_writes_an_intrinsic_elements_own_text_with_no_extra_box() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive

                              @code {
                                  public Signal<string> Caption { get; } = new("Terrains to carve");
                              }

                              <fact-name use="@(cell => cell.Text = Caption.Value)" />
                              """;

        var (component, instance, document) = Run(Source);

        using var owned = document;
        var cell = component.Root.Children.Single();

        document.Effects.Flush();
        Assert.Equal("Terrains to carve", cell.Text);
        Assert.Empty(cell.Children);

        ((Signal<string>)Property(instance, "Caption")).Value = "Zones";
        document.Effects.Flush();

        Assert.Equal("Zones", cell.Text);
        Assert.Empty(cell.Children);
    }

    /// <summary>
    ///     A <c>use</c> is an ordinary effect, so it belongs to the region that declared it: an arm
    ///     that leaves takes it with it. Without that a <c>use</c> inside an <c>@if</c> would go on
    ///     feeding an element that is no longer in the tree — the failure regions exist to prevent,
    ///     and the one a hand-written subscription in <c>OnComposed</c> actually has.
    /// </summary>
    [Fact]
    public void A_use_leaves_with_the_branch_that_declared_it() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive

                              @code {
                                  public Signal<bool> Shown { get; } = new(true);
                                  public Signal<string> Subject { get; } = new("a");
                              }

                              <div>
                                  @if (Shown.Value) {
                                      <Roster use="@(view => view.Inspect(Subject.Value, 1))" />
                                  }
                              </div>
                              """;

        var (component, instance, document) = Run(Source);

        using var owned = document;
        var root = component.Root.Children.Single();

        document.Effects.Flush();
        var roster = root.Children.Single();
        var inspections = roster.GetType().GetProperty("Inspections")!;
        Assert.Equal(1, inspections.GetValue(roster));

        ((Signal<bool>)Property(instance, "Shown")).Value = false;
        document.Effects.Flush();
        Assert.Empty(root.Children);

        // The element is gone; the effect that fed it must be too.
        ((Signal<string>)Property(instance, "Subject")).Value = "b";
        document.Effects.Flush();
        Assert.Equal(1, inspections.GetValue(roster));
    }

    static string Text(UiElement element) => element.Children.Single().Text ?? string.Empty;

    static Signal<int> Count(object instance) => (Signal<int>)Property(instance, "Count");

    static Signal<string[]> Items(object instance) => (Signal<string[]>)Property(instance, "Items");

    static object Property(object instance, string name) =>
        instance.GetType().GetProperty(name)!.GetValue(instance)!;

    /// <summary>A property or a field, because markup puts <c>ref</c> targets in both.</summary>
    static object? Member(object instance, string name) =>
        instance.GetType().GetProperty(name) is { } property
            ? property.GetValue(instance)
            : instance.GetType().GetField(name)!.GetValue(instance);

    /// <summary>Emits, compiles and loads one generated type.</summary>
    static Type Load(string source, string name) {
        var compilation = Compile(Emit(source));
        Assert.Empty(Errors(compilation));

        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success);

        return Assembly.Load(image.ToArray()).GetType(name)!;
    }

    /// <summary>Emits, compiles, loads and builds — the whole pipeline, end to end.</summary>
    static (Component Component, object Instance, UiDocument Document) Run(string source) {
        var type = Load(source, "Greeter");
        var document = new UiDocument(400f, 400f);

        var built = typeof(BuildContext)
            .GetMethod(nameof(BuildContext.Build))!
            .MakeGenericMethod(type)
            .Invoke(null, [document, document.Root])!;

        return ((Component)built, built, document);
    }

    // ================================================================== The @for key rule

    /// <summary>
    ///     One component, two keys. <c>Stable</c> keys each row on a field that does not change and
    ///     <c>Whole</c> keys it on the record, which for immutable data is its value.
    /// </summary>
    const string Rows = """
                        @component Greeter
                        @using Vixen.Ui.Reactive

                        @code {
                            public record struct Row(string Label, int Count);
                            public Signal<Row[]> Items { get; } = new([]);
                        }

                        <stable>
                            @for (var row in Items.Value) {
                                <li key="@(row.Label, 0)">@row.Count</li>
                            }
                        </stable>

                        <whole>
                            @for (var row in Items.Value) {
                                <li key="@row">@row.Count</li>
                            }
                        </whole>
                        """;

    /// <summary>
    ///     ⚠ <b>The sabotage: a row keyed on a stable field of immutable data shows the first
    ///     reading for ever.</b> <c>BuildContext.For</c> matches the key, <i>reuses the region and
    ///     does not re-run the body</i> — so every per-item binding stays closed over the item as it
    ///     was when that key first appeared. Which is the exact opposite of what <c>VXML2004</c>
    ///     teaches a reader: it warns against keying on the index, from which the natural conclusion
    ///     is that any stable field is safe.
    /// </summary>
    /// <remarks>
    ///     The stable key is written as a tuple so that <c>VXML2011</c> does not fire on the half of
    ///     this test that is deliberately wrong — the warning's job is to stop a reader writing
    ///     <c>key="@row.Label"</c>, and its job here would be to stop the test being written at all.
    /// </remarks>
    [Fact]
    public void A_row_keyed_on_a_stable_field_of_immutable_data_freezes_at_the_first_reading() {
        var (component, instance, document) = Run(Rows);

        using var owned = document;
        var stable = component.Root.Children[0];
        var whole = component.Root.Children[1];
        var items = instance.GetType().GetProperty("Items")!;
        var row = instance.GetType().GetNestedType("Row")!;

        void Show(int count) {
            var array = Array.CreateInstance(row, 1);
            array.SetValue(Activator.CreateInstance(row, "cpu", count), 0);

            items.GetValue(instance)!.GetType().GetProperty("Value")!.SetValue(items.GetValue(instance), array);
            document.Effects.Flush();
        }

        Show(1);
        Assert.Equal(["1"], stable.Children.Select(Text));
        Assert.Equal(["1"], whole.Children.Select(Text));

        Show(2);

        // The key survived, so the region did, so the body never ran again.
        Assert.Equal(["1"], stable.Children.Select(Text));
        Assert.Equal(["2"], whole.Children.Select(Text));
    }

    // ================================================================== @inherits

    /// <summary>
    ///     A <c>.vxml</c> that names a base, and the whole point of it: the class is a
    ///     <see cref="UiElement" />, so a caller adds it with <c>Add&lt;T&gt;</c>, holds it as its own
    ///     type and finds it by walking the tree — none of which a <see cref="Component" /> can be
    ///     made to do, because a component is not in the tree at all.
    /// </summary>
    const string Meter = """
                         @component Meter
                         @inherits Panel
                         @tag meter
                         @using Vixen.Ui.Reactive

                         @code {
                             public Signal<int> Count { get; } = new(0);
                             public Signal<string[]> Items { get; } = new([]);
                             public Vixen.Ui.UiElement Body = null!;
                             public int Composed;
                             public int Runs;

                             // Counted rather than inferred: an effect that outlives its element is
                             // only visible as an effect that *ran*, and the element it would have
                             // written to may not complain.
                             public int Read() { Runs++; return Count.Value; }

                             // The same, from inside a loop body — whose effects are tracked on a
                             // region opened against the *nested* element the loop is in, not on the
                             // host's. Whether disposal reaches those is a different question.
                             // Reads `Count` so the effect depends on it, and returns the item alone
                             // so the row's text stays the item — the reconciliation test above
                             // identifies rows by it.
                             public int RowRuns;
                             public string Row(string item) { RowRuns++; _ = Count.Value; return item; }

                             partial void OnComposed() => Composed++;
                         }

                         <meter-body ref="@Body">
                             <span>Count: @Read()</span>

                             @if (Count.Value > 0) {
                                 <em>positive</em>
                             }

                             @for (var item in Items.Value) {
                                 <li key="@item">@Row(item)</li>
                             }
                         </meter-body>
                         """;

    [Fact]
    public void An_inherits_component_is_an_element_a_caller_can_add_and_find() {
        using var document = new UiDocument(400f, 400f);
        var meter = Add(document, Meter);

        // The three things a component could not do, in order: it is a `UiElement`; `Add<T>` made
        // it, so its own type is what the caller holds; and a walk of the tree reaches it.
        Assert.IsAssignableFrom<UiElement>(meter);
        Assert.Equal("meter", ((UiElement)meter).Tag);
        Assert.Same(meter, Descendants(document.Root).Single(child => child.Tag == "meter"));
    }

    /// <summary>
    ///     ⚠ <b>The claim the choice of <c>@inherits</c> over a wider <c>Descendants</c> rests
    ///     on.</b> An element-flavoured class gets the <i>same</i> <c>BuildContext</c>, so it gets
    ///     the same effects, the same <c>@if</c> primitive and the same keyed <c>@for</c>
    ///     reconciliation. If any of that were weaker the markup would be a worse way to write the
    ///     imperative code it replaced.
    /// </summary>
    [Fact]
    public void An_inherits_component_gets_every_reactive_property_a_component_does() {
        using var document = new UiDocument(400f, 400f);
        var meter = Add(document, Meter);
        var body = (UiElement)Member(meter, "Body")!;

        document.Effects.Flush();
        Assert.Equal(["Count: ", "0"], body.Children[0].Children.Select(child => child.Text));

        ((Signal<int>)Property(meter, "Count")).Value = 3;
        document.Effects.Flush();

        Assert.Equal(["Count: ", "3"], body.Children[0].Children.Select(child => child.Text));
        Assert.Equal(["span", "em"], body.Children.Select(child => child.Tag));

        var items = (Signal<string[]>)Property(meter, "Items");
        items.Value = ["a", "b"];
        document.Effects.Flush();

        var b = body.Children.Single(child => child.Tag == "li" && Text(child) == "b");

        items.Value = ["b", "a"];
        document.Effects.Flush();

        // Keyed reconciliation, which is the one thing an imperative rebuild cannot do by accident.
        Assert.Same(b, body.Children.Single(child => child.Tag == "li" && Text(child) == "b"));
        Assert.Equal(["b", "a"], body.Children.Where(child => child.Tag == "li").Select(Text));
    }

    /// <summary>
    ///     An effect that outlived its element would keep assigning to it and keep it alive through
    ///     its closure. A component's are disposed by the region its host hangs from; an element's
    ///     have no such region above them, so the generated <c>OnRemoved</c> stops them.
    /// </summary>
    [Fact]
    public void An_inherits_component_stops_its_effects_when_it_leaves_the_tree() {
        using var document = new UiDocument(400f, 400f);
        var meter = Add(document, Meter);
        var body = (UiElement)Member(meter, "Body")!;

        // A write the effect does see, so that the count below is a difference rather than a zero.
        ((Signal<int>)Property(meter, "Count")).Value = 1;
        document.Effects.Flush();

        var ran = (int)Member(meter, "Runs")!;
        Assert.True(ran > 0);

        ((UiElement)meter).Remove();

        // The base's own hook still ran, so the generated override chained rather than replaced it.
        Assert.Equal(1, (int)Member(meter, "Removals")!);
        Assert.True(body.IsRemoved);

        // ⚠ Counted, not inferred from an exception. The first version of this test wrote the signal
        // and asserted that flushing did not throw — which passed with the disposal deleted, because
        // assigning `Text` to a removed element happens not to complain. What an undisposed effect
        // actually does is *run*, holding its elements alive through its closure, so that is what is
        // measured.
        ((Signal<int>)Property(meter, "Count")).Value = 9;
        document.Effects.Flush();

        Assert.Equal(ran, (int)Member(meter, "Runs")!);
    }

    /// <summary>
    ///     And the effects inside an <c>@for</c> body, which are tracked somewhere else entirely.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A loop's per-item effects hang off the region of the element the loop is
    ///     <i>in</i>, not off the host's</b> — <c>BuildContext.Open</c> takes the parent it was given
    ///     — so a disposal that only walked the host's region would stop the loop from reconciling
    ///     and leave every row's bindings running against removed elements. That is the shape of leak
    ///     regions exist to prevent, and it is worth an assertion of its own rather than an
    ///     assumption that one region tree covers the other.
    /// </remarks>
    [Fact]
    public void An_inherits_component_stops_the_effects_inside_its_loops_too() {
        using var document = new UiDocument(400f, 400f);
        var meter = Add(document, Meter);

        ((Signal<string[]>)Property(meter, "Items")).Value = ["a", "b"];
        ((Signal<int>)Property(meter, "Count")).Value = 1;
        document.Effects.Flush();

        var ran = (int)Member(meter, "RowRuns")!;
        Assert.True(ran >= 2);

        ((UiElement)meter).Remove();

        ((Signal<int>)Property(meter, "Count")).Value = 9;
        document.Effects.Flush();

        Assert.Equal(ran, (int)Member(meter, "RowRuns")!);
    }

    /// <summary>
    ///     ⚠ <b>And the <c>Component</c> flavour, which used to be the gap and is not.</b> Two
    ///     things were wrong and this one test needed both fixed. A region hangs off the element its
    ///     content has as a parent, so the loop's region is keyed on the <c>&lt;body&gt;</c> and
    ///     nothing above pointed at it; and a component built onto a mount has no region above it at
    ///     all, so removing its host ended nothing. <c>BuildContext.RegionOf</c> links what it opens
    ///     into the region being built, and <c>UiDocument</c> announces a host's removal to whatever
    ///     mounted there.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Written against <c>Root.Remove</c> deliberately, because that is how a panel
    ///     actually goes.</b> <c>InspectorView.Rebuild</c> removes the body's children on every
    ///     selection change; nothing clears a region, and before the fix the assertion below was
    ///     <c>NotEqual</c>.
    /// </remarks>
    [Fact]
    public void A_component_stops_the_effects_inside_a_nested_loop_when_its_host_is_removed() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive

                              @code {
                                  public Signal<string[]> Items { get; } = new([]);
                                  public Signal<int> Count { get; } = new(0);
                                  public int RowRuns;
                                  public string Row(string item) { RowRuns++; _ = Count.Value; return item; }
                              }

                              <body>
                                  @for (var item in Items.Value) {
                                      <li key="@item">@Row(item)</li>
                                  }
                              </body>
                              """;

        var (component, instance, document) = Run(Source);

        using var owned = document;
        ((Signal<string[]>)Property(instance, "Items")).Value = ["a", "b"];
        document.Effects.Flush();

        var ran = (int)Member(instance, "RowRuns")!;
        Assert.True(ran >= 2);

        component.Root.Remove();

        ((Signal<int>)Property(instance, "Count")).Value = 9;
        document.Effects.Flush();

        Assert.Equal(ran, (int)Member(instance, "RowRuns")!);
    }

    [Fact]
    public void The_composed_hook_runs_once_the_whole_body_is_built() {
        using var document = new UiDocument(400f, 400f);
        var meter = Add(document, Meter);

        Assert.Equal(1, (int)Member(meter, "Composed")!);
        Assert.NotNull(Member(meter, "Body"));
    }

    /// <summary>
    ///     ⚠ <b>An unknown base is Roslyn's error, on the characters after <c>@inherits</c>.</b>
    ///     Nothing here resolves a type — the emitter writes the name where a base type goes under a
    ///     <c>#line</c>, which is the same bargain the tag name is emitted under.
    /// </summary>
    [Fact]
    public void An_unknown_base_is_reported_at_the_inherits_directive() {
        const string Source = """
                              @component Counter
                              @inherits Nope
                              <div />
                              """;

        // The first, because a base that does not resolve is also a base the scaffold's own calls
        // cannot be checked against — one real mistake and a cascade behind it.
        var span = Errors(Compile(Emit(Source)))[0].Location.GetMappedLineSpan();

        Assert.Equal(Path, span.Path);
        Assert.Equal(1, span.StartLinePosition.Line);
        Assert.Equal(10, span.StartLinePosition.Character);
    }

    /// <summary>
    ///     The other half of the claim, and the reason <c>@inherits</c> exists: without it the same
    ///     call site does not compile, and a walk of the tree does not reach the object.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Asserted rather than assumed, because it is the whole argument.</b> The rejected
    ///     alternative to this header was widening <c>Add&lt;T&gt;</c> and <c>Descendants</c> to see
    ///     components — and the first half of that cannot be written at all: <c>Add&lt;T&gt;</c> is
    ///     <c>where T : UiElement, new()</c>, and a second overload differing only in its constraint
    ///     is CS0695. What the tree does hold is the component's <i>host</i>, which is why
    ///     <c>UiDocument.ComponentAt</c> is the join every consumer of a ported panel had to learn.
    /// </remarks>
    [Fact]
    public void A_component_cannot_be_added_as_an_element_and_is_only_reachable_through_its_host() {
        const string Caller = """
                              public static class Adds {
                                  public static object Make(Vixen.Ui.UiElement parent) => Element<Callout>(parent);

                                  static T Element<T>(Vixen.Ui.UiElement parent)
                                      where T : Vixen.Ui.UiElement, new() => parent.Add<T>(null, null, default);
                              }
                              """;

        // `Callout` is a `Component` in the fixture, so it fails the constraint — at the type
        // argument, which is exactly where a ported panel's callers failed.
        var errors = Errors(Compile(Emit("@component Greeter\n<div />"), Caller));
        Assert.Contains(errors, error => error.Id is "CS0311" or "CS0315" or "CS0453");

        // And the walk: the host element is in the tree under the component's tag, the component is
        // not in the tree at all, and `ComponentAt` is the only way across.
        var (component, _, document) = Run("@component Greeter\n<div />");

        using var owned = document;
        var host = Descendants(document.Root).Single(child => child.Tag == "greeter");

        Assert.Same(component.Root, host);
        Assert.Same(component, document.ComponentAt(host));
        Assert.DoesNotContain(Descendants(document.Root), child => ReferenceEquals(child, component));
    }

    /// <summary>
    ///     The two things a <c>UiElement</c> answers differently from a <c>Component</c>, and both
    ///     have to keep working rather than merely compile.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>&lt;slot /&gt;</c> becomes <c>ContentHost</c>.</b> A component is handed its
    ///     content by a dictionary <c>Component.Declare</c> fills; an element has one place for it
    ///     and a virtual property that names it — which is what <c>BuildContext.Inner(UiElement)</c>
    ///     already reads for every control in the library.
    ///
    ///     ⚠ And <c>&lt;style scoped&gt;</c> loads from the generated <c>OnCreated</c>, because a
    ///     <c>UiElement</c> has no <c>Style</c> property for <c>Component.Mount</c> to read. The
    ///     scope class has to reach the elements the body made, not just the host: a sheet welded to
    ///     a class nothing carries is a sheet that matches nothing.
    /// </remarks>
    [Fact]
    public void An_inherits_component_projects_content_through_ContentHost_and_scopes_its_styles() {
        const string Source = """
                              @component Meter
                              @inherits Panel
                              <meter-body>
                                  <slot />
                              </meter-body>
                              <style scoped>.row { display: flex; }</style>
                              """;

        using var document = new UiDocument(400f, 400f);
        var meter = (UiElement)Add(document, Source);
        var body = meter.Children.Single();

        Assert.Equal("slot", BuildContext.Inner(meter).Tag);
        Assert.Same(body.Children.Single(), BuildContext.Inner(meter));

        var scope = ScopedStyles.ScopeOf(meter.GetType());
        Assert.True(meter.HasClass(scope));
        Assert.True(body.HasClass(scope));
    }

    /// <summary>
    ///     A named slot needs a component. An element has one place for content because
    ///     <c>ContentHost</c> is one property, and a second name would be an element nothing can
    ///     address — a hole in the tree that looks like a feature.
    /// </summary>
    [Fact]
    public void A_named_slot_in_an_inherits_component_is_refused() {
        var component = Markup.Binding.Binder.Bind(
            Markup.Syntax.SyntaxTree.ParseText(
                "@component Meter\n@inherits Panel\n<slot name=\"footer\" />",
                Path
            ),
            out var diagnostics
        );

        Assert.NotNull(component);
        Assert.Contains("VXML2012", diagnostics.Select(d => d.Descriptor.Id));
    }

    // ================================================================== ref

    /// <summary>
    ///     <c>ref</c> on a capitalised tag hands back the <i>component</i>, not the element it drew
    ///     — which is the opposite of what <c>class</c> and <c>on:</c> do, and is right for the same
    ///     reason: what a caller holds a component for is its methods.
    /// </summary>
    [Fact]
    public void A_ref_hands_back_whatever_the_tag_named() {
        const string Source = """
                              @component Greeter
                              @code {
                                  public Vixen.Ui.UiElement? Box;
                                  public Dial? Knob;
                                  public Callout? Note;
                              }
                              <div ref="@Box">
                                  <Dial ref="@Knob" />
                                  <Callout ref="@Note" />
                              </div>
                              """;

        var (component, instance, document) = Run(Source);

        using var owned = document;
        var box = (UiElement)Member(instance, "Box")!;

        Assert.Same(component.Root.Children.Single(), box);
        Assert.Equal("dial", ((UiElement)Member(instance, "Knob")!).Tag);

        // The component object, whose own host is the `callout` element beside the dial.
        Assert.Equal("callout", BuildContext.Host((Component)Member(instance, "Note")!).Tag);
    }

    /// <summary>
    ///     ⚠ <b>A <c>ref</c> in a dead arm is simply not assigned, and that is the honest
    ///     answer.</b> An arm that is not live built no element, so there is nothing to hand back;
    ///     when it becomes live the assignment runs, and when it leaves the member points at a
    ///     removed element — which <see cref="UiElement.IsRemoved" /> answers and nothing else can,
    ///     because clearing it would mean the region knowing the member's name.
    /// </summary>
    [Fact]
    public void A_ref_under_an_if_is_assigned_when_the_arm_becomes_live() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive
                              @code {
                                  public Signal<int> Count { get; } = new(0);
                                  public Vixen.Ui.UiElement? Warning;
                              }
                              <div>
                                  @if (Count.Value > 0) {
                                      <warn ref="@Warning" />
                                  }
                              </div>
                              """;

        var (_, instance, document) = Run(Source);

        using var owned = document;
        document.Effects.Flush();
        Assert.Null(Member(instance, "Warning"));

        ((Signal<int>)Property(instance, "Count")).Value = 1;
        document.Effects.Flush();

        var warning = (UiElement)Member(instance, "Warning")!;
        Assert.Equal("warn", warning.Tag);

        ((Signal<int>)Property(instance, "Count")).Value = 0;
        document.Effects.Flush();

        // Stale rather than null, and detectable.
        Assert.Same(warning, Member(instance, "Warning"));
        Assert.True(warning.IsRemoved);
    }

    /// <summary>
    ///     ⚠ <b>Who typechecks a <c>ref</c>: Roslyn, at the member name inside the quotes.</b> The
    ///     member is written where an assignment's target goes, so a wrong type, a missing member
    ///     and a readonly one are all reported on the <c>.vxml</c> — which is the philosophy the
    ///     rest of the language is built on, and the reason a <c>ref</c> needed no new checking.
    /// </summary>
    [Fact]
    public void A_ref_of_the_wrong_type_is_reported_at_the_member_in_the_vxml() {
        const string Source = """
                              @component Counter
                              @code { public string Box = ""; }
                              <div ref="@Box" />
                              """;

        var error = Assert.Single(Errors(Compile(Emit(Source))));
        var span = error.Location.GetMappedLineSpan();

        // Column 11 is `Box` inside the quotes, and the error is the *conversion* — which Roslyn
        // reports at the value, so without the second directive it would have landed past the `/>`.
        Assert.Equal(Path, span.Path);
        Assert.Equal(2, span.StartLinePosition.Line);
        Assert.Equal(11, span.StartLinePosition.Character);
    }

    /// <summary>A rebuild reassigns, because the assignment is a statement in the body.</summary>
    /// <remarks>
    ///     ⚠ <b>Which is the whole answer to "what happens on a hot reload".</b> The elements do not
    ///     survive a rebuild and cannot; the member is written again by the same statement that
    ///     wrote it the first time, so nothing has to remember that a <c>ref</c> exists.
    /// </remarks>
    [Fact]
    public void A_rebuild_points_every_ref_at_the_new_elements() {
        const string Source = """
                              @component Greeter
                              @code { public Vixen.Ui.UiElement? Box; }
                              <div ref="@Box" />
                              """;

        using var document = new UiDocument(400f, 400f);
        var component = (Component)Activator.CreateInstance(Load(Source, "Greeter"))!;

        // The hot-reload path exactly: one instance, kept, built twice.
        var context = BuildContext.BuildInto(component, document, document.Root);
        var first = (UiElement)Member(component, "Box")!;

        context.Rebuild(component);

        var second = (UiElement)Member(component, "Box")!;
        Assert.NotSame(first, second);
        Assert.Equal("div", second.Tag);
        Assert.True(first.IsRemoved);
    }

    // ================================================================== Helpers

    /// <summary>Emits, compiles, loads and adds an element-flavoured class to a document.</summary>
    /// <remarks>
    ///     ⚠ <b>The call site is compiled rather than reflected, and that is the assertion.</b>
    ///     <c>parent.Add&lt;Meter&gt;()</c> has to <i>type-check</i> — its constraint is
    ///     <c>where T : UiElement, new()</c>, which is exactly what a <c>Component</c> cannot
    ///     satisfy and is the whole complaint <c>@inherits</c> answers. Reflection would have proved
    ///     nothing about the constraint, and cannot call this method anyway: its last parameter is a
    ///     <c>ReadOnlySpan&lt;string&gt;</c>.
    /// </remarks>
    static object Add(UiDocument document, string source) {
        // ⚠ The constraint is the assertion, so it is named. `UiElement.Add<T>` is
        // `where T : UiElement, new()` — exactly what a `Component` cannot satisfy — and this file
        // compiling is the proof that a generated `@inherits` class does. The three explicit
        // arguments are the harness's Roslyn (4.11) not reading `params ReadOnlySpan<string>` out of
        // metadata; a project on the current compiler writes `parent.Add<Meter>()`.
        const string Caller = """
                              public static class Adds {
                                  public static object Make(Vixen.Ui.UiElement parent) => Element<Meter>(parent);

                                  static T Element<T>(Vixen.Ui.UiElement parent)
                                      where T : Vixen.Ui.UiElement, new() => parent.Add<T>(null, null, default);
                              }
                              """;

        var compilation = Compile(Emit(source), Caller);
        Assert.Empty(Errors(compilation));

        using var image = new MemoryStream();
        Assert.True(compilation.Emit(image).Success);

        return Assembly.Load(image.ToArray())
            .GetType("Adds")!
            .GetMethod("Make")!
            .Invoke(null, [document.Root])!;
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    // ------------------------------------------------------------ @namespace

    /// <summary>
    ///     ⚠ The file wins over the caller. The generator offers the project's root namespace plus
    ///     the file's folders, which is right nearly always and is not right for a component whose
    ///     folder is not what its namespace should be — and renaming the folder is not a fix a
    ///     library can rely on.
    /// </summary>
    [Fact]
    public void The_namespace_directive_overrides_what_the_caller_offered() {
        const string Source = """
            @component Counter
            @namespace Game.Screens
            <panel />
            """;

        Assert.Contains("namespace Game.Screens;", Emit(Source, "Project.Generated"), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_directive_the_caller_still_decides() {
        const string Source = """
            @component Counter
            <panel />
            """;

        Assert.Contains("namespace Project.Generated;", Emit(Source, "Project.Generated"), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ File-scoped, whatever the namespace came from. Every <c>#line</c> span carries a
    ///     generated column computed from the emitter's depth, and a braced namespace shifts all of
    ///     them by four — so a debugger following one lands in the wrong column of the .vxml.
    /// </summary>
    [Fact]
    public void The_namespace_stays_file_scoped_so_the_line_columns_do_not_move() {
        const string Framed = """
            @component Counter
            @namespace Game.Screens
            <panel />
            """;

        const string Bare = """
            @component Counter
            <panel />
            """;

        var framed = Emit(Framed);
        var bare = Emit(Bare);

        Assert.Contains("namespace Game.Screens;", framed, StringComparison.Ordinal);
        Assert.DoesNotContain("namespace Game.Screens {", framed, StringComparison.Ordinal);

        // The same markup, so the same `#line` directives whether or not it is in a namespace.
        Assert.Equal(Lines(bare, "#line"), Lines(framed, "#line"));
    }

    [Fact]
    public void A_namespaced_component_still_compiles() {
        const string Source = """
            @component Counter
            @namespace Game.Screens
            @using System
            <panel />
            """;

        Assert.Empty(Errors(Compile(Emit(Source))));
    }

    static IReadOnlyList<string> Lines(string generated, string prefix) =>
        [.. generated.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(prefix, StringComparison.Ordinal))];

    static string Emit(string source, string? @namespace = null) {
        var component = Binder.Bind(Markup.Syntax.SyntaxTree.ParseText(source, Path), out var diagnostics);

        Assert.Empty(diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        return ComponentEmitter.Emit(component!, Path, @namespace);
    }

    static CSharpCompilation Compile(string generated, string? caller = null) =>
        CSharpCompilation.Create(
            "Generated",
            [
                Parse(RuntimeContract.Components, "Components.cs"),
                Parse(generated, "Counter.g.cs"),
                .. caller is null ? Array.Empty<Microsoft.CodeAnalysis.SyntaxTree>() : [Parse(caller, "Caller.cs")]
            ],
            References,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

    static Microsoft.CodeAnalysis.SyntaxTree Parse(string text, string path) =>
        CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest), path);

    // ================================================================== The @for index

    /// <summary>Three keyed rows, each showing where it is.</summary>
    const string Indexed = """
                           @component Greeter
                           @using System.Collections.Generic
                           @using Vixen.Ui.Reactive

                           @code {
                               public Signal<IReadOnlyList<string>> Rows { get; } = new(["a", "b", "c"]);
                           }

                           @for (var row, i in Rows.Value) {
                               <row-line key="@row">@i.Value</row-line>
                           }
                           """;

    /// <summary>
    ///     ⚠ <b>The index is a per-row signal, and a captured <c>int</c> is the bug this shape
    ///     exists to avoid.</b> <c>BuildContext.For</c> keeps a surviving key's region and does
    ///     <i>not</i> re-run its body, so a position handed to the body as a value is the position
    ///     that row had when its key first appeared — right until anything moves, and silently
    ///     wrong afterwards. That is <c>VXML2011</c>'s mistake with no key to blame it on.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The instrument is checked before the claim.</b> The three elements are asserted to
    ///     be the same objects in a new order, because a test whose rows were rebuilt would pass
    ///     against a captured <c>int</c> as happily as against a signal — the rebuild would hand the
    ///     new position to the new body either way, and the assertion would say nothing.
    /// </remarks>
    [Fact]
    public void A_for_index_is_a_signal_so_a_row_that_moved_reports_its_new_position() {
        var (component, instance, document) = Run(Indexed);

        using var owned = document;
        document.Effects.Flush();

        var rows = component.Root.Children.ToArray();
        Assert.Equal(["0", "1", "2"], rows.Select(Text));

        var sequence = (Signal<IReadOnlyList<string>>)Property(instance, "Rows");

        sequence.Value = ["c", "a", "b"];
        document.Effects.Flush();

        // The instrument: the same three elements, moved rather than remade.
        Assert.Equal<UiElement>([rows[2], rows[0], rows[1]], component.Root.Children);

        // What every row reads is its position now.
        Assert.Equal(["0", "1", "2"], component.Root.Children.Select(Text));

        // And said the other way round, which is the assertion a captured int fails: `a` was row 0
        // and is row 1, `b` was 1 and is 2, `c` was 2 and is 0.
        Assert.Equal(["1", "2", "0"], rows.Select(Text));
    }

    /// <summary>A row that leaves takes its index with it, and the rows after it close up.</summary>
    /// <remarks>
    ///     The half a reorder does not cover: removal shortens the sequence, so a position table
    ///     that only ever grew would answer for keys that are gone and hold their rows alive.
    /// </remarks>
    [Fact]
    public void Removing_a_row_renumbers_the_ones_after_it() {
        var (component, instance, document) = Run(Indexed);

        using var owned = document;
        document.Effects.Flush();

        var rows = component.Root.Children.ToArray();
        var last = rows[2];

        ((Signal<IReadOnlyList<string>>)Property(instance, "Rows")).Value = ["b", "c"];
        document.Effects.Flush();

        Assert.Equal<UiElement>([rows[1], last], component.Root.Children);
        Assert.Equal(["0", "1"], component.Root.Children.Select(Text));
    }

    /// <summary>A loop that declares no index compiles to exactly the call it always did.</summary>
    [Fact]
    public void A_loop_with_no_index_emits_the_three_parameter_body() {
        const string Source = """
                              @component Greeter
                              @using System.Collections.Generic

                              @code {
                                  public IReadOnlyList<string> Rows { get; } = ["a"];
                              }

                              @for (var row in Rows) {
                                  <row-line key="@row">@row</row-line>
                              }
                              """;

        Assert.Contains(", row) => {", Emit(Source), StringComparison.Ordinal);
    }

    // ================================================================== help

    /// <summary>
    ///     ⚠ <b>The layering decision <c>help</c> had to make, pinned as a property of the generated
    ///     text.</b> A <c>Tooltip</c> is <c>Vixen.Ui.Controls</c>' and <c>BuildContext</c> is
    ///     <c>Vixen.Ui</c>'s, so the three candidate answers were: name the type in the generated
    ///     file, move the mechanism down, or register a seam. Naming the type resolves in a project
    ///     that references the controls and produces a generated file that <i>does not compile</i>
    ///     in one that references only <c>Vixen.Ui</c> — and the generator never touches the
    ///     compilation, so it could not even refuse. This assembly is exactly that project: it
    ///     references <c>Vixen.Ui</c> and not the controls, and it compiles the output.
    /// </summary>
    [Fact]
    public void A_help_attribute_compiles_in_a_project_that_has_no_control_library() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive

                              @code {
                                  public Signal<string> Caption { get; } = new("live");
                              }

                              <Dial help="What it counts" />
                              <div help="@Caption.Value" />
                              """;

        var generated = Emit(Source);

        Assert.Equal(2, Occurrences(generated, ".Help("));

        // ⚠ The whole of the decision, in one assertion: nothing in the output names the control
        // library, so the file compiles wherever `Vixen.Ui` does.
        Assert.DoesNotContain("Vixen.Ui.Controls", generated, StringComparison.Ordinal);
        Assert.Empty(Errors(Compile(generated)));
    }

    /// <summary>
    ///     A description is made by whatever filled the seam, and removed with the region that asked
    ///     for it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Tracked rather than left to the element tree, because the thing that describes an
    ///     element is not under it.</b> An overlay is a root child — the draw list is document
    ///     order — so clearing the branch that built the target takes the target and leaves the
    ///     description, which in a <c>@for</c> would be one abandoned tooltip per row per reorder,
    ///     each holding the element it described alive.
    /// </remarks>
    [Fact]
    public void A_description_leaves_with_the_branch_that_declared_it() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui.Reactive

                              @code {
                                  public Signal<bool> Shown { get; } = new(true);
                              }

                              @if (Shown.Value) {
                                  <hinted help="Only while the arm is live" />
                              }
                              """;

        List<UiElement> made = [];

        // What `Vixen.Ui.Controls` registers, minus the tooltip: the seam's contract is "make the
        // thing that describes this element and hand it back", and a bare element satisfies it.
        BuildContext.Describes(target => {
            var note = target.Document.Root.Add<UiElement>("description");
            made.Add(note);

            return note;
        });

        var (_, instance, document) = Run(Source);

        using var owned = document;
        document.Effects.Flush();

        var note = Assert.Single(made);
        Assert.False(note.IsRemoved);
        Assert.Equal("Only while the arm is live", note.Text);

        ((Signal<bool>)Property(instance, "Shown")).Value = false;
        document.Effects.Flush();

        Assert.True(note.IsRemoved);
    }

    /// <summary>
    ///     <c>context-menu</c> rides <c>help</c>'s seam and names no control library either, so it
    ///     compiles in this project — which has none.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A static call, and that is the runtime saying it owns nothing.</b> A menu is made by
    ///     whoever holds it and the handler goes on the target, so there is nothing to register
    ///     against the region — unlike a description, whose tooltip is a root child the directive
    ///     made and therefore has to take away.
    /// </remarks>
    [Fact]
    public void A_context_menu_attribute_compiles_in_a_project_that_has_no_control_library() {
        const string Source = """
                              @component Greeter
                              @using Vixen.Ui

                              @code {
                                  public UiElement Rows { get; } = new();
                              }

                              <sheet-row context-menu="@Rows" />
                              """;

        var generated = Emit(Source);

        Assert.Contains("BuildContext.Menu(", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("Vixen.Ui.Controls", generated, StringComparison.Ordinal);
        Assert.Empty(Errors(Compile(generated)));
    }

    // ================================================================== change: and refs

    /// <summary>
    ///     A loop whose rows carry both new directives, in the shape <c>AudioMixerView</c> needs:
    ///     each row hands its control to a keyed handle and reports its own value changes.
    /// </summary>
    const string Strips = """
                          @component Greeter
                          @using System.Collections.Generic
                          @using Vixen.Ui.Composition
                          @using Vixen.Ui.Reactive

                          @code {
                              public Signal<string[]> Rows { get; } = new([]);
                              public ElementRefs<Fader> Faders { get; } = new();
                              public List<string> Written { get; } = [];

                              void Record(string row, int level) => Written.Add(row + level);
                          }

                          <div>
                              @for (var row in Rows.Value) {
                                  <Fader key="@row" refs="@Faders" change:Level="@(v => Record(row, v))" />
                              }
                          </div>
                          """;

    /// <summary>
    ///     ⚠ <b>The end of the chain for both halves at once, and the only test that can fail if the
    ///     emitter writes the right call with the wrong argument.</b> Markup to C# to IL to a
    ///     document, then a value is changed on one row's control and the panel is asked what it
    ///     heard — which is the question a test asserting that a subscription was registered cannot
    ///     ask.
    /// </summary>
    [Fact]
    public void A_loop_row_reports_its_own_value_through_a_handle_the_key_found() {
        var (component, instance, document) = Run(Strips);

        using var owned = document;
        var rows = Property(instance, "Rows");
        var faders = Member(instance, "Faders")!;
        var written = (System.Collections.IEnumerable)Property(instance, "Written");

        rows.GetType().GetProperty("Value")!.SetValue(rows, new[] { "kick", "snare" });
        document.Effects.Flush();

        var snare = (UiElement)faders.GetType().GetProperty("Item")!.GetValue(faders, ["snare"])!;
        var kick = (UiElement)faders.GetType().GetProperty("Item")!.GetValue(faders, ["kick"])!;

        Assert.NotSame(kick, snare);
        Assert.Empty(written.Cast<string>());

        snare.GetType().GetProperty("Level")!.SetValue(snare, 3);

        // The row's own name, so the handler closed over its own iteration — and the value, which
        // is what no `on:` handler could have been given.
        Assert.Equal(["snare3"], written.Cast<string>());
    }

    /// <summary>
    ///     <c>change:</c> names a property, so a name that is not one is Roslyn's error on the
    ///     characters of the attribute name — the same bargain every other directive is emitted
    ///     under, and the reason the binder resolves no types.
    /// </summary>
    [Fact]
    public void An_unknown_property_in_a_change_is_reported_at_the_attribute_name() {
        const string Source = """
                              @component Counter
                              <Fader change:Missing="@(v => v.ToString())" />
                              """;

        var error = Assert.Single(Errors(Compile(Emit(Source))));
        var span = error.Location.GetMappedLineSpan();

        Assert.Equal(Path, span.Path);
        Assert.Equal(1, span.StartLinePosition.Line);
        Assert.Equal(7, span.StartLinePosition.Character);
    }

    /// <summary>
    ///     And a <c>refs</c> whose handle holds another element type is wrong at the member, for
    ///     <c>ref</c>'s reason: a failed conversion is reported at the value, so the value's span is
    ///     mapped back to the characters between the quotes.
    /// </summary>
    [Fact]
    public void A_refs_handle_of_the_wrong_element_type_is_reported_at_the_member() {
        const string Source = """
                              @component Counter
                              @using Vixen.Ui.Composition
                              @code { public ElementRefs<Dial> Handles { get; } = new(); }
                              @for (var row in new[] { "a" }) {
                                  <Fader key="@row" refs="@Handles" />
                              }
                              """;

        var errors = Errors(Compile(Emit(Source)));
        Assert.NotEmpty(errors);

        foreach (var span in errors.Select(error => error.Location.GetMappedLineSpan())) {
            Assert.Equal(Path, span.Path);
            Assert.Equal(4, span.StartLinePosition.Line);
        }
    }

    // ================================================================== on:keydown, and the capture leg

    /// <summary>
    ///     The three pickers' handler, written as markup: a key taken on the way <i>down</i>, before
    ///     the field under it turns Down into caret movement.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An explicitly typed lambda, and it is the only spelling that works.</b> The emitter
    ///     writes <c>ctx.On(target, "keydown", …, "capture")</c> and
    ///     <see cref="BuildContext" /> overloads that on <c>Action</c> and
    ///     <c>Action&lt;TEvent&gt;</c>, so what fixes <c>TEvent</c> has to come from the handler —
    ///     and a lambda that names its parameter's type is the one form that carries it. A method
    ///     group does not; see
    ///     <see cref="A_method_group_handler_cannot_type_itself_and_says_so_at_the_handler" />.
    /// </remarks>
    const string Keys = """
                        @component Greeter
                        @using System.Collections.Generic
                        @using Vixen.Ui

                        @code {
                            public List<string> Seen { get; } = [];

                            void Down(KeyEvent args) => Seen.Add("panel:" + args.Key);
                            void Up(KeyEvent args) => Seen.Add("up:" + args.Key);
                        }

                        <panel-root on:keydown.capture="@((KeyEvent e) => Down(e))"
                                    on:keyup="@((KeyEvent e) => Up(e))">
                            <field />
                        </panel-root>
                        """;

    /// <summary>
    ///     ⚠ <b>The whole of what "<c>on:</c> has no way to say which leg" was wrong about.</b>
    ///     <c>capture</c> has been in the modifier list and in <see cref="BuildContext.On{TEvent}" />
    ///     since both were written; what was missing was any <c>keydown</c> entry in the
    ///     subscription table, so the attribute compiled and threw <i>"'keydown' is not an event"</i>
    ///     at compose. Reverting the two table entries fails this test at <c>Run</c>.
    /// </summary>
    [Fact]
    public void A_keydown_on_the_capture_leg_runs_before_the_element_it_guards() {
        var (component, instance, document) = Run(Keys);

        using var owned = document;
        var seen = (List<string>)Property(instance, "Seen");
        var field = component.Root.Children.Single().Children.Single();

        // On the field, so the panel is an ancestor: a bubble handler would run after the field's
        // own, and the pickers' whole reason for capture is that it runs before.
        field.AddHandler<KeyEvent>((_, args) => seen.Add("field:" + args.Key));
        field.Raise(new KeyEvent { Key = InputKey.Down, Action = KeyAction.Pressed });

        Assert.Equal(["panel:Down", "field:Down"], seen);
    }

    /// <summary>
    ///     And the two names are two names over one event type, the way <c>pointerdown</c> and
    ///     <c>pointerup</c> are — a release does not reach the <c>keydown</c> handler, so nothing
    ///     written against one of them has to test <c>KeyAction</c> for itself.
    /// </summary>
    [Fact]
    public void Keydown_and_keyup_split_one_event_on_its_action() {
        var (component, instance, document) = Run(Keys);

        using var owned = document;
        var seen = (List<string>)Property(instance, "Seen");
        var field = component.Root.Children.Single().Children.Single();

        field.Raise(new KeyEvent { Key = InputKey.Enter, Action = KeyAction.Released });
        Assert.Equal(["up:Enter"], seen);

        field.Raise(new KeyEvent { Key = InputKey.Enter, Action = KeyAction.Pressed });
        Assert.Equal(["up:Enter", "panel:Enter"], seen);
    }

    // ================================================================== <self />, and on:…​.handled

    /// <summary>
    ///     The picker's handler, on the element the picker <i>is</i> — with a second root beside the
    ///     first, because that is what the attribute on a root could not cover.
    /// </summary>
    const string Own = """
                       @component Greeter
                       @using System.Collections.Generic
                       @using Vixen.Ui

                       @code {
                           public List<string> Seen { get; } = [];

                           void Down(KeyEvent args) => Seen.Add("host:" + args.Key);
                       }

                       <self on:keydown.capture="@((KeyEvent e) => Down(e))" />
                       <search-box />
                       <result-list />
                       """;

    /// <summary>
    ///     ⚠ <b>The gap five capture-leg handlers across three editor pickers stayed hand-written
    ///     for.</b> A component's markup roots are its host's <i>children</i>, so
    ///     <c>on:keydown.capture</c> written on the first of them is a different element with
    ///     different route coverage — a key arriving while the focus is on anything else in the
    ///     panel never reaches it. The list below is the second root, and a handler on the first one
    ///     would not hear this at all.
    /// </summary>
    [Fact]
    public void Self_subscribes_the_component_s_own_element_and_not_its_first_root() {
        var (component, instance, document) = Run(Own);

        using var owned = document;
        var seen = (List<string>)Property(instance, "Seen");

        // Two children, not three: `<self />` names an element that already exists rather than
        // making one, which is also what stops it appearing in the layout.
        Assert.Equal(2, component.Root.Children.Count);
        Assert.Equal(["search-box", "result-list"], component.Root.Children.Select(child => child.Tag));

        component.Root.Children[1].Raise(new KeyEvent { Key = InputKey.Down, Action = KeyAction.Pressed });

        Assert.Equal(["host:Down"], seen);
    }

    /// <summary>And on an <c>@inherits</c> class, where the host is the object itself.</summary>
    /// <remarks>
    ///     ⚠ <b>One emitted expression covers both flavours, which is the reason it is
    ///     <c>Host(this)</c> and not <c>Root</c>.</b> A <c>@inherits</c> class <i>is</i> a
    ///     <see cref="UiElement" /> and a plain component's is not; <c>Host</c> is overloaded on the
    ///     two and C# picks, the same bargain <c>Target</c> and <c>Inner</c> already make.
    /// </remarks>
    [Fact]
    public void Self_on_an_inherits_class_is_the_element_itself() {
        using var document = new UiDocument(400f, 400f);

        var meter = (UiElement)Add(
            document,
            """
            @component Meter
            @inherits Vixen.Ui.UiElement

            @code {
                public int Presses { get; private set; }
            }

            <self class="meter" on:tap="@(() => Presses++)" />
            <bar />
            """
        );

        Assert.Single(meter.Children);
        Assert.True(meter.HasClass("meter"));

        meter.Raise(new TapEvent { Count = 1 });
        Assert.Equal(1, Property(meter, "Presses"));
    }

    /// <summary>
    ///     ⚠ <b>A second composition of the same instance does not subscribe the host twice, and
    ///     <c>&lt;self /&gt;</c> is the only tag for which that had to be arranged.</b> Every other
    ///     element a markup body binds a handler to is <i>made</i> by that body, so disposing the
    ///     composition removes the element and the subscription goes with it.
    ///     <c>BuildContext.Host(this)</c> names an element that outlives the composition — an
    ///     <c>@inherits</c> panel taken out of one document and added to another runs
    ///     <c>OnCreated</c> again on the same object, because <c>UiDocument.Adopt</c> calls it every
    ///     time — so without a removal paired to the subscription the handler count follows the
    ///     number of times the panel has been opened.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The two flavours differ here and only one of them can reach it.</b> An
    ///     <c>@inherits</c> class composes in <c>OnCreated</c>, and <c>UiElement.Remove</c> is
    ///     terminal — a removed element throws on any further use — so its body runs exactly once
    ///     per instance and <c>&lt;self /&gt;</c> is safe there by construction. That is what the
    ///     four ported pickers are, which is why porting them needed no guard. A plain
    ///     <c>Component</c> is the case that bites: <c>BuildContext.Rebuild</c> clears the host's
    ///     children and re-enters <c>Build</c> on <b>the same</b> <c>component.Root</c>, which is
    ///     precisely what <c>Host(component)</c> returns.
    /// </remarks>
    [Fact]
    public void Self_does_not_subscribe_the_host_again_when_a_component_is_rebuilt() {
        var type = Load(
            """
            @component Greeter
            @using Vixen.Ui

            @code {
                public int Presses { get; private set; }
            }

            <self on:tap="@(() => Presses++)" />
            <bar />
            """,
            "Greeter"
        );

        using var document = new UiDocument(400f, 400f);

        var component = (Component)Activator.CreateInstance(type)!;
        var context = BuildContext.BuildInto(component, document, document.Root);

        component.Root.Raise(new TapEvent { Count = 1 });
        Assert.Equal(1, Property(component, "Presses"));

        // What a `.vxml` save does: the children go and the body runs again against the same host.
        context.Rebuild(component);

        // One child, so the body really did re-run rather than no-op.
        Assert.Single(component.Root.Children);

        component.Root.Raise(new TapEvent { Count = 1 });

        // Two in total, not three: the rebuild replaced the host's subscription rather than adding
        // a second one beside it.
        Assert.Equal(2, Property(component, "Presses"));
    }

    /// <summary>A component with a handler on its own element that also wants handled events.</summary>
    const string Nosy = """
                        @component Greeter
                        @using System.Collections.Generic
                        @using Vixen.Ui

                        @code {
                            public List<string> Seen { get; } = [];
                        }

                        <panel-root on:pointerdown="@(() => Seen.Add("plain"))"
                                    on:pointerdown.handled="@(() => Seen.Add("nosy"))">
                            <field />
                        </panel-root>
                        """;

    /// <summary>
    ///     ⚠ <b>The modifier that could not be written in <c>BuildContext.On</c>, and the reason the
    ///     subscription table's entries take an <c>EventSubscription</c>.</b> <c>stop</c>,
    ///     <c>once</c> and <c>self</c> are filters around a handler <c>On</c> already owns; whether
    ///     a handler is called at all once something downstream has marked the event handled is
    ///     decided by <c>UiElement.AddHandler</c>, which only the table entry can pass. Two of the
    ///     five hand-written picker handlers want it.
    /// </summary>
    [Fact]
    public void A_handled_binding_still_hears_an_event_something_else_dealt_with() {
        var (component, instance, document) = Run(Nosy);

        using var owned = document;
        var seen = (List<string>)Property(instance, "Seen");
        var field = component.Root.Children.Single().Children.Single();

        field.AddHandler<PointerEvent>((_, args) => args.Handled = true);
        field.Raise(new PointerEvent { Action = PointerAction.Pressed });

        // The plain one is skipped by the router and the nosy one is not, which is the whole of the
        // difference and is invisible to a test where nothing marks anything.
        Assert.Equal(["nosy"], seen);
    }

    /// <summary>
    ///     ⚠ <b>The one place <c>on:</c> is narrower than every other directive, and it is C#'s
    ///     rule rather than the emitter's.</b> A handler that wants the event has to name its
    ///     parameter's type, because <c>TEvent</c> is inferred from the argument and a method group
    ///     supplies nothing to infer it from — the group has no natural type until the delegate's
    ///     parameter types are known, and here they are exactly what is being solved for. So
    ///     <c>on:click="@Increment"</c> keeps working (<c>Increment()</c> is an <c>Action</c>) and
    ///     <c>on:keydown="@Keyed"</c> does not, however singular <c>Keyed</c> is.
    /// </summary>
    /// <remarks>
    ///     Pinned rather than merely written down, and pinned to the <i>author's</i> characters: the
    ///     message is Roslyn's and it lands inside the quotes, which is the bargain every directive
    ///     is emitted under. If a later C# widens method-group inference this test starts failing,
    ///     which is the right way to be told.
    /// </remarks>
    [Fact]
    public void A_method_group_handler_cannot_type_itself_and_says_so_at_the_handler() {
        const string Source = """
                              @component Counter
                              @using Vixen.Ui
                              @code { void Keyed(KeyEvent args) { } }
                              <div on:keydown.capture="@Keyed" />
                              """;

        var error = Errors(Compile(Emit(Source)))[0];
        var span = error.Location.GetMappedLineSpan();

        Assert.Contains("method group", error.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(Path, span.Path);
        Assert.Equal(3, span.StartLinePosition.Line);

        // Column 26 is `Keyed` inside the quotes, one past the `@`.
        Assert.Equal(26, span.StartLinePosition.Character);
    }

    static ImmutableArray<Diagnostic> Errors(Compilation compilation) =>
        [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];

    static int Occurrences(string text, string needle) {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }

    /// <summary>
    ///     Everything loaded next to the test, which is the framework and the runtime the generated
    ///     code calls.
    /// </summary>
    static readonly ImmutableArray<MetadataReference> References = [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "Vixen.*.dll"))
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
    ];
}
