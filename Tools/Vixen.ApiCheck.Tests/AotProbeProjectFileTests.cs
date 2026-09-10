// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ Verifying an instrument that had none: the reader behind <c>CheckAot</c>'s and
///     <c>CheckAotIos</c>'s two pre-publish assertions.
/// </summary>
/// <remarks>
///     <para>
///         Those assertions used to read <c>Vixen.AotProbe.csproj</c> as a string, so
///         <c>&lt;!-- &lt;PublishAot&gt;true&lt;/PublishAot&gt; --&gt;</c> satisfied them exactly as
///         the live declaration did. The fixtures below are the three edits that were invisible: a
///         commented-out property, a commented-out root, and a group given a <c>Condition</c> that
///         never evaluates. Each one is asserted against the real probe rather than a toy, so the
///         subject is the file the gate actually reads.
///     </para>
///     <para>
///         Here rather than beside <c>build/_build.csproj</c> because there is nowhere beside it:
///         the build project is outside <c>Vixen.slnx</c> and no suite in the tree tests it. This
///         assembly already walks the repository to ask what the build reads, and the reader is
///         linked into it as source, so there is no second copy to drift.
///     </para>
/// </remarks>
public sealed class AotProbeProjectFileTests : IDisposable {
    readonly string directory = Path.Combine(Path.GetTempPath(), "vixen-aot-probe-tests", Guid.NewGuid().ToString("N"));

    public AotProbeProjectFileTests() => Directory.CreateDirectory(directory);

    public void Dispose() {
        try {
            if (Directory.Exists(directory)) {
                Directory.Delete(directory, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>
    ///     The floor, and the reason the other three tests mean anything: a reader that had stopped
    ///     recognising how the probe is written would answer "nothing declared" to every question
    ///     below and every one of them would still pass.
    /// </summary>
    [Fact]
    public void TheRealProbeReadsAsFullyDeclaredAndFullyRooted() {
        var probe = ProbeProject();

        Assert.True(AotProbeProjectFile.DeclaresProperty(probe, "PublishAot", "true"));
        Assert.True(AotProbeProjectFile.DeclaresProperty(probe, "TrimmerSingleWarn", "false"));

        var referenced = AotProbeProjectFile.ReferencedAssemblies(probe);

        Assert.True(referenced.Count >= 25, $"only {referenced.Count} references read out of the probe.");
        Assert.Equal(
            referenced.Order(StringComparer.Ordinal),
            AotProbeProjectFile.RootedAssemblies(probe).Order(StringComparer.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ The edit somebody actually makes while debugging a probe, and the one the substring
    ///     test could not see. On iOS these four properties are the only enforcement there is.
    /// </summary>
    [Fact]
    public void ACommentedOutPropertyIsNotDeclared() {
        var sabotaged = Fixture(
            "commented-property.csproj",
            "<PublishAot>true</PublishAot>",
            "<!-- <PublishAot>true</PublishAot> -->"
        );

        Assert.Contains("<!-- <PublishAot>true</PublishAot> -->", File.ReadAllText(sabotaged), StringComparison.Ordinal);
        Assert.False(AotProbeProjectFile.DeclaresProperty(sabotaged, "PublishAot", "true"));
        Assert.True(AotProbeProjectFile.DeclaresProperty(sabotaged, "TreatWarningsAsErrors", "true"));
    }

    /// <summary>
    ///     ⚠ Commenting out a reference and its root together stays symmetric and is harmless.
    ///     Commenting out only the root leaves that assembly covered by nothing but what
    ///     <c>Main</c> reaches, which is the case the rooting comparison exists for.
    /// </summary>
    [Fact]
    public void ACommentedOutRootIsNotARoot() {
        var sabotaged = Fixture(
            "commented-root.csproj",
            """<TrimmerRootAssembly Include="Vixen.Ecs" />""",
            """<!-- <TrimmerRootAssembly Include="Vixen.Ecs" /> -->"""
        );

        Assert.Contains("Vixen.Ecs", AotProbeProjectFile.ReferencedAssemblies(sabotaged));
        Assert.DoesNotContain("Vixen.Ecs", AotProbeProjectFile.RootedAssemblies(sabotaged));
    }

    /// <summary>
    ///     A property inside a group whose condition never evaluates reads identically to one in the
    ///     unconditional group — the same blindness one level out.
    /// </summary>
    [Fact]
    public void APropertyUnderAConditionedGroupIsNotDeclared() {
        var sabotaged = Fixture(
            "conditioned-group.csproj",
            "<PropertyGroup>",
            """<PropertyGroup Condition="'$(NeverTrue)' == 'yes'">""",
            once: true
        );

        Assert.False(AotProbeProjectFile.DeclaresProperty(sabotaged, "PublishAot", "true"));
    }

    /// <summary>
    ///     ⚠ What <c>CheckAotIos</c> is believed to gate, against what its probe references.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The README's non-negotiable is that <em>iOS is NativeAOT-only, and that is gated</em>.
    ///         The gate is a publish of one probe, so its reach is exactly that probe's reference
    ///         list — and the iOS probe names <b>21</b> assemblies where the desktop probe names
    ///         <b>84</b>. Counted 2026-09-10: 63 of the engine are outside the iOS gate, and eight of
    ///         those were outside it before #506's expansion ever ran. Among the eight are
    ///         <c>Vixen.Physics</c> and the whole audio stack: the reflection-heaviest subsystems in
    ///         the tree, which is to say the ones an ahead-of-time publish is most likely to break.
    ///         Nothing in either csproj, the README or <c>docs/overview.md</c> says whether that is a
    ///         decision.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And no technical reason separates them by target framework.</b> Re-measured
    ///         2026-09-10: every one of the eight declares plain <c>net10.0</c>, exactly like the
    ///         twenty-one the iOS probe does reference — the iOS probe is <c>net10.0-ios</c> and
    ///         takes <c>net10.0</c> references happily. So "they cannot build for iOS" is not the
    ///         explanation, and the shape this has is the shape of references nobody added rather
    ///         than references somebody left out. ⚠ The reason tokens below are <em>inferred by
    ///         whoever wrote this test</em> and are recorded nowhere else; #961 carries the question
    ///         for the owner and #1252 carries the fifty-three that #506 added to it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What actually enforces anything about iOS today is this test and nothing
    ///         else, which is less than it sounds.</b> <c>CheckAotIos</c> needs the <c>ios</c>
    ///         workload — <c>dotnet build</c> of the iOS probe fails <c>NETSDK1147</c> on a machine
    ///         without it, confirmed here — no workflow runs the target (<c>ci.yml</c>'s own comment
    ///         above the <c>aot</c> job says so and cites #327), and by the record it has never been
    ///         executed at all. So <c>AotProbeProjectFile</c>'s "on iOS these four properties are the
    ///         only enforcement there is" is conditional on a target that does not run. What this
    ///         test enforces is narrower and worth naming plainly: that the difference between the
    ///         two lists is <em>written down</em>. It says nothing about whether any of the 63 would
    ///         publish clean for <c>ios-arm64</c>, because nothing here can find out.
    ///     </para>
    ///     <para>
    ///         The difference is asserted in both directions. An assembly that joins the desktop
    ///         probe and not the iOS one fails here rather than quietly shrinking the iOS gate's
    ///         reach, and a name deleted from the iOS probe's absent list without being added to the
    ///         probe fails too. This runs in <c>Test</c>, on every machine, in milliseconds.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheIosProbeCoversTheDesktopProbeExceptWhereWrittenDown() {
        string[] absentOnPurpose = [
            // Inferred: a graphics backend for the browser. An iOS publish would never load it.
            "Vixen.Graphics.WebGPU",

            // Inferred: the desktop windowing backend, and the phone has Vixen.Platform.Native.
            "Vixen.Platform.Desktop",

            // ⚠ Written down rather than inferred, which is what separates it from the block below.
            // Platform/Vixen.Xr.OpenXR/README.md's first line is "The XR backend for the three
            // desktops and Android", so iOS is outside its scope by its author's own statement — and
            // twice over at run time: Apple ships no OpenXR runtime, and OpenXrLoader finds the
            // loader with NativeLibrary.TryLoad, which on iOS cannot load a library the application
            // did not ship inside its own bundle. An iOS publish of this would root an assembly
            // whose whole entry point is unreachable there. #1239 added it to the desktop probe.
            "Vixen.Xr.OpenXR",

            // ⚠ Unexplained, all six. A game on a phone has physics and sound, and these are the
            // assemblies whose serialization and component registration lean hardest on
            // reflection — the ones an AOT gate exists for. #961.
            "Vixen.App.Hosting",
            "Vixen.Audio",
            "Vixen.Audio.Backend.OpenAL",
            "Vixen.Audio.Codecs",
            "Vixen.Audio.Physics",
            "Vixen.Physics",

            // ⚠ Not a decision — these arrived on 2026-09-10 with `7c4b55ad4`, which grew the DESKTOP
            // probe from 29 rooted assemblies to 82 of 95 (#506) and did not grow the iOS one.
            // ⚠ #1252's body says "to 95" and that is the total rather than the rooted count; the
            // other thirteen went to NotRooted.txt, and two of those have since come off it (#1239).
            // Nobody has established whether these root cleanly for an iOS publish, and nobody can
            // here: the `ios` workload is not installed on the machine that merged this — `dotnet
            // build` of the iOS probe fails NETSDK1147, re-confirmed 2026-09-10 — `CheckAotIos` has
            // no CI leg (#327) and by the record has never run. Writing the 53 ProjectReferences
            // into the iOS probe would have made this test green over a claim no build has ever
            // checked, which is the opposite of what it is for. This list must SHRINK — #1252.
            "Vixen.Platform.Linux",
            "Vixen.Platform.MacOS",
            "Vixen.Platform.Windows",

            // ⚠ The three desktop platform backends above are the only ones with an inferable reason,
            // and it is not certain either: a phone has Vixen.Platform.Native, so they may belong
            // beside Vixen.Platform.Desktop permanently rather than in this block.
            "Vixen.Ai",
            "Vixen.Ai.Diagnostics",
            "Vixen.Ai.Nodes",
            "Vixen.Ai.Perception",
            "Vixen.Animation",
            "Vixen.Core.Imaging",
            "Vixen.Core.Syntax",
            "Vixen.Core.Yaml",
            "Vixen.Engine.Renderer",
            "Vixen.Foliage",
            "Vixen.Geometry",
            "Vixen.Geometry.Remeshing",
            "Vixen.Geometry.Uv",
            "Vixen.Graphics.OpenGL",
            "Vixen.Input",
            "Vixen.Navigation",
            "Vixen.Net",
            "Vixen.Net.Animation",
            "Vixen.Net.Audio",
            "Vixen.Net.Engine",
            "Vixen.Net.Engine.Content",
            "Vixen.Net.Physics",
            "Vixen.Net.Telemetry",
            "Vixen.Net.Transport.Composite",
            "Vixen.Net.Transport.Local",
            "Vixen.Net.Transport.Udp",
            "Vixen.Net.Transport.WebSocket",
            "Vixen.Rendering",
            "Vixen.Rendering.DistanceFields",
            "Vixen.Rendering.IrradianceFields",
            "Vixen.Rendering.PostFx",
            "Vixen.Rendering.RayTracing",
            "Vixen.Rendering.Reflections",
            "Vixen.Rendering.ScreenProbes",
            "Vixen.Rendering.SurfaceCache",
            "Vixen.Rendering.Terrain",
            "Vixen.Rendering.VirtualGeometry",
            "Vixen.Rendering.Water",
            "Vixen.Shaders",
            "Vixen.Terrain",
            "Vixen.Terrain.Physics",
            "Vixen.Ui.Styling",
            "Vixen.Ui.Styling.Utilities",
            "Vixen.Ui.Text",
            "Vixen.Vfx",
            "Vixen.Video",
            "Vixen.Video.Codecs",
            "Vixen.Video.Rendering",
            "Vixen.Water",
            "Vixen.Water.Physics",

            // ⚠ Owed on the same terms, and added on 2026-09-10 by #1239 rather than by #506. The
            // abstraction has no platform statement of its own and a phone is a plausible XR
            // target — visionOS is where that question gets asked — so unlike its OpenXR head above
            // there is nothing written down to justify leaving it out. Nobody can settle it from
            // here: the `ios` workload is not installed and `CheckAotIos` has no CI leg (#327).
            // This entry belongs to #1252, and the list it is in must SHRINK.
            "Vixen.Xr",
        ];

        var desktop = AotProbeProjectFile.ReferencedAssemblies(ProbeProject());
        var ios = AotProbeProjectFile.ReferencedAssemblies(IosProbeProject());

        Assert.True(
            ios.Count >= 15,
            $"only {ios.Count} references read out of the iOS probe, which is too few to be it — "
            + "the reader has stopped recognising how it is written and the comparison below would "
            + "be a comparison against nothing."
        );

        var absent = desktop.Except(ios, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            absent.SequenceEqual(absentOnPurpose.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "The assemblies in the desktop AOT probe and not the iOS one are supposed to be exactly "
            + "the ones written down above. A NEW name means an assembly was added to the desktop "
            + "probe and not to the iOS one, so it is outside the iOS gate while the README still "
            + "says iOS is gated — add it to Vixen.AotProbe.iOS.csproj, with its TrimmerRootAssembly, "
            + "or write down why not. A name that has GONE has been added to the iOS probe; delete "
            + "it here in the same commit, because a list nobody prunes is one more instrument "
            + "reporting success.\n  expected: "
            + string.Join(", ", absentOnPurpose.Order(StringComparer.Ordinal))
            + "\n  found:    "
            + string.Join(", ", absent)
        );
    }

    static string ProbeProject() =>
        Path.Combine(RepositoryRoot(), "Tools", "Vixen.AotProbe", "Vixen.AotProbe.csproj");

    static string IosProbeProject() =>
        Path.Combine(RepositoryRoot(), "Tools", "Vixen.AotProbe.iOS", "Vixen.AotProbe.iOS.csproj");

    string Fixture(string name, string from, string to, bool once = false) {
        var text = File.ReadAllText(ProbeProject());

        Assert.Contains(from, text, StringComparison.Ordinal);

        var path = Path.Combine(directory, name);

        File.WriteAllText(path, once ? ReplaceFirst(text, from, to) : text.Replace(from, to, StringComparison.Ordinal));

        return path;
    }

    static string ReplaceFirst(string text, string from, string to) {
        var index = text.IndexOf(from, StringComparison.Ordinal);

        return text[..index] + to + text[(index + from.Length)..];
    }

    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
