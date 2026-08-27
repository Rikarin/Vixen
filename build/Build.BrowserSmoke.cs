// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

/// <summary>
///     The gate that loads the published browser head in a real browser and fails if the
///     <c>[JSImport]</c> boundary did not actually work.
/// </summary>
/// <remarks>
///     <para>
///         <b>What it covers that nothing else does, and it is the whole of the interop.</b>
///         <see cref="CompileWeb" /> compiles the <c>[JSImport]</c> <em>declarations</em>;
///         <c>BrowserModuleUrlTests</c> knows the module-URL <em>constants</em>;
///         <see cref="PublishWeb" /> knows where the SDK put the <em>files</em>; and
///         <c>Vixen.Platform.Web.Tests/js/vixen-platform.test.mjs</c> tests the <em>JavaScript</em>
///         half against a DOM stub. Not one of them executes a marshalled call. Until this target
///         there was no run, anywhere in this repository or its CI, in which a single
///         <c>[JSImport]</c> was invoked — which is what <c>docs/overview.md</c>'s row means by
///         "still the only coverage for the <c>[JSImport]</c> calls themselves".
///     </para>
///     <para>
///         ⚠ <b>It must drive a real browser over CDP, and this is measured rather than assumed.</b>
///         <c>chrome-headless-shell --dump-dom</c> <b>never fires</b>
///         <c>requestAnimationFrame</c> — with or without <c>--virtual-time-budget</c>,
///         <c>--screenshot</c> or SwiftShader, a pure-JS control page counted <b>zero</b> callbacks
///         in three seconds, while the same page over CDP counted 120/s
///         (<c>docs/plan/spikes/web-head/RESULT.md</c>). A leg built on <c>--dump-dom</c> would
///         report a live frame loop as dead, which is the most expensive shape a red build can have.
///         So <c>browser-smoke.mjs</c> counts <c>requestAnimationFrame</c> from its own side, in a
///         page with none of our code in it, <em>before</em> it believes anything the probe says
///         about frames — and calls a zero there an INSTRUMENT FAILURE in those words.
///     </para>
///     <para>
///         ⚠ <b>Playwright is what doc 10 and the overview row name, and it is deliberately not what
///         this uses.</b> What is needed is one page, one navigation, a console transcript, two
///         <c>Runtime.evaluate</c> calls and five synthesised input events — about 200 lines of CDP
///         over a WebSocket, which Node has had built in since 22. Against that,
///         <c>playwright-core</c> is a third-party dependency in a repository whose dependencies are
///         attributed by <see cref="CheckAttribution" />, which reads
///         <c>Directory.Packages.props</c> and <c>native-dependencies.json</c> and would therefore
///         <b>not see an npm package at all</b>. Adding one would create exactly the class of
///         unattributed dependency that gate exists to prevent, in the one place it cannot look.
///         <c>vixen-platform.test.mjs</c> already makes the same call in its own header: "No
///         dependencies, no package.json, no install step."
///     </para>
///     <para>
///         <b>What does it print on the day it does not run?</b> A failure, on every path that was
///         thought of. No Node: this target fails rather than warning, unlike the
///         <c>TestBrowserHalf</c> step in <c>Vixen.Platform.Web.Tests</c> — that one is attached to
///         an ordinary build and must not break it for somebody who has no Node, this one is an
///         opt-in gate nobody trips by accident. No Chrome: the script exits 1 saying so, and does
///         not skip. No published head: the script exits 1. A browser that started and never opened
///         a debugging port, a page that 404'd, a request that failed, a page that printed nothing:
///         each is its own message. And the count of checks that actually executed is itself
///         asserted against a floor, so a probe that quietly stopped reporting half of them cannot
///         pass by reporting none — which is the failure this repository has met twice, in a
///         comparator that called three empty manifests "identical bytes" and in eighteen golden
///         files that passed without a device.
///     </para>
///     <para>
///         <b>Seen failing.</b> Four sabotages were run against the finished leg and each is
///         recorded in <c>docs/overview.md</c>'s row: a <c>[JSImport]</c> pointed at a function the
///         module does not export, a frame loop that is started and immediately stopped, a page
///         served without COOP/COEP, and a probe whose checks were commented out.
///     </para>
///     <para>
///         <b>Why a Nuke target and not a test.</b> The same reason <see cref="PublishWeb" /> and
///         <see cref="SampleFrame" /> are targets: the subject is a process. A head has to be
///         published, served over HTTP with specific headers, and driven by a second process over a
///         socket — and <c>Tools/Vixen.WebProbe</c> is outside <c>Vixen.slnx</c> anyway, because
///         <c>net10.0-browser</c> cannot be evaluated without the <c>wasm-tools</c> workload, so no
///         <c>dotnet test</c> run can reach it.
///     </para>
/// </remarks>
partial class Build {
    AbsolutePath BrowserSmokeDirectory => ArtifactsDirectory / "browser-smoke";

    AbsolutePath BrowserSmokeScript =>
        RootDirectory / "Tools" / "Vixen.WebProbe" / "browser-smoke.mjs";

    /// <summary>
    ///     How many checks the leg must have executed before its result is believed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the check on the checks, and it is the most important number here.</b>
    ///         Every other assertion in the leg can be satisfied by a run that made no assertion at
    ///         all — nothing failed, because nothing was asked. Naming the expected count on the
    ///         command line turns "the probe stopped reporting" from a shorter, greener run into a
    ///         failure with a number in it.
    ///     </para>
    ///     <para>
    ///         Thirty-seven: twenty-three the page reports and fourteen the driver makes. The count
    ///         is exact rather than approximate on purpose — the probe's catch blocks are written to
    ///         leave the same number of lines behind them whether they threw or not, so a failing
    ///         run and a passing run report the same total and differ only in the verdicts.
    ///     </para>
    /// </remarks>
    [Parameter("How many checks the browser smoke leg must execute before its result is believed")]
    readonly int BrowserSmokeChecks = 37;

    /// <summary>How long the page is given to boot the runtime and finish its checks.</summary>
    /// <remarks>
    ///     Ninety seconds, which is not a frame budget — it is how long a cold runner may take to
    ///     fetch and instantiate a WebAssembly runtime of tens of megabytes over loopback. Nothing
    ///     inside the leg waits on a clock: every wait is on a line the page prints. This is only
    ///     the outer bound that turns a hang into a message.
    /// </remarks>
    [Parameter("How long, in milliseconds, the page is given to finish its checks")]
    readonly int BrowserSmokeTimeout = 90_000;

    Target BrowserSmoke => definition => definition
        .Description("Drives the published browser head in a real browser and fails if the interop did not run")
        .DependsOn(PublishWeb)
        .Produces(BrowserSmokeDirectory / "**")
        .Executes(() => {
                Assert.FileExists(
                    BrowserSmokeScript,
                    $"the browser smoke driver is not at '{BrowserSmokeScript}'. If it moved, point "
                    + "this target at wherever it went; if it was deleted, this gate went with it "
                    + "and every [JSImport] in the three browser projects is uncovered again."
                );

                var siteRoot = WebPublishDirectory / "wwwroot";

                Assert.FileExists(
                    siteRoot / "index.html",
                    $"there is no published head at '{siteRoot}'. PublishWeb runs before this and "
                    + "asserts that file exists, so reaching here without it means the publish was "
                    + "skipped rather than run."
                );

                // ⚠ Fatal rather than a warning, and that is the difference between this and the
                // TestBrowserHalf step in Vixen.Platform.Web.Tests. That one hangs off an ordinary
                // build and must not break `dotnet build` for somebody with no Node over a file
                // they did not touch. This is a gate that is asked for by name, and a gate that
                // silently does nothing on the machine that runs it is the thing this repository
                // keeps writing rows about.
                var probe = ProcessTasks.StartProcess("node", "--version", RootDirectory);
                probe.WaitForExit();

                Assert.True(
                    probe.ExitCode == 0,
                    "`node --version` failed, so there is no Node to run the browser smoke driver "
                    + "with. This target fails rather than skipping: a browser gate that reports "
                    + "success on a machine that never started a browser is worse than no gate. "
                    + "Node ships on every GitHub runner image."
                );

                Log.Information("Node {Version}", probe.Output.Select(line => line.Text).FirstOrDefault());

                BrowserSmokeDirectory.CreateOrCleanDirectory();

                var arguments = string.Join(
                    ' ',
                    $"\"{BrowserSmokeScript}\"",
                    $"\"{siteRoot}\"",
                    $"--minimum-checks={BrowserSmokeChecks}",
                    $"--timeout={BrowserSmokeTimeout}"
                );

                var smoke = ProcessTasks.StartProcess("node", arguments, RootDirectory);
                smoke.WaitForExit();

                var console = BrowserSmokeDirectory / "console.txt";
                console.WriteAllLines(smoke.Output.Select(line => line.Text));

                Assert.True(
                    smoke.ExitCode == 0,
                    $"the browser smoke leg exited {smoke.ExitCode}. Every failure it can report "
                    + "names itself; the whole transcript, including everything the page printed, "
                    + $"is at '{console}'. ⚠ If the message says INSTRUMENT FAILURE, the browser "
                    + "was not animating and nothing about the engine has been shown — see this "
                    + "target's remarks and docs/plan/spikes/web-head/RESULT.md."
                );

                Log.Information(
                    "The [JSImport] boundary was executed in a real browser. Transcript at {Console}",
                    console
                );
            }
        );
}
