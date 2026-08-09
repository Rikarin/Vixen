// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
