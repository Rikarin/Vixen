// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Artefacts;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Cli;

/// <summary>
///     Runs a compilation front to back — parse, bind, lower, verify, generate,
///     write — and reports what happened. Console-free on purpose: it takes the two
///     writers, so a test can drive it exactly as the command does.
///     Each stage reports its diagnostics and stops if any of them was an error, so
///     a parse failure never cascades into a wall of semantic noise.
/// </summary>
public static class CompileDriver {
    /// <summary>
    ///     Indented, with enums as names: this file is read by people as often as by the engine,
    ///     and a bare number for a DescriptorType tells a reader nothing.
    /// </summary>
    static readonly JsonSerializerOptions ReflectionJson = new() {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ExitCode Run(CompileRequest request, TextWriter output, TextWriter error) {
        var formatting = new DiagnosticFormatterOptions { UseColor = request.UseColor };

        if (TargetBackends.Create(request.Target) is not { } backend) {
            error.WriteLine(
                $"error: unknown target '{request.Target}'. Available: {string.Join(", ", TargetBackends.Names)}"
            );
            return ExitCode.UsageError;
        }

        if (request.Inputs.Count == 0) {
            error.WriteLine("error: no input files");
            return ExitCode.UsageError;
        }

        List<SyntaxTree> trees = [];

        foreach (var input in request.Inputs) {
            if (!File.Exists(input)) {
                error.WriteLine($"error: input file not found: {input}");
                return ExitCode.UsageError;
            }

            try {
                trees.Add(SyntaxTree.ParseText(File.ReadAllText(input), path: input));
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                error.WriteLine($"error: could not read {input}: {exception.Message}");
                return ExitCode.UsageError;
            }
        }

        if (Report(trees.SelectMany(tree => tree.Diagnostics), error, formatting)) {
            return ExitCode.CompilationFailed;
        }

        // A malformed define is the caller's mistake, not the shader's, so it is a usage
        // error rather than a compilation failure.
        if (!PermutationValues.TryParse(request.Defines, out var permutations, out var defineError)) {
            error.WriteLine($"error: {defineError}");
            return ExitCode.UsageError;
        }

        if (!ComposeBindings.TryParse(request.Composes, out var composes, out var composeError)) {
            error.WriteLine($"error: {composeError}");
            return ExitCode.UsageError;
        }

        List<RavenReference> references = [];

        foreach (var path in request.References) {
            try {
                references.Add(RavenReference.FromFile(path));
            } catch (Exception exception) when (exception
                is InvalidDataException or IOException or UnauthorizedAccessException) {
                error.WriteLine($"error: could not read reference {path}: {exception.Message}");
                return ExitCode.UsageError;
            }
        }

        var compilation = Compilation.Create(AssemblyName(request), permutations, composes, references, trees);

        if (Report(compilation.GetDiagnostics(), error, formatting)) {
            return ExitCode.CompilationFailed;
        }

        // Lowering and code generation share one bag, so only the part each
        // stage added gets reported — an info from lowering is not said twice.
        var bag = new DiagnosticBag();
        var seen = 0;

        var lowered = Lowerer.LowerWithLinks(compilation, bag);
        var module = lowered.Module;
        IrVerifier.Verify(module, bag);

        if (ReportNew(bag, ref seen, error, formatting)) {
            return ExitCode.CompilationFailed;
        }

        // A library instead of a target, not as well as: it has no target and no entry points, so
        // the two are different jobs and would be sharing one output path.
        if (request.EmitLibrary) {
            return WriteLibrary(request, compilation, lowered, bag, ref seen, output, error, formatting);
        }

        if (request.ShowCapabilities) {
            // Per shader, because a host gates a pipeline, not a compilation.
            foreach (var shader in module.Shaders) {
                var required = IrCapabilities.Of(shader);
                output.WriteLine(
                    required.Count == 0
                        ? $"{shader.Name}: no capabilities required"
                        : $"{shader.Name}: {string.Join(", ", required)}"
                );
            }
        }

        var generated = backend.Generate(module, bag);

        if (ReportNew(bag, ref seen, error, formatting)) {
            return ExitCode.CompilationFailed;
        }

        if (generated.Count == 0) {
            error.WriteLine("error: nothing to generate — the input declares no entry points");
            return ExitCode.CompilationFailed;
        }

        return Write(request, backend, module, generated, compilation, permutations, output, error);
    }

    /// <summary>
    ///     Builds and writes the <c>.rvnlib</c> for a compilation.
    /// </summary>
    /// <remarks>
    ///     The export checks report here, and an error among them fails the build. That is the point
    ///     of running them at write time: a body that cannot be linked is fixed in the library, not
    ///     rediscovered in every consumer.
    /// </remarks>
    static ExitCode WriteLibrary(
        CompileRequest request,
        Compilation compilation,
        LoweringResult lowered,
        DiagnosticBag bag,
        ref int seen,
        TextWriter output,
        TextWriter error,
        DiagnosticFormatterOptions formatting
    ) {
        var library = LibraryBuilder.Build(compilation, lowered, bag);

        if (ReportNew(bag, ref seen, error, formatting)) {
            return ExitCode.CompilationFailed;
        }

        // An output path with no extension names a directory, matching the target case; the file in
        // it is named after the library.
        var path = Path.GetExtension(request.Output).Length > 0
            ? request.Output
            : Path.Combine(request.Output, library.Name + CompiledLibraryFormat.Extension);

        try {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory) {
                Directory.CreateDirectory(directory);
            }

            CompiledLibraryWriter.WriteFile(path, library);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            error.WriteLine($"error: could not write output: {exception.Message}");
            return ExitCode.UsageError;
        }

        if (request.Verbose) {
            output.WriteLine(path);
        }

        return ExitCode.Success;
    }

    static ExitCode Write(
        CompileRequest request,
        ITargetBackend backend,
        IrModule module,
        IReadOnlyList<GeneratedSource> generated,
        Compilation compilation,
        PermutationValues permutations,
        TextWriter output,
        TextWriter error
    ) {
        // An output path that names a file can only take one unit; a shader with
        // both a vertex and a fragment stage needs somewhere to put both.
        var single = Path.GetExtension(request.Output).Length > 0;

        if (single && generated.Count > 1) {
            var names = string.Join(", ", generated.Select(unit => unit.Name));
            error.WriteLine(
                $"error: {generated.Count} translation units were generated ({names}), "
                + $"but '{request.Output}' names a single file. Pass a directory instead."
            );
            return ExitCode.UsageError;
        }

        var directory = single ? Path.GetDirectoryName(request.Output) : request.Output;

        try {
            if (!string.IsNullOrEmpty(directory)) {
                Directory.CreateDirectory(directory);
            }

            foreach (var unit in generated) {
                var path = single
                    ? request.Output
                    : Path.Combine(request.Output, unit.Name + backend.FileExtension);

                if (unit.Binary is { } binary) {
                    File.WriteAllBytes(path, binary);
                } else {
                    File.WriteAllText(path, unit.Code);
                }

                if (request.Verbose) {
                    output.WriteLine(path);
                }

                // A binary target's readable form is worth keeping around; there
                // is nothing to look at in the .spv itself.
                if (request.EmitListing && unit.IsBinary) {
                    var listing = Path.ChangeExtension(path, ".spvasm");
                    File.WriteAllText(listing, unit.Code);

                    if (request.Verbose) {
                        output.WriteLine(listing);
                    }
                }
            }

            if (request.EmitEffect) {
                var sources = compilation.SyntaxTrees.Select(tree => tree.Text?.ToString() ?? string.Empty).ToArray();

                foreach (var shader in module.Shaders) {
                    var path = single
                        ? Path.ChangeExtension(request.Output, ".rvnfx")
                        : Path.Combine(request.Output, shader.Name + ".rvnfx");

                    // The backend names each unit "<shader>.<stage>", which is how a unit is
                    // attributed back to the shader that produced it.
                    var units = generated
                        .Where(unit => unit.Name.StartsWith(shader.Name + ".", StringComparison.Ordinal))
                        .ToArray();

                    var effect = CompiledEffect.Create(
                        shader.Name,
                        request.Target,
                        units,
                        ReflectionBuilder.Describe(shader, compilation.UsedPermutationKeys),
                        permutations,
                        sources
                    );

                    CompiledEffectWriter.WriteFile(path, effect);

                    if (request.Verbose) {
                        output.WriteLine(path);
                    }
                }
            }

            if (request.EmitReflection) {
                foreach (var shader in module.Shaders) {
                    var path = single
                        ? Path.ChangeExtension(request.Output, ".reflect.json")
                        : Path.Combine(request.Output, shader.Name + ".reflect.json");

                    var reflection = ReflectionBuilder.Describe(shader, compilation.UsedPermutationKeys);
                    File.WriteAllText(path, JsonSerializer.Serialize(reflection, ReflectionJson));

                    if (request.Verbose) {
                        output.WriteLine(path);
                    }
                }
            }

            if (request.EmitIr) {
                var path = single
                    ? Path.ChangeExtension(request.Output, ".ir")
                    : Path.Combine(request.Output, AssemblyName(request) + ".ir");

                File.WriteAllText(path, IrPrinter.Print(module));

                if (request.Verbose) {
                    output.WriteLine(path);
                }
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            error.WriteLine($"error: could not write output: {exception.Message}");
            return ExitCode.UsageError;
        }

        return ExitCode.Success;
    }

    /// <summary>Reports the diagnostics a bag has grown since it was last read.</summary>
    static bool ReportNew(DiagnosticBag bag, ref int seen, TextWriter error, DiagnosticFormatterOptions formatting) {
        var all = bag.ToArray();
        var fresh = all[seen..];
        seen = all.Length;
        return Report(fresh, error, formatting);
    }

    /// <summary>Reports every diagnostic and answers whether any was an error.</summary>
    static bool Report(IEnumerable<Diagnostic> diagnostics, TextWriter error, DiagnosticFormatterOptions formatting) {
        var reported = diagnostics.Where(d => d.Severity > DiagnosticSeverity.Hidden).ToArray();

        foreach (var diagnostic in reported) {
            error.Write(DiagnosticFormatter.Format(diagnostic, formatting));
            error.WriteLine();
        }

        var errors = reported.Count(d => d.IsError);

        if (errors > 0) {
            error.WriteLine($"error: compilation failed with {errors} error{(errors == 1 ? "" : "s")}");
        }

        return errors > 0;
    }

    /// <summary>The compilation's name, taken from the first input file.</summary>
    static string AssemblyName(CompileRequest request) =>
        Path.GetFileNameWithoutExtension(request.Inputs[0]) is { Length: > 0 } name ? name : "Shaders";
}
