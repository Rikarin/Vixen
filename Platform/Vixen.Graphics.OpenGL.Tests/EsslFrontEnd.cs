// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Xunit;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>
///     A real GLSL ES front end, so that what <see cref="GlslTranslator" /> emits is checked against
///     a compiler rather than against another regex.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists here and not only in <c>Vixen.Raven.Transpile.Tests</c>.</b> That suite
///         asks whether SPIRV-Cross's output compiles; this one asks whether the <em>translator's</em>
///         does, and they are different strings. Everything this file's caller adds — the version
///         directive, the precision defaults, the rebuilt layout qualifier list, the wrapped vertex
///         entry point — is added after cross-compilation and is invisible to that suite. Two of the
///         three defects on #475 lived in exactly that gap for as long as the only input this project
///         tested with was a shape nothing produces.
///     </para>
///     <para>
///         ⚠ <b>Its absence is a failure rather than a skip</b>, matching
///         <c>Vixen.Raven.Transpile.Tests.EsslOracle</c> and <c>SpirvDifferentialTests</c>: a tool
///         that is not installed makes every case here return early, and an instrument that reports
///         success on the day it does not run is the failure mode this repository has already shipped
///         once. <c>ci.yml</c> installs <c>glslang-tools</c> on Linux, <c>glslang</c> on macOS, and
///         the Vulkan SDK carries <c>glslangValidator.exe</c> on Windows.
///     </para>
///     <para>
///         ⚠ <c>glslangValidator</c> with no target flag, never <c>glslc</c>: <c>glslc</c> compiles to
///         SPIR-V and therefore turns on Vulkan semantics, which accepts <c>layout(set = …)</c> and a
///         separate texture and sampler — the exact constructs whose rejection is the point.
///     </para>
/// </remarks>
static class EsslFrontEnd {
    /// <summary>Where <c>glslangValidator</c> is, or null when it is not installed.</summary>
    public static string? Validator { get; } = FindTool("glslangValidator");

    /// <summary>What to tell the reader when the front end cannot run.</summary>
    public const string HowToInstall =
        "glslangValidator (brew install glslang, apt-get install glslang-tools) is the GLSL ES front "
        + "end this project checks GlslTranslator's output against. It is not a build dependency.";

    /// <summary>Runs the front end over one translated shader.</summary>
    /// <param name="source">The GLSL, starting with its own <c>#version</c> line.</param>
    /// <param name="stage">Which stage it is.</param>
    /// <returns>Whether it compiled, and everything the tool said.</returns>
    public static (bool Accepted, string Log) Validate(string source, ShaderStage stage) {
        ArgumentNullException.ThrowIfNull(source);
        Assert.NotNull(Validator);

        // ⚠ The extension is the stage. glslangValidator has -S, but it infers from the name first
        // and a `.glsl` file with no -S is "unknown stage".
        var path = Path.Combine(Path.GetTempPath(), $"vixen_gl_{Guid.NewGuid():n}.{Suffix(stage)}");
        File.WriteAllText(path, source);

        try {
            var process = Process.Start(
                new ProcessStartInfo(Validator!, [path]) {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
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
            ShaderStage.Compute => "comp",
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No GLSL stage suffix for this.")
        };

    /// <summary>Looks a tool up on PATH.</summary>
    /// <remarks>
    ///     The two Homebrew prefixes are appended for the reason <c>EsslOracle.FindTool</c> gives: a
    ///     GUI-launched test runner on macOS does not inherit the shell's PATH, so a tool that is
    ///     plainly installed is invisible to it — and that reads as "not installed" rather than as
    ///     the hole it is.
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
