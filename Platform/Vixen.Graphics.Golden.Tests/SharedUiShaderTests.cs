// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The UI fragment shaders this suite renders with are the ones the sample and the template ship.</summary>
/// <remarks>
///     <para>
///         <b>An invariant three projects were asserting in prose and nothing was checking.</b>
///         <c>Samples/02-HelloUi</c>'s project file says of these files: "These four are the same GLSL
///         the golden-image fixture drives the renderer with, so the sample and the reference pictures
///         cannot disagree about what the shaders do." That sentence was false when it was read on
///         2026-08-09 — <c>ui-box.frag</c> here was sixteen lines longer than the other two, and the
///         missing lines were the whole shadow path.
///     </para>
///     <para>
///         ⚠ <b>The divergence was worse than a missing feature, which is why this is a test and not a
///         note.</b> The struct is shared, and its own comment reserves <c>axis.z</c> for "a shadow's
///         blur". The two stale copies declared that field and never read it, so a shape asking for a
///         soft shadow got <c>coverage_of</c> instead — a hard-edged box exactly where the shadow
///         should have been, at full opacity. Nothing rendered blank, which is why nobody saw it: the
///         sample draws no shadow, so the only way to meet the bug was to write a new application from
///         the template and add one.
///     </para>
///     <para>
///         ⚠ <b>The <c>.spv</c> is what actually ships, and editing the <c>.frag</c> alone changes
///         nothing.</b> Both consumers embed <c>Shaders\*.spv</c>, so the committed SPIR-V is the
///         artefact and the GLSL beside it is only its source of record. Regenerate with
///         <c>glslc Shaders/ui-box.frag -o Shaders/ui-box.frag.spv</c> from the project directory
///         after any change here. This test deliberately compares the <b>source</b> and not the
///         modules: <c>glslc</c>'s output is not reproducible across toolchain versions, so comparing
///         <c>.spv</c> bytes would fail for everyone whose Vulkan SDK differs from the last person's.
///         The consequence is that this test cannot catch a stale <c>.spv</c> — see the remark on
///         <see cref="TheCommittedModuleIsNewerThanItsSource" />, which can.
///     </para>
///     <para>
///         The real fix is generating all three from one source, or driving the UI renderer from
///         <c>Raven/Library/Ui</c> the way <c>UiRenderer</c>'s remarks say it eventually should. Until
///         then, three files are maintained by hand and this is what says so out loud.
///     </para>
/// </remarks>
public class SharedUiShaderTests {
    /// <summary>The copies that have to agree, relative to the repository root.</summary>
    /// <remarks>
    ///     The first is this suite's, and it is the one the reference images were rendered with, so it
    ///     is the source of truth by construction rather than by choice.
    /// </remarks>
    static readonly string[] Copies = [
        Path.Combine("Platform", "Vixen.Graphics.Golden.Tests", "Shaders"),
        Path.Combine("Samples", "02-HelloUi", "Shaders"),
        Path.Combine("Tools", "Vixen.Templates", "templates", "vixen-app", "Shaders"),
    ];

    /// <summary>Every UI shader this suite renders with is byte-identical wherever else it is shipped.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ Byte-identical rather than equivalent, and the strictness is the point. A copy that is
    ///         merely "the same shader with a comment reworded" is one where the next reader cannot
    ///         tell at a glance whether a difference is deliberate, and every divergence starts as one
    ///         that looked harmless. There is no reason for these files to differ at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Absence is allowed and difference is not, which is a weaker contract than "the same
    ///         set" on purpose.</b> The template ships four of these and the sample five: it has no
    ///         <c>ui-image.frag</c> because a new application draws no images until someone adds one,
    ///         and demanding the full set would either fail forever or push a fifth shader into the
    ///         template to satisfy a test. What is never legitimate is a copy that exists and disagrees.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("ui-box.frag")]
    [InlineData("ui-image.frag")]
    [InlineData("ui-solid.frag")]
    [InlineData("ui-text.frag")]
    [InlineData("ui.vert")]
    public void TheSampleAndTheTemplateShipTheShaderThisSuiteRendersWith(string name) {
        var root = RepositoryRoot();
        var canonical = Path.Combine(root, Copies[0], name);

        Assert.True(File.Exists(canonical), $"{Path.Combine(Copies[0], name)} is missing, and it is the one the reference images were rendered with.");

        var expected = File.ReadAllText(canonical);
        var compared = 0;

        foreach (var directory in Copies[1..]) {
            var path = Path.Combine(root, directory, name);

            if (!File.Exists(path)) {
                continue;
            }

            compared++;
            var actual = File.ReadAllText(path);

            Assert.True(
                expected == actual,
                $"{Path.Combine(directory, name)} has drifted from the copy this suite renders with. "
                + $"The reference images were made with {Path.Combine(Copies[0], name)}, so that one is "
                + $"right by construction: copy it over, then regenerate the module with "
                + $"`glslc Shaders/{name} -o Shaders/{name}.spv` from that project's directory. "
                + $"({expected.Length} characters here against {actual.Length} there.)"
            );
        }

        // ⚠ Otherwise a rename quietly turns this theory into five assertions about nothing — the
        // failure mode of every test that compares a file against files found by path.
        Assert.True(compared > 0, $"no copy of {name} was found to compare against; the paths in {nameof(Copies)} have gone stale.");
    }

    /// <summary>A committed module is no older than the GLSL it was compiled from.</summary>
    /// <remarks>
    ///     ⚠ <b>The half the source comparison cannot see, and the half that actually shipped broken.</b>
    ///     The two consumers embed the <c>.spv</c>, so a correct <c>.frag</c> beside a stale module is a
    ///     shader that is right in the repository and wrong in the binary — which is exactly the state
    ///     the tree was in for the hours between the source being fixed and <c>glslc</c> being run.
    ///     A timestamp is a weak check and deliberately so: it cannot prove the module came from this
    ///     source, only that nobody edited the source and forgot the module, which is the mistake that
    ///     is actually made. ⚠ It is also the check that git cannot help with — checkout order sets
    ///     both files' times, so this can only be trusted to fire on a tree someone has edited, and it
    ///     is skipped when the two are within a second of each other.
    /// </remarks>
    [Theory]
    [InlineData("ui-box.frag")]
    [InlineData("ui-image.frag")]
    [InlineData("ui-solid.frag")]
    [InlineData("ui-text.frag")]
    [InlineData("ui.vert")]
    public void TheCommittedModuleIsNewerThanItsSource(string name) {
        var root = RepositoryRoot();

        foreach (var directory in Copies[1..]) {
            var source = Path.Combine(root, directory, name);

            if (!File.Exists(source)) {
                continue;
            }

            var module = source + ".spv";

            Assert.True(File.Exists(module), $"{Path.Combine(directory, name)}.spv is missing, and it is the artefact that ships.");

            var sourceTime = File.GetLastWriteTimeUtc(source);
            var moduleTime = File.GetLastWriteTimeUtc(module);

            Assert.True(
                moduleTime >= sourceTime.AddSeconds(-1),
                $"{Path.Combine(directory, name)}.spv was written {(sourceTime - moduleTime).TotalSeconds:F0}s "
                + $"before the GLSL beside it, so the module that ships is not this source. Regenerate it: "
                + $"`glslc Shaders/{name} -o Shaders/{name}.spv` from that project's directory."
            );
        }
    }

    /// <summary>The repository root, found by walking up rather than by counting directories.</summary>
    static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
