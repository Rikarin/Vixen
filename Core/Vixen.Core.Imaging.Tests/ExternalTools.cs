// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>Finds and runs the outside implementations the conformance suites check against.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The point of these suites is that nothing in them was written here.</b> Every other
///         test in this assembly asserts against a value worked out from the specification by the
///         same person who wrote the code, so a misread of the spec produces a fixture that agrees
///         with the bug. An outside implementation is the only thing that catches that, and when
///         these suites were first run they found six defects the hand-computed fixtures had been
///         agreeing with for as long as they existed.
///     </para>
///     <para>
///         <b>Neither tool is vendored.</b> <c>ktx</c> comes from Khronos's KTX-Software
///         (<c>brew install ktx</c>) and the BCn oracle is built from
///         <see href="https://github.com/iOrange/bcdec">bcdec.h</see>, which
///         <c>Tools/Vixen.BcnOracle/build.sh</c> downloads into a cache outside the tree. Committing
///         either would put third-party source under this repository's attribution gate for no gain:
///         a developer-run check that says loudly when it did not run is worth more than a committed
///         copy of somebody else's decoder.
///     </para>
///     <para>
///         <b>What this prints on the day it does not run.</b> Absent tools <i>skip</i>, which xunit
///         counts and prints, rather than passing vacuously — and setting
///         <c>VIXEN_REQUIRE_EXTERNAL_TOOLS=1</c> turns every skip into a failure, which is what a
///         machine that is supposed to have the tools should set. The suites also assert their own
///         case counts, so a filter that quietly matched nothing is a failure too.
///     </para>
/// </remarks>
public static class ExternalTools {
    /// <summary>Set this to <c>1</c> and a missing tool fails instead of skipping.</summary>
    public const string RequireVariable = "VIXEN_REQUIRE_EXTERNAL_TOOLS";

    /// <summary>Overrides where the BCn oracle binary lives.</summary>
    public const string OracleVariable = "VIXEN_BCN_ORACLE";

    /// <summary>Whether a missing tool has to fail rather than skip.</summary>
    public static bool Required => Environment.GetEnvironmentVariable(RequireVariable) == "1";

    /// <summary>The Khronos <c>ktx</c> command line, if this machine has one.</summary>
    public static string? KtxTool { get; } = Locate("ktx");

    /// <summary>The compiled bcdec oracle, if it has been built.</summary>
    public static string? BcnOracle { get; } = LocateOracle();

    /// <summary>Skips — or fails, when the suite is required — because a tool is missing.</summary>
    /// <param name="tool">What could not be found.</param>
    /// <param name="how">How to get it.</param>
    public static void Missing(string tool, string how) {
        Assert.False(Required, $"{RequireVariable}=1 and {tool} is not available. {how}");
        Assert.Skip($"{tool} is not on this machine, so nothing external checked this. {how}");
    }

    /// <summary>Runs a command and collects what it said.</summary>
    /// <param name="path">The executable.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The exit code and the combined output.</returns>
    public static (int ExitCode, string Output) Run(string path, params string[] arguments) {
        var start = new ProcessStartInfo(path) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{path} did not start.");
        var output = new StringBuilder();

        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());
        process.WaitForExit();

        return (process.ExitCode, output.ToString());
    }

    /// <summary>Runs a command, writing to its input and reading its output as bytes.</summary>
    /// <param name="path">The executable.</param>
    /// <param name="input">What to write to its standard input.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>Its standard output.</returns>
    /// <exception cref="InvalidOperationException">It failed.</exception>
    public static byte[] Pipe(string path, ReadOnlySpan<byte> input, params string[] arguments) {
        var start = new ProcessStartInfo(path) {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{path} did not start.");

        process.StandardInput.BaseStream.Write(input);
        process.StandardInput.BaseStream.Flush();
        process.StandardInput.Close();

        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);

        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0) {
            throw new InvalidOperationException($"{path} exited {process.ExitCode}: {error}");
        }

        return buffer.ToArray();
    }

    /// <summary>A directory this run may write files into, emptied first.</summary>
    /// <param name="name">What to call it.</param>
    /// <returns>The directory.</returns>
    public static string Scratch(string name) {
        var path = Path.Combine(Path.GetTempPath(), "vixen-imaging-conformance", name);

        if (Directory.Exists(path)) {
            Directory.Delete(path, true);
        }

        Directory.CreateDirectory(path);

        return path;
    }

    static string? LocateOracle() {
        var declared = Environment.GetEnvironmentVariable(OracleVariable);

        if (!string.IsNullOrEmpty(declared)) {
            return File.Exists(declared) ? declared : null;
        }

        var cached = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "vixen",
            "bcn-oracle",
            "bcn-oracle"
        );

        return File.Exists(cached) ? cached : null;
    }

    static string? Locate(string tool) {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path)) {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator)) {
            if (directory.Length == 0) {
                continue;
            }

            var candidate = Path.Combine(directory, tool);

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        return null;
    }
}
