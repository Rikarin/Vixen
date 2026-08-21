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
///         compared. Neither is a build dependency: they are found on PATH, a case that cannot
///         run is <em>skipped</em>, and their absence is a failure of its own
///         (<c>SpirvDifferentialTests.The_oracle_is_installed_so_this_file_means_something</c>).
///         ⚠ It used to be neither — a missing tool made every case return early and report a
///         pass, and two of the three CI legs never installed shaderc, so the only differential
///         check on Raven's SPIR-V emitter was green on both without ever running.
///     </para>
///     <para>
///         The command-line tools rather than <c>Silk.NET.Shaderc</c>, deliberately, and that
///         package is <em>declined</em> rather than owed. The oracle is a test-time thing, and a
///         native NuGet asset would put shaderc's binaries in the restore graph of a project that
///         must never ship them — see docs/plan/01's register row and docs/plan/07 § C.
///     </para>
/// </remarks>
public static class ReferenceCompiler {
    /// <summary>Path to <c>glslc</c>, or null when it is not installed.</summary>
    public static string? Glslc => SpirvTestBase.FindTool("glslc");

    /// <summary>Path to <c>spirv-dis</c>, or null when it is not installed.</summary>
    public static string? SpirvDis => SpirvTestBase.FindTool("spirv-dis");

    /// <summary>What to tell the reader when the oracle cannot run.</summary>
    public const string HowToInstall =
        "glslc (brew install shaderc, apt-get install glslc) and spirv-dis (brew install "
        + "spirv-tools, apt-get install spirv-tools) are needed to run the differential oracle. "
        + "Neither is a build dependency, so this check was skipped.";

    /// <summary>True when both tools are present, so the oracle can run.</summary>
    public static bool Available => Glslc is not null && SpirvDis is not null;

    /// <summary>
    ///     Compiles GLSL to SPIR-V with <c>glslc</c>, targeting Vulkan 1.0 — the same target
    ///     Raven's own backend emits for. A unit that declares <c>GL_EXT_ray_query</c> targets
    ///     Vulkan 1.2 instead, because glslang refuses the extension below it; sniffed off the
    ///     source so every caller gets the right environment per shader without saying so.
    /// </summary>
    /// <remarks>
    ///     A failure here is a failure of Raven's GLSL, not of the test: it means the emitter
    ///     produced something a real GLSL front end rejects. The compiler's own message and the
    ///     source are both in the assertion for that reason.
    /// </remarks>
    public static byte[] GlslToSpirv(string glsl, ShaderStage stage) {
        ArgumentNullException.ThrowIfNull(glsl);
        Assert.NotNull(Glslc);

        var environment = glsl.Contains("GL_EXT_ray_query", StringComparison.Ordinal)
            ? "--target-env=vulkan1.2"
            : "--target-env=vulkan1.0";

        var stem = Path.Combine(Path.GetTempPath(), $"raven_oracle_{Guid.NewGuid():n}");
        var source = stem + "." + StageFlag(stage);
        var output = stem + ".spv";

        File.WriteAllText(source, glsl);

        try {
            var (exitCode, log) = Run(
                Glslc!,
                [environment, $"-fshader-stage={StageFlag(stage)}", source, "-o", output]
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
            ShaderStage.Fragment => "frag",
            ShaderStage.Geometry => "geom",
            ShaderStage.Compute => "comp",
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No glslc stage for this.")
        };

    static (int ExitCode, string Log) Run(string tool, string[] arguments) {
        var process = Process.Start(
            new ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true }
        )!;

        // ⚠ Both at once. Draining stdout to the end first is nearly right and deadlocks anyway: a
        // tool that fills the error pipe while this is still reading the output pipe blocks, and
        // glslc on a broken shader is exactly that tool. spirv-dis filling stdout is the case that
        // hung the Windows leg — see GoldenSpirvTests.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        process.WaitForExit();

        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
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
