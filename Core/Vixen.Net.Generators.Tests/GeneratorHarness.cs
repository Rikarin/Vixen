// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vixen.Ecs;
using Vixen.Net.Replication;

namespace Vixen.Net.Generators.Tests;

/// <summary>Runs the generator over a string of C#, the way the compiler would.</summary>
public static class GeneratorHarness {
    static readonly ImmutableArray<MetadataReference> References = CollectReferences();

    /// <summary>Compiles source and runs the generator over it.</summary>
    /// <param name="source">The C# to compile.</param>
    /// <returns>What the generator produced and complained about.</returns>
    public static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> Sources) Run(string source) =>
        Run(source, rpc: false);

    /// <summary>Compiles source and runs the RPC generator over it.</summary>
    /// <param name="source">The C# to compile.</param>
    /// <returns>What the generator produced and complained about.</returns>
    public static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> Sources) RunRpc(string source) =>
        Run(source, rpc: true);

    static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> Sources) Run(string source, bool rpc) {
        Drive(Compile(source), rpc, out var run);

        var result = run.Results[0];
        var generated = ImmutableArray.CreateBuilder<string>();

        foreach (var produced in result.GeneratedSources) {
            generated.Add(produced.SourceText.ToString());
        }

        return (result.Diagnostics, generated.ToImmutable());
    }

    /// <summary>Runs the generator, then runs it again over a compilation with one unrelated file added.</summary>
    /// <param name="source">The C# to compile.</param>
    /// <returns>The reasons the second run recorded for the per-component step.</returns>
    public static ImmutableArray<IncrementalStepRunReason> ReasonsOnSecondRun(string source, bool rpc = false) {
        var compilation = Compile(source);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [rpc ? new RpcGenerator().AsSourceGenerator() : new ReplicationGenerator().AsSourceGenerator()],
            driverOptions: new(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true)
        );

        driver = driver.RunGenerators(compilation);

        var again = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("namespace Elsewhere; internal sealed class Unrelated { }")
        );

        driver = driver.RunGenerators(again);

        var tracked = rpc ? RpcGenerator.DescribeStep : ReplicationGenerator.DescribeStep;
        var steps = driver.GetRunResult().Results[0].TrackedSteps[tracked];
        var reasons = ImmutableArray.CreateBuilder<IncrementalStepRunReason>();

        foreach (var step in steps) {
            foreach (var output in step.Outputs) {
                reasons.Add(output.Reason);
            }
        }

        return reasons.ToImmutable();
    }

    /// <summary>Compiles source together with what the generator made of it.</summary>
    /// <param name="source">The C# to compile.</param>
    /// <returns>Everything the compiler said about the result.</returns>
    public static ImmutableArray<Diagnostic> CompileWithGeneratedCode(string source, bool rpc = false) {
        var driver = Drive(Compile(source), rpc, out _);
        driver.RunGeneratorsAndUpdateCompilation(Compile(source), out var updated, out _);

        return updated.GetDiagnostics();
    }

    static CSharpCompilation Compile(string source) =>
        CSharpCompilation.Create(
            "GeneratedUnderTest",
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

    static GeneratorDriver Drive(CSharpCompilation compilation, bool rpc, out GeneratorDriverRunResult run) {
        var driver = CSharpGeneratorDriver
            .Create(rpc ? new RpcGenerator() : new ReplicationGenerator())
            .RunGenerators(compilation);

        run = driver.GetRunResult();

        return driver;
    }

    static ImmutableArray<MetadataReference> CollectReferences() {
        // Touch a type from each assembly the generated code needs, so that they are loaded and
        // therefore in the list below. A test that discovered its references by walking directories
        // would break the first time somebody moved one.
        _ = typeof(World).Assembly;
        _ = typeof(NetworkId).Assembly;
        _ = typeof(Core.Entity).Assembly;

        var references = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!assembly.IsDynamic && assembly.Location.Length != 0) {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        return references.ToImmutable();
    }
}
