// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Transpile;

namespace Vixen.Raven.Cli;

/// <summary>The <c>raven</c> command line.</summary>
public static class RavenCommand {
    public static RootCommand Create(TextWriter? output = null, TextWriter? error = null) {
        // ⚠ Here rather than in Program.cs, and before the subcommand is built.
        //
        // Before, because `--target` is validated by `AcceptOnlyFromAmong(TargetBackends.Names)`,
        // which is read at construction: register afterwards and `essl` works while `--target essl`
        // is refused as an unknown value, a difference visible only in the parse error.
        //
        // Here, because this is the method the tests call. The registration is the ONE thing that
        // makes the ESSL backend reachable at all — `Vixen.Raven` does not reference the project it
        // lives in — so putting it in `Program.cs` would leave it untested by construction, which is
        // this repository's commonest defect wearing a different hat.
        EsslBackend.Register();

        var root = new RootCommand("Raven — universal shader compiler");
        root.Subcommands.Add(Compile(output, error));
        return root;
    }

    static Command Compile(TextWriter? output, TextWriter? error) {
        // One input, not a greedy list: a variadic argument followed by another
        // positional one cannot be split unambiguously. The driver still takes a
        // list, because a compilation is many trees — and `--source` is how a caller
        // adds to it without making the positionals ambiguous.
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

        var define = new Option<string[]>("--define", "-D") {
            Description =
                "Set a [Permutation] key: --define UseSkinning=true, --define TapCount=8. "
                + "A bare name means true. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var compose = new Option<string[]>("--compose", "-C") {
            Description =
                "Fill a compose slot: --compose diffuse=Lambert. Qualify with the shader when two "
                + "declare the same slot name: Lit.diffuse=Lambert. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var reference = new Option<string[]>("--reference", "-r") {
            Description =
                "Bind against a compiled library: --reference Core/Math.rvnlib. Its declarations and "
                + "lowered bodies are linked in without its source being reparsed. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var source = new Option<string[]>("--source", "-s") {
            Description =
                "Another source file in the same compilation: --source Core/Math.rvn. A package's "
                + "declarations are visible to its sibling files only within one compilation, so a "
                + "shader that imports a package needs that package's files here — or a .rvnlib in "
                + "--reference. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var shader = new Option<string[]>("--shader") {
            Description =
                "Write only these shaders: --shader Terrain. The compilation is still every input, "
                + "because that is what makes the imports resolve; this is what is emitted from it. "
                + "Repeatable. Default is every shader.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var emitLibrary = new Option<bool>("--emit-library") {
            Description =
                "Write a .rvnlib for these inputs instead of generating for a target — the compiled "
                + "library other shaders reference. Output names the file."
        };

        var emitIr = new Option<bool>("--emit-ir") { Description = "Also write the target-independent IR dump." };

        var emitListing = new Option<bool>("--emit-listing") {
            Description = "For a binary target, also write the readable listing (.spvasm) beside the bytes."
        };

        var emitEffect = new Option<bool>("--emit-effect") {
            Description = "Also write a .rvnfx per shader — the compiled effect the runtime loads."
        };

        var emitReflection = new Option<bool>("--emit-reflection") {
            Description = "Also write the reflection (descriptor sets, offsets, parameters) as JSON."
        };

        var showCapabilities = new Option<bool>("--capabilities") {
            Description = "Print the target features each shader requires (Float64, Texture3D, …)."
        };

        var verbose = new Option<bool>("--verbose", "-v") { Description = "Name every file as it is written." };

        var noColor = new Option<bool>("--no-color") { Description = "Never colour the diagnostics." };

        var command = new Command("compile", "Compile shaders to a target language.") {
            input,
            outputPath,
            target,
            define,
            compose,
            reference,
            source,
            shader,
            emitLibrary,
            emitIr,
            emitListing,
            emitReflection,
            emitEffect,
            showCapabilities,
            verbose,
            noColor
        };

        command.SetAction(parseResult => (int)CompileDriver.Run(
                new() {
                    Inputs = [parseResult.GetRequiredValue(input), .. parseResult.GetValue(source) ?? []],
                    Output = parseResult.GetRequiredValue(outputPath),
                    Target = parseResult.GetRequiredValue(target),
                    Defines = parseResult.GetValue(define) ?? [],
                    Composes = parseResult.GetValue(compose) ?? [],
                    References = parseResult.GetValue(reference) ?? [],
                    Shaders = parseResult.GetValue(shader) ?? [],
                    EmitLibrary = parseResult.GetValue(emitLibrary),
                    EmitIr = parseResult.GetValue(emitIr),
                    EmitListing = parseResult.GetValue(emitListing),
                    EmitReflection = parseResult.GetValue(emitReflection),
                    EmitEffect = parseResult.GetValue(emitEffect),
                    ShowCapabilities = parseResult.GetValue(showCapabilities),
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
