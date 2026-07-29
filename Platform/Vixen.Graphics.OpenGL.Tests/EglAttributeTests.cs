// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>The attribute lists EGL is asked for things with.</summary>
/// <remarks>
///     Every one of these is a decision that shows up as a driver saying no, hours later, on
///     somebody else's phone. They are cheap to assert here and expensive to find there.
/// </remarks>
public sealed class EglAttributeTests {
    /// <summary>A config list terminates, asks for ES 3 and carries eight bits of alpha.</summary>
    /// <remarks>
    ///     The ES3 renderable bit rather than ES2's: the ES2 bit says a config can run an ES 2.0
    ///     context and nothing about ES 3, which is this backend's floor. Asking for it is what makes
    ///     "this driver is too old" a failure at <c>eglChooseConfig</c> rather than at the first
    ///     shader.
    /// </remarks>
    [Fact]
    public void AsksForAnEs3RgbaConfig() {
        var attributes = EglAttributes.Config(new(0x900), window: true);

        Assert.Equal(EglConstants.None, attributes[^1]);
        Assert.Equal(EglConstants.OpenGlEs3Bit, Value(attributes, EglConstants.RenderableType));
        Assert.Equal(EglConstants.WindowBit, Value(attributes, EglConstants.SurfaceType));
        Assert.Equal(8, Value(attributes, EglConstants.RedSize));
        Assert.Equal(8, Value(attributes, EglConstants.AlphaSize));
        Assert.Equal(24, Value(attributes, EglConstants.DepthSize));
        Assert.Equal(8, Value(attributes, EglConstants.StencilSize));
    }

    /// <summary>An offscreen device asks for a pbuffer config, which is a different bit.</summary>
    /// <remarks>
    ///     A config matched for a window is not required to support a pbuffer, and on tiled mobile
    ///     GPUs frequently does not.
    /// </remarks>
    [Fact]
    public void AsksForAPbufferConfigOffscreen() {
        var attributes = EglAttributes.Config(new(0), window: false);

        Assert.Equal(EglConstants.PbufferBit, Value(attributes, EglConstants.SurfaceType));
    }

    /// <summary>One sample is not a multisample request.</summary>
    /// <remarks>
    ///     <c>EGL_SAMPLES 1</c> asks for a multisample config with one sample, which some drivers
    ///     have and none need. Omitting it asks for whatever the driver's ordinary config is.
    /// </remarks>
    [Fact]
    public void OmitsSamplesWhenThereIsOne() {
        var attributes = EglAttributes.Config(new(0x900), window: true);

        Assert.Null(Value(attributes, EglConstants.Samples));
        Assert.Null(Value(attributes, EglConstants.SampleBuffers));
    }

    /// <summary>More than one asks for both the buffer and the count.</summary>
    [Fact]
    public void AsksForSamplesWhenThereAreSeveral() {
        var attributes = EglAttributes.Config(new(0x900, Samples: 4), window: true);

        Assert.Equal(1, Value(attributes, EglConstants.SampleBuffers));
        Assert.Equal(4, Value(attributes, EglConstants.Samples));
    }

    /// <summary>A negative depth or stencil becomes none rather than reaching the driver.</summary>
    /// <remarks>EGL reads these as a floor, and a negative floor is <c>EGL_BAD_ATTRIBUTE</c>.</remarks>
    [Fact]
    public void ClampsANegativeFloorToNone() {
        var attributes = EglAttributes.Config(new(0x900, DepthBits: -1, StencilBits: -8), window: true);

        Assert.Equal(0, Value(attributes, EglConstants.DepthSize));
        Assert.Equal(0, Value(attributes, EglConstants.StencilSize));
    }

    /// <summary>A GLES 3.0 request carries the major version alone.</summary>
    /// <remarks>
    ///     <c>EGL_CONTEXT_MINOR_VERSION</c> is EGL 1.5, or <c>EGL_KHR_create_context</c> before it,
    ///     and a driver with neither refuses an attribute it does not recognise whatever its value.
    ///     Sending <c>minor = 0</c> would turn every old driver into a failure for no gain.
    /// </remarks>
    [Fact]
    public void OmitsTheMinorVersionForEs30() {
        var attributes = EglAttributes.Context(GlProfile.Es30, debug: false, eglMinor: 5);

        Assert.Equal(3, Value(attributes, EglConstants.ContextMajorVersion));
        Assert.Null(Value(attributes, EglConstants.ContextMinorVersion));
        Assert.Equal(EglConstants.None, attributes[^1]);
    }

    /// <summary>A GLES 3.2 request has to carry it, which is what it risks the refusal for.</summary>
    [Fact]
    public void CarriesTheMinorVersionForEs32() {
        var attributes = EglAttributes.Context(GlProfile.Es32, debug: false, eglMinor: 5);

        Assert.Equal(3, Value(attributes, EglConstants.ContextMajorVersion));
        Assert.Equal(2, Value(attributes, EglConstants.ContextMinorVersion));
    }

    /// <summary>A debug context is asked for on EGL 1.5 and not below it.</summary>
    [Theory]
    [InlineData(5, true)]
    [InlineData(4, false)]
    public void AsksForDebugOnlyWhereTheAttributeExists(int eglMinor, bool expected) {
        var attributes = EglAttributes.Context(GlProfile.Es32, debug: true, eglMinor);

        Assert.Equal(expected ? EglConstants.True : null, Value(attributes, EglConstants.ContextDebug));
    }

    /// <summary>Nothing asks for a debug context that did not want one.</summary>
    [Fact]
    public void DoesNotAskForDebugUnasked() {
        var attributes = EglAttributes.Context(GlProfile.Es32, debug: false, eglMinor: 5);

        Assert.Null(Value(attributes, EglConstants.ContextDebug));
    }

    /// <summary>A pbuffer carries its size, and never a zero one.</summary>
    /// <remarks>
    ///     A zero-sized pbuffer is <c>EGL_BAD_PARAMETER</c>. A device created offscreen with no size
    ///     is a caller who never intends to present, which one pixel serves.
    /// </remarks>
    [Theory]
    [InlineData(320, 240, 320, 240)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-4, 8, 1, 8)]
    public void SizesAPbufferAtLeastOnePixel(int width, int height, int expectedWidth, int expectedHeight) {
        var attributes = EglAttributes.PbufferSurface(new Int2(width, height));

        Assert.Equal(expectedWidth, Value(attributes, EglConstants.Width));
        Assert.Equal(expectedHeight, Value(attributes, EglConstants.Height));
        Assert.Equal(EglConstants.None, attributes[^1]);
    }

    /// <summary>A window surface asks for nothing beyond its config.</summary>
    /// <remarks>
    ///     <c>EGL_GL_COLORSPACE</c> is what would go here, and setting it would encode sRGB a second
    ///     time: the engine renders into its own attachments, whose format already says so, and blits.
    /// </remarks>
    [Fact]
    public void AsksNothingExtraOfAWindowSurface() => Assert.Equal([EglConstants.None], EglAttributes.WindowSurface());

    /// <summary>The value an attribute list carries for a key, or null if it carries none.</summary>
    static int? Value(int[] attributes, int key) {
        for (var index = 0; index + 1 < attributes.Length; index += 2) {
            if (attributes[index] == EglConstants.None) {
                return null;
            }

            if (attributes[index] == key) {
                return attributes[index + 1];
            }
        }

        return null;
    }
}
