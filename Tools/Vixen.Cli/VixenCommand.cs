// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Vixen.ContentServer;
using Vixen.Core.IO;

namespace Vixen.Cli;

/// <summary>The <c>vixen</c> command line.</summary>
/// <remarks>
///     <para>
///         Every command takes its writers rather than reaching for <see cref="Console" />, so the
///         tests drive the same parser a person does and read what it said. A CLI whose behaviour can
///         only be checked by running a process is one whose behaviour is not checked.
///     </para>
///     <para>
///         <b><c>new</c>, <c>run</c> and <c>build</c> are missing rather than stubbed.</b> They are
///         the three that need something that does not exist yet: <c>new</c> needs the
///         <c>Vixen.Sdk</c> package layout to scaffold against, and <c>build</c> and <c>run</c> wrap
///         <c>dotnet publish</c> of a game project, which is doc 17's shipping story. A verb that
///         parses and then apologises is worse than one that is not there, because a build script can
///         only discover the second kind.
///     </para>
/// </remarks>
public static class VixenCommand {
    /// <summary>Builds the command line.</summary>
    /// <param name="output">Where commands write, or <see langword="null" /> for the console.</param>
    /// <param name="error">Where commands complain, or <see langword="null" /> for the console.</param>
    /// <returns>The root command.</returns>
    public static RootCommand Create(TextWriter? output = null, TextWriter? error = null) {
        var root = new RootCommand("Vixen — start a project, import it, build its content, publish it, run it.");

        root.Subcommands.Add(Import(output, error));
        root.Subcommands.Add(Content(output, error));
        root.Subcommands.Add(Doctor(output, error));
        root.Subcommands.Add(New(output, error));
        root.Subcommands.Add(Build(output, error));
        root.Subcommands.Add(Run(output, error));

        return root;
    }

    static Option<string?> ProjectOption() =>
        new("--project", "-p") {
            Description = "The project directory. Default: the working directory or the nearest ancestor with an Assets/ folder."
        };

    /// <summary>
    ///     How diagnostics come out. <c>msbuild</c> is what <c>Vixen.Sdk</c> passes, so that what an
    ///     importer said reaches the IDE's error list instead of scrolling past in a build log.
    /// </summary>
    static Option<DiagnosticFormat> FormatOption() =>
        new("--format") {
            Description = "How to write diagnostics: text for a person, msbuild for a build.",
            DefaultValueFactory = _ => DiagnosticFormat.Text
        };

    static Option<string> TargetOption() =>
        new("--target", "-t") {
            Description = "Which build target — Windows, Linux, MacOS, Android, iOS, Web, optionally narrowed as Android/Vulkan.",
            DefaultValueFactory = _ => Project.HostTarget
        };

    static Command Import(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var target = TargetOption();
        var format = FormatOption();
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Name every asset, not only the ones with something to say." };

        // Off by default. It costs a process start and a copy of every artefact over a pipe, and
        // what it buys — surviving an importer that takes its process down rather than one that
        // throws — is rare enough to be worth asking for rather than paying for always.
        var isolated = new Option<bool>("--isolated") {
            Description = "Run importers in worker processes, so a crash in one fails that asset instead of the run."
        };

        var command = new Command("import", "Import everything in the project that has changed.") {
            project,
            target,
            format,
            verbose,
            isolated
        };

        command.SetAction(async (parseResult, cancellationToken) => {
                var writer = output ?? Console.Out;

                if (!Project.TryOpen(parseResult.GetValue(project), out var opened, out var why)) {
                    Complain(parseResult.GetValue(format), error ?? Console.Error, why);
                    return (int)ExitCode.UsageError;
                }

                var diagnostics = new DiagnosticWriter(writer, parseResult.GetValue(format), opened.Paths.Root);

                var summary = await ImportRunner.RunAsync(
                        opened,
                        parseResult.GetRequiredValue(target),
                        diagnostics,
                        parseResult.GetValue(verbose),
                        parseResult.GetValue(isolated),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                ImportRunner.Report(summary, diagnostics);
                return (int)(summary.Failed > 0 ? ExitCode.Failed : ExitCode.Success);
            }
        );

        return command;
    }

    static Command Content(TextWriter? output, TextWriter? error) =>
        new("content", "Build and serve the project's addressable content.") {
            ContentBuild(output, error),
            ContentServe(output, error)
        };

    static Command ContentBuild(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var target = TargetOption();
        var format = FormatOption();

        var outputDirectory = new Option<string?>("--output", "-o") {
            Description = "Where to write the bundles and the catalog. Default: Build/<target>."
        };

        // For a caller that has provably just imported — Vixen.Sdk runs `vixen import` as its own
        // build step so that generated C# exists before the compiler runs, and importing again here
        // would be the same scan and the same ten thousand decisions a second time in one build.
        var noImport = new Option<bool>("--no-import") {
            Description = "Do not import first. Only for a caller that has already done it in this build."
        };

        // Which Raven backend the shader bundle is compiled for. Not derived from --target, because
        // the mapping is a device's business rather than a platform's: an Android build may want
        // SPIR-V for Vulkan or GLSL for GLES, and the same is true of a desktop build running under
        // an OpenGL backend.
        var shaderTarget = new Option<string>("--shader-target") {
            Description = "Which Raven backend to compile the shader bundle for: spirv or glsl.",
            DefaultValueFactory = _ => ShaderBuildRunner.DefaultBackend
        };

        var command = new Command("build", "Pack imported content into bundles and write the catalog.") {
            project,
            target,
            format,
            outputDirectory,
            noImport,
            shaderTarget
        };

        command.SetAction(async (parseResult, cancellationToken) => {
                var writer = output ?? Console.Out;
                var chosen = parseResult.GetValue(format);

                if (!Project.TryOpen(parseResult.GetValue(project), out var opened, out var why)) {
                    Complain(chosen, error ?? Console.Error, why);
                    return (int)ExitCode.UsageError;
                }

                var forTarget = parseResult.GetRequiredValue(target);
                var diagnostics = new DiagnosticWriter(writer, chosen, opened.Paths.Root);

                // Imported first by default. It is incremental, so it costs nothing when nothing has
                // changed — and a build that packed a stale artefact because somebody forgot a step
                // is a bug report about the wrong thing.
                if (!parseResult.GetValue(noImport)) {
                    var summary = await ImportRunner.RunAsync(opened, forTarget, diagnostics, false, isolated: false, cancellationToken)
                        .ConfigureAwait(false);

                    ImportRunner.Report(summary, diagnostics);

                    if (summary.Failed > 0) {
                        Complain(
                            chosen,
                            error ?? Console.Error,
                            $"{summary.Failed} asset(s) failed to import, so there is nothing to pack for them."
                        );

                        return (int)ExitCode.Failed;
                    }
                } else {
                    // The scan still has to run: the planner reads the asset index, and a caller
                    // that skipped the import did not skip having a project.
                    opened.Database.Scan();
                }

                var directory = parseResult.GetValue(outputDirectory) is { Length: > 0 } named
                    ? Path.GetFullPath(named)
                    : opened.DefaultOutput(forTarget);

                if (!ContentBuildRunner.Run(opened, forTarget, directory, diagnostics)) {
                    return (int)ExitCode.Failed;
                }

                // After the content, and into the same directory. A shipping build's only effect
                // source is this file, so it belongs wherever the catalog it ships beside does.
                return (int)(ShaderBuildRunner.Run(opened, parseResult.GetRequiredValue(shaderTarget), directory, diagnostics)
                    ? ExitCode.Success
                    : ExitCode.Failed);
            }
        );

        return command;
    }

    /// <summary>
    ///     Writes the tool's own failure — the kind that is about the invocation rather than about an
    ///     asset — in whichever form the caller asked for.
    /// </summary>
    /// <remarks>
    ///     It has to carry a code in the MSBuild form too. A build that stops because the tool could
    ///     not find a project and says so only in prose leaves MSBuild reporting "exited with code 2"
    ///     and nothing else, which is the least actionable failure a build can have.
    /// </remarks>
    static void Complain(DiagnosticFormat format, TextWriter error, string message) =>
        error.WriteLine(format == DiagnosticFormat.MSBuild ? $"error {DiagnosticCode.Usage}: {message}" : message);

    static Command ContentServe(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var target = TargetOption();

        var root = new Option<string?>("--root", "-r") {
            Description = "What to serve. Default: the project's build for the target."
        };

        var port = new Option<int>("--port") { Description = "What to listen on.", DefaultValueFactory = _ => 8080 };

        var any = new Option<bool>("--any") {
            Description = "Bind every interface rather than localhost, which is what a phone on the same network needs."
        };

        var command = new Command("serve", "Serve a content build over HTTP, so a device can be pointed at this machine.") {
            project,
            target,
            root,
            port,
            any
        };

        command.SetAction(async (parseResult, cancellationToken) => {
                var writer = output ?? Console.Out;
                string directory;

                if (parseResult.GetValue(root) is { Length: > 0 } named) {
                    directory = Path.GetFullPath(named);
                } else {
                    if (!Project.TryOpen(parseResult.GetValue(project), out var opened, out var why)) {
                        (error ?? Console.Error).WriteLine(why);
                        return (int)ExitCode.UsageError;
                    }

                    directory = opened.DefaultOutput(parseResult.GetRequiredValue(target));
                }

                if (!Directory.Exists(directory)) {
                    (error ?? Console.Error).WriteLine(
                        $"There is no content build at '{directory}'. Run `vixen content build` first."
                    );

                    return (int)ExitCode.Failed;
                }

                var files = new VirtualFileSystem();
                files.Mount(new("/content"), new PhysicalFileProvider(directory, isReadOnly: true));

                var server = new Vixen.ContentServer.ContentServer(files, new("/content"));
                using var listener = new ContentServerHost(server, parseResult.GetValue(port), parseResult.GetValue(any) ? "+" : "localhost");

                writer.WriteLine($"Serving {directory} at {listener.Prefix}");
                writer.WriteLine("A development server: no TLS, no authentication. Ctrl-C to stop.");

                try {
                    await listener.RunAsync(cancellationToken).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    // Ctrl-C, which is how this is meant to end.
                }

                writer.WriteLine($"Stopped after {server.Served} requests.");
                return (int)ExitCode.Success;
            }
        );

        return command;
    }

    static Command Doctor(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var target = TargetOption();

        var command = new Command("doctor", "Say what is wrong with the project, and change nothing.") {
            project,
            target
        };

        command.SetAction(parseResult => {
                var writer = output ?? Console.Out;

                if (!Project.TryOpen(parseResult.GetValue(project), out var opened, out var why)) {
                    (error ?? Console.Error).WriteLine(why);
                    return (int)ExitCode.UsageError;
                }

                var forTarget = parseResult.GetRequiredValue(target);

                var findings = DoctorRunner.Examine(opened, forTarget, opened.DefaultOutput(forTarget));

                return (int)(DoctorRunner.Report(findings, writer) ? ExitCode.Success : ExitCode.Failed);
            }
        );

        return command;
    }

    static Option<string> VariantOption() =>
        new("--variant") {
            Description = "Which build variant — Debug, Development, Release or Server. See docs/plan/17.",
            DefaultValueFactory = _ => "Release"
        };

    /// <summary>
    ///     <c>vixen new</c> — write a project.
    /// </summary>
    /// <remarks>
    ///     The name defaults to the directory's, because `vixen new game` inside a directory somebody
    ///     just made is the shortest thing that could work and is what they will type.
    /// </remarks>
    static Command New(TextWriter? output, TextWriter? error) {
        var template = new Argument<Template>("template") {
            Description = "What to write: game or library."
        };

        var name = new Argument<string?>("name") {
            Description = "The project's name. Default: the output directory's name.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var directory = new Option<string?>("--output", "-o") {
            Description = "Where to write it. Default: the working directory."
        };

        var command = new Command("new", "Write a new Vixen project.") { template, name, directory };

        command.SetAction(parseResult => {
                var where = Path.GetFullPath(parseResult.GetValue(directory) ?? Environment.CurrentDirectory);

                var chosen = parseResult.GetValue(name) is { Length: > 0 } given
                    ? given
                    : new DirectoryInfo(where).Name;

                return (int)ScaffoldRunner.Run(
                    parseResult.GetRequiredValue(template),
                    chosen,
                    where,
                    output ?? Console.Out
                );
            }
        );

        return command;
    }

    /// <summary>
    ///     <c>vixen build</c> — content, then publish.
    /// </summary>
    /// <remarks>
    ///     The ordering is the reason this command exists rather than a note in a README: the content
    ///     build has to run before the publish, or the publish copies whatever was in the output
    ///     directory from last time — which is a stale-content bug that looks like a caching bug.
    /// </remarks>
    static Command Build(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var target = TargetOption();
        var variant = VariantOption();
        var format = FormatOption();

        var into = new Option<string?>("--output", "-o") {
            Description = "Where the artefact goes. Default: Build/<target>/."
        };

        var noContent = new Option<bool>("--no-content") {
            Description = "Publish without importing or rebuilding the content first."
        };

        var command = new Command("build", "Build the content and publish the application.") {
            project, target, variant, format, into, noContent
        };

        command.SetAction(async (parseResult, cancellationToken) => {
                var writer = output ?? Console.Out;
                var complaints = error ?? Console.Error;

                if (!Project.TryOpen(parseResult.GetValue(project), out var opened, out var why)) {
                    Complain(parseResult.GetValue(format), complaints, why);
                    return (int)ExitCode.UsageError;
                }

                var forTarget = parseResult.GetRequiredValue(target);

                if (!PublishRunner.TryDescribe(forTarget, out var shape)) {
                    Complain(
                        parseResult.GetValue(format),
                        complaints,
                        $"'{forTarget}' is not a target this tool can publish. Try Windows, Linux, MacOS, Android or iOS."
                    );

                    return (int)ExitCode.UsageError;
                }

                if (!TryFindProjectFile(opened.Paths.Root, out var projectFile)) {
                    Complain(
                        parseResult.GetValue(format),
                        complaints,
                        $"There is no .csproj in '{opened.Paths.Root}', so there is nothing to publish. "
                        + "`vixen new game` writes one."
                    );

                    return (int)ExitCode.UsageError;
                }

                var artefact = parseResult.GetValue(into) ?? Path.Combine(opened.Paths.Root, "Build", forTarget);
                var chosenVariant = parseResult.GetRequiredValue(variant);

                var code = await BuildAsync(
                    opened, projectFile, forTarget, shape, chosenVariant, artefact,
                    parseResult.GetValue(format), parseResult.GetValue(noContent), writer, complaints, cancellationToken
                ).ConfigureAwait(false);

                if (code is ExitCode.Success) {
                    writer.WriteLine();
                    writer.WriteLine($"  {shape.Artefact} in {artefact}");
                }

                return (int)code;
            }
        );

        return command;
    }

    /// <summary>
    ///     <c>vixen run</c> — build for this machine, then play it.
    /// </summary>
    /// <remarks>
    ///     Debug by default and the host target always. A `run` that could cross-compile would be a
    ///     `run` that cannot launch what it produced, and the useful error is the one that says so
    ///     before spending a minute publishing.
    /// </remarks>
    static Command Run(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var variant = new Option<string>("--variant") {
            Description = "Which build variant. Default: Debug.",
            DefaultValueFactory = _ => "Debug"
        };

        var format = FormatOption();

        var passthrough = new Argument<string[]>("arguments") {
            Description = "Passed to the application. Put them after `--`.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var command = new Command("run", "Build for this machine and run it.") { project, variant, format, passthrough };

        command.SetAction(async (parseResult, cancellationToken) => {
                var writer = output ?? Console.Out;
                var complaints = error ?? Console.Error;

                if (!Project.TryOpen(parseResult.GetValue(project), out var opened, out var why)) {
                    Complain(parseResult.GetValue(format), complaints, why);
                    return (int)ExitCode.UsageError;
                }

                var forTarget = Project.HostTarget;
                PublishRunner.TryDescribe(forTarget, out var shape);

                if (!TryFindProjectFile(opened.Paths.Root, out var projectFile)) {
                    Complain(
                        parseResult.GetValue(format),
                        complaints,
                        $"There is no .csproj in '{opened.Paths.Root}', so there is nothing to run."
                    );

                    return (int)ExitCode.UsageError;
                }

                var artefact = Path.Combine(opened.Paths.Root, "Build", forTarget);
                var chosenVariant = parseResult.GetRequiredValue(variant);

                var code = await BuildAsync(
                    opened, projectFile, forTarget, shape, chosenVariant, artefact,
                    parseResult.GetValue(format), skipContent: false, writer, complaints, cancellationToken
                ).ConfigureAwait(false);

                if (code is not ExitCode.Success) {
                    return (int)code;
                }

                writer.WriteLine();

                return await PublishRunner.LaunchAsync(
                    artefact,
                    Path.GetFileNameWithoutExtension(projectFile),
                    parseResult.GetValue(passthrough) ?? [],
                    writer,
                    cancellationToken
                ).ConfigureAwait(false);
            }
        );

        return command;
    }

    /// <summary>Import, content build, publish — the sequence both `build` and `run` need.</summary>
    static async Task<ExitCode> BuildAsync(
        Project opened,
        string projectFile,
        string target,
        TargetShape shape,
        string variant,
        string artefact,
        DiagnosticFormat format,
        bool skipContent,
        TextWriter writer,
        TextWriter complaints,
        CancellationToken cancellationToken
    ) {
        var diagnostics = new DiagnosticWriter(writer, format, opened.Paths.Root);

        if (!skipContent) {
            var summary = await ImportRunner
                .RunAsync(opened, target, diagnostics, verbose: false, isolated: false, cancellationToken)
                .ConfigureAwait(false);

            ImportRunner.Report(summary, diagnostics);

            if (summary.Failed > 0) {
                return ExitCode.Failed;
            }

            // Into the project's own content directory rather than the artefact directory: the SDK's
            // targets are what copy it beside the binary, and doing it here as well would put two
            // copies in the publish and disagree about which is current.
            if (!ContentBuildRunner.Run(opened, target, opened.DefaultOutput(target), diagnostics)) {
                return ExitCode.Failed;
            }

            // A publish is the one build where a missing shader bundle is certainly wrong: there is
            // no compiler in what it produces, so a variant absent here is an object that never draws
            // for whoever installs it.
            if (!ShaderBuildRunner.Run(opened, ShaderBuildRunner.DefaultBackend, opened.DefaultOutput(target), diagnostics)) {
                return ExitCode.Failed;
            }
        }

        writer.WriteLine();

        var (code, _) = await PublishRunner
            .PublishAsync(projectFile, shape, variant, artefact, writer, cancellationToken)
            .ConfigureAwait(false);

        return code;
    }

    /// <summary>The one project file in the project root.</summary>
    /// <remarks>
    ///     Refuses when there is more than one rather than picking. A directory with two of them is a
    ///     solution, and guessing which is the game is how a tool publishes the wrong thing quietly.
    /// </remarks>
    static bool TryFindProjectFile(string root, out string projectFile) {
        var candidates = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly);
        projectFile = candidates.Length == 1 ? candidates[0] : string.Empty;

        return projectFile.Length > 0;
    }
}
