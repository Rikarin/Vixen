// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vixen.Platform.MacOS;

/// <summary>The other direction: a class whose methods are managed code, so Objective-C can ask us.</summary>
/// <remarks>
///     <para>
///         <b><see cref="ObjC" /> can send a message and cannot answer one, and half of AppKit is a
///         protocol that asks.</b> <c>NSAccessibility</c> is the case this exists for — AppKit calls
///         <c>accessibilityRole</c>, <c>accessibilityChildren</c>, <c>accessibilityFrame</c> and some
///         forty more selectors on an object it was handed, and until a Vixen object can <i>be</i>
///         that object there is no bridge to write. The same shape holds for
///         <c>NSApplicationDelegate</c>, <c>NSTextInputClient</c> and every other callback protocol
///         on this platform.
///     </para>
///     <para>
///         <b>The IMPs are <c>[UnmanagedCallersOnly]</c> static methods.</b> That is not a style
///         choice: it is what makes the function pointer a real, statically compiled entry point with
///         no reverse-P/Invoke stub to generate at run time, which is what keeps this NativeAOT-clean
///         — and iOS is AOT-only. It is the rule <c>VulkanInstance</c>'s debug callback already
///         follows, for the same reason.
///     </para>
///     <para>
///         ⚠ <b>An <c>[UnmanagedCallersOnly]</c> method must not let an exception escape.</b> There is
///         no managed frame above it to catch one: the caller is Objective-C, and an exception
///         crossing that boundary aborts the process. Every IMP written against this has to be its own
///         <c>try</c>/<c>catch</c> returning a defined nothing, and <see cref="ObjCBridge" /> is what
///         lets one find its managed object without throwing on the way.
///     </para>
///     <para>
///         ⚠ <b>Type encodings are not checked, and a wrong one is not an error.</b> Dispatch never
///         reads them — <c>objc_msgSend</c> jumps to the IMP with whatever ABI the <i>caller</i>
///         assumed — so a wrong encoding is invisible until something introspects: <c>NSInvocation</c>,
///         message forwarding, KVC, or a framework building an <c>NSMethodSignature</c> to decide how
///         to call you. That is why <see cref="ObjCTypes" /> spells the needed ones out as constants
///         instead of leaving string literals at call sites, and why the tests read the encoding back
///         through Foundation rather than trusting what was passed in.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
static partial class ObjCRuntime {
    const string Runtime = "/usr/lib/libobjc.A.dylib";

    /// <summary>Begins a class, which is not usable until <see cref="Register" /> closes it.</summary>
    /// <param name="name">The Objective-C class name. Process-global, so make it specific.</param>
    /// <param name="superclass">The class to inherit from — <c>NSObject</c> unless there is a reason.</param>
    /// <returns>The class, or <see cref="nint.Zero" /> if the name is taken or the superclass is missing.</returns>
    /// <remarks>
    ///     ⚠ <b>The name is taken for the life of the process, and a second attempt answers nil rather
    ///     than the existing class.</b> Registration is therefore a once-per-process act, and a caller
    ///     that might run twice — a test, a hot reload — asks <see cref="Lookup" /> first.
    /// </remarks>
    public static nint Define(string name, string superclass) {
        if (!ObjC.Load()) {
            return 0;
        }

        var parent = ObjC.GetClass(superclass);

        return parent == 0 ? 0 : AllocateClassPair(parent, name, 0);
    }

    /// <summary>The class of that name, if this process has one.</summary>
    /// <param name="name">The Objective-C class name.</param>
    /// <returns>The class, or <see cref="nint.Zero" />.</returns>
    public static nint Lookup(string name) => ObjC.Load() ? ObjC.GetClass(name) : 0;

    /// <summary>Gives a class a method whose implementation is managed code.</summary>
    /// <param name="cls">A class from <see cref="Define" />, before <see cref="Register" />.</param>
    /// <param name="selector">The selector, spelled exactly — <c>accessibilityPerformPress</c>.</param>
    /// <param name="types">The type encoding, from <see cref="ObjCTypes" />.</param>
    /// <param name="implementation">
    ///     A function pointer to an <c>[UnmanagedCallersOnly]</c> static method whose first two
    ///     parameters are the receiver and the selector.
    /// </param>
    /// <returns>Whether the method was added; <see langword="false" /> if the class already had one.</returns>
    public static bool AddMethod(nint cls, string selector, string types, nint implementation) =>
        cls != 0 && implementation != 0 && ClassAddMethod(cls, ObjC.Selector(selector), implementation, types);

    /// <summary>Closes a class and makes it usable.</summary>
    /// <param name="cls">A class from <see cref="Define" />.</param>
    /// <returns>The same class, for chaining.</returns>
    /// <remarks>⚠ Methods cannot be added after this, so add every one of them first.</remarks>
    public static nint Register(nint cls) {
        if (cls != 0) {
            RegisterClassPair(cls);
        }

        return cls;
    }

    /// <summary>A new instance of a registered class, through <c>+alloc</c> and <c>-init</c>.</summary>
    /// <param name="cls">A registered class.</param>
    /// <returns>The instance, retained, or <see cref="nint.Zero" />.</returns>
    /// <remarks>
    ///     The caller owns it: <c>+alloc</c> is one of the prefixes that returns a +1 reference, so
    ///     <see cref="Release" /> is what ends it and nothing else will.
    /// </remarks>
    public static nint New(nint cls) =>
        cls == 0 ? 0 : ObjC.Send(ObjC.Send(cls, ObjC.Selector("alloc")), ObjC.Selector("init"));

    /// <summary>Drops one reference.</summary>
    /// <param name="instance">An object owned by this side.</param>
    public static void Release(nint instance) {
        if (instance != 0) {
            ObjC.Send(instance, ObjC.Selector("release"));
        }
    }

    /// <summary>The IMP a class would run for a selector.</summary>
    /// <param name="cls">The class.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The implementation, or <see cref="nint.Zero" /> if the class does not respond.</returns>
    /// <remarks>
    ///     ⚠ <b>Calling a managed IMP from managed code proves nothing about the bridge.</b> It never
    ///     leaves the runtime, and a shim that simply invoked a delegate would pass such a test. This
    ///     is one step better — it asserts the <i>method table</i> holds the pointer — and the real
    ///     assertion is the one above it: hand the instance to Foundation and read what Foundation did.
    /// </remarks>
    public static nint ImplementationOf(nint cls, string selector) {
        var method = ClassGetInstanceMethod(cls, ObjC.Selector(selector));

        return method == 0 ? 0 : MethodGetImplementation(method);
    }

    /// <summary>The type encoding a class recorded for a selector.</summary>
    /// <param name="cls">The class.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The encoding, or <see langword="null" /> if the class does not respond.</returns>
    public static string? EncodingOf(nint cls, string selector) {
        var method = ClassGetInstanceMethod(cls, ObjC.Selector(selector));

        return method == 0 ? null : Marshal.PtrToStringUTF8(MethodGetTypeEncoding(method));
    }

    /// <summary>A message whose return type is a <c>CGRect</c> — <c>accessibilityFrame</c>.</summary>
    /// <remarks>
    ///     ⚠ <b><c>objc_msgSend</c> and not <c>objc_msgSend_stret</c>, and only because this engine's
    ///     Mac is arm64.</b> Four <c>double</c>s are a homogeneous floating-point aggregate on that
    ///     ABI and come back in <c>v0</c>–<c>v3</c> like any other; on x86-64 a 32-byte structure is
    ///     returned through a hidden pointer and needs the <c>_stret</c> entry point, which this
    ///     declaration would silently fail to use.
    /// </remarks>
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial CGRect SendRect(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint AllocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(Runtime, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool ClassAddMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(Runtime, EntryPoint = "objc_registerClassPair")]
    private static partial void RegisterClassPair(nint cls);

    [LibraryImport(Runtime, EntryPoint = "class_getInstanceMethod")]
    private static partial nint ClassGetInstanceMethod(nint cls, nint selector);

    [LibraryImport(Runtime, EntryPoint = "method_getImplementation")]
    private static partial nint MethodGetImplementation(nint method);

    [LibraryImport(Runtime, EntryPoint = "method_getTypeEncoding")]
    private static partial nint MethodGetTypeEncoding(nint method);
}

/// <summary>The Objective-C type encodings this assembly needs, spelled once.</summary>
/// <remarks>
///     <para>
///         The grammar is Apple's <i>Objective-C Runtime Programming Guide</i> § Type Encodings. A
///         method's encoding is its return type followed by every argument, and the first two
///         arguments of every method are the receiver and the selector — which is why
///         <see cref="Signature" /> supplies them rather than letting a call site remember to.
///     </para>
///     <para>
///         ⚠ <b><see cref="Bool" /> is <c>B</c> because this engine's Mac is arm64.</b> On 64-bit ARM
///         and on the simulators <c>BOOL</c> is C99 <c>_Bool</c> and encodes as <c>B</c>; on x86-64
///         macOS it is <c>signed char</c> and encodes as <c>c</c>. It is the same fork
///         <see cref="ObjC" /> documents for <c>objc_msgSend_fpret</c>, with the same answer: this is
///         written for the architecture the engine runs on and would be wrong on the other one.
///     </para>
/// </remarks>
static class ObjCTypes {
    /// <summary>An object pointer — <c>id</c>. The receiver, and every <c>NSString</c> answer.</summary>
    public const string Object = "@";

    /// <summary>A selector — <c>SEL</c>. Every method's second argument.</summary>
    public const string Selector = ":";

    /// <summary>A class — <c>Class</c>.</summary>
    public const string Class = "#";

    /// <summary>A <c>BOOL</c> on arm64. See this class's remarks before using it elsewhere.</summary>
    public const string Bool = "B";

    /// <summary>A <c>long</c>, which is what <c>NSInteger</c> is on a 64-bit target.</summary>
    public const string Long = "q";

    /// <summary>An <c>unsigned long</c> — <c>NSUInteger</c>.</summary>
    public const string UnsignedLong = "Q";

    /// <summary>A <c>double</c>, which is what <c>CGFloat</c> is on a 64-bit target.</summary>
    public const string Double = "d";

    /// <summary>No return value.</summary>
    public const string Void = "v";

    /// <summary>A <c>CGRect</c>, structure and all — what <c>accessibilityFrame</c> answers with.</summary>
    public const string Rect = "{CGRect={CGPoint=dd}{CGSize=dd}}";

    /// <summary>A <c>CGPoint</c>.</summary>
    public const string Point = "{CGPoint=dd}";

    /// <summary>A <c>CGSize</c>.</summary>
    public const string Size = "{CGSize=dd}";

    /// <summary>Builds a method's encoding from its return type and its own arguments.</summary>
    /// <param name="returns">The return encoding.</param>
    /// <param name="arguments">The encodings of the arguments after the receiver and the selector.</param>
    /// <returns>The full encoding.</returns>
    /// <remarks>The hidden <c>self</c> and <c>_cmd</c> are supplied here so no call site can forget them.</remarks>
    public static string Signature(string returns, params string[] arguments) =>
        string.Concat(returns, Object, Selector, string.Concat(arguments));
}

/// <summary>Which managed object an Objective-C instance stands for.</summary>
/// <remarks>
///     <para>
///         <b>A side table rather than an ivar, deliberately.</b> <c>class_addIvar</c> means computing
///         a layout and an alignment for a pointer this side never sees the declaration of, and
///         reading it back means <c>object_getInstanceVariable</c> and an offset lookup per call. A
///         dictionary keyed by the instance pointer needs none of that, survives every question about
///         trimming and AOT, and costs one hash on a path that is already crossing a language
///         boundary.
///     </para>
///     <para>
///         ⚠ <b>The reference is strong, so <see cref="Detach" /> is not optional.</b> An
///         accessibility node holds a <c>UiElement</c>, an element holds its subtree, and a bridge
///         that forgot to detach would keep every window it ever opened. The pairing is
///         <see cref="Attach" /> when the instance is made and <see cref="Detach" /> when it is
///         released, and nothing else.
///     </para>
///     <para>
///         ⚠ <b>Locked, because the question "which thread" has two answers.</b> AppKit asks on the
///         main thread; a bridge builds its instances on whichever thread owns the document. The
///         table is the seam between them and is the one part that must not assume.
///     </para>
/// </remarks>
static class ObjCBridge {
    static readonly Dictionary<nint, object> Targets = [];
    static readonly Lock Gate = new();

    /// <summary>How many instances are attached. For a leak assertion, and for nothing else.</summary>
    public static int Count {
        get {
            lock (Gate) {
                return Targets.Count;
            }
        }
    }

    /// <summary>Says which managed object an instance stands for.</summary>
    /// <param name="instance">The Objective-C instance.</param>
    /// <param name="target">The managed object.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    public static void Attach(nint instance, object target) {
        ArgumentNullException.ThrowIfNull(target);

        if (instance == 0) {
            return;
        }

        lock (Gate) {
            Targets[instance] = target;
        }
    }

    /// <summary>The managed object an instance stands for, or <see langword="null" />.</summary>
    /// <param name="instance">The Objective-C instance, as an IMP received it.</param>
    /// <returns>The object, or <see langword="null" /> if nothing is attached.</returns>
    /// <remarks>
    ///     ⚠ <b>Null is a normal answer and never an exception.</b> The caller is an IMP with
    ///     Objective-C above it, so throwing here would abort the process; an instance detached while
    ///     AppKit still held a pointer to it is exactly the race this has to survive.
    /// </remarks>
    public static object? Target(nint instance) {
        if (instance == 0) {
            return null;
        }

        lock (Gate) {
            return Targets.GetValueOrDefault(instance);
        }
    }

    /// <summary>Forgets an instance.</summary>
    /// <param name="instance">The Objective-C instance.</param>
    /// <returns>Whether anything was attached to it.</returns>
    public static bool Detach(nint instance) {
        lock (Gate) {
            return Targets.Remove(instance);
        }
    }
}

/// <summary>A <c>CGRect</c>, laid out the way the ABI returns one.</summary>
/// <param name="X">The left edge, in the receiver's coordinate space.</param>
/// <param name="Y">The bottom edge — ⚠ AppKit's origin is bottom-left and Vixen's is top-left.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <remarks>
///     Four <c>CGFloat</c>s, which are <c>double</c> on every 64-bit Apple target. Declared here
///     rather than reached for from <c>Vixen.Core.Mathematics</c> because the point of it is to be
///     bit-for-bit the C structure, and a type that is also used for arithmetic acquires members that
///     make that a coincidence rather than a contract.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
readonly record struct CGRect(double X, double Y, double Width, double Height);
