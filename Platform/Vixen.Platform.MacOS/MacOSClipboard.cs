// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vixen.Platform.MacOS;

/// <summary>The macOS pasteboard: images and application-defined types.</summary>
/// <remarks>
///     <para>
///         <b>A type here is a UTI.</b> <c>public.png</c>, <c>public.tiff</c>,
///         <c>com.adobe.pdf</c>, or an application's own reverse-DNS identifier — the same
///         namespace the whole system uses for file types, and the one place where the abstract
///         "platform-neutral format name" of <see cref="IClipboard.TryGetData" /> has a
///         well-defined home. Names are passed through unchanged; inventing a fourth vocabulary to
///         map onto three real ones would help nobody.
///     </para>
///     <para>
///         <b>Reading an image goes through <c>NSBitmapImageRep</c> and then reads its bytes
///         directly.</b> The alternative — drawing the representation into a second one of a known
///         layout — is what a graphics application does and needs a graphics context, a saved state
///         and an <c>NSRect</c> through <c>objc_msgSend</c>. Reading the bytes handles what a
///         pasteboard actually contains: eight bits a sample, interleaved, three or four samples.
///         A 16-bit or floating-point representation is refused rather than misread, which is a
///         paste that does nothing instead of a paste that is wrong.
///     </para>
/// </remarks>
/// <param name="fallback">The portable clipboard, which keeps the text half.</param>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacOSClipboard(IClipboard fallback) : IClipboard {
    const string Png = "public.png";
    const string Tiff = "public.tiff";

    /// <summary><c>NSBitmapFormatAlphaFirst</c>.</summary>
    const nuint AlphaFirst = 1 << 0;

    /// <summary><c>NSBitmapFormatAlphaNonpremultiplied</c>.</summary>
    const nuint AlphaNonpremultiplied = 1 << 1;

    /// <summary><c>NSBitmapFormatFloatingPointSamples</c>.</summary>
    const nuint FloatingPoint = 1 << 2;

    /// <inheritdoc />
    public bool HasText => fallback.HasText;

    /// <inheritdoc />
    public bool HasImage => Data(Png) != 0 || Data(Tiff) != 0;

    /// <inheritdoc />
    public bool TryGetText([NotNullWhen(true)] out string? text) => fallback.TryGetText(out text);

    /// <inheritdoc />
    public bool SetText(string text) => fallback.SetText(text);

    /// <inheritdoc />
    /// <remarks>
    ///     Refuses off the main thread rather than crashing there. <c>NSBitmapImageRep</c> is
    ///     AppKit, and AppKit aborts the process when it is called from anywhere else — see
    ///     <see cref="ObjC.IsMainThread" />, which records how that was found out. The pasteboard
    ///     itself is not AppKit in this respect and is read from any thread, which is why only the
    ///     two image methods are guarded.
    /// </remarks>
    public bool TryGetImage(out ClipboardImage image) {
        image = default;

        if (!ObjC.IsMainThread) {
            return false;
        }

        // PNG first and TIFF second: an application that offers both offers the same picture twice,
        // and PNG is the smaller copy to decode. Every macOS application offers TIFF.
        var data = Data(Png);

        if (data == 0) {
            data = Data(Tiff);
        }

        if (data == 0) {
            return false;
        }

        var representation = ObjC.Send(
            ObjC.GetClass("NSBitmapImageRep"),
            ObjC.Selector("imageRepWithData:"),
            data
        );

        return representation != 0 && TryRead(representation, out image);
    }

    /// <inheritdoc cref="TryGetImage" />
    public bool SetImage(in ClipboardImage image) {
        if (!ObjC.IsMainThread) {
            return false;
        }

        var (width, height) = (image.Size.X, image.Size.Y);
        var stride = width * 4;

        if (width <= 0 || height <= 0 || image.Pixels.Length < stride * height) {
            return false;
        }

        var representation = ObjC.Send(ObjC.GetClass("NSBitmapImageRep"), ObjC.Selector("alloc"));

        if (representation == 0) {
            return false;
        }

        try {
            representation = ObjC.SendInitBitmap(
                representation,
                ObjC.Selector(
                    "initWithBitmapDataPlanes:pixelsWide:pixelsHigh:bitsPerSample:samplesPerPixel:"
                    + "hasAlpha:isPlanar:colorSpaceName:bitmapFormat:bytesPerRow:bitsPerPixel:"
                ),
                // Null, so AppKit allocates the plane and owns it. Handing it a pointer into the
                // managed heap would mean the representation outliving the pin that made the
                // pointer valid, and the bug that produces is a picture of whatever the garbage
                // collector moved into that memory afterwards.
                null,
                width,
                height,
                8,
                4,
                true,
                false,
                ObjC.String("NSDeviceRGBColorSpace"),

                // Straight alpha, which is what ClipboardImage carries. Omitting the flag would tell
                // AppKit the channels are premultiplied and make everything translucent darker than
                // it is.
                AlphaNonpremultiplied,
                stride,
                32
            );

            if (representation == 0) {
                return false;
            }

            var plane = ObjC.Send(representation, ObjC.Selector("bitmapData"));

            if (plane == 0) {
                return false;
            }

            image.Pixels.Span[..(stride * height)].CopyTo(new((void*)plane, stride * height));

            // TIFF rather than PNG: TIFFRepresentation is on NSBitmapImageRep itself, every macOS
            // application reads it, and the encoder that produces PNG here would be one more thing
            // that can be wrong. The pasteboard's own conversions cover the rest.
            var tiff = ObjC.Send(representation, ObjC.Selector("TIFFRepresentation"));

            return tiff != 0 && Write(Tiff, tiff);
        } finally {
            ObjC.Send(representation, ObjC.Selector("release"));
        }
    }

    /// <inheritdoc cref="IClipboard.TryGetData" />
    public bool TryGetData(string format, out ReadOnlyMemory<byte> data) {
        ArgumentException.ThrowIfNullOrEmpty(format);

        var found = Data(format);

        if (found == 0) {
            data = default;
            return false;
        }

        data = Bytes(found);
        return !data.IsEmpty;
    }

    /// <inheritdoc cref="IClipboard.SetData" />
    public bool SetData(string format, ReadOnlySpan<byte> data) {
        ArgumentException.ThrowIfNullOrEmpty(format);

        if (!ObjC.Load()) {
            return false;
        }

        fixed (byte* bytes = data) {
            var value = ObjC.Send(
                ObjC.GetClass("NSData"),
                ObjC.Selector("dataWithBytes:length:"),
                (nint)bytes,
                data.Length
            );

            return value != 0 && Write(format, value);
        }
    }

    /// <inheritdoc />
    public void Clear() {
        if (ObjC.Load() && Pasteboard() is var pasteboard && pasteboard != 0) {
            ObjC.Send(pasteboard, ObjC.Selector("clearContents"));
        }
    }

    static nint Pasteboard() =>
        ObjC.Send(ObjC.GetClass("NSPasteboard"), ObjC.Selector("generalPasteboard"));

    /// <summary>The <c>NSData</c> the pasteboard holds for a type, or nothing.</summary>
    static nint Data(string type) {
        if (!ObjC.Load()) {
            return 0;
        }

        var pasteboard = Pasteboard();

        return pasteboard == 0
            ? 0
            : ObjC.Send(pasteboard, ObjC.Selector("dataForType:"), ObjC.String(type));
    }

    static byte[] Bytes(nint data) {
        var length = (int)ObjC.Send(data, ObjC.Selector("length"));
        var pointer = ObjC.Send(data, ObjC.Selector("bytes"));

        if (length <= 0 || pointer == 0) {
            return [];
        }

        return new ReadOnlySpan<byte>((void*)pointer, length).ToArray();
    }

    /// <summary>Puts one type on the pasteboard, replacing what was there.</summary>
    /// <remarks>
    ///     <c>clearContents</c> first, because it is what takes ownership: without it
    ///     <c>setData:forType:</c> is writing to a pasteboard whose owner is somebody else and
    ///     returns <see langword="false" />. It is also what makes the change generation-visible to
    ///     the applications watching for one.
    /// </remarks>
    static bool Write(string type, nint data) {
        var pasteboard = Pasteboard();

        if (pasteboard == 0) {
            return false;
        }

        ObjC.Send(pasteboard, ObjC.Selector("clearContents"));

        var types = ObjC.StringArray([type]);
        ObjC.Send(pasteboard, ObjC.Selector("declareTypes:owner:"), types, 0);

        return ObjC.SendBool(
            pasteboard,
            ObjC.Selector("setData:forType:"),
            data,
            ObjC.String(type)
        );
    }

    /// <summary>Reads an <c>NSBitmapImageRep</c>'s own bytes as straight RGBA8.</summary>
    static bool TryRead(nint representation, out ClipboardImage image) {
        image = default;

        var width = (int)ObjC.Send(representation, ObjC.Selector("pixelsWide"));
        var height = (int)ObjC.Send(representation, ObjC.Selector("pixelsHigh"));
        var bitsPerSample = (int)ObjC.Send(representation, ObjC.Selector("bitsPerSample"));
        var samples = (int)ObjC.Send(representation, ObjC.Selector("samplesPerPixel"));
        var stride = (int)ObjC.Send(representation, ObjC.Selector("bytesPerRow"));
        var format = (nuint)ObjC.Send(representation, ObjC.Selector("bitmapFormat"));
        var planar = ObjC.SendBool(representation, ObjC.Selector("isPlanar"));
        var source = ObjC.Send(representation, ObjC.Selector("bitmapData"));

        if (width <= 0 || height <= 0 || bitsPerSample != 8 || samples is not (3 or 4) || planar
            || (format & FloatingPoint) != 0 || source == 0 || stride < width * samples) {
            return false;
        }

        var alphaFirst = (format & AlphaFirst) != 0;
        var premultiplied = samples == 4 && (format & AlphaNonpremultiplied) == 0;
        var pixels = new byte[width * height * 4];
        var bytes = new ReadOnlySpan<byte>((void*)source, stride * height);

        for (var y = 0; y < height; y++) {
            var row = bytes.Slice(y * stride, width * samples);

            for (var x = 0; x < width; x++) {
                var from = x * samples;
                var to = (y * width + x) * 4;
                var offset = samples == 4 && alphaFirst ? 1 : 0;
                var alpha = samples == 4 ? row[from + (alphaFirst ? 0 : 3)] : (byte)255;

                pixels[to] = row[from + offset];
                pixels[to + 1] = row[from + offset + 1];
                pixels[to + 2] = row[from + offset + 2];
                pixels[to + 3] = alpha;

                if (premultiplied && alpha is > 0 and < 255) {
                    // ClipboardImage is straight alpha. Dividing back out is lossy — the low bits
                    // were thrown away when it was multiplied in — and it is the only way to hand
                    // over the colour the image was made of rather than the colour it composites to.
                    pixels[to] = Unpremultiply(pixels[to], alpha);
                    pixels[to + 1] = Unpremultiply(pixels[to + 1], alpha);
                    pixels[to + 2] = Unpremultiply(pixels[to + 2], alpha);
                }
            }
        }

        image = new(pixels, new(width, height));
        return true;
    }

    static byte Unpremultiply(byte channel, byte alpha) =>
        (byte)Math.Min(255, (channel * 255 + alpha / 2) / alpha);
}
