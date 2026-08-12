// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Raven.CodeGen;
using Xunit;

namespace Tests;

/// <summary>
///     Shared plumbing for the SPIR-V tests. Every module these produce is handed to
///     <c>spirv-val</c>, because a binary format gives no other signal: a listing can
///     read perfectly and still be a module no driver would load.
/// </summary>
public static class SpirvTestBase {
    /// <summary>
    ///     True when the reference validator is installed. It is not a build
    ///     dependency, so its absence is reported rather than silently passing.
    /// </summary>
    public static bool ValidatorAvailable => FindTool("spirv-val") is not null;

    /// <summary>Generates, validates, and returns the single unit.</summary>
    public static GeneratedSource One(string source) {
        var unit = Assert.Single(CodeGenTestBase.GenerateClean(source, "spirv"));
        Validate(unit);
        return unit;
    }

    /// <summary>The assembly listing of a shader with one fragment entry point.</summary>
    public static string Fragment(string body, string members = "", string signature = "func Fragment(): float4") =>
        One(
                $$"""
                  package A

                  shader S {
                  {{members}}
                      [FragmentShader]
                      {{signature}} {
                  {{body}}
                      }
                  }

                  """
            )
            .Code;

    /// <summary>
    ///     The id a listing gave this name — <c>%11</c> for <c>OpName %11 "albedo"</c>.
    /// </summary>
    /// <remarks>
    ///     Ids are assigned as the module is built, so a test that wants to assert about one
    ///     variable's decorations has to look it up rather than hard-code a number that the
    ///     next emitter change would shift.
    /// </remarks>
    public static string IdNamed(string listing, string name) {
        ArgumentNullException.ThrowIfNull(listing);

        var line = Assert.Single(
            listing.Split('\n'),
            l => l.StartsWith("OpName ", StringComparison.Ordinal)
                && l.EndsWith($" \"{name}\"", StringComparison.Ordinal)
        );

        return line.Split(' ')[1];
    }

    /// <summary>Runs the reference validator over a unit, failing the test if it objects.</summary>
    /// <remarks>
    ///     The target environment is the module's own: a unit that traces rays is emitted as
    ///     SPIR-V 1.4 — the floor <c>SPV_KHR_ray_query</c> sets — and validating it as Vulkan 1.0
    ///     would report the version rather than the module. Read off the header word instead of a
    ///     flag so the validator always judges what was actually emitted.
    /// </remarks>
    public static void Validate(GeneratedSource unit) {
        Assert.NotNull(unit.Binary);

        if (FindTool("spirv-val") is not { } validator) {
            return;
        }

        var environment = BitConverter.ToUInt32(unit.Binary, 4) >= 0x00010400
            ? "vulkan1.1spv1.4"
            : "vulkan1.0";

        var path = Path.Combine(Path.GetTempPath(), $"raven_{Guid.NewGuid():n}.spv");
        File.WriteAllBytes(path, unit.Binary);

        try {
            var process = Process.Start(
                new ProcessStartInfo(validator, ["--target-env", environment, path]) {
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            )!;

            process.WaitForExit();
            var log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            Assert.True(process.ExitCode == 0, $"spirv-val rejected {unit.Name}:\n{log}\n\n{unit.Code}");
        } finally {
            File.Delete(path);
        }
    }

    public static string? FindTool(string name) {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin"]);

        foreach (var directory in directories) {
            if (string.IsNullOrWhiteSpace(directory)) {
                continue;
            }

            // ⚠ With Windows' suffix as well as without. A validator on PATH as spirv-val.exe is not
            // a file called spirv-val, so asking only for the bare name reports "not installed" for
            // one that is — and that reads as a skip rather than as the hole it is.
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
