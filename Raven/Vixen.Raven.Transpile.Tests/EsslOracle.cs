// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Raven.Symbols;
using Xunit;

namespace Vixen.Raven.Transpile.Tests;

/// <summary>
///     A real GLSL ES front end, used as the oracle for everything this suite claims.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why <c>glslangValidator</c> and not <c>glslc</c>.</b> They are the same front end, but
///         <c>glslc</c> is shaderc's Vulkan-facing driver: it compiles <em>to SPIR-V</em>, and to do
///         that it turns on Vulkan semantics, which is the one thing this suite must not have. A
///         Vulkan-semantics compile accepts <c>layout(set = …)</c> and separate textures and
///         samplers — the exact constructs whose rejection is the reason this project exists. So a
///         "green" from <c>glslc</c> here would be a green from the wrong compiler.
///         <c>glslangValidator</c> with no target flag reads the <c>#version 300 es</c> line and
///         applies GLSL ES 3.00 rules, which is what a phone will do.
///     </para>
///     <para>
///         ⚠ <b>It is a reference front end and not a driver.</b> It will not catch a
///         vendor-specific limit — a uniform count, a varying budget, ANGLE's own translation to
///         D3D. What it does catch is the whole class this project is about: constructs that are not
///         in the ES grammar at all. A driver-level check needs a device and belongs beside the
///         other GPU gates in <c>Platform/</c>, and is not claimed here.
///     </para>
///     <para>
///         Found on PATH and never restored, like <c>ReferenceCompiler</c>'s tools, for the reason
///         docs/plan/01 gives about not putting a compiler's binaries in a restore graph.
///         ⚠ Its absence is a <em>failure</em> rather than a skip — see
///         <c>CrossCompilationTests.The_oracle_is_installed_so_this_file_means_something</c>. The
///         alternative has already happened once in this repository: two CI legs never installed
///         shaderc, every case returned early, and the differential oracle reported a pass on both
///         without ever running.
///     </para>
/// </remarks>
static class EsslOracle {
    /// <summary>Where <c>glslangValidator</c> is, or null when it is not installed.</summary>
    public static string? Validator => FindTool("glslangValidator");

    /// <summary>What to tell the reader when the oracle cannot run.</summary>
    public const string HowToInstall =
        "glslangValidator (brew install glslang, apt-get install glslang-tools) is the GLSL ES front "
        + "end this suite checks its output against. It is not a build dependency.";

    /// <summary>
    ///     Runs the ES front end over one shader.
    /// </summary>
    /// <param name="source">The GLSL, starting with its own <c>#version</c> line.</param>
    /// <param name="stage">Which stage it is — the file name is what tells the tool.</param>
    /// <returns>Whether it compiled, and everything the tool said.</returns>
    public static (bool Accepted, string Log) Validate(string source, ShaderStage stage) {
        ArgumentNullException.ThrowIfNull(source);
        Assert.NotNull(Validator);

        // ⚠ The extension is the stage. glslangValidator has -S, but it infers from the name first
        // and a `.glsl` file with no -S is "unknown stage"; naming the file for the stage is what
        // every other caller of this tool in the repository does.
        var path = Path.Combine(
            Path.GetTempPath(),
            $"vixen_essl_{Guid.NewGuid():n}.{Suffix(stage)}"
        );

        File.WriteAllText(path, source);

        try {
            var process = Process.Start(
                new ProcessStartInfo(Validator!, [path]) {
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            );

            Assert.NotNull(process);

            var log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode == 0, log);
        } finally {
            File.Delete(path);
        }
    }

    static string Suffix(ShaderStage stage) =>
        stage switch {
            ShaderStage.Vertex => "vert",
            ShaderStage.Fragment => "frag",
            ShaderStage.Geometry => "geom",
            ShaderStage.Compute => "comp",
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No GLSL stage suffix for this.")
        };

    /// <summary>
    ///     Looks a tool up on PATH.
    /// </summary>
    /// <remarks>
    ///     The two Homebrew prefixes are appended for the reason <c>SpirvTestBase.FindTool</c> gives:
    ///     a GUI-launched test runner on macOS does not inherit the shell's PATH, so a tool that is
    ///     plainly installed is invisible to it. Windows' <c>.exe</c> is tried too — asking only for
    ///     the bare name reports "not installed" for one that is, and that reads as a skip rather
    ///     than as the hole it is.
    /// </remarks>
    static string? FindTool(string name) {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin"]);

        foreach (var directory in directories) {
            if (string.IsNullOrWhiteSpace(directory)) {
                continue;
            }

            foreach (var candidate in OperatingSystem.IsWindows()
                         ? [Path.Combine(directory, name + ".exe"), Path.Combine(directory, name)]
                         : new[] { Path.Combine(directory, name) }) {
                if (File.Exists(candidate)) {
                    return candidate;
                }
            }
        }

        return null;
    }
}
