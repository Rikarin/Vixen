// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.RegularExpressions;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     Golden-file SPIR-V tests. The golden is the assembly listing rather than the
///     bytes, because a diff over 4 kB of hex tells a reviewer nothing — and the
///     listing is rendered from the very instructions that get encoded, so it cannot
///     say one thing while the binary holds another.
///     Regenerate with <c>UPDATE_GOLDEN=1</c> and read the diff.
/// </summary>
public partial class GoldenSpirvTests(ITestOutputHelper output) {
    static bool ShouldUpdate => Environment.GetEnvironmentVariable("UPDATE_GOLDEN") is "1" or "true";

    [Theory]
    [InlineData("lambert")]
    public void Matches_golden(string name) {
        List<string> regenerated = [];

        foreach (var unit in Compile(name)) {
            var goldenPath = FixturePath($"{name}.{Suffix(unit)}.spvasm");
            var actual = Normalize(unit.Code);

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
    ///     The exit criterion for this phase: SPIR-V the reference validator accepts,
    ///     under Vulkan's rules rather than the looser universal ones.
    /// </summary>
    [Theory]
    [InlineData("lambert")]
    public void Passes_spirv_val(string name) {
        Assert.True(
            SpirvTestBase.ValidatorAvailable,
            "spirv-val was not found. Install SPIR-V Tools (brew install spirv-tools)."
        );

        foreach (var unit in Compile(name)) {
            SpirvTestBase.Validate(unit);
            output.WriteLine($"{unit.Name}: valid ({unit.Binary!.Length} bytes)");
        }
    }

    /// <summary>
    ///     Cross-checks the listing against a real disassembler. If the two agree on
    ///     the whole opcode sequence, then the words that were encoded are the words
    ///     the listing claims — which is the one thing a hand-written encoder can
    ///     plausibly get wrong without anything else noticing.
    /// </summary>
    [Theory]
    [InlineData("lambert")]
    public void The_listing_agrees_with_a_real_disassembler(string name) {
        if (SpirvTestBase.FindTool("spirv-dis") is not { } disassembler) {
            output.WriteLine("spirv-dis was not found, so the listing was not cross-checked.");
            return;
        }

        foreach (var unit in Compile(name)) {
            var path = Path.Combine(Path.GetTempPath(), $"raven_{name}_{Suffix(unit)}.spv");
            File.WriteAllBytes(path, unit.Binary!);

            try {
                var process = Process.Start(
                    new ProcessStartInfo(disassembler, ["--no-color", "--raw-id", path]) {
                        RedirectStandardOutput = true, RedirectStandardError = true
                    }
                )!;

                process.WaitForExit();
                var disassembled = process.StandardOutput.ReadToEnd();

                Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
                Assert.Equal(Opcodes(disassembled), Opcodes(unit.Code));
            } finally {
                File.Delete(path);
            }
        }
    }

    /// <summary>Every opcode in a listing, in order, ignoring ids and operands.</summary>
    static string[] Opcodes(string listing) => [
        .. listing.Split('\n')
            .Select(line => OpcodePattern().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
    ];

    [GeneratedRegex(@"\b(Op[A-Za-z0-9]+)")]
    private static partial Regex OpcodePattern();

    static IReadOnlyList<GeneratedSource> Compile(string name) {
        var source = File.ReadAllText(FixturePath(name + ".rvn"));

        var tree = SyntaxTree.ParseText(source, path: name + ".rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        var generated = TargetBackends.Create("spirv")!.Generate(module, bag);

        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        return generated;
    }

    static string Suffix(GeneratedSource unit) => ShaderStageNames.Suffix(unit.Stage);

    static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

    // bin/Debug/net10.0 -> Tests project root -> Fixtures
    static string FixturePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", file);
}
