using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Cli;

/// <summary>
/// Runs a compilation front to back — parse, bind, lower, verify, generate,
/// write — and reports what happened. Console-free on purpose: it takes the two
/// writers, so a test can drive it exactly as the command does.
///
/// Each stage reports its diagnostics and stops if any of them was an error, so
/// a parse failure never cascades into a wall of semantic noise.
/// </summary>
public static class CompileDriver {
    public static ExitCode Run(CompileRequest request, TextWriter output, TextWriter error) {
        var formatting = new DiagnosticFormatterOptions { UseColor = request.UseColor };

        if (TargetBackends.Create(request.Target) is not { } backend) {
            error.WriteLine(
                $"error: unknown target '{request.Target}'. Available: {string.Join(", ", TargetBackends.Names)}");
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

        var compilation = Compilation.Create(AssemblyName(request), trees);

        if (Report(compilation.GetDiagnostics(), error, formatting)) {
            return ExitCode.CompilationFailed;
        }

        // Lowering and code generation share one bag, so only the part each
        // stage added gets reported — an info from lowering is not said twice.
        var bag = new DiagnosticBag();
        var seen = 0;

        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        if (ReportNew(bag, ref seen, error, formatting)) {
            return ExitCode.CompilationFailed;
        }

        var generated = backend.Generate(module, bag);

        if (ReportNew(bag, ref seen, error, formatting)) {
            return ExitCode.CompilationFailed;
        }

        if (generated.Count == 0) {
            error.WriteLine("error: nothing to generate — the input declares no entry points");
            return ExitCode.CompilationFailed;
        }

        return Write(request, backend, module, generated, output, error);
    }

    static ExitCode Write(
        CompileRequest request,
        ITargetBackend backend,
        IrModule module,
        IReadOnlyList<GeneratedSource> generated,
        TextWriter output,
        TextWriter error
    ) {
        // An output path that names a file can only take one unit; a shader with
        // both a vertex and a pixel stage needs somewhere to put both.
        var single = Path.GetExtension(request.Output).Length > 0;

        if (single && generated.Count > 1) {
            var names = string.Join(", ", generated.Select(unit => unit.Name));
            error.WriteLine(
                $"error: {generated.Count} translation units were generated ({names}), "
                + $"but '{request.Output}' names a single file. Pass a directory instead.");
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

                File.WriteAllText(path, unit.Code);

                if (request.Verbose) {
                    output.WriteLine(path);
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
