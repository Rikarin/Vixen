// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

/// <summary>
///     The gate that boots a whole sample, renders it on a real graphics device with no display, and
///     fails if what came back is not a picture.
/// </summary>
/// <remarks>
///     <para>
///         <b>What it covers that nothing else does.</b> <c>Test</c> exercises assemblies and
///         <see cref="GoldenImages" /> renders fixtures a test built in memory. Neither of them boots
///         an application: the content build's output, the asset bundle, the compositor document, the
///         standard frame's expansion, the shader variants compiled on demand, terrain, foliage, the
///         ECS world and the host's own frame loop are all upstream of a sample's first pixel and all
///         of them were, until this target, run by nobody on any push. A sample that stopped
///         producing a picture would have been found by whoever next ran one by hand.
///     </para>
///     <para>
///         ⚠ <b>THE FLAG THAT MATTERS IS <c>--vixen-capture</c>, AND IT IS NOT ABOUT THE PICTURE.</b>
///         <c>--vixen-headless</c> on its own gives a window whose surface is <c>SurfaceKind.None</c>,
///         and <c>GraphicsHost.TryOpen</c> makes Vulkan decline a surface it cannot present to — on
///         every platform, deliberately, because without that refusal a dedicated server would
///         silently stop running on the device that draws nothing and start needing a driver. The
///         chain then falls through to the Null device. Asking for a picture is the one statement of
///         intent specific enough to overrule it. See
///         <c>docs/guide/rendering/capturing-a-frame.md § Headless means the Null device unless you
///         ask for a picture</c>.
///     </para>
///     <para>
///         ⚠ <b>And a run that lands on the Null device does not look like a failure</b>, which is
///         the whole reason this target asserts what it asserts rather than an exit code. Measured on
///         this repository on 2026-08-25, sample 03 at 64 frames, the same binary, once with a real
///         device and once with <c>--vixen-backend null</c>: <b>both exited 0</b>, both took the same
///         wall clock to within a second, and every counter either run reported was
///         <b>character-for-character identical</b> — 26 objects extracted, 39 shader variants
///         compiled, 1 terrain and 1 grass field drawn, 1 foliage volume, TAA active, 111
///         reprojection draws. Two lines differed: the adapter, and the file. A leg that ran a sample
///         and checked the exit code would have passed on both, forever.
///     </para>
///     <para>
///         ⚠ <b>The validation assertions are three and not one, because "no validation errors" is a
///         sentence a run with no validation layers says just as loudly.</b> An instance that asks for
///         <c>VK_LAYER_KHRONOS_validation</c> and cannot load it is <em>not</em> refused —
///         <c>VulkanInstance.TryCreate</c> retries without the layer and logs a warning — so the
///         package being absent produces a picture, an exit code of 0 and a validation error count of
///         zero. The gate therefore asks whether a summary was written at all, whether the layers were
///         active, and only then what they counted.
///     </para>
///     <para>
///         ⚠ <b>And the summary is in the file only because the logger factory now outlives the
///         device.</b> <c>DisposeBag</c> disposes in reverse registration order, and
///         <c>VixenApplication</c> registered the factory last under a comment saying it was
///         therefore torn down last — so it was torn down <em>first</em>, and the whole teardown phase
///         reached the console and no log file. Anything logged at <c>Error</c> on the way down was
///         invisible to this target's error assertion for the same reason. Measured: the .jsonl ended
///         at the game's last shutdown line while the console carried a Vulkan record 74 ms later.
///     </para>
///     <para>
///         ⚠ <b>"A real device rather than a software one" is the WRONG test here and would break the
///         leg this target is written for.</b> The Linux runner has no GPU and renders on lavapipe,
///         which <c>VulkanAdapter.Kind</c> maps from <c>PhysicalDeviceType.Cpu</c> to
///         <c>AdapterKind.Software</c> — the very same value <c>NullDevice.Kind</c> returns. The kind
///         cannot separate them. The <em>name</em> can, and exactly: <c>NullDevice.Name</c> is the
///         constant <c>"Vixen Null Device"</c>, while lavapipe answers <c>llvmpipe (LLVM …)</c>. So
///         this target names the one device it refuses and accepts every other, which is also the
///         only form of the assertion that means the same thing on a laptop, on a hosted runner and
///         on a machine with a card in it.
///     </para>
///     <para>
///         <b>Why a Nuke target and not a test.</b> The subject is a process: a sample has to be
///         built with its content, started with a command line, and read back through the files and
///         the log it left behind. That is the shape <see cref="PublishWeb" /> already has, for the
///         same reason, and it keeps <c>dotnet test</c> free of a fixture that spawns applications.
///     </para>
/// </remarks>
partial class Build {
    /// <summary>Which sample the gate runs — a directory name under <c>Samples/</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not every sample can be the subject, and the ones that cannot are not broken.</b>
    ///         <c>01-HelloTriangle</c>, <c>11-VideoPlayback</c> and <c>12-VirtualGeometry</c> set
    ///         <c>config.Graphics.Enabled = false</c> and own their device, their swapchain and their
    ///         present — so the host has no frame to copy out of and <c>--vixen-capture</c> writes
    ///         nothing for them. Their leg is <c>--vixen-frames N</c> on a machine with a display,
    ///         which is a different gate and not this one. <c>02-HelloUi</c> is a third case again:
    ///         it deliberately has no <c>Vixen.App</c> at all — that is the boundary it exists to
    ///         prove — so it has no <c>--vixen-*</c> arguments to give.
    ///     </para>
    ///     <para>
    ///         <c>03-PbrShowcase</c> is the default because it is the smallest complete project on
    ///         the standard frame and its own README already says a run of it is "how CI proves the
    ///         whole frame — cascades, occlusion, the temporal resolve, the meter — builds, runs and
    ///         stops without a validation error or a hang". <c>13-ThirdPersonShooter</c> is the other
    ///         one this target has been run against and it passes: 1600×900, mean channel 44.07,
    ///         9 237 distinct colours — a shade-heavy view from the spawn corner, and the narrowest
    ///         measured headroom over the colour floor below, at nine times rather than forty-three.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>15-AiVillage</c> supports the recipe and is still not a subject for this
    ///         gate</b>, which was measured rather than assumed: it runs headless, captures, and
    ///         comes back with <b>16 distinct colours at a mean channel of 6.6</b>, because it ships
    ///         no content bundle at all — "No content: there is no catalog.bin" — and draws only its
    ///         diagnostic overlay. Its own README says as much in as many words: <i>the picture is
    ///         not the evidence; the log is</i>. Pointing <c>--frame-sample</c> at it fails this
    ///         target correctly, and that is not a defect in either of them.
    ///     </para>
    /// </remarks>
    [Parameter("Which sample the frame gate runs — a directory name under Samples/")]
    readonly string FrameSample = "03-PbrShowcase";

    /// <summary>How many frames it renders before the last one is captured.</summary>
    /// <remarks>
    ///     <para>
    ///         Sixty-four, which the capture guide calls the count for "did this pass break" and
    ///         which is four times its floor of sixteen. Nothing here compares against a reference,
    ///         so convergence is not what the number buys: what it buys is being past the streaming
    ///         transient, which settles by about frame 64 and which is what makes a low count charge
    ///         a startup artefact to whatever change trips the leg next.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The frame count is not what this costs, on a device that can draw.</b> Measured
    ///         on an M1 Max: 16 frames took 34 s and 64 frames took 23–28 s, and the difference is
    ///         noise around one fixed cost — the sample compiles 39 shader variants on demand,
    ///         because no <c>Shaders.effects.json</c> has been captured for it, and that is most of
    ///         the run. <b>On lavapipe the balance is the other way and nobody has watched it</b>, so
    ///         this is a parameter: if the first Linux run says 64 frames is minutes of software
    ///         rasterisation, lower it here rather than deleting the leg.
    ///     </para>
    /// </remarks>
    [Parameter("How many frames the frame gate renders before capturing the last one")]
    readonly int FrameCount = 64;

    AbsolutePath SampleFrameDirectory => ArtifactsDirectory / "sample-frame";

    Target SampleFrame => definition => definition
        .Description("Runs a sample headless on a real device and fails if the frame it captured is not a picture")
        .DependsOn(Compile)
        .Produces(SampleFrameDirectory / "**")
        .Executes(() => {
                var sample = RootDirectory / "Samples" / FrameSample;

                Assert.DirectoryExists(
                    sample,
                    $"there is no sample at '{sample}'. --frame-sample takes a directory name under "
                    + "Samples/, and the sample it names must be one that lets the host own the "
                    + "frame — see FrameSample's remarks for the three that do not."
                );

                var projects = sample.GlobFiles("*.csproj");

                Assert.True(
                    projects.Count == 1,
                    $"'{sample}' holds {projects.Count} .csproj files, so there is nothing "
                    + "unambiguous to run."
                );

                var project = projects.Single();

                SampleFrameDirectory.CreateOrCleanDirectory();

                var shots = SampleFrameDirectory / "shots";
                var logs = SampleFrameDirectory / "logs";
                var console = SampleFrameDirectory / "console.txt";

                // --vixen-log-file writes structured JSON lines rather than the console's prose, and
                // that is the difference between a gate and a grep: every assertion below reads a
                // named property off a record instead of matching an English sentence somebody is
                // free to reword. The console is kept too, but only as an artefact for whoever reads
                // a failure — nothing is asserted from it, because the Vulkan debug messenger writes
                // there directly and its wording belongs to the layers rather than to us.
                var arguments = string.Join(
                    ' ',
                    "run",
                    $"--project \"{project}\"",
                    $"--configuration {Configuration}",
                    "--no-build",
                    "--no-restore",
                    "--",
                    "--vixen-headless",
                    $"--vixen-frames {FrameCount}",
                    $"--vixen-capture \"{shots}\"",
                    "--vixen-log-level information",
                    $"--vixen-log-file \"{logs}\""
                );

                Log.Information("Running {Sample} for {Frames} frames with no display", FrameSample, FrameCount);

                var process = ProcessTasks.StartProcess("dotnet", arguments, RootDirectory, LayerEnvironment());
                process.WaitForExit();

                console.WriteAllLines(process.Output.Select(line => line.Text));

                Assert.True(
                    process.ExitCode == 0,
                    $"{FrameSample} exited {process.ExitCode}. This is the cheapest of the checks in "
                    + "this target and the least informative — see the remarks: a run that fell "
                    + $"through to the Null device exits 0 too. The console is at '{console}'."
                );

                var record = ReadFrameLog(logs);

                // ⚠ The whole Null-device class, in one line, and the reason it is a name and not a
                // kind is at the top of this file: lavapipe reports AdapterKind.Software exactly as
                // NullDevice does, so a run on the Linux leg and a run that drew nothing are the
                // same value of Kind. Refuse the one device by the name it is required to have.
                Assert.True(
                    !string.Equals(record.Adapter, NullAdapterName, StringComparison.Ordinal),
                    $"{FrameSample} rendered on the Null device, which draws nothing. The run still "
                    + "exited 0 and every counter it printed still read healthy — that is what this "
                    + "device is for and why it is checked here. Either --vixen-capture stopped "
                    + "reaching GraphicsHost.Create (it is what waives the no-surface refusal in "
                    + "GraphicsHost.TryOpen, so a run without it lands here on every platform), or "
                    + "Vulkan would not open on this machine and the chain fell through to Null. "
                    + $"The console at '{console}' carries each backend's refusal."
                );

                // A sample that hands AppBuilder.WithPlatform a DesktopPlatform never reaches
                // PlatformHost, so --vixen-headless is parsed, stored and never read: the run opens
                // a window and contends for the display. HeadlessFlagTests gates the shape by
                // scanning Samples/ for it; this is the same claim made by the run itself, which is
                // the half a source scan cannot make.
                Assert.True(
                    string.Equals(record.Platform, "Headless", StringComparison.Ordinal),
                    $"{FrameSample} ran on the {record.Platform} platform rather than Headless, so "
                    + "--vixen-headless did not reach PlatformHost. The usual cause is a head that "
                    + "hands AppBuilder.WithPlatform a platform of its own, which Build honours "
                    + "ahead of the factory — see docs/guide/rendering/capturing-a-frame.md § Two "
                    + "ways a head takes --vixen-headless away."
                );

                Assert.True(
                    record.Errors.Count == 0,
                    $"{FrameSample} logged {record.Errors.Count} record(s) at Error or Critical, and "
                    + "a sample that reports an error is a sample whose picture is not evidence of "
                    + "anything:\n  "
                    + string.Join("\n  ", record.Errors)
                );

                // ⚠ Three assertions rather than one, and the first two are the instrument. A run
                // with no validation layers reports zero errors, which is character for character
                // what a clean run reports — and an unloadable layer does not stop the instance
                // being created: VulkanInstance.TryCreate retries without it and logs a warning. So
                // "no errors" is only evidence when something also says the layers were there.
                //
                // ⚠ And the record is only in the file because the logger factory outlives the
                // device. It used to be torn down first — DisposeBag disposes in reverse
                // registration order and this line was registered last — so the whole teardown phase
                // was dropped by the file sink while still reaching the console. Anything logged at
                // Error on the way down was invisible to the Errors assertion above for the same
                // reason.
                Assert.True(
                    record.ValidationReported,
                    $"{FrameSample}'s log carries no validation summary, so nothing in this run said "
                    + "whether the layers had anything to report. The record is VulkanLog 2004, "
                    + "written when the Vulkan device is disposed and carrying ValidationActive, "
                    + "ValidationErrors and ValidationWarnings. Its absence means the device was "
                    + "never torn down, the record was renamed, or the log sink stopped accepting "
                    + $"records before shutdown. The console is at '{console}'."
                );

                Assert.True(
                    record.ValidationActive,
                    $"{FrameSample} ran without the Vulkan validation layers, so it could not have "
                    + "reported a validation error whatever it did wrong. On Linux install "
                    + "vulkan-validationlayers; on macOS the layer is Homebrew's and needs "
                    + "DYLD_LIBRARY_PATH=/opt/homebrew/lib, which this target passes — see "
                    + "LayerEnvironment. A missing layer is a warning in the log rather than a "
                    + "failure to start, which is exactly why this is asserted here."
                );

                Assert.True(
                    record.ValidationErrors == 0,
                    $"the validation layers reported {record.ValidationErrors} error(s) during "
                    + $"{FrameSample}. The picture below may look perfectly ordinary and still be "
                    + "the product of undefined behaviour — a resource destroyed between frames, a "
                    + "descriptor set that was never bound — which is the class of defect this "
                    + $"property exists to catch. The messages are on the console at '{console}'."
                );

                if (record.ValidationWarnings > 0) {
                    // Reported and not asserted. A warning is frequently the layers telling us
                    // something true and harmless about a driver — sample 03 raises one about a
                    // vertex output the fragment stage does not read — and a gate that failed on
                    // those would be turned off within a week.
                    Log.Warning(
                        "{Sample}: the validation layers reported {Count} warning(s); see {Console}",
                        FrameSample,
                        record.ValidationWarnings,
                        console
                    );
                }

                var captured = shots / "frame.png";

                Assert.FileExists(
                    captured,
                    $"{FrameSample} logged its capture to '{record.CapturePath}' but there is no "
                    + $"file at '{captured}'. The host writes the last frame under the directory "
                    + "--vixen-capture named; a missing file with a capture line above it means the "
                    + "readback wrote nothing."
                );

                var frame = ReadFrame(captured);

                // The size the host said the frame was, against the size the file actually is. It
                // is free, and it is the one check that would notice a capture of the wrong
                // resource — GraphicsOptions.Output names what is copied out, and a frame document
                // whose last node writes somewhere else would still produce a plausible PNG.
                Assert.True(
                    frame.Width == record.Width && frame.Height == record.Height,
                    $"the captured frame is {frame.Width}×{frame.Height} but the host reported a "
                    + $"{record.Width}×{record.Height} frame. The file is not a picture of the frame "
                    + "that was rendered."
                );

                Log.Information(
                    "{Sample}: {Width}×{Height}, mean channel {Mean:F2}/255, deviation {Deviation:F2}, "
                    + "{Distinct} distinct colours, on {Adapter}",
                    FrameSample,
                    frame.Width,
                    frame.Height,
                    frame.Mean,
                    frame.Deviation,
                    frame.Distinct,
                    record.Adapter
                );

                // ⚠ Three loose floors rather than one reference image, and the looseness is the
                // point. A committed golden would have to be per-driver — MoltenVK and lavapipe do
                // not agree to the byte and never will — and a per-pixel comparison on a view with
                // grass and GI on it is measuring the asset loader rather than the renderer:
                // measured, six runs of one build spread 640k–880k flipped pixels of 1.44M while
                // their whole-frame mean channel held to 0.05%. What survives is a statistic over
                // the whole frame, and what this target is actually asked to catch is not a subtle
                // shift but a picture that is not one.
                //
                // The numbers each have a measured floor under them, from this repository on
                // 2026-08-25, sample 03 at 64 frames on an M1 Max — against the two ways of getting
                // a wrong frame that were actually produced and run through this target. Both of
                // those exited 0.
                //
                //                    correct     Null device    orphaned output     floor here
                //   distinct          44 038               1                  1          1 024
                //   deviation          18.40            0.00               0.00            2.0
                //   mean               74.53            0.00             170.00    2.0 … 253.0
                //
                // Forty-three times the headroom on the statistic that matters most. ⚠ The third
                // column is why the mean is a band and not a floor, and why the deviation is taken
                // per channel: a compositor whose last pass writes a resource it declared itself
                // draws a correct frame into memory nobody reads (GraphicsOptions.Output's remarks
                // say so), and what is captured instead is one flat mid-grey — which passes a
                // brightness check and, pooled across the channels, passes a variance check too.
                // Only the colour count caught it before the deviation was split.
                //
                // ⚠ Forty-three times is sample 03's headroom and not the floor's. Sample 13 from
                // its spawn corner — a view that is almost entirely in shade, which is correct and
                // has been mistaken for a bug — measures 9 237, so the real margin this threshold
                // carries is nine. Raising it would start measuring how much of a sample is lit.
                Assert.True(
                    frame.Distinct >= MinimumDistinctColours,
                    $"the captured frame holds {frame.Distinct} distinct colour(s), under the "
                    + $"{MinimumDistinctColours} this gate requires. A frame with one colour in it "
                    + "is a clear and nothing else; a frame with a handful is a sky and nothing "
                    + "else. Sample 03 at 64 frames measured 44 038 on this repository, so this is "
                    + "not a threshold an ordinary content change can drift under — something "
                    + "stopped drawing."
                );

                Assert.True(
                    frame.Deviation >= MinimumDeviation,
                    $"the captured frame's channel deviation is {frame.Deviation:F3}, under the "
                    + $"{MinimumDeviation} this gate requires, so the picture is very nearly flat. "
                    + "See the note above for the measured floor."
                );

                Assert.True(
                    frame.Mean is >= MinimumMean and <= MaximumMean,
                    $"the captured frame's mean channel is {frame.Mean:F2}/255, outside "
                    + $"{MinimumMean}–{MaximumMean}. It is black or it is blown out; either way the "
                    + "exposure, the tonemap or the readback is wrong rather than the content."
                );

                Log.Information(
                    "{Sample} rendered {Frames} frames with no display and captured a picture to {Path}",
                    FrameSample,
                    FrameCount,
                    captured
                );
            }
        );

    /// <summary>The one adapter name this gate refuses. <c>NullDevice.Name</c> returns exactly this.</summary>
    const string NullAdapterName = "Vixen Null Device";

    /// <summary>
    ///     The environment the sample is started with: this process's, plus the one variable macOS
    ///     needs for a Vulkan instance to be creatable at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this, every macOS run of this target reports the Null device — and it is
    ///         the gate that found it.</b> The chain is three steps and none of them is obvious.
    ///         Homebrew's validation-layer manifest names its library by bare filename, dyld's
    ///         default search path does not include <c>/opt/homebrew/lib</c>, and the engine asks for
    ///         the layer — so <c>vkCreateInstance</c> fails with the layer it was just told about,
    ///         Vulkan declines, and <c>GraphicsHost</c> falls through to the device that draws
    ///         nothing. <c>.runsettings</c> records the same diagnosis at length and fixes it for
    ///         <c>dotnet test</c>; this is the same fix for a target that starts a process instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it has to be re-added here rather than inherited</b>, because
    ///         <c>build.sh</c> begins <c>#!/usr/bin/env bash</c>: <c>/usr/bin/env</c> is protected by
    ///         System Integrity Protection, which strips every <c>DYLD_*</c> variable from what it
    ///         passes on. So a developer who has the export in their shell profile still loses it the
    ///         moment they type <c>./build.sh</c>, and everything Nuke starts is downstream of that.
    ///     </para>
    ///     <para>
    ///         Ignored on Windows and on the Linux leg this target is written for, where the loader
    ///         resolves the layer on its own — which is why it is set only where it is needed rather
    ///         than everywhere.
    ///     </para>
    /// </remarks>
    static IReadOnlyDictionary<string, string> LayerEnvironment() {
        var variables = EnvironmentInfo.Variables.ToDictionary(entry => entry.Key, entry => entry.Value);

        if (!OperatingSystem.IsMacOS()) {
            return variables;
        }

        const string path = "DYLD_LIBRARY_PATH";
        const string libraries = "/opt/homebrew/lib:/usr/local/lib";

        variables[path] = variables.TryGetValue(path, out var existing) && existing.Length > 0
            ? $"{existing}:{libraries}"
            : libraries;

        return variables;
    }

    /// <inheritdoc cref="SampleFrame" />
    const int MinimumDistinctColours = 1024;

    /// <inheritdoc cref="SampleFrame" />
    const double MinimumDeviation = 2d;

    /// <inheritdoc cref="SampleFrame" />
    const double MinimumMean = 2d;

    /// <inheritdoc cref="SampleFrame" />
    const double MaximumMean = 253d;

    /// <summary>What the run said about itself, read out of the structured log it wrote.</summary>
    sealed record FrameLog {
        public string Adapter { get; init; } = string.Empty;

        public string Platform { get; init; } = string.Empty;

        public string CapturePath { get; init; } = string.Empty;

        public int Width { get; init; }

        public int Height { get; init; }

        public IReadOnlyList<string> Errors { get; init; } = [];

        /// <summary>Whether the run reported a validation summary at all. See VulkanLog 2004.</summary>
        public bool ValidationReported { get; init; }

        /// <summary>Whether the layers were actually loaded, as the run itself reported it.</summary>
        public bool ValidationActive { get; init; }

        public int ValidationErrors { get; init; }

        public int ValidationWarnings { get; init; }
    }

    /// <summary>Reads the run's own account of itself.</summary>
    /// <remarks>
    ///     Records are found by the properties they carry rather than by their wording. The device
    ///     line is the one with an adapter <em>and</em> a size — the Vulkan backend logs an adapter
    ///     too, at a point where the frame has no size yet — and the platform line is the one with a
    ///     platform and a worker count. Matching on a sentence would make this gate fail the day
    ///     somebody improves a log message.
    /// </remarks>
    static FrameLog ReadFrameLog(AbsolutePath directory) {
        Assert.DirectoryExists(
            directory,
            $"the run wrote no log directory at '{directory}', so it did not get as far as opening "
            + "its log. Read the console beside it."
        );

        var files = directory.GlobFiles("*.jsonl");

        Assert.NotEmpty(
            files,
            $"'{directory}' holds no .jsonl, so --vixen-log-file produced nothing and there is "
            + "nothing to assert against."
        );

        string? adapter = null;
        string? platform = null;
        string? capturePath = null;
        var width = 0;
        var height = 0;
        var errors = new List<string>();
        var validationReported = false;
        var validationActive = false;
        var validationErrors = 0;
        var validationWarnings = 0;

        foreach (var line in files.SelectMany(file => file.ReadAllLines())) {
            if (line.Length == 0) {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("LogLevel", out var level)
                && level.GetString() is "Error" or "Critical") {
                errors.Add(root.TryGetProperty("Message", out var text) ? text.GetString() ?? line : line);
            }

            if (root.TryGetProperty("Adapter", out var name)
                && root.TryGetProperty("Width", out var pixelWidth)
                && root.TryGetProperty("Height", out var pixelHeight)) {
                adapter = name.GetString();
                width = pixelWidth.GetInt32();
                height = pixelHeight.GetInt32();
            }

            if (root.TryGetProperty("Platform", out var host) && root.TryGetProperty("Workers", out _)) {
                platform = host.GetString();
            }

            // VulkanLog 2004, written when the device is torn down. Found by its properties like
            // everything else here — and all three are read, because the count alone is a number a
            // run with no validation layers at all also produces.
            if (root.TryGetProperty("ValidationActive", out var active)
                && root.TryGetProperty("ValidationErrors", out var reportedErrors)
                && root.TryGetProperty("ValidationWarnings", out var reportedWarnings)) {
                validationReported = true;
                validationActive = active.GetBoolean();
                validationErrors = reportedErrors.GetInt32();
                validationWarnings = reportedWarnings.GetInt32();
            }

            if (root.TryGetProperty("Path", out var path)
                && root.TryGetProperty("Message", out var message)
                && message.GetString()?.StartsWith("Captured the frame", StringComparison.Ordinal) == true) {
                capturePath = path.GetString();
            }
        }

        Assert.NotNull(
            adapter,
            "the run's log never said which device it opened, so there is no way to tell a real one "
            + "from the device that draws nothing. HostLog 13011/13026 is the record this reads; if "
            + "it was renamed or its properties changed, this gate has to be taught the new shape "
            + "rather than left passing."
        );

        Assert.NotNull(
            platform,
            "the run's log never said which platform it started on, so there is no way to tell a "
            + "headless run from one that opened a window."
        );

        Assert.NotNull(
            capturePath,
            "the run's log never said it captured a frame. --vixen-capture without --vixen-frames "
            + "warns and writes nothing, because the frame written is the last one and a run with "
            + "no count has no last frame."
        );

        return new() {
            Adapter = adapter!,
            Platform = platform!,
            CapturePath = capturePath!,
            Width = width,
            Height = height,
            Errors = errors,
            ValidationReported = validationReported,
            ValidationActive = validationActive,
            ValidationErrors = validationErrors,
            ValidationWarnings = validationWarnings
        };
    }

    /// <summary>What the captured frame is, as three numbers over its pixels.</summary>
    readonly record struct FrameStatistics(int Width, int Height, double Mean, double Deviation, int Distinct);

    /// <summary>Reads a captured frame and measures it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This decodes the PNG itself rather than calling <c>Vixen.Core.Imaging</c>, and
    ///         that is a decision rather than an oversight.</b> The build project must compile
    ///         without the engine: <c>build.sh</c> compiles it before any target runs, so a
    ///         <c>ProjectReference</c> into <c>Core/</c> would make <c>Clean</c>, <c>CheckFormat</c>
    ///         and every other target unrunnable on a branch where the engine does not build — which
    ///         is precisely the branch somebody is trying to run a gate on. The second reason is
    ///         smaller and real: the file under test was written by <c>PngCodec.Encode</c>, and a
    ///         gate that reads it back with <c>PngCodec.Decode</c> is asking one component whether
    ///         it agrees with itself.
    ///     </para>
    ///     <para>
    ///         It is deliberately not a general PNG reader. <c>PngCodec</c> writes 8-bit RGBA,
    ///         non-interlaced, filter 0 on every row — its own comment says why the filter is fixed
    ///         — so anything else is asserted rather than handled. A gate that quietly mis-decoded a
    ///         format it was never given would report a statistic about nothing.
    ///     </para>
    /// </remarks>
    static FrameStatistics ReadFrame(AbsolutePath path) {
        var data = path.ReadAllBytes();
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

        Assert.True(
            data.Length > 8 && data.AsSpan(0, 8).SequenceEqual(signature),
            $"'{path}' does not begin with a PNG signature, so the capture wrote something that is "
            + "not a picture."
        );

        var width = 0;
        var height = 0;
        using var compressed = new MemoryStream();

        for (var offset = 8; offset + 12 <= data.Length;) {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            var kind = Encoding.ASCII.GetString(data, offset + 4, 4);
            var body = data.AsSpan(offset + 8, length);

            switch (kind) {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);

                    Assert.True(
                        body[8] == 8 && body[9] == 6 && body[12] == 0,
                        $"'{path}' is a {body[8]}-bit PNG of colour type {body[9]}, interlace "
                        + $"{body[12]}. PngCodec writes 8-bit RGBA, non-interlaced, and this reader "
                        + "understands nothing else — see its remarks."
                    );

                    break;

                case "IDAT":
                    compressed.Write(body);
                    break;
            }

            offset += length + 12;
        }

        Assert.True(width > 0 && height > 0, $"'{path}' declares a {width}×{height} picture.");

        var stride = (width * 4) + 1;
        var raw = new byte[stride * height];
        compressed.Position = 0;

        using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress)) {
            inflate.ReadExactly(raw);
        }

        // ⚠ Per channel, and the three kept apart. Pooling red, green and blue into one deviation
        // makes a flat *coloured* frame look varied — the spread between the channels stands in for
        // a spread within them — and this is not a hypothetical: the frame produced by orphaning the
        // compositor's output resource is one solid colour and pooled at a deviation of 120.21,
        // which is six times what the correct picture measures. Three separate deviations, and the
        // largest of them reported, because a picture needs only one channel to vary.
        var sums = new double[3];
        var squares = new double[3];
        var distinct = new HashSet<int>();

        for (var y = 0; y < height; y++) {
            var row = y * stride;

            Assert.True(
                raw[row] == 0,
                $"row {y} of '{path}' carries PNG filter {raw[row]}. PngCodec writes filter 0 on "
                + "every row, deliberately — see its comment — and this reader does not unfilter."
            );

            for (var x = 0; x < width; x++) {
                var pixel = row + 1 + (x * 4);

                for (var channel = 0; channel < 3; channel++) {
                    int value = raw[pixel + channel];
                    sums[channel] += value;
                    squares[channel] += value * value;
                }

                distinct.Add((raw[pixel] << 16) | (raw[pixel + 1] << 8) | raw[pixel + 2]);
            }
        }

        var pixels = (double)width * height;
        var deviation = 0d;

        for (var channel = 0; channel < 3; channel++) {
            var average = sums[channel] / pixels;
            deviation = Math.Max(deviation, Math.Sqrt(Math.Max(0d, (squares[channel] / pixels) - (average * average))));
        }

        return new(width, height, (sums[0] + sums[1] + sums[2]) / (pixels * 3), deviation, distinct.Count);
    }
}
