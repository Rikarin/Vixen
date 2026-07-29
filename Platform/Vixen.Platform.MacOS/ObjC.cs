// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vixen.Platform.MacOS;

/// <summary>Objective-C, as far as this assembly needs it: send a message, read a string.</summary>
/// <remarks>
///     <para>
///         <c>docs/plan/10</c> § macOS says this in one line — "ObjC interop via
///         <c>[LibraryImport]</c> against <c>objc_msgSend</c> for the handful of calls needed. No
///         Xamarin.Mac bindings" — and this is that. Three runtime functions and a set of
///         <c>objc_msgSend</c> declarations, one per shape of call, because the symbol is a single
///         untyped entry point and the caller is what gives it a prototype.
///     </para>
///     <para>
///         <b>Getting a prototype wrong is not a compile error and is a crash.</b> Every declaration
///         below is written against the method it is used with, and the call sites name the
///         Objective-C selector they are sending in a comment for that reason. This is why the set
///         is deliberately small: the alternative to a handful of hand-written signatures is a
///         binding generator, and the alternative to that is a hundred of them.
///     </para>
///     <para>
///         <b>The frameworks have to be loaded first.</b> A .NET process on macOS links neither
///         Foundation nor AppKit, so <c>objc_getClass("NSPasteboard")</c> answers with
///         <see cref="nint.Zero" /> until something has <c>dlopen</c>ed the framework that defines
///         it. <see cref="Load" /> is that something, and every entry point into this assembly calls
///         it first.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
static unsafe partial class ObjC {
    const string Runtime = "/usr/lib/libobjc.A.dylib";

    static readonly Lock Gate = new();
    static bool loaded;

    /// <summary>Loads Foundation and AppKit, once.</summary>
    /// <returns><see langword="false" /> if either is missing, which on a Mac means something is
    /// very wrong and is still not a reason to throw from a property getter.</returns>
    public static bool Load() {
        lock (Gate) {
            if (loaded) {
                return true;
            }

            loaded = NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _)
                && NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _);

            return loaded;
        }
    }

    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetClass(string name);

    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Selector(string name);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint first, nint second);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendBool(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendBool(nint receiver, nint selector, nint first, nint second);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint SendSetBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.U1)] bool value);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint SendUtf8(nint receiver, nint selector, string value);

    /// <summary>
    ///     <c>-[NSBitmapImageRep initWithBitmapDataPlanes:pixelsWide:pixelsHigh:bitsPerSample:
    ///     samplesPerPixel:hasAlpha:isPlanar:colorSpaceName:bitmapFormat:bytesPerRow:bitsPerPixel:]</c>,
    ///     which is the one call here with enough arguments to be worth naming.
    /// </summary>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitBitmap(
        nint receiver,
        nint selector,
        byte** planes,
        nint width,
        nint height,
        nint bitsPerSample,
        nint samplesPerPixel,
        [MarshalAs(UnmanagedType.U1)] bool hasAlpha,
        [MarshalAs(UnmanagedType.U1)] bool isPlanar,
        nint colourSpace,
        nuint format,
        nint bytesPerRow,
        nint bitsPerPixel
    );

    /// <summary>Whether the calling thread is the one AppKit will accept work from.</summary>
    /// <remarks>
    ///     <b>Measured, on 2026-07-29, by a test that crashed the runner.</b> AppKit aborts with
    ///     <c>EXC_BAD_ACCESS (SIGBUS)</c> and the code <c>0xbad4007</c> — its "this must be called
    ///     from the main thread" assertion — for more than just windows: encoding an
    ///     <c>NSBitmapImageRep</c> with <c>TIFFRepresentation</c> does it too, on a thread with no
    ///     window in sight. So the rule is not "do not create windows off the main thread", it is
    ///     "do not call AppKit off the main thread", and everything in this assembly that reaches
    ///     into AppKit rather than Foundation asks this first. What is left unguarded is the
    ///     pasteboard's own reads and writes and <c>NSProcessInfo</c>, which are documented as
    ///     thread-safe and are exercised from a test runner's worker thread on every run.
    /// </remarks>
    public static bool IsMainThread =>
        Load() && SendBool(Send(GetClass("NSThread"), Selector("currentThread")), Selector("isMainThread"));

    /// <summary>An <c>NSString</c> from a managed string. Autoreleased, so it is not freed here.</summary>
    /// <remarks>
    ///     <c>+[NSString stringWithUTF8String:]</c> rather than <c>alloc</c>/<c>init</c>: the result
    ///     belongs to the autorelease pool, which is what every caller here wants — the string is
    ///     handed to one message and never referred to again. Without a pool on this thread the
    ///     object leaks, which for the handful of short-lived strings this assembly makes is a
    ///     trade the alternative does not justify.
    /// </remarks>
    public static nint String(string value) =>
        SendUtf8(GetClass("NSString"), Selector("stringWithUTF8String:"), value);

    /// <summary>A managed string from an <c>NSString</c>, or <see langword="null" />.</summary>
    public static string? ToString(nint text) =>
        text == 0 ? null : Marshal.PtrToStringUTF8(Send(text, Selector("UTF8String")));

    /// <summary>An empty <c>NSArray</c>, for the calls that insist on one.</summary>
    public static nint EmptyArray() => Send(GetClass("NSArray"), Selector("array"));

    /// <summary>An <c>NSArray</c> of <c>NSString</c>, built one <c>addObject:</c> at a time.</summary>
    /// <remarks>
    ///     Rather than <c>+arrayWithObjects:</c>, which is variadic — and a variadic
    ///     <c>objc_msgSend</c> has a different calling convention from a fixed one on arm64, so
    ///     declaring one with a fixed prototype is the kind of mistake that works on x86-64 and
    ///     corrupts the stack on Apple silicon.
    /// </remarks>
    public static nint StringArray(IReadOnlyList<string> values) {
        var array = Send(GetClass("NSMutableArray"), Selector("array"));
        var add = Selector("addObject:");

        foreach (var value in values) {
            Send(array, add, String(value));
        }

        return array;
    }

    /// <summary>How many objects an <c>NSArray</c> holds.</summary>
    public static nint Count(nint array) => array == 0 ? 0 : Send(array, Selector("count"));

    /// <summary>One object out of an <c>NSArray</c>.</summary>
    public static nint At(nint array, nint index) => Send(array, Selector("objectAtIndex:"), index);
}
