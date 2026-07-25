// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Vixen.Raven.CodeGen;

namespace Vixen.Raven.Cli;

/// <summary>The <c>raven</c> command line.</summary>
public static class RavenCommand {
    public static RootCommand Create(TextWriter? output = null, TextWriter? error = null) {
        var root = new RootCommand("Raven — universal shader compiler");
        root.Subcommands.Add(Compile(output, error));
        return root;
    }

    static Command Compile(TextWriter? output, TextWriter? error) {
        // One input, not a greedy list: a variadic argument followed by another
        // positional one cannot be split unambiguously. The driver still takes a
        // list, because a compilation is many trees.
        var input = new Argument<string>("input") { Description = "Shader source file (.rvn)." };

        var outputPath = new Argument<string>("output") {
            Description =
                "Where to write. A path with an extension names a single file; anything else is a directory, "
                + "which is what a shader with more than one stage needs."
        };

        var target = new Option<string>("--target", "-t") {
            Description = "Backend to generate for.", DefaultValueFactory = _ => "glsl"
        };

        target.AcceptOnlyFromAmong([.. TargetBackends.Names]);

        var emitIr = new Option<bool>("--emit-ir") { Description = "Also write the target-independent IR dump." };

        var emitListing = new Option<bool>("--emit-listing") {
            Description = "For a binary target, also write the readable listing (.spvasm) beside the bytes."
        };

        var verbose = new Option<bool>("--verbose", "-v") { Description = "Name every file as it is written." };

        var noColor = new Option<bool>("--no-color") { Description = "Never colour the diagnostics." };

        var command = new Command("compile", "Compile shaders to a target language.") {
            input,
            outputPath,
            target,
            emitIr,
            emitListing,
            verbose,
            noColor
        };

        command.SetAction(parseResult => (int)CompileDriver.Run(
                new() {
                    Inputs = [parseResult.GetRequiredValue(input)],
                    Output = parseResult.GetRequiredValue(outputPath),
                    Target = parseResult.GetRequiredValue(target),
                    EmitIr = parseResult.GetValue(emitIr),
                    EmitListing = parseResult.GetValue(emitListing),
                    Verbose = parseResult.GetValue(verbose),
                    UseColor = !parseResult.GetValue(noColor) && ColorIsWelcome()
                },
                output ?? Console.Out,
                error ?? Console.Error
            )
        );

        return command;
    }

    /// <summary>
    ///     Colour only when a terminal is there to read it. Redirected output is
    ///     usually a log or a build system, and NO_COLOR is the standing convention
    ///     for "I do not want escape codes".
    /// </summary>
    static bool ColorIsWelcome() =>
        !Console.IsErrorRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && Environment.GetEnvironmentVariable("TERM") != "dumb";
}
