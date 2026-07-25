using System.Diagnostics;
using Vixen.Raven.CodeGen;
using Xunit;

namespace Tests;

/// <summary>
/// Shared plumbing for the SPIR-V tests. Every module these produce is handed to
/// <c>spirv-val</c>, because a binary format gives no other signal: a listing can
/// read perfectly and still be a module no driver would load.
/// </summary>
public static class SpirvTestBase {
    /// <summary>Generates, validates, and returns the single unit.</summary>
    public static GeneratedSource One(string source) {
        var unit = Assert.Single(CodeGenTestBase.GenerateClean(source, "spirv"));
        Validate(unit);
        return unit;
    }

    /// <summary>The assembly listing of a shader with one pixel entry point.</summary>
    public static string Pixel(string body, string members = "", string signature = "func Pixel(): float4") =>
        One($$"""
            package A

            shader S {
            {{members}}
                [PixelShader]
                {{signature}} {
            {{body}}
                }
            }

            """).Code;

    /// <summary>Runs the reference validator over a unit, failing the test if it objects.</summary>
    public static void Validate(GeneratedSource unit) {
        Assert.NotNull(unit.Binary);

        if (FindTool("spirv-val") is not { } validator) {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"raven_{Guid.NewGuid():n}.spv");
        File.WriteAllBytes(path, unit.Binary);

        try {
            var process = Process.Start(new ProcessStartInfo(validator, ["--target-env", "vulkan1.0", path]) {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;

            process.WaitForExit();
            var log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            Assert.True(process.ExitCode == 0, $"spirv-val rejected {unit.Name}:\n{log}\n\n{unit.Code}");
        } finally {
            File.Delete(path);
        }
    }

    /// <summary>
    /// True when the reference validator is installed. It is not a build
    /// dependency, so its absence is reported rather than silently passing.
    /// </summary>
    public static bool ValidatorAvailable => FindTool("spirv-val") is not null;

    public static string? FindTool(string name) {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin"]);

        foreach (var directory in directories) {
            if (string.IsNullOrWhiteSpace(directory)) {
                continue;
            }

            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        return null;
    }
}
