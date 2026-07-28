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

    [Fact]
    public void An_unknown_parameter_is_reported_at_the_attribute_name() {
        const string Source = """
                              @component Counter
                              <Label Missing="x" />
                              """;

        var error = Assert.Single(Errors(Compile(Emit(Source))));
        var span = error.Location.GetMappedLineSpan();

        Assert.Equal(Path, span.Path);
        Assert.Equal(1, span.StartLinePosition.Line);
        Assert.Equal(7, span.StartLinePosition.Character);
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

        EffectScheduler.Default.Flush();
        Assert.Equal("div", root.Tag);
        Assert.True(root.HasClass("root"));
        Assert.Equal(["Count: ", "0"], span.Children.Select(child => child.Text));

        // A signal write reaches exactly the effect that reads it.
        Count(instance).Value = 3;
        EffectScheduler.Default.Flush();
        Assert.Equal(["Count: ", "3"], span.Children.Select(child => child.Text));

        // ...and the branch it gates appears, in its place among the siblings.
        Assert.Equal(["span", "em"], root.Children.Select(child => child.Tag));

        Count(instance).Value = 0;
        EffectScheduler.Default.Flush();
        Assert.Equal(["span"], root.Children.Select(child => child.Tag));
    }

    [Fact]
    public void A_compiled_loop_keeps_the_elements_of_the_items_that_survive() {
        var (component, instance, document) = Run(Greeter);

        using var owned = document;
        var root = component.Root.Children.Single();

        Items(instance).Value = ["a", "b"];
        EffectScheduler.Default.Flush();

        var b = root.Children.Single(child => child.Tag == "li" && Text(child) == "b");

        Items(instance).Value = ["b", "a"];
        EffectScheduler.Default.Flush();

        Assert.Same(b, root.Children.Single(child => child.Tag == "li" && Text(child) == "b"));
        Assert.Equal(["b", "a"], root.Children.Where(child => child.Tag == "li").Select(Text));
    }

    static string Text(UiElement element) => element.Children.Single().Text ?? string.Empty;

    static Signal<int> Count(object instance) => (Signal<int>)Property(instance, "Count");

    static Signal<string[]> Items(object instance) => (Signal<string[]>)Property(instance, "Items");

    static object Property(object instance, string name) =>
        instance.GetType().GetProperty(name)!.GetValue(instance)!;

    /// <summary>Emits, compiles, loads and builds — the whole pipeline, end to end.</summary>
    static (Component Component, object Instance, UiDocument Document) Run(string source) {
        var compilation = Compile(Emit(source));
        Assert.Empty(Errors(compilation));

        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success);

        var type = Assembly.Load(image.ToArray()).GetType("Greeter")!;
        var document = new UiDocument(400f, 400f);

        var built = typeof(BuildContext)
            .GetMethod(nameof(BuildContext.Build))!
            .MakeGenericMethod(type)
            .Invoke(null, [document, document.Root])!;

        return ((Component)built, built, document);
    }

    // ================================================================== Helpers

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

    static CSharpCompilation Compile(string generated) =>
        CSharpCompilation.Create(
            "Generated",
            [Parse(RuntimeContract.Components, "Components.cs"), Parse(generated, "Counter.g.cs")],
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
