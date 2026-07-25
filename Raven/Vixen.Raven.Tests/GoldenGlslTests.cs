using System.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.CodeGen.Glsl;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     Golden-file GLSL tests: each fixture <c>Fixtures/&lt;name&gt;.rvn</c> is
///     compiled all the way through and each generated stage is compared against
///     <c>Fixtures/&lt;name&gt;.&lt;stage&gt;.glsl</c>. This is what makes a change in
///     code generation visible in review.
///     Regenerate with <c>UPDATE_GOLDEN=1</c> and read the diff.
/// </summary>
public class GoldenGlslTests(ITestOutputHelper output) {
    static bool ShouldUpdate => Environment.GetEnvironmentVariable("UPDATE_GOLDEN") is "1" or "true";

    [Theory]
    [InlineData("lambert")]
    public void Matches_golden(string name) {
        List<string> regenerated = [];

        foreach (var unit in Compile(name)) {
            var goldenPath = FixturePath($"{name}.{StageSuffix(unit)}.glsl");
            var actual = Normalize(unit.Code);

            // Regenerate every stage before failing, so one run refreshes them all.
            if (ShouldUpdate || !File.Exists(goldenPath)) {
                File.WriteAllText(goldenPath, actual);
                regenerated.Add(Path.GetFileName(goldenPath));
                continue;
            }

            var expected = Normalize(File.ReadAllText(goldenPath));

            if (expected != actual) {
                File.WriteAllText(goldenPath + ".actual", actual);
            }

            Assert.Equal(expected, actual);
        }

        Assert.True(
            regenerated.Count == 0,
            $"Goldens were (re)generated: {string.Join(", ", regenerated)}. Review the diff and re-run."
        );
    }

    /// <summary>
    ///     The exit criterion for this phase: real GLSL that a real compiler accepts.
    ///     Runs only when <c>glslangValidator</c> is on PATH — it is not a build
    ///     dependency, and its absence is reported rather than silently ignored.
    /// </summary>
    [Theory]
    [InlineData("lambert")]
    public void Passes_glslang(string name) {
        if (FindGlslang() is not { } glslang) {
            output.WriteLine(
                "glslangValidator was not found on PATH, so the generated GLSL was not validated. "
                + "Install it (brew install glslang) to check this properly."
            );
            return;
        }

        foreach (var unit in Compile(name)) {
            var suffix = StageSuffix(unit);
            var path = Path.Combine(Path.GetTempPath(), $"raven_{name}_{suffix}.{suffix}");
            File.WriteAllText(path, unit.Code);

            var process = Process.Start(
                new ProcessStartInfo(glslang, path) { RedirectStandardOutput = true, RedirectStandardError = true }
            )!;

            process.WaitForExit();
            var log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            output.WriteLine($"{unit.Name}: {log}");

            Assert.True(process.ExitCode == 0, $"glslang rejected {unit.Name}:\n{log}\n\n{unit.Code}");
        }
    }

    static IReadOnlyList<GeneratedSource> Compile(string name) {
        var source = File.ReadAllText(FixturePath(name + ".rvn"));

        var tree = SyntaxTree.ParseText(source, path: name + ".rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        var generated = TargetBackends.Create("glsl")!.Generate(module, bag);

        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        return generated;
    }

    static string StageSuffix(GeneratedSource unit) => GlslBackend.StageSuffix(unit.Stage);

    static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

    static string? FindGlslang() {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin"]);

        foreach (var directory in paths) {
            if (string.IsNullOrWhiteSpace(directory)) {
                continue;
            }

            var candidate = Path.Combine(directory, "glslangValidator");
            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        return null;
    }

    // bin/Debug/net10.0 -> Tests project root -> Fixtures
    static string FixturePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", file);
}
