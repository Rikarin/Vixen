// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Graphics;
using Vixen.Shaders.Generated;
using Vixen.Ui.Renderer;

namespace Vixen.Ui.Desktop;

/// <summary>The eight modules <see cref="UiRenderer" /> draws with, embedded and compiled on demand.</summary>
/// <remarks>
///     <para>
///         <b>The whole set, because half of it is optional in the way that hurts.</b>
///         <see cref="UiShaders" /> takes four stages positionally and four as init properties, and
///         the four optional ones are documented — correctly — as degrading to a picture rather than
///         to a failure. That is exactly what makes forgetting them expensive: an application with no
///         <c>Image</c> stage has <c>UiRenderer.Compose</c> return having done nothing, so every
///         faded subtree draws at full strength and a disabled button comes out opaque. One with no
///         <c>Colour</c> stage has <c>filter: grayscale(1)</c> cascade, resolve, and do nothing.
///     </para>
///     <para>
///         ⚠ <b><c>Samples/02-HelloUi</c> wired four of the eight for months and had both bugs.</b>
///         Nothing failed, nothing logged, and the sample's own theme puts an <c>opacity</c> on every
///         disabled control — so the demonstration of the control set was quietly demonstrating the
///         wrong picture. A host that has to name eight modules is a host where somebody names four.
///     </para>
///     <para>
///         ⚠ <b>These are Raven's, compiled from <c>Shaders/Ui.rvn</c> by this repository's own
///         compiler.</b> They were hand-written GLSL until 2026-08-23, committed three times and
///         compiled by whatever <c>glslc</c> was on the machine of whoever last touched them — which
///         is what <c>SharedUiShaderTests</c> existed to police, after two of the three copies had
///         already lost the whole shadow path.
///     </para>
///     <para>
///         ⚠ <b>So the vertex attribute locations are not 0 to 3, and that is the one thing a host
///         cannot guess.</b> Raven's <c>StreamPlan</c> puts a stage's own parameters <i>after</i> the
///         shader's streams, so <c>Ui.rvn</c>'s three streams push the four vertex attributes to 3
///         through 6 — and adding a stream moves them again. They are read out of the compiler's own
///         reflection below rather than written down, because a wrong location is not a validation
///         error: the pipeline binds nothing to that attribute and the stage reads whatever the
///         driver left there, which is an interface drawn from uninitialised memory, on one driver,
///         silently.
///     </para>
/// </remarks>
public static class UiShaderLibrary {
    /// <summary>Compiles every stage against a device.</summary>
    /// <param name="device">The device to create the modules on.</param>
    /// <returns>A complete shader table: four required stages and all four optional ones.</returns>
    /// <remarks>
    ///     ⚠ <b>Once per device, not once per window.</b> A <see cref="ShaderHandle" /> is a module
    ///     and a module is not a pipeline — two windows each build their own
    ///     <see cref="UiRenderer" /> from one of these tables, which is what
    ///     <see cref="UiWindowSurface" /> does and why it takes the table rather than making one.
    /// </remarks>
    public static UiShaders Load(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        return new UiShaders(
            device.CreateShader(ShaderStage.Vertex, Module("UiVertex.vert.spv"), "ui vertex"),
            device.CreateShader(ShaderStage.Fragment, Module("UiBox.frag.spv"), "ui box"),
            device.CreateShader(ShaderStage.Fragment, Module("UiText.frag.spv"), "ui text"),
            device.CreateShader(ShaderStage.Fragment, Module("UiSolid.frag.spv"), "ui solid")
        ) {
            // The stage that samples a texture — an image, a video frame, a viewport — and also the
            // one `Compose` composites a translucent group's surface back with. See the remark
            // above: its absence is what turns a faded panel opaque.
            Image = device.CreateShader(ShaderStage.Fragment, Module("UiImage.frag.spv"), "ui image"),

            // One axis of the separable Gaussian a group's `filter: blur()` is made of, run twice
            // with a different kernel rather than shipped twice.
            Blur = device.CreateShader(ShaderStage.Fragment, Module("UiBlur.frag.spv"), "ui blur"),

            // The seven colour functions — grayscale, brightness, invert and the rest. It adds no
            // pass and no surface: the matrix rides the composite draw the group was making anyway.
            Colour = device.CreateShader(ShaderStage.Fragment, Module("UiColour.frag.spv"), "ui colour"),

            // ⚠ `mask-image`, and it carries the colour matrix too. A pipeline is bound once per
            // draw, so a group with both a `filter` and a `mask-image` has to be served by one
            // module — which is why supplying this one without `Colour` would be stranger than
            // supplying neither: `grayscale` would then work on masked elements and nowhere else.
            Mask = device.CreateShader(ShaderStage.Fragment, Module("UiMask.frag.spv"), "ui mask"),

            // ⚠ Read out of Raven's reflection rather than written down — see the remark on the
            // class. `Vixen.Shaders.Generators` turns `Shaders/UiVertex.reflect.json` into these four
            // constants at build time, so a stream added to `Ui.rvn` moves them and nothing in this
            // file has to notice.
            Locations = new(
                UiVertexKeys.PositionLocation,
                UiVertexKeys.TexcoordLocation,
                UiVertexKeys.VertexColourLocation,
                UiVertexKeys.VertexShapeLocation
            )
        };
    }

    /// <summary>Reads one embedded module.</summary>
    /// <remarks>
    ///     ⚠ Found by suffix rather than named outright. The manifest name is the root namespace plus
    ///     the folder plus the file — <c>Vixen.Ui.Desktop.Shaders.UiVertex.vert.spv</c> — so it is not
    ///     something a reader would guess and it changes if the assembly is renamed.
    /// </remarks>
    static byte[] Module(string name) {
        var assembly = typeof(UiShaderLibrary).Assembly;

        var resource = assembly.GetManifestResourceNames()
                .SingleOrDefault(entry => entry.EndsWith(name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"'{name}' is not embedded in {assembly.GetName().Name}. The project file globs Shaders\\*.spv; "
                + "a module that is not there was not regenerated after its source changed."
            );

        using var stream = assembly.GetManifestResourceStream(resource)!;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return memory.ToArray();
    }
}
