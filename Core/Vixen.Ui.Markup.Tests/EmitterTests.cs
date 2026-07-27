// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vixen.Ui.Markup.Binding;
using Vixen.Ui.Markup.Emit;
using Xunit;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Vixen.Ui.Markup.Tests;

/// <summary>
///     The generated C#, and the mapping that makes an error in it land on the markup.
/// </summary>
/// <remarks>
///     These tests do not read the output and check it looks right. They hand it to Roslyn, because
///     the only two claims worth making about a code generator are "this compiles" and "when it
///     does not, the message points at what the author wrote" — and both of those are questions
///     only a real compiler can answer.
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

    // ================================================================== Helpers

    static string Emit(string source) {
        var component = Binder.Bind(Markup.Syntax.SyntaxTree.ParseText(source, Path), out var diagnostics);

        Assert.Empty(diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        return ComponentEmitter.Emit(component!, Path);
    }

    static CSharpCompilation Compile(string generated) =>
        CSharpCompilation.Create(
            "Generated",
            [
                Parse(RuntimeContract.Source, "Contract.cs"),
                Parse(RuntimeContract.Components, "Components.cs"),
                Parse(generated, "Counter.g.cs")
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

    static readonly ImmutableArray<MetadataReference> References = [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
    ];
}
