// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The GLSL this suite renders with, and the committed modules beside it.</summary>
/// <remarks>
///     <para>
///         <b>This file used to compare three hand-maintained copies of the same eight shaders and
///         it now checks one.</b> The invariant was real and it had already been broken: on
///         2026-08-09 <c>ui-box.frag</c> here was sixteen lines longer than the copies under
///         <c>Samples/02-HelloUi</c> and the <c>vixen-app</c> template, and the missing lines were
///         the whole shadow path. The struct is shared and its own comment reserves <c>axis.z</c>
///         for "a shadow's blur", so the two stale copies declared that field and never read it — a
///         shape asking for a soft shadow got a hard-edged box exactly where the shadow should have
///         been, at full opacity, on two of three copies, with nothing rendering blank.
///     </para>
///     <para>
///         ⚠ <b>There is one copy because the other two stopped being GLSL.</b> Everything that is
///         not this suite draws the interface from <c>Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn</c>,
///         compiled by this repository's own compiler and gated by <c>./build.sh CheckShaders</c>,
///         which is a far stronger check than a byte comparison of two files somebody has to keep
///         equal by hand: it recompiles the source and fails if the committed module differs.
///     </para>
///     <para>
///         ⚠ <b>What is left uncovered is worth naming rather than papering over.</b> The reference
///         images in this suite were rendered with the GLSL below, and every shipping application
///         renders with the Raven modules — and <i>nothing compares the two</i>. They are two
///         implementations of one specification in two languages, so no byte comparison can, and the
///         only real check is a golden image rendered through each. The right end state is this
///         suite driving the Raven modules too, which is a change that regenerates every reference
///         image in it and belongs on its own.
///     </para>
///     <para>
///         So what stays here is the half that applies to one copy: a committed module is no older
///         than the GLSL it was compiled from. <see cref="Copies" /> is gone with the comparison it
///         existed for.
///     </para>
/// </remarks>
public class SharedUiShaderTests {
    /// <summary>Where this suite's own GLSL and its modules live, relative to the repository root.</summary>
    static readonly string Shaders = Path.Combine("Platform", "Vixen.Graphics.Golden.Tests", "Shaders");

    /// <summary>A committed module is no older than the GLSL it was compiled from.</summary>
    /// <remarks>
    ///     ⚠ <b>The half a source comparison could never see, and the half that actually shipped
    ///     broken.</b> This suite loads the <c>.spv</c>, so a correct <c>.frag</c> beside a stale
    ///     module is a shader that is right in the repository and wrong in the binary — which is
    ///     exactly the state the tree was in for the hours between the source being fixed and
    ///     <c>glslc</c> being run.
    ///     <para>
    ///         A timestamp is a weak check and deliberately so: it cannot prove the module came from
    ///         this source, only that nobody edited the source and forgot the module, which is the
    ///         mistake that is actually made. ⚠ It is also the check git cannot help with — a
    ///         checkout sets both files' times — so it can only be trusted to fire on a tree someone
    ///         has edited, and it is skipped when the two are within a second of each other.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("ui-blur.frag")]
    [InlineData("ui-box.frag")]
    [InlineData("ui-colour.frag")]
    [InlineData("ui-image.frag")]
    [InlineData("ui-mask.frag")]
    [InlineData("ui-solid.frag")]
    [InlineData("ui-text.frag")]
    [InlineData("ui.vert")]
    public void TheCommittedModuleIsNewerThanItsSource(string name) {
        var source = Path.Combine(RepositoryRoot(), Shaders, name);

        Assert.True(File.Exists(source), $"{Path.Combine(Shaders, name)} is missing, and the reference images were rendered with it.");

        var module = source + ".spv";

        Assert.True(File.Exists(module), $"{Path.Combine(Shaders, name)}.spv is missing, and it is the artefact this suite loads.");

        var sourceTime = File.GetLastWriteTimeUtc(source);
        var moduleTime = File.GetLastWriteTimeUtc(module);

        Assert.True(
            moduleTime >= sourceTime.AddSeconds(-1),
            $"{Path.Combine(Shaders, name)}.spv was written {(sourceTime - moduleTime).TotalSeconds:F0}s "
            + $"before the GLSL beside it, so the module this suite renders with is not this source. "
            + $"Regenerate it: `glslc Shaders/{name} -o Shaders/{name}.spv` from this project's directory."
        );
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
