// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>Compiles a fixture and hands back its symbols.</summary>
/// <remarks>
///     The engine's own attributes are declared here rather than referenced, deliberately. The
///     taxonomy's rules are written against <em>full names</em>, so a stub with the right namespace
///     is the same input the real type is — and a test that referenced eleven engine assemblies
///     would fail for reasons that have nothing to do with classification.
/// </remarks>
static class Fixture {
    /// <summary>The declarations every fixture gets, matching the real ones by name only.</summary>
    public const string Preamble =
        """
        namespace Vixen.Core {
            public sealed class ComponentAttribute : System.Attribute;
            public sealed class DataContractAttribute : System.Attribute;
        }

        namespace Vixen.Ecs.Systems {
            public interface ISystem : System.IDisposable { }
            public abstract class SystemBase : ISystem { public void Dispose() { } }

            public enum SystemPhase { EarlyUpdate, Input, FixedUpdate, Update, LateUpdate }

            public sealed class ReadsAttribute(params System.Type[] types) : System.Attribute;
            public sealed class WritesAttribute(params System.Type[] types) : System.Attribute;
            public sealed class UpdateInGroupAttribute(SystemPhase phase) : System.Attribute;
            public sealed class UpdateBeforeAttribute(System.Type type) : System.Attribute;
            public sealed class UpdateAfterAttribute(System.Type type) : System.Attribute;
        }

        namespace Vixen.Engine.Behaviors {
            public abstract class Behavior;
        }

        namespace Vixen.Net.Replication {
            public enum Channel { Unreliable, Reliable }

            public sealed class ReplicatedAttribute : System.Attribute {
                public Channel Channel { get; set; }
                public int SendRate { get; set; }
                public int Priority { get; set; }
            }

            public sealed class QuantizeAttribute(float min, float max, int bits) : System.Attribute;
        }

        namespace Vixen.Editor.NodeGraph {
            public sealed class NodeAttribute(string path) : System.Attribute {
                public string Path { get; } = path;
                public string Summary { get; init; } = "";
                public bool Preview { get; init; }
            }
        }

        namespace Vixen.Editor.Assets {
            public sealed class ImporterAttribute(params string[] extensions) : System.Attribute {
                public string[] Extensions { get; } = extensions;
            }
        }
        """;

    public static Compilation Compile(string source, bool includePreamble = true) {
        var text = includePreamble ? Preamble + Environment.NewLine + source : source;

        var compilation = CSharpCompilation.Create(
            "Fixture",
            [CSharpSyntaxTree.ParseText(
                text,
                new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose),
                // A path, because a symbol with no file has no source link to assert on.
                path: Path.Combine(Path.GetTempPath(), "Fixture.cs"))],
            Net10.References.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0, "the fixture does not compile: " + string.Join("; ", errors));

        return compilation;
    }

    /// <summary>The named type from a compiled fixture.</summary>
    public static INamedTypeSymbol Type(this Compilation compilation, string metadataName) =>
        compilation.GetTypeByMetadataName(metadataName)
        ?? throw new InvalidOperationException($"the fixture declares no {metadataName}");
}

/// <summary>The reference set a fixture compiles against.</summary>
static class Net10 {
    public static class References {
        /// <summary>Every assembly the test host itself loaded, which is more than enough for a fixture.</summary>
        public static readonly MetadataReference[] All = [
            .. AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
        ];
    }
}
