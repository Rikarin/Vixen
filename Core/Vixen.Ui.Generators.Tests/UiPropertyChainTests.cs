// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Vixen.Ui.Generators.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/1240">#1240</a>: a generated static
///     constructor names its nearest property-declaring ancestor, so an ahead-of-time build keeps
///     that ancestor's constructor and its registrations.
/// </summary>
/// <remarks>
///     <para>
///         <b>What the text has to say, and why it is the text that is asserted.</b> ILC preserves a
///         class constructor it can <em>name</em> — <c>typeof(Base)</c> in the emitted source — and
///         not one the registry reaches through <c>Type.BaseType</c> at run time. Measured with a
///         NativeAOT publish: a derived type whose constructor carried that one line got its base's
///         properties back, and the identical pair without it did not. No test here runs against an
///         AOT publish, so the closest thing to the guarantee is the line being there, with the right
///         type in it, and absent where there is nothing to chain to.
///     </para>
///     <para>
///         The framework under test is declared in the snippet, in the namespace the generator
///         resolves by metadata name, for the reason <see cref="AnalyzerHarness" /> gives: a test
///         that referenced the real <c>Vixen.Ui</c> would be asserting that a restore happened.
///         ⚠ <c>UiElement</c> here declares a property of its own, because the real one declares
///         eight and the chain's first link is the one that reaches it.
///     </para>
/// </remarks>
public class UiPropertyChainTests {
    const string Framework = """
        namespace Vixen.Ui {
            [System.AttributeUsage(System.AttributeTargets.Property)]
            public sealed class UiPropertyAttribute : System.Attribute {
                public bool Inherits { get; set; }
                public string? Changed { get; set; }
                public string? Coerce { get; set; }
                public object? Default { get; set; }
            }

            public sealed class UiPropertyKey { }

            public static class UiPropertyRegistry {
                public static UiPropertyKey Register(
                    string name,
                    System.Type ownerType,
                    System.Type valueType,
                    bool inherits,
                    System.Func<UiElement, object?> get,
                    System.Action<UiElement, object?> set
                ) => new();
            }

            public partial class UiElement {
                public UiElement? Parent => null;

                protected void RaisePropertyChanged(UiPropertyKey key) { }

                [UiProperty]
                public partial bool AllowDrop { get; set; }
            }
        }
        """;

    /// <summary>The generated file for one type, by the type's name.</summary>
    static string Generated(string source, string typeName) {
        // ⚠ Preview, because a partial property is C# 13 and the driver parses with the options it
        // is given rather than the project's.
        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source + Environment.NewLine + Framework, options, path: "Declarations.cs");

        var compilation = CSharpCompilation.Create(
            "ApplicationUnderTest",
            [tree],
            References,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var driver = CSharpGeneratorDriver.Create([new UiPropertyGenerator().AsSourceGenerator()], parseOptions: options);

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics, TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        // ⚠ The output has to compile, or an assertion about its text is an assertion about a file
        // the build would have thrown away.
        var broken = updated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        Assert.True(broken.Length == 0, $"The generated code does not compile: {string.Join("; ", broken)}");

        var file = updated.SyntaxTrees.SingleOrDefault(candidate => candidate.FilePath.EndsWith($"{typeName}.UiProperties.g.cs", StringComparison.Ordinal));

        Assert.True(file is not null, $"nothing was generated for {typeName}");

        return file!.ToString();
    }

    /// <summary>A control chains to the framework element it derives from, across the metadata boundary.</summary>
    [Fact]
    public void A_type_deriving_from_a_declaring_base_names_that_base_in_its_static_constructor() {
        var source = """
            using Vixen.Ui;

            namespace App {
                public partial class Panel : UiElement {
                    [UiProperty(Default = 1f)]
                    public partial float Radius { get; set; }
                }
            }
            """;

        var generated = Generated(source, "App_Panel");

        Assert.Contains("static Panel() {", generated, StringComparison.Ordinal);
        Assert.Contains(
            "System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(global::Vixen.Ui.UiElement).TypeHandle);",
            generated,
            StringComparison.Ordinal
        );
    }

    /// <summary>Only the nearest declaring ancestor is named; the one above it is that ancestor's business.</summary>
    [Fact]
    public void The_nearest_declaring_ancestor_is_named_and_a_silent_one_between_is_skipped() {
        var source = """
            using Vixen.Ui;

            namespace App {
                public partial class Panel : UiElement {
                    [UiProperty(Default = 1f)]
                    public partial float Radius { get; set; }
                }

                public class Frame : Panel { }

                public partial class Card : Frame {
                    [UiProperty(Default = 2)]
                    public partial int Elevation { get; set; }
                }
            }
            """;

        var generated = Generated(source, "App_Card");

        Assert.Contains("RunClassConstructor(typeof(global::App.Panel).TypeHandle);", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("typeof(global::App.Frame)", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("typeof(global::Vixen.Ui.UiElement).TypeHandle", generated, StringComparison.Ordinal);
    }

    /// <summary>The root of a chain has nothing to name, and its constructor stays empty.</summary>
    /// <remarks>
    ///     The instrument's other half: a generator that named <em>something</em> in every
    ///     constructor would pass the two tests above by accident. The framework element declares a
    ///     property and derives from nothing that does.
    /// </remarks>
    [Fact]
    public void A_type_with_no_declaring_ancestor_chains_to_nothing() {
        var generated = Generated(string.Empty, "Vixen_Ui_UiElement");

        Assert.Contains("static UiElement() {\n    }", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("RunClassConstructor", generated, StringComparison.Ordinal);
    }

    static readonly ImmutableArray<MetadataReference> References = [
        .. AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && assembly.Location.Length != 0)
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
    ];
}
