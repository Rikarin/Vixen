// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Reflection.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The reader, driven the way the gate drives it: over a compiled assembly rather than over
///     source symbols.
/// </summary>
/// <remarks>
///     Each test compiles its own subject and reads the resulting binary, because reading source
///     symbols would test a path the tool never takes — what a consumer references is metadata, and
///     metadata is where the compiler's own members, the accessors and the nullability annotations
///     look different from the code that produced them.
/// </remarks>
public sealed class ApiSurfaceReaderTests : IDisposable {
    readonly string directory = Path.Combine(Path.GetTempPath(), "vixen-api-check-tests", Guid.NewGuid().ToString("N"));

    public ApiSurfaceReaderTests() => Directory.CreateDirectory(directory);

    public void Dispose() {
        try {
            if (Directory.Exists(directory)) {
                Directory.Delete(directory, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    [Fact]
    public void PublicMembers_AreEntries_AndTheRestIsNot() {
        var surface = Read(
            """
            namespace Sample;

            public class Visible {
                public int Field;
                public void Method() { }
                protected void Derivable() { }
                internal void Hidden() { }
                private void Invisible() { }
            }

            internal class Internal {
                public void AlsoInvisible() { }
            }
            """
        );

        Assert.Contains("Sample.Visible.Method() -> void", surface);
        Assert.Contains("Sample.Visible.Derivable() -> void", surface);
        Assert.Contains("Sample.Visible.Field -> int", surface);
        Assert.DoesNotContain(surface, entry => entry.Contains("Hidden", StringComparison.Ordinal));
        Assert.DoesNotContain(surface, entry => entry.Contains("Invisible", StringComparison.Ordinal));
        Assert.DoesNotContain(surface, entry => entry.Contains("Sample.Internal", StringComparison.Ordinal));
    }

    /// <summary>A nested type is API; a nested type inside an invisible one is not.</summary>
    [Fact]
    public void NestedTypes_FollowTheirContainer() {
        var surface = Read(
            """
            namespace Sample;

            public class Outer {
                public class Inner { }
                internal class Concealed { public class Deep { } }
            }
            """
        );

        Assert.Contains("Sample.Outer.Inner -> class", surface);
        Assert.DoesNotContain(surface, entry => entry.Contains("Deep", StringComparison.Ordinal));
    }

    [Fact]
    public void Accessors_AreSeparateEntries() {
        var surface = Read(
            """
            namespace Sample;

            public class Holder {
                public int Both { get; set; }
                public int ReadOnly { get; }
                public int Initialised { get; init; }
                public int Guarded { get; private set; }
            }
            """
        );

        Assert.Contains("Sample.Holder.Both.get -> int", surface);
        Assert.Contains("Sample.Holder.Both.set -> int", surface);
        Assert.Contains("Sample.Holder.ReadOnly.get -> int", surface);
        Assert.Contains("Sample.Holder.Initialised.init -> int", surface);

        // The getter is API and the setter is not, which one line for the property could not say.
        Assert.Contains("Sample.Holder.Guarded.get -> int", surface);
        Assert.DoesNotContain("Sample.Holder.Guarded.set -> int", surface);
    }

    [Fact]
    public void TypeLine_CarriesKindAndModifiers() {
        var surface = Read(
            """
            namespace Sample;

            public sealed class Sealed { }
            public abstract class Abstract { }
            public static class Static { }
            public readonly struct Value { }
            public ref struct Borrowed { }
            public interface Contract { }
            public enum Small : byte { One }
            public delegate int Transform(int value);
            """
        );

        Assert.Contains("Sample.Sealed -> sealed class", surface);
        Assert.Contains("Sample.Abstract -> abstract class", surface);
        Assert.Contains("Sample.Static -> static class", surface);
        Assert.Contains("Sample.Value -> readonly struct", surface);
        Assert.Contains("Sample.Borrowed -> ref struct", surface);
        Assert.Contains("Sample.Contract -> interface", surface);
        Assert.Contains("Sample.Small -> enum : byte", surface);
        Assert.Contains("Sample.Transform -> delegate", surface);
    }

    /// <summary>
    ///     A delegate's metadata carries four members. Only the one that says what it means is API.
    /// </summary>
    [Fact]
    public void Delegates_ContributeTheirInvokeSignature() {
        var surface = Read("namespace Sample; public delegate int Transform(int value, string label);");

        // `virtual` because that is what a delegate's Invoke is in metadata.
        Assert.Contains("virtual Sample.Transform.Invoke(int value, string label) -> int", surface);
        Assert.DoesNotContain(surface, entry => entry.Contains("BeginInvoke", StringComparison.Ordinal));
        Assert.DoesNotContain(surface, entry => entry.Contains("Transform.Transform(", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseTypesAndInterfaces_AreTheirOwnEntries() {
        var surface = Read(
            """
            using System;

            namespace Sample;

            public class Root { }
            public class Leaf : Root, IDisposable {
                public void Dispose() { }
            }
            """
        );

        Assert.Contains("Sample.Leaf : Sample.Root", surface);
        Assert.Contains("Sample.Leaf : System.IDisposable", surface);

        // object is what `class` already says.
        Assert.DoesNotContain("Sample.Root : object", surface);
    }

    [Fact]
    public void Modifiers_AndDefaults_AndNullability_AreInTheSignature() {
        var surface = Read(
            """
            #nullable enable

            namespace Sample;

            public class Signatures {
                public static string? Find(string name, int limit = 10) => null;
                public virtual void Extend() { }
            }
            """
        );

        Assert.Contains("static Sample.Signatures.Find(string name, int limit = 10) -> string?", surface);
        Assert.Contains("virtual Sample.Signatures.Extend() -> void", surface);
    }

    /// <summary>
    ///     A method whose only reference-type parameter is nullable carries <c>[NullableContext(2)]</c>
    ///     in metadata — the compiler's shorthand for "annotate the parameters" written at the method
    ///     rather than one attribute per parameter. The shorthand says nothing about the value types
    ///     beside them, and the reading must not spell an optional enum or an <c>int</c> as nullable.
    /// </summary>
    [Fact]
    public void AMethodLevelNullableContext_DoesNotAnnotateValueTypes() {
        var surface = Read(
            """
            #nullable enable

            namespace Sample;

            public enum Level { Trace = 0, Information = 2 }

            public sealed class Sink {
                public Sink(Level minimumLevel = Level.Information, string? filter = null, int capacity = 8) { }
            }
            """
        );

        Assert.Contains(
            "Sample.Sink.Sink(Sample.Level minimumLevel = Sample.Level.Information, string? filter = null, int capacity = 8) -> void",
            surface
        );
    }

    /// <summary>
    ///     A record's equality contract, its clone helper and its printer are consequences of the
    ///     keyword, which the type's own line already records.
    /// </summary>
    [Fact]
    public void CompilerWrittenRecordMembers_AreNotSurface() {
        var surface = Read("namespace Sample; public sealed record Point(int X, int Y);");

        Assert.Contains("Sample.Point -> sealed record", surface);
        Assert.Contains("Sample.Point.X.get -> int", surface);
        Assert.DoesNotContain(surface, entry => entry.Contains("EqualityContract", StringComparison.Ordinal));
        Assert.DoesNotContain(surface, entry => entry.Contains("PrintMembers", StringComparison.Ordinal));
        Assert.DoesNotContain(surface, entry => entry.Contains("Clone", StringComparison.Ordinal));
    }

    /// <summary>
    ///     An extension block's grouping type is the compiler's, and its name says so by being one
    ///     no source file could write.
    /// </summary>
    /// <remarks>
    ///     ⚠ It carries no <c>[CompilerGenerated]</c>, which is why the attribute check is not
    ///     enough on its own — <c>Vixen.Raven</c>'s first baseline had three
    ///     <c>&lt;G&gt;$7B1EA48CCC2BB39DA1D1E341962695C5</c> lines under its syntax extensions. The
    ///     name carries a hash of the block's shape, so freezing one would report a type removed and
    ///     a type added the next time an unrelated member was added to the same block.
    /// </remarks>
    [Fact]
    public void UnspeakablyNamedTypes_AreNotSurface() {
        var surface = ApiSurfaceReader.Read(EmitAssemblyWithAnUnspeakableNestedType());

        Assert.Contains("Sample.Extensions -> static class", surface);
        Assert.DoesNotContain(surface, entry => entry.Contains('<', StringComparison.Ordinal));
        Assert.DoesNotContain(surface, entry => entry.Contains('$', StringComparison.Ordinal));
    }

    /// <summary>
    ///     Writes the shape by hand, because the pinned Roslyn is a C# version older than the one
    ///     that emits it: an <c>extension</c> block is C# 14 and <c>Microsoft.CodeAnalysis.CSharp</c>
    ///     here is 4.11. Metadata names are free-form strings, so the fixture states the fact under
    ///     test — a public nested type whose name no source file could write — without needing a
    ///     compiler that would produce one.
    /// </summary>
    string EmitAssemblyWithAnUnspeakableNestedType() {
        var assembly = new PersistedAssemblyBuilder(new("Sample"), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("Sample");

        var container = module.DefineType(
            "Sample.Extensions",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
        );

        container
            .DefineNestedType("<G>$7B1EA48CCC2BB39DA1D1E341962695C5", TypeAttributes.NestedPublic | TypeAttributes.Sealed)
            .CreateType();

        container.CreateType();

        var path = Path.Combine(directory, "Sample.dll");
        using (var stream = File.Create(path)) {
            assembly.Save(stream);
        }

        return path;
    }

    [Fact]
    public void ConstantsAndEnumMembers_CarryTheirValue() {
        var surface = Read(
            """
            namespace Sample;

            public static class Limits {
                public const int Max = 64;
            }

            public enum Level { Low = 0, High = 7 }
            """
        );

        Assert.Contains("const Sample.Limits.Max = 64 -> int", surface);
        Assert.Contains("Sample.Level.High = 7 -> Sample.Level", surface);
    }

    /// <summary>
    ///     The same assembly read twice gives the same lines in the same order — which is what makes
    ///     a baseline diffable at all.
    /// </summary>
    [Fact]
    public void TheReadingIsSortedAndStable() {
        const string source = """
            namespace Sample;

            public class Zebra { public void Zoo() { } }
            public class Alpha { public void Ant() { } }
            """;

        var first = Read(source, "stable-one");
        var second = Read(source, "stable-two");

        Assert.Equal(first, second);
        Assert.Equal(first.OrderBy(entry => entry, StringComparer.Ordinal), first);
    }

    IReadOnlyList<string> Read(string source, string name = "Sample") {
        var path = Compile(source, name);

        return ApiSurfaceReader.Read(path);
    }

    /// <summary>Compiles a fixture to a real assembly on disk, which is what the tool reads.</summary>
    string Compile(string source, string name) {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var path = Path.Combine(directory, name + ".dll");
        var result = compilation.Emit(path);

        Assert.True(
            result.Success,
            "The fixture did not compile: "
            + string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        );

        return path;
    }
}
