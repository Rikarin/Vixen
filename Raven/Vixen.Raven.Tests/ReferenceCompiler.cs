// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Raven.Symbols;
using Xunit;

namespace Tests;

/// <summary>
///     The reference GLSL and SPIR-V tools, used as oracles.
/// </summary>
/// <remarks>
///     <para>
///         <c>glslc</c> (from shaderc) compiles Raven's GLSL back to SPIR-V, and
///         <c>spirv-dis</c> disassembles both that and Raven's own module so the two can be
///         compared. Neither is a build dependency: they are found on PATH and their absence
///         is reported rather than silently passing.
///     </para>
///     <para>
///         The command-line tools rather than <c>Silk.NET.Shaderc</c>, deliberately. The oracle
///         is a test-time thing, and a native NuGet asset would put shaderc's binaries in the
///         restore graph of a project that must never ship them — docs/plan/01 lists shaderc as
///         a test oracle only.
///     </para>
/// </remarks>
public static class ReferenceCompiler {
    /// <summary>Path to <c>glslc</c>, or null when it is not installed.</summary>
    public static string? Glslc => SpirvTestBase.FindTool("glslc");

    /// <summary>Path to <c>spirv-dis</c>, or null when it is not installed.</summary>
    public static string? SpirvDis => SpirvTestBase.FindTool("spirv-dis");

    /// <summary>What to tell the reader when the oracle cannot run.</summary>
    public const string HowToInstall =
        "glslc (brew install shaderc) and spirv-dis (brew install spirv-tools) are needed to run the "
        + "differential oracle. Neither is a build dependency, so this check was skipped.";

    /// <summary>True when both tools are present, so the oracle can run.</summary>
    public static bool Available => Glslc is not null && SpirvDis is not null;

    /// <summary>
    ///     Compiles GLSL to SPIR-V with <c>glslc</c>, targeting Vulkan 1.0 — the same target
    ///     Raven's own backend emits for.
    /// </summary>
    /// <remarks>
    ///     A failure here is a failure of Raven's GLSL, not of the test: it means the emitter
    ///     produced something a real GLSL front end rejects. The compiler's own message and the
    ///     source are both in the assertion for that reason.
    /// </remarks>
    public static byte[] GlslToSpirv(string glsl, ShaderStage stage) {
        ArgumentNullException.ThrowIfNull(glsl);
        Assert.NotNull(Glslc);

        var stem = Path.Combine(Path.GetTempPath(), $"raven_oracle_{Guid.NewGuid():n}");
        var source = stem + "." + StageFlag(stage);
        var output = stem + ".spv";

        File.WriteAllText(source, glsl);

        try {
            var (exitCode, log) = Run(
                Glslc!,
                ["--target-env=vulkan1.0", $"-fshader-stage={StageFlag(stage)}", source, "-o", output]
            );

            Assert.True(exitCode == 0, $"glslc rejected Raven's GLSL:\n{log}\n\n{Numbered(glsl)}");
            return File.ReadAllBytes(output);
        } finally {
            File.Delete(source);
            File.Delete(output);
        }
    }

    /// <summary>Disassembles a module with <c>spirv-dis</c>, keeping its friendly names.</summary>
    /// <remarks>
    ///     Friendly names are the point: they are what let a variable in one module be matched
    ///     with the same variable in the other, without depending on ids that both compilers
    ///     assign in their own order.
    /// </remarks>
    public static string Disassemble(byte[] spirv) {
        ArgumentNullException.ThrowIfNull(spirv);
        Assert.NotNull(SpirvDis);

        var path = Path.Combine(Path.GetTempPath(), $"raven_dis_{Guid.NewGuid():n}.spv");
        File.WriteAllBytes(path, spirv);

        try {
            var (exitCode, log) = Run(SpirvDis!, [path]);
            Assert.True(exitCode == 0, $"spirv-dis failed:\n{log}");
            return log;
        } finally {
            File.Delete(path);
        }
    }

    /// <summary>The <c>-fshader-stage</c> value for a stage.</summary>
    static string StageFlag(ShaderStage stage) =>
        stage switch {
            ShaderStage.Vertex => "vert",
            ShaderStage.Pixel => "frag",
            ShaderStage.Geometry => "geom",
            ShaderStage.Compute => "comp",
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No glslc stage for this.")
        };

    static (int ExitCode, string Log) Run(string tool, string[] arguments) {
        var process = Process.Start(
            new ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true }
        )!;

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output + error);
    }

    /// <summary>Numbers the lines, so a compiler's "line 34" points at something.</summary>
    static string Numbered(string text) =>
        string.Join(
            "\n",
            text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select((line, i) => $"{i + 1,4}  {line}")
        );
}
