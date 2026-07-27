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
        var root = new RootCommand("Vixen — import a project, build its content, check its health.");

        root.Subcommands.Add(Import(output, error));
        root.Subcommands.Add(Content(output, error));
        root.Subcommands.Add(Doctor(output, error));

        return root;
    }

    static Option<string?> ProjectOption() =>
        new("--project", "-p") {
            Description = "The project directory. Default: the working directory or the nearest ancestor with an Assets/ folder."
        };

    static Option<string> TargetOption() =>
        new("--target", "-t") {
            Description = "Which build target — Windows, Linux, MacOS, Android, iOS, Web, optionally narrowed as Android/Vulkan.",
            DefaultValueFactory = _ => Project.HostTarget
        };

    static Command Import(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var target = TargetOption();
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Name every asset, not only the ones with something to say." };

        var command = new Command("import", "Import everything in the project that has changed.") {
            project,
            target,
            verbose
        };

        command.SetAction(async (parseResult, cancellationToken) => {
                var writer = output ?? Console.Out;

                if (!Project.TryOpen(parseResult.GetValue(project), out var opened, out var why)) {
                    (error ?? Console.Error).WriteLine(why);
                    return (int)ExitCode.UsageError;
                }

                var summary = await ImportRunner.RunAsync(
                        opened,
                        parseResult.GetRequiredValue(target),
                        writer,
                        parseResult.GetValue(verbose),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                ImportRunner.Report(summary, writer);
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

        var outputDirectory = new Option<string?>("--output", "-o") {
            Description = "Where to write the bundles and the catalog. Default: Build/<target>."
        };

        var command = new Command("build", "Pack imported content into bundles and write the catalog.") {
            project,
            target,
            outputDirectory
        };

        command.SetAction(async (parseResult, cancellationToken) => {
                var writer = output ?? Console.Out;

                if (!Project.TryOpen(parseResult.GetValue(project), out var opened, out var why)) {
                    (error ?? Console.Error).WriteLine(why);
                    return (int)ExitCode.UsageError;
                }

                var forTarget = parseResult.GetRequiredValue(target);

                // Imported first, always. It is incremental, so it costs nothing when nothing has
                // changed — and a build that packed a stale artefact because somebody forgot a step
                // is a bug report about the wrong thing.
                var summary = await ImportRunner.RunAsync(opened, forTarget, writer, false, cancellationToken)
                    .ConfigureAwait(false);

                ImportRunner.Report(summary, writer);

                if (summary.Failed > 0) {
                    (error ?? Console.Error).WriteLine(
                        $"{summary.Failed} asset(s) failed to import, so there is nothing to pack for them."
                    );

                    return (int)ExitCode.Failed;
                }

                var directory = parseResult.GetValue(outputDirectory) is { Length: > 0 } named
                    ? Path.GetFullPath(named)
                    : opened.DefaultOutput(forTarget);

                return (int)(ContentBuildRunner.Run(opened, forTarget, directory, writer)
                    ? ExitCode.Success
                    : ExitCode.Failed);
            }
        );

        return command;
    }

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
}
