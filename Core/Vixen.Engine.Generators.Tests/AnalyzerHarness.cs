// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Vixen.Engine.Generators.Tests;

/// <summary>Runs a Vixen.Engine analyzer over a string of C#, the way the compiler would.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The compilation under test declares the engine itself</b>, in <see cref="Engine" />.
///         An analyzer resolves a metadata name, so a declaration here is the same thing to it as one
///         in a referenced assembly — and referencing the real <c>Vixen.Engine</c> from here would be
///         circular, because that project references the generator this one tests.
///     </para>
///     <para>
///         The shapes are the ones the rules name and no more: <c>Entity</c>, <c>[Component]</c>,
///         <c>[DataContract]</c>, <c>ITagComponent</c>, a <c>World</c> with the structural calls and
///         a chunk walk, the generated query extension and visitor interface, and <c>Behavior</c>.
///         Anything the analyzer does not resolve by name is left out, because a preamble that drifts
///         towards being the engine is one nobody reads.
///     </para>
/// </remarks>
public static class AnalyzerHarness {
    /// <summary>Enough of the engine for the rules to bind against.</summary>
    public const string Engine = """
        namespace Vixen.Core {
            public readonly record struct Entity(int Id, int Version, short WorldId);

            [System.AttributeUsage(System.AttributeTargets.Struct | System.AttributeTargets.Class)]
            public sealed class ComponentAttribute : System.Attribute;

            [System.AttributeUsage(System.AttributeTargets.Struct | System.AttributeTargets.Class)]
            public sealed class DataContractAttribute : System.Attribute {
                public DataContractAttribute() { }
                public DataContractAttribute(string alias) { }
            }
        }

        namespace Vixen.Ecs {
            using Vixen.Core;

            public interface ITagComponent;

            public sealed class QueryDescription;

            public readonly struct Chunk {
                public int Count => 0;
            }

            public readonly struct ChunkSequence {
                public Enumerator GetEnumerator() => new();

                public struct Enumerator {
                    public Chunk Current => default;
                    public bool MoveNext() => false;
                }
            }

            static class Storage<T> {
                public static T Value = default!;
            }

            public sealed class Query {
                public ChunkSequence Chunks(uint since = 0) => default;
            }

            public sealed class World {
                public Entity Create() => default;
                public void CreateMany(int count) { }
                public void Destroy(Entity entity) { }
                public void Add<T>(Entity entity, in T value) { }
                public void Remove<T>(Entity entity) { }
                public void Set<T>(Entity entity, in T value) { }
                public ref T Get<T>(Entity entity) => ref Storage<T>.Value;
                public ref readonly T Read<T>(Entity entity) => ref Storage<T>.Value;
                public Query Query(QueryDescription description) => new();
                public ChunkSequence Chunks(QueryDescription description, uint since = 0) => default;
            }

            public delegate void QueryAction<T0>(ref T0 component0);

            public interface IForEach<T0> {
                void Update(ref T0 component0);
            }

            public static class WorldQueryExtensions {
                public static void Query<T0>(this World world, QueryDescription description, QueryAction<T0> action, uint since = 0) { }

                public static void ForEach<TVisitor, T0>(this World world, QueryDescription description, ref TVisitor visitor, uint since = 0)
                    where TVisitor : struct, IForEach<T0> { }
            }
        }

        namespace Vixen.Engine.Behaviors {
            using Vixen.Core;
            using Vixen.Ecs;

            public abstract class Behavior {
                public Entity Entity { get; set; }
                public World World { get; set; } = new();
                public ref T Get<T>() => ref World.Get<T>(Entity);
                public ref readonly T Read<T>() => ref World.Read<T>(Entity);
            }
        }
        """;

    static readonly ImmutableArray<MetadataReference> References = CollectReferences();

    /// <summary>Compiles source with the engine preamble and runs one analyzer over it.</summary>
    /// <param name="source">
    ///     The C# to compile, without the <see cref="Engine" /> declarations — which are appended,
    ///     because a <c>using</c> has to be the first thing in a file and every snippet starts with
    ///     one. It has to compile: a snippet with an error in it binds to nothing, and an analyzer
    ///     that reported nothing about nothing would pass.
    /// </param>
    /// <param name="analyzer">Which analyzer to run.</param>
    /// <returns>What the analyzer reported, in file order.</returns>
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(string source, DiagnosticAnalyzer analyzer) {
        ArgumentNullException.ThrowIfNull(analyzer);

        var tree = CSharpSyntaxTree.ParseText(source + Environment.NewLine + Engine, path: "Game.cs");

        var compilation = CSharpCompilation.Create(
            "GameUnderTest",
            [tree],
            References,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var broken = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        if (broken.Length != 0) {
            throw new InvalidOperationException(
                $"The source under test does not compile: {string.Join("; ", broken)}"
            );
        }

        var reported = await compilation
            .WithAnalyzers(ImmutableArray.Create(analyzer))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        return [.. reported.OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)];
    }

    /// <summary>The source a diagnostic underlined.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    /// <returns>The text of its span.</returns>
    /// <remarks>
    ///     Where a diagnostic points is half of what it says. A rule that reports a whole class when
    ///     it means one field is a rule people learn to read past.
    /// </remarks>
    public static string Underlined(Diagnostic diagnostic) {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);
    }

    static ImmutableArray<MetadataReference> CollectReferences() {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!assembly.IsDynamic && assembly.Location.Length != 0) {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        return references.ToImmutable();
    }
}
