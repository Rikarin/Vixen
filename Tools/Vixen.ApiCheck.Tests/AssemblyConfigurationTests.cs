// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ Verifying the instrument: the one fact about an input assembly that does not appear in its
///     surface, and that the tool used to take on trust.
/// </summary>
/// <remarks>
///     <para>
///         A baseline records the <em>Release</em> surface — <c>CheckApi</c> hard-codes the
///         configuration and says why. The tool underneath it takes a path, and
///         <c>Core/Vixen.Ui/bin/Debug/net10.0/Vixen.Ui.dll</c> is a path. Because a <c>const</c>'s
///         value is part of the surface, and the engine has <c>public const bool</c> flags whose
///         values are <c>#if DEBUG</c>, a regeneration from Debug rewrote
///         <c>UiDiagnostics.RecordsRegions</c> from <c>false</c> to <c>true</c> and broke the gate
///         on master — twice in one session, each time reverted by hand out of a fifty-line diff
///         where one changed literal reads as noise.
///     </para>
///     <para>
///         So the subjects here are compiled with an <c>AssemblyConfigurationAttribute</c> of the
///         test's own choosing, which is exactly what the SDK writes from <c>$(Configuration)</c>,
///         and read back from the emitted binary. Asserting against the configuration <em>these
///         tests</em> were built in would prove nothing on a machine that only ever builds one.
///     </para>
/// </remarks>
public sealed class AssemblyConfigurationTests : IDisposable {
    readonly string directory = Path.Combine(Path.GetTempPath(), "vixen-api-config-tests", Guid.NewGuid().ToString("N"));

    public AssemblyConfigurationTests() => Directory.CreateDirectory(directory);

    public void Dispose() {
        try {
            if (Directory.Exists(directory)) {
                Directory.Delete(directory, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    [Theory]
    [InlineData("Release")]
    [InlineData("Debug")]
    [InlineData("Profile")]
    public void TheConfigurationIsReadFromTheAssembly(string configuration) {
        var path = Compile($"""[assembly: System.Reflection.AssemblyConfiguration("{configuration}")]""", configuration);

        Assert.Equal(configuration, AssemblyConfiguration.Read(path));
    }

    /// <summary>
    ///     An assembly that does not say what it is. The distinction matters because the two
    ///     answers lead to different words in the refusal, and because "I could not tell" must not
    ///     collapse into "Release".
    /// </summary>
    [Fact]
    public void AnAssemblyWithNoConfigurationAttributeReadsAsUnknown() {
        var path = Compile("public class Silent { }", "no-attribute");

        Assert.Null(AssemblyConfiguration.Read(path));
        Assert.False(AssemblyConfiguration.IsBaseline(AssemblyConfiguration.Read(path)));
    }

    /// <summary>
    ///     The decision itself, which is what <c>--update</c> consults. ⚠ Both halves, because a
    ///     predicate that cannot be false would refuse the gate's own regeneration run and a
    ///     predicate that cannot be true is the defect it was written to stop.
    /// </summary>
    [Fact]
    public void OnlyAReleaseAssemblyMayRewriteABaseline() {
        Assert.True(AssemblyConfiguration.IsBaseline(Read("Release")));
        Assert.False(AssemblyConfiguration.IsBaseline(Read("Debug")));
        Assert.False(AssemblyConfiguration.IsBaseline(null));

        string? Read(string configuration) => AssemblyConfiguration.Read(
            Compile($"""[assembly: System.Reflection.AssemblyConfiguration("{configuration}")]""", "decision-" + configuration)
        );
    }

    /// <summary>
    ///     The trap itself, end to end: the same source compiled twice differs in the surface by one
    ///     entry, and only the assembly's configuration says which of the two is the promise.
    /// </summary>
    [Fact]
    public void ADebugAndAReleaseSurfaceOfTheSameSourceDisagree() {
        const string Source = """
            namespace Sample;

            public static class Flags {
            #if DEBUG
                public const bool Records = true;
            #else
                public const bool Records = false;
            #endif
            }
            """;

        var debug = ApiSurfaceReader.Read(Compile(Source, "flags-debug", "Debug", ["DEBUG"]));
        var release = ApiSurfaceReader.Read(Compile(Source, "flags-release", "Release", []));

        Assert.Contains("const Sample.Flags.Records = true -> bool", debug);
        Assert.Contains("const Sample.Flags.Records = false -> bool", release);
        Assert.NotEqual(debug, release);
    }

    /// <summary>Compiles a fixture to a real assembly on disk, which is what the tool reads.</summary>
    string Compile(string source, string name, string? configuration = null, string[]? symbols = null) {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        var text = configuration is null
            ? source
            : $"""[assembly: System.Reflection.AssemblyConfiguration("{configuration}")]{Environment.NewLine}{source}""";

        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(preprocessorSymbols: symbols ?? []))],
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
