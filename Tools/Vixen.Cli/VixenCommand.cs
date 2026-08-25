// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Globalization;
using Vixen.ContentServer;
using Vixen.Core.IO;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.Gameplay;
using Vixen.Editor.Core;
using Vixen.Live;

namespace Vixen.Cli;

/// <summary>The <c>vixen</c> command line.</summary>
/// <remarks>
///     <para>
///         Every command takes its writers rather than reaching for <see cref="Console" />, so the
///         tests drive the same parser a person does and read what it said. A CLI whose behaviour can
///         only be checked by running a process is one whose behaviour is not checked.
///     </para>
///     <para>
///         <b>A verb that parses and then apologises is worse than one that is not there, because a
///         build script can only discover the second kind.</b> That rule is why <c>new</c>, <c>run</c>
///         and <c>build</c> were absent until doc 17's shipping story gave them something to wrap, and
///         it is why <c>live up</c>, <c>live down</c> and <c>live upgrade --content</c> still are. It
///         is also why <c>remesh</c> has no <c>--bake</c>: docs/plan/41 § D16's example line has one
///         and the bake is R5's, so the flag arrives with the maps it would write.
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
        root.Subcommands.Add(Frame(output, error));
        root.Subcommands.Add(Doctor(output, error));
        root.Subcommands.Add(New(output, error));
        root.Subcommands.Add(Build(output, error));
        root.Subcommands.Add(Run(output, error));
        root.Subcommands.Add(Live(output, error));
        root.Subcommands.Add(RemeshCommand(output, error));
        root.Subcommands.Add(UnwrapCommand(output, error));
        root.Subcommands.Add(Uv(output, error));

        return root;
    }

    /// <summary>`vixen remesh` — docs/plan/41 § D16's CLI row, batchable over a directory.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The section's example line reads <c>--bake</c> and this verb has no such flag.</b>
    ///         The normal and displacement bake is R5's and the remesher does not perform it —
    ///         <c>RemeshSettings.TransferAttributes</c> is declared and unread by every stage. A flag
    ///         that parsed and wrote no maps is the apologising verb this file's header refuses; it
    ///         arrives when the bake does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Neither this nor <c>unwrap</c> opens a project</b>, unlike every other verb here.
    ///         The case both plans were written for is a directory of generated GLBs that are not
    ///         assets yet, so a file is the unit and <c>--project</c> would be a question with no
    ///         answer.
    ///     </para>
    /// </remarks>
    static Command RemeshCommand(TextWriter? output, TextWriter? error) {
        var (input, into) = Files("The mesh file to retopologise.");

        var quads = new Option<int>("--quads") {
            Description = "How many quads to aim for, per mesh.",
            DefaultValueFactory = _ => 5000
        };

        var adaptivity = new Option<float>("--adaptivity") {
            Description = "Zero for uniform squares, one to let curvature decide the density.",
            DefaultValueFactory = _ => 0.5f
        };

        var angle = new Option<float>("--feature-angle") {
            Description = "The angle, in degrees, above which a shared edge is a hard feature.",
            DefaultValueFactory = _ => 35f
        };

        var symmetry = new Option<string?>("--symmetry") {
            Description =
                "Mirror across an axis plane through the origin: x, y or z. docs/plan/41 § D11 — the "
                + "half is solved, the seam is snapped exactly and the other half is its reflection."
        };

        var seams = new Option<bool>("--keep-uv-seams") {
            Description = "Treat the source's coordinate seams as features, so an existing atlas survives."
        };

        var guides = new Option<string[]>("--guide") {
            Description = "A .vxspline whose curve the edge flow should follow. Repeatable.",
            AllowMultipleArgumentsPerToken = false,
            DefaultValueFactory = _ => []
        };

        var strength = new Option<float>("--guide-strength") {
            Description = "How hard the guides pull, in [0, 1].",
            DefaultValueFactory = _ => 1f
        };

        var command = new Command("remesh", "Retopologise a mesh file into all-quads.") {
            input, into, quads, adaptivity, angle, symmetry, seams, guides, strength
        };

        command.SetAction(parseResult => {
                var axis = parseResult.GetValue(symmetry);

                if (axis is { Length: > 0 } named && Axis(named) is null) {
                    (error ?? Console.Error).WriteLine($"'{named}' is not an axis. It is x, y or z.");

                    return (int) ExitCode.UsageError;
                }

                var pull = parseResult.GetValue(strength);

                return (int) GeometryRunner.Remesh(
                    parseResult.GetRequiredValue(input),
                    parseResult.GetRequiredValue(into),
                    new() {
                        TargetQuads = parseResult.GetValue(quads),
                        Adaptivity = parseResult.GetValue(adaptivity),
                        FeatureAngle = parseResult.GetValue(angle),
                        KeepUvSeams = parseResult.GetValue(seams),
                        Symmetry = axis is { Length: > 0 } ? Axis(axis) : null
                    },
                    [.. (parseResult.GetValue(guides) ?? []).Select(path => (path, pull))],
                    output ?? Console.Out,
                    error ?? Console.Error
                );
            }
        );

        return command;
    }

    /// <summary>`vixen unwrap` — docs/plan/42 § D13's CLI row, all three stages.</summary>
    static Command UnwrapCommand(TextWriter? output, TextWriter? error) {
        var (input, into) = Files("The mesh file to unwrap.");
        var (resolution, margin, density) = Atlas();

        var angle = new Option<float>("--feature-angle") {
            Description = "The angle, in degrees, a seam prefers to run along.",
            DefaultValueFactory = _ => 40f
        };

        var command = new Command("unwrap", "Cut, flatten and pack a mesh file's texture coordinates.") {
            input, into, resolution, margin, density, angle
        };

        command.SetAction(parseResult => (int) GeometryRunner.Unwrap(
                parseResult.GetRequiredValue(input),
                parseResult.GetRequiredValue(into),
                new() { FeatureAngle = parseResult.GetValue(angle) },
                new() {
                    Resolution = parseResult.GetValue(resolution),
                    Margin = parseResult.GetValue(margin),
                    TexelDensity = parseResult.GetValue(density)
                },
                output ?? Console.Out,
                error ?? Console.Error
            )
        );

        return command;
    }

    /// <summary>`vixen uv pack` — docs/plan/42's third stage alone, which is the UV-Packer comparison.</summary>
    /// <remarks>
    ///     ⚠ <b>A group with one verb rather than a bare <c>vixen pack</c>, because "pack" on its own
    ///     is what a build tool calls bundling content</b> — which this repository already has under
    ///     <c>content build</c>, and two unrelated meanings of one word on one command line is a
    ///     mistake somebody makes at four in the afternoon.
    /// </remarks>
    static Command Uv(TextWriter? output, TextWriter? error) {
        var (input, into) = Files("The mesh file whose islands to repack.");
        var (resolution, margin, density) = Atlas();

        var pack = new Command(
            "pack",
            "Repack a mesh file's existing islands. Seams and island shapes are untouched."
        ) { input, into, resolution, margin, density };

        pack.SetAction(parseResult => (int) GeometryRunner.Pack(
                parseResult.GetRequiredValue(input),
                parseResult.GetRequiredValue(into),
                new() {
                    Resolution = parseResult.GetValue(resolution),
                    Margin = parseResult.GetValue(margin),
                    TexelDensity = parseResult.GetValue(density)
                },
                output ?? Console.Out,
                error ?? Console.Error
            )
        );

        return new Command("uv", "Work with a mesh file's texture coordinates.") { pack };
    }

    /// <summary>The input and output arguments the three geometry verbs share.</summary>
    static (Argument<string> Input, Argument<string> Output) Files(string description) =>
        (
            new Argument<string>("input") { Description = description },
            new Argument<string>("output") {
                Description = "Where to write it. The extension chooses the format: .obj, .gltf or .glb."
            }
        );

    /// <summary>The three atlas options the two packing verbs share.</summary>
    /// <remarks>
    ///     ⚠ <b>The resolution is here because the margin is in texels</b> — docs/plan/42 § B4 and
    ///     § D8. A margin expressed as a fraction of UV space means a different number of texels on
    ///     every island of every atlas, which is how a bleed appears on the small islands only.
    /// </remarks>
    static (Option<int> Resolution, Option<int> Margin, Option<float> Density) Atlas() =>
        (
            new Option<int>("--resolution") {
                Description = "The atlas resolution the margin is counted in texels of.",
                DefaultValueFactory = _ => 1024
            },
            new Option<int>("--margin") {
                Description = "How many texels of empty space each island keeps around it.",
                DefaultValueFactory = _ => 4
            },
            new Option<float>("--density") {
                Description = "Texels per world unit to hold across every chart, or zero to fill the atlas.",
                DefaultValueFactory = _ => 0f
            }
        );

    /// <summary>An axis name as the plane through the origin it stands for.</summary>
    static Vixen.Core.Mathematics.Plane? Axis(string? name) =>
        name?.ToLowerInvariant() switch {
            "x" => new Vixen.Core.Mathematics.Plane(Vixen.Core.Mathematics.Vector3.UnitX, 0f),
            "y" => new Vixen.Core.Mathematics.Plane(Vixen.Core.Mathematics.Vector3.UnitY, 0f),
            "z" => new Vixen.Core.Mathematics.Plane(Vixen.Core.Mathematics.Vector3.UnitZ, 0f),
            _ => null
        };

    /// <summary>`vixen live` — doc 27 § Diagnostics, in a terminal, because 3 a.m.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>up</c>, <c>down</c> and <c>upgrade --content</c> are missing rather than
    ///         stubbed</b>, on the same rule the header states for <c>new</c>, <c>run</c> and
    ///         <c>build</c>. <c>up</c> and <c>down</c> stand a silo and a fleet up, which is a hosting
    ///         story doc 17 owns and which needs something to deploy; <c>upgrade --content</c> cannot
    ///         answer honestly until the content build records a shape per address, because
    ///         <c>ContentDiff</c> refuses every entry whose shape is unknown and a verb that always
    ///         says "this needs a drain" teaches an operator to stop reading it.
    ///     </para>
    ///     <para>
    ///         What is here is the four that can be answered today, and each of them talks to a real
    ///         cluster: <c>status</c>, <c>drain</c>, <c>explain</c> and the build half of
    ///         <c>upgrade</c>.
    ///     </para>
    /// </remarks>
    static Command Live(TextWriter? output, TextWriter? error) {
        var live = new Command("live", "Operate a running fleet: what it is doing, and what to do about it.");

        var map = new Option<string>("--map") { Description = "The map address, e.g. maps/queensdale.", Required = true };
        var region = new Option<string>("--region") { Description = "The latency zone.", Required = true };
        var version = new Option<string?>("--version") { Description = "The build and content pair, e.g. 0.1.0+00000000c0ffee." };
        var cluster = new Option<string?>("--cluster") { Description = "The Orleans cluster to reach. Default: localhost." };

        var status = new Command("status", "Every shard of a map: state, population, endpoint.");

        status.Options.Add(map);
        status.Options.Add(region);
        status.Options.Add(version);
        status.Options.Add(cluster);
        status.SetAction((parsed, token) =>
            LiveRunner.StatusAsync(Connect(parsed, cluster), KeyOf(parsed, map, region, version), output ?? Console.Out, token)
        );

        var shard = new Option<string>("--shard") { Description = "Which shard.", Required = true };
        var why = new Option<string>("--reason") { Description = "Why, for the log and the fleet view.", DefaultValueFactory = _ => "asked by an operator" };
        var drain = new Command("drain", "Move a shard's players out. Nobody is disconnected.");

        drain.Options.Add(shard);
        drain.Options.Add(why);
        drain.Options.Add(cluster);
        drain.SetAction((parsed, token) => {
            return LiveRunner.DrainAsync(
                Connect(parsed, cluster),
                ShardOf(parsed.GetValue(shard)),
                parsed.GetValue(why) ?? "",
                output ?? Console.Out,
                token
            );
        });

        var player = new Option<string>("--player") { Description = "account/character.", Required = true };
        var explain = new Command("explain", "Why a player went where they went.");

        explain.Options.Add(player);
        explain.Options.Add(map);
        explain.Options.Add(region);
        explain.Options.Add(version);
        explain.Options.Add(cluster);
        explain.SetAction((parsed, token) => {
            return LiveRunner.ExplainAsync(
                Connect(parsed, cluster),
                KeyOf(parsed, map, region, version),
                PlayerOf(parsed.GetValue(player)),
                output ?? Console.Out,
                token
            );
        });

        var upgrade = new Command("upgrade", "Read a region's rollout target, or point it somewhere. Rolling back is the same command.");

        upgrade.Options.Add(region);
        upgrade.Options.Add(version);
        upgrade.Options.Add(cluster);
        upgrade.SetAction((parsed, token) => {
            return LiveRunner.UpgradeAsync(
                Connect(parsed, cluster),
                parsed.GetRequiredValue(region),
                VersionOf(parsed.GetValue(version)),
                output ?? Console.Out,
                token
            );
        });

        live.Subcommands.Add(status);
        live.Subcommands.Add(drain);
        live.Subcommands.Add(explain);
        live.Subcommands.Add(upgrade);

        return live;
    }

    /// <summary>The cluster a `live` verb talks to.</summary>
    /// <remarks>
    ///     ⚠ <b>Replaced wholesale in tests.</b> <see cref="LiveOperationsFactory" /> is what a test
    ///     sets so that the verbs' formatting and argument handling are assertable without a silo —
    ///     the same reason the gate has <c>IFleetDirectory</c>.
    /// </remarks>
    internal static Func<string?, ILiveOperations>? LiveOperationsFactory { get; set; }

    static ILiveOperations Connect(System.CommandLine.ParseResult parsed, Option<string?> cluster) =>
        LiveOperationsFactory is { } factory
            ? factory(parsed.GetValue(cluster))
            : throw new InvalidOperationException(
                "No cluster client is configured. `vixen live` needs an orchestrator to talk to, and "
                + "wiring one up is the host application's job — see Vixen.Live.Orchestrator's README."
            );

    static ShardKey KeyOf(
        System.CommandLine.ParseResult parsed,
        Option<string> map,
        Option<string> region,
        Option<string?> version
    ) {
        return new(parsed.GetRequiredValue(map), parsed.GetRequiredValue(region), VersionOf(parsed.GetValue(version)));
    }

    // ⚠ The failures are deliberately silent HERE and loud in LiveRunner. A shard id that will not
    // parse becomes ShardId.None, which the runner recognises and explains — one place that knows how
    // to talk to a person, rather than an exception thrown out of an argument binder.
    static ShardId ShardOf(string? text) => ShardId.TryParse(text, out var shard) ? shard : ShardId.None;

    static PlayerKey PlayerOf(string? text) => PlayerKey.TryParse(text, out var player) ? player : PlayerKey.None;

    static RealmVersion VersionOf(string? text) =>
        RealmVersion.TryParse(text, out var version) ? version : RealmVersion.None;

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

    /// <summary>
    ///     <c>--assembly</c>, which is how a project's own scene components reach a build.
    /// </summary>
    /// <remarks>
    ///     Repeatable, because a game is rarely one assembly: a shared gameplay library and the
    ///     executable that uses it both declare components, and naming only the second would compile
    ///     half the level. See <see cref="GameAssemblies" /> for what loading one does.
    /// </remarks>
    static Option<string[]> AssemblyOption() =>
        new("--assembly") {
            Description =
                "A built game assembly whose components this build should know about. Repeatable. "
                + "Vixen.Sdk passes the project's own; a path that does not exist is skipped.",
            AllowMultipleArgumentsPerToken = false,
            DefaultValueFactory = _ => []
        };

    static Command Import(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var target = TargetOption();
        var format = FormatOption();
        var assemblies = AssemblyOption();
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Name every asset, not only the ones with something to say." };

        // Off by default. It costs a process start and a copy of every artefact over a pipe, and
        // what it buys — surviving an importer that takes its process down rather than one that
        // throws — is rare enough to be worth asking for rather than paying for always.
        var isolated = new Option<bool>("--isolated") {
            Description = "Run importers in worker processes, so a crash in one fails that asset instead of the run."
        };

        // Written here rather than by `content build`, and the ordering is the reason: Vixen.Sdk runs
        // the import BeforeTargets=CoreCompile precisely so generated C# exists before the compiler
        // reads its inputs, and the content build AfterTargets=Build. A constant emitted by the
        // second is one build out of date, every build.
        var addresses = new Option<string?>("--addresses") {
            Description = "Write the project's addresses as C# constants to this file."
        };

        var addressNamespace = new Option<string?>("--addresses-namespace") {
            Description = "What namespace the address constants go in. Default: the project's name."
        };

        // Off by default: the generated file would otherwise reference Vixen.Gameplay, and a game
        // that declined the gameplay libraries would get a file it cannot compile from a build step
        // it did not know it had turned on.
        var addressIds = new Option<bool>("--address-ids") {
            Description = "Emit a DefId beside each address. Needs the project to reference Vixen.Gameplay."
        };

        // ⚠ The flag that says this run is not the verdict, for the one caller that can know it.
        // Vixen.Sdk runs this BeforeTargets=CoreCompile so generated C# exists before the compiler
        // reads its inputs — which means that on a clean build the game assembly does not exist yet
        // and a level naming the game's own !BoxCollision cannot resolve it *here*, ever. The
        // authority is the content build after Build, which loads the assembly and does fail.
        //
        // What this changes is two things and not the amount of output. Nothing is dressed as an
        // MSBuild diagnostic, because a warning nobody can act on is one that teaches its readers to
        // skip the code it carries; and an asset that failed no longer ends the run, because the one
        // job this pass has — writing the addresses CoreCompile is about to read — happens after the
        // import and was being skipped exactly on the builds where it is advisory.
        var advisory = new Option<bool>("--advisory") {
            Description =
                "This pass runs before the game assembly is compiled, so an unresolved type is "
                + "expected: report findings as notes rather than diagnostics and do not fail."
        };

        var command = new Command("import", "Import everything in the project that has changed.") {
            project,
            target,
            format,
            verbose,
            isolated,
            assemblies,
            addresses,
            addressNamespace,
            addressIds,
            advisory
        };

        command.SetAction(async (parseResult, cancellationToken) => {
                var writer = output ?? Console.Out;

                if (!Project.TryOpen(parseResult.GetValue(project), out var opened, out var why)) {
                    Complain(parseResult.GetValue(format), error ?? Console.Error, why);
                    return (int)ExitCode.UsageError;
                }

                var isAdvisory = parseResult.GetValue(advisory);

                var diagnostics = new DiagnosticWriter(
                    writer,
                    parseResult.GetValue(format),
                    opened.Paths.Root,
                    isAdvisory
                );

                // Once, before anything, because the alternative is repeating it on every line: the
                // reason a line is a note rather than a warning is a property of the run and not of
                // the asset it is about.
                if (isAdvisory) {
                    diagnostics.Line(
                        "Advisory import: the game assembly is not compiled yet, so a level naming the "
                        + "game's own types cannot resolve them here. The content build after Build "
                        + "loads it and is the authority; anything real is reported there, as an error."
                    );
                }

                // Before anything is imported, so that a scene compiled in this run can name a
                // component the game declares.
                GameAssemblies.Load(
                    parseResult.GetValue(assemblies) ?? [],
                    complaint => diagnostics.Line($"  note    could not load {complaint}")
                );

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

                // ⚠ Not a return under --advisory, and the addresses below are the reason rather
                // than the exit code. This runs before CoreCompile to put generated C# in front of
                // it; returning here skips that write, so a project using address constants lost
                // them on precisely the build that had no assembly to resolve a level against —
                // which is a compile error about a missing constant, three steps from its cause.
                if (summary.Failed > 0 && !isAdvisory) {
                    return (int)ExitCode.Failed;
                }

                // After the import, because an address is a property of an asset that imported.
                if (parseResult.GetValue(addresses) is { Length: > 0 } into
                    && !AddressRunner.Run(
                        opened,
                        Path.GetFullPath(into),
                        parseResult.GetValue(addressNamespace) is { Length: > 0 } named
                            ? named
                            : AddressConstants.Identifier(Path.GetFileName(opened.Paths.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
                        parseResult.GetValue(addressIds),
                        diagnostics
                    )) {
                    return (int)ExitCode.Failed;
                }

                return (int)ExitCode.Success;
            }
        );

        return command;
    }

    static Command Content(TextWriter? output, TextWriter? error) =>
        new("content", "Build and serve the project's addressable content.") {
            ContentBuild(output, error),
            ContentLoose(output, error),
            ContentServe(output, error)
        };

    /// <summary>
    ///     <c>vixen content loose</c> — a catalog over what the import produced, with nothing packed.
    /// </summary>
    /// <remarks>
    ///     Doc 17's Editor variant, as a command. What a player pointed at the resulting directory
    ///     reads is the same address for the same asset a shipped build would give it, because the
    ///     same planner decided both; what it does not pay for is the pack. That is what makes
    ///     "change a texture and look at it" a re-import of one asset rather than a rebuild of every
    ///     group.
    /// </remarks>
    static Command ContentLoose(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var target = TargetOption();
        var format = FormatOption();

        var noImport = new Option<bool>("--no-import") {
            Description = "Do not import first. Only for a caller that has already done it in this build."
        };

        var command = new Command(
            "loose",
            "Write a catalog over the imported artefacts, with nothing packed, for --vixen-loose-content."
        ) { project, target, format, noImport };

        command.SetAction(async (parseResult, cancellationToken) => {
                var writer = output ?? Console.Out;
                var chosen = parseResult.GetValue(format);

                if (!Project.TryOpen(parseResult.GetValue(project), out var opened, out var why)) {
                    Complain(chosen, error ?? Console.Error, why);
                    return (int)ExitCode.UsageError;
                }

                var forTarget = parseResult.GetRequiredValue(target);
                var diagnostics = new DiagnosticWriter(writer, chosen, opened.Paths.Root);

                if (!parseResult.GetValue(noImport)) {
                    var summary = await ImportRunner
                        .RunAsync(opened, forTarget, diagnostics, false, isolated: false, cancellationToken)
                        .ConfigureAwait(false);

                    ImportRunner.Report(summary, diagnostics);

                    if (summary.Failed > 0) {
                        Complain(
                            chosen,
                            error ?? Console.Error,
                            $"{summary.Failed} asset(s) failed to import, so there is nothing to catalogue for them."
                        );

                        return (int)ExitCode.Failed;
                    }
                } else {
                    opened.Database.Scan();
                }

                var written = LooseContent.Write(
                    opened.Workspace,
                    diagnostic => ContentBuildRunner.Write(diagnostics, diagnostic)
                );

                if (!written.Succeeded) {
                    return (int)ExitCode.Failed;
                }

                writer.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Catalogued {written.Addresses} address(es) at {written.Directory}, unpacked. "
                        + $"Run a player with --vixen-loose-content {written.Directory}"
                    )
                );

                return (int)ExitCode.Success;
            }
        );

        return command;
    }

    static Command ContentBuild(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var target = TargetOption();
        var format = FormatOption();
        var assemblies = AssemblyOption();

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
            shaderTarget,
            assemblies
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

                // This is the pass that has one to load: Vixen.Sdk runs the import before the
                // compiler and the content build after it, so the game's assembly exists here and
                // may not have existed there.
                GameAssemblies.Load(
                    parseResult.GetValue(assemblies) ?? [],
                    complaint => diagnostics.Line($"  note    could not load {complaint}")
                );

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

    /// <summary>
    ///     <c>vixen frame</c> — a project's frame document, as a thing the CLI can operate on.
    /// </summary>
    /// <remarks>
    ///     One verb today: <c>explode</c>, docs/plan/39 § 5's escape hatch. It takes a path rather
    ///     than a project, because a frame document is one file and naming it is simpler and more
    ///     honest than discovering it — a project may carry several (a game's, a test's, an
    ///     editor's), and guessing which one somebody meant to eject is not a guess a one-way
    ///     operation should make.
    /// </remarks>
    static Command Frame(TextWriter? output, TextWriter? error) {
        var document = new Argument<string>("document") {
            Description = "The .vxcompositor whose !StandardFrame to expand."
        };

        var inPlace = new Option<bool>("--in-place") {
            Description = "Overwrite the document itself rather than writing <name>.exploded.vxcompositor beside it."
        };

        var explode = new Command(
            "explode",
            "Expand a !StandardFrame into the full graph it stands for, comments included. One-way."
        ) { document, inPlace };

        explode.SetAction(parseResult => (int)FrameRunner.Explode(
                Path.GetFullPath(parseResult.GetRequiredValue(document)),
                parseResult.GetValue(inPlace),
                output ?? Console.Out,
                error ?? Console.Error
            )
        );

        return new Command("frame", "Work with a project's frame document.") { explode };
    }

    static Command Doctor(TextWriter? output, TextWriter? error) {
        var project = ProjectOption();
        var target = TargetOption();

        var command = new Command("doctor", "Say what is wrong with the project, and change nothing.") {
            project,
            target,
            DoctorSystems(output, error)
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

    /// <summary>
    ///     <c>vixen doctor systems</c> — the frame, read out of a built assembly.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An assembly and no project, which is the opposite of every other subcommand here.</b>
    ///     A frame is not in the project's assets; it is in its code, declared by <c>[GameSystem]</c>
    ///     and ordered by attributes the compiler has already baked into metadata. So this takes the
    ///     same repeatable <c>--assembly</c> that <c>import</c> and <c>content build</c> take, and
    ///     loads it exactly the same way — see <see cref="GameAssemblies" />. A second way to load a
    ///     game's code would be a second set of registrations and a second set of bugs.
    /// </remarks>
    static Command DoctorSystems(TextWriter? output, TextWriter? error) {
        var assemblies = AssemblyOption();

        var command = new Command(
            "systems",
            "Read a built game assembly's frame: what runs, in what order, and what it needs."
        ) { assemblies };

        command.SetAction(parseResult => {
                var paths = parseResult.GetValue(assemblies) ?? [];

                if (paths.Length == 0) {
                    (error ?? Console.Error).WriteLine(
                        "Name at least one built game assembly with --assembly. A frame is declared in "
                        + "a project's code, so there is nothing to read without one."
                    );

                    return (int)ExitCode.UsageError;
                }

                var findings = SystemsRunner.Examine(paths);

                return (int)(DoctorRunner.Report(findings, output ?? Console.Out)
                    ? ExitCode.Success
                    : ExitCode.Failed);
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
        // ⚠ A string validated against the catalog rather than an enum. The templates and their
        // names live in Tools/Vixen.Templates, and an enum here would be a second list of them —
        // which is how `dotnet new vixen-app` and `vixen new app` end up disagreeing about what
        // exists.
        var template = new Argument<string>("template") {
            Description = "What to write: " + string.Join(", ", TemplateCatalog.All.Select(known => known.ShortName)) + "."
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

                if (!PlayerBuild.TryDescribe(forTarget, out var shape)) {
                    // The same sentence the editor's Build Settings window greys its Build button
                    // with, because a target being unpublishable is one fact and two explanations of
                    // it would be two things to keep in step.
                    Complain(parseResult.GetValue(format), complaints, PlayerBuild.WhyNotPublishable(forTarget)!);

                    return (int)ExitCode.UsageError;
                }

                if (!PlayerBuild.TryFindProjectFile(opened.Paths.Root, out var projectFile)) {
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
                PlayerBuild.TryDescribe(forTarget, out var shape);

                if (!PlayerBuild.TryFindProjectFile(opened.Paths.Root, out var projectFile)) {
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

                return await PlayerBuild.LaunchAsync(
                    artefact,
                    Path.GetFileNameWithoutExtension(projectFile),
                    parseResult.GetValue(passthrough) ?? [],
                    writer,
                    capture: false,
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

        // ⚠ The child's output is inherited rather than captured, which is the one thing this call
        // wants differently from the editor's. A terminal is where MSBuild's colours and its
        // in-place progress belong; a window with no console of its own is where they have to be
        // read back and written into a panel. Same call, one flag.
        var published = await PlayerBuild
            .PublishAsync(projectFile, shape, variant, artefact, writer, capture: false, cancellationToken)
            .ConfigureAwait(false);

        return published ? ExitCode.Success : ExitCode.Failed;
    }
}
