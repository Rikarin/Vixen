// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace Vixen.Platform.MacOS.Tests;

/// <summary>A class whose methods are managed code, proved by making Foundation call them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A test that registers a class and then calls the selector from managed code proves
///         nothing.</b> It never leaves the runtime, and a shim that simply invoked a delegate would
///         pass it — which is the whole reason <c>ObjCRuntime</c> exists rather than an interface with
///         a managed implementation behind it. So every assertion here is about what <i>Objective-C</i>
///         did: <c>-[NSArray description]</c> asks each element for its own description,
///         <c>NSInvocation</c> lays out a call frame from the type encoding and invokes through it,
///         and <c>-[NSObject methodSignatureForSelector:]</c> makes Foundation parse that encoding.
///         None of the three can be satisfied by managed code calling itself.
///     </para>
///     <para>
///         ⚠ <b>What this prints on the day it does not run.</b> Skipped off macOS, and every
///         assertion is on a value that could only have come back through Objective-C — a description
///         string that would read <c>&lt;VixenObjCProbe: 0x…&gt;</c> if the override had not taken, a
///         return byte still holding the <c>0xFF</c> it was seeded with if Foundation copied nothing,
///         a null encoding if the method were not in the table. There is no arrangement of these that
///         passes vacuously, which is the property <c>AccessibilitySnapshot.Unnamed</c> is built
///         around and the one an interop test most easily loses.
///     </para>
/// </remarks>
public unsafe class ObjCRuntimeTests {
    const string ClassName = "VixenObjCProbe";
    const string EqualKey = "vixen-equal-key";

    static readonly Lock Gate = new();
    static nint probeClass;

    /// <summary>What an instance of the probe class stands for, found through the side table.</summary>
    sealed class Probe {
        public required string Description { get; init; }

        public required long Answer { get; init; }

        public required CGRect Frame { get; init; }
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public void AClassCanBeDefinedAndRegistered() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Registers an Objective-C class.");

        var cls = ProbeClass();

        Assert.NotEqual(0, cls);

        // The runtime's own answer to "is this class in this process", which is a different question
        // from "did the local we just built come back non-nil".
        Assert.Equal(cls, ObjCRuntime.Lookup(ClassName));

        // ⚠ And the second attempt is nil rather than the existing class — the property the remarks
        // on `Define` warn about, asserted rather than described.
        Assert.Equal(0, ObjCRuntime.Define(ClassName, "NSObject"));
    }

    /// <summary>
    ///     <c>-[NSArray description]</c> asks every element for its own, so a description that reads
    ///     back is a description Foundation went and fetched.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void FoundationCallsAManagedImplementation() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Registers an Objective-C class.");

        var instance = NewProbe(new() { Description = "a managed description", Answer = 7, Frame = default });

        try {
            var array = ObjC.Send(ObjC.GetClass("NSMutableArray"), ObjC.Selector("array"));
            ObjC.Send(array, ObjC.Selector("addObject:"), instance);

            var text = ObjC.ToString(ObjC.Send(array, ObjC.Selector("description")));

            Assert.NotNull(text);
            Assert.Contains("a managed description", text);

            // The failure this excludes: the override not taking, in which case NSObject's own
            // description answers and the string is the class name and an address.
            Assert.DoesNotContain("VixenObjCProbe: 0x", text);
        } finally {
            ReleaseProbe(instance);
        }
    }

    /// <summary>
    ///     <c>NSInvocation</c> dispatching a managed method — a <c>BOOL</c> return and an object
    ///     argument, laid out by Foundation from the type encoding rather than by a caller's prototype.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The strongest of the three, and it is the one the encoding paragraph is about.</b>
    ///         <c>objc_msgSend</c> never reads an encoding; <c>NSInvocation</c> reads nothing else. It
    ///         sizes the argument frame, decides where the <c>@</c> at index 2 goes and how many bytes
    ///         to copy out of the return, entirely from the string that was handed to
    ///         <c>class_addMethod</c> — so a wrong encoding here is a wrong answer rather than an
    ///         invisible one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is here because the obvious route was measured and does not work.</b>
    ///         <c>-[NSArray indexOfObject:]</c> was written first, on the documented reading that
    ///         "each element is sent an <c>isEqual:</c> message". It answers <c>NSNotFound</c> against
    ///         an element that returns <c>YES</c> from an overridden <c>isEqual:</c>, because the real
    ///         implementation sends the message to the <i>argument</i> and not to the elements. That is
    ///         a fact about Foundation worth keeping written down: an equality override on an object
    ///         put into a collection is not what decides a lookup in it.
    ///     </para>
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void FoundationDispatchesAManagedBoolThroughTheTypeEncoding() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Registers an Objective-C class.");

        var instance = NewProbe(new() { Description = "equality probe", Answer = 0, Frame = default });

        try {
            Assert.True(Matches(instance, EqualKey));
            Assert.False(Matches(instance, "something else"));
        } finally {
            ReleaseProbe(instance);
        }
    }

    /// <summary>Sends <c>-vixenMatches:</c> through an <c>NSInvocation</c> and reads the BOOL back.</summary>
    [SupportedOSPlatform("macos")]
    static bool Matches(nint instance, string argument) {
        var selector = ObjC.Selector("vixenMatches:");
        var signature = ObjC.Send(instance, ObjC.Selector("methodSignatureForSelector:"), selector);

        Assert.NotEqual(0, signature);

        var invocation = ObjC.Send(
            ObjC.GetClass("NSInvocation"),
            ObjC.Selector("invocationWithMethodSignature:"),
            signature
        );

        Assert.NotEqual(0, invocation);

        ObjC.Send(invocation, ObjC.Selector("setTarget:"), instance);
        ObjC.Send(invocation, ObjC.Selector("setSelector:"), selector);

        // ⚠ Index 2, because 0 and 1 are the receiver and the selector — the two arguments
        // `ObjCTypes.Signature` supplies and the two a hand-written encoding forgets.
        var value = ObjC.String(argument);
        ObjC.Send(invocation, ObjC.Selector("setArgument:atIndex:"), (nint)(&value), 2);
        ObjC.Send(invocation, ObjC.Selector("invoke"));

        byte answer = 0xFF;
        ObjC.Send(invocation, ObjC.Selector("getReturnValue:"), (nint)(&answer));

        // Not `answer != 0`: 0xFF is what is there if Foundation copied nothing, and treating a
        // sentinel as truth is how this assertion would stop being able to fail.
        Assert.InRange(answer, 0, 1);

        return answer == 1;
    }

    /// <summary>
    ///     Foundation parsing the type encoding, which is the only thing in the process that reads
    ///     one — dispatch does not, so a wrong encoding is invisible until something introspects.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void FoundationParsesTheTypeEncodingWeRegistered() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Registers an Objective-C class.");

        var cls = ProbeClass();

        Assert.Equal("q@:", ObjCRuntime.EncodingOf(cls, "vixenAnswer"));
        Assert.Equal("B@:@", ObjCRuntime.EncodingOf(cls, "vixenMatches:"));
        Assert.Equal("{CGRect={CGPoint=dd}{CGSize=dd}}@:", ObjCRuntime.EncodingOf(cls, "vixenFrame"));
        Assert.Null(ObjCRuntime.EncodingOf(cls, "aSelectorNobodyImplemented"));

        var instance = NewProbe(new() { Description = "signature probe", Answer = 0, Frame = default });

        try {
            // NSMethodSignature is built by Foundation *from* the encoding string, so its arithmetic
            // is the encoding read back through a parser rather than through our own memory.
            var signature = ObjC.Send(
                instance,
                ObjC.Selector("methodSignatureForSelector:"),
                ObjC.Selector("vixenAnswer")
            );

            Assert.NotEqual(0, signature);
            Assert.Equal(2, ObjC.Send(signature, ObjC.Selector("numberOfArguments")));
            Assert.Equal(
                "q",
                Marshal.PtrToStringUTF8(ObjC.Send(signature, ObjC.Selector("methodReturnType")))
            );
        } finally {
            ReleaseProbe(instance);
        }
    }

    /// <summary>The side table, which is how an IMP with two pointers finds the object it stands for.</summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void TheSideTableIsHowAnImpFindsItsManagedObject() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Registers an Objective-C class.");

        var first = NewProbe(new() { Description = "first", Answer = 11, Frame = default });
        var second = NewProbe(new() { Description = "second", Answer = 22, Frame = default });

        try {
            Assert.NotEqual(first, second);

            // Not "the table holds two things" — that the *right* answer comes back for each, which
            // is the failure a table keyed on anything but the instance pointer would produce.
            Assert.Equal(11L, ObjC.Send(first, ObjC.Selector("vixenAnswer")));
            Assert.Equal(22L, ObjC.Send(second, ObjC.Selector("vixenAnswer")));

            Assert.Null(ObjCBridge.Target(0));
            Assert.False(ObjCBridge.Detach(0));
        } finally {
            ReleaseProbe(first);
            ReleaseProbe(second);
        }

        Assert.Null(ObjCBridge.Target(first));
        Assert.Null(ObjCBridge.Target(second));
    }

    /// <summary>
    ///     A <c>CGRect</c> out of a managed IMP, which is <c>accessibilityFrame</c>'s shape and the
    ///     one where a wrong assumption about the ABI is four garbage doubles rather than an error.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void ARectSurvivesTheReturn() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Registers an Objective-C class.");

        var frame = new CGRect(12.5, -3.25, 640, 480.75);
        var instance = NewProbe(new() { Description = "rect probe", Answer = 0, Frame = frame });

        try {
            Assert.Equal(frame, ObjCRuntime.SendRect(instance, ObjC.Selector("vixenFrame")));
        } finally {
            ReleaseProbe(instance);
        }
    }

    /// <summary>The method table holds the pointer we handed it — one step below the Foundation tests.</summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void TheMethodTableHoldsTheManagedImplementation() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Registers an Objective-C class.");

        var cls = ProbeClass();
        var implementation = ObjCRuntime.ImplementationOf(cls, "vixenAnswer");

        Assert.NotEqual(0, implementation);
        Assert.Equal((nint)(delegate* unmanaged[Cdecl]<nint, nint, long>)&Answer, implementation);
        Assert.Equal(0, ObjCRuntime.ImplementationOf(cls, "aSelectorNobodyImplemented"));
    }

    [Fact]
    public void TheEncodingBuilderSuppliesTheHiddenArguments() {
        Assert.Equal("@@:", ObjCTypes.Signature(ObjCTypes.Object));
        Assert.Equal("B@:@", ObjCTypes.Signature(ObjCTypes.Bool, ObjCTypes.Object));
        Assert.Equal("v@:qd", ObjCTypes.Signature(ObjCTypes.Void, ObjCTypes.Long, ObjCTypes.Double));
    }

    [SupportedOSPlatform("macos")]
    static nint NewProbe(Probe target) {
        var instance = ObjCRuntime.New(ProbeClass());

        Assert.NotEqual(0, instance);
        ObjCBridge.Attach(instance, target);

        return instance;
    }

    [SupportedOSPlatform("macos")]
    static void ReleaseProbe(nint instance) {
        ObjCBridge.Detach(instance);
        ObjCRuntime.Release(instance);
    }

    /// <summary>
    ///     The probe class, registered once per process because the name is taken for the life of one.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static nint ProbeClass() {
        lock (Gate) {
            if (probeClass != 0) {
                return probeClass;
            }

            var existing = ObjCRuntime.Lookup(ClassName);

            if (existing != 0) {
                return probeClass = existing;
            }

            var cls = ObjCRuntime.Define(ClassName, "NSObject");

            Assert.NotEqual(0, cls);

            Assert.True(
                ObjCRuntime.AddMethod(
                    cls,
                    "description",
                    ObjCTypes.Signature(ObjCTypes.Object),
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&Description
                )
            );

            Assert.True(
                ObjCRuntime.AddMethod(
                    cls,
                    "vixenMatches:",
                    ObjCTypes.Signature(ObjCTypes.Bool, ObjCTypes.Object),
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&Matches
                )
            );

            Assert.True(
                ObjCRuntime.AddMethod(
                    cls,
                    "vixenAnswer",
                    ObjCTypes.Signature(ObjCTypes.Long),
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, long>)&Answer
                )
            );

            Assert.True(
                ObjCRuntime.AddMethod(
                    cls,
                    "vixenFrame",
                    ObjCTypes.Signature(ObjCTypes.Rect),
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, CGRect>)&Frame
                )
            );

            return probeClass = ObjCRuntime.Register(cls);
        }
    }

    /// <summary>
    ///     ⚠ Retained rather than autoreleased. An IMP runs on whatever thread AppKit or Foundation
    ///     called it from, and there is no promise that thread has a pool — an autoreleased return
    ///     would be a leak on a good day and a use-after-free on a bad one.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static nint Retained(string value) => ObjC.Send(ObjC.String(value), ObjC.Selector("retain"));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    static nint Description(nint self, nint selector) {
        try {
            return ObjCBridge.Target(self) is Probe probe ? Retained(probe.Description) : 0;
        } catch {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    static byte Matches(nint self, nint selector, nint other) {
        try {
            if (other == 0 || !ObjC.SendBool(other, ObjC.Selector("isKindOfClass:"), ObjC.GetClass("NSString"))) {
                return 0;
            }

            return ObjC.ToString(other) == EqualKey ? (byte)1 : (byte)0;
        } catch {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    static long Answer(nint self, nint selector) {
        try {
            return ObjCBridge.Target(self) is Probe probe ? probe.Answer : -1;
        } catch {
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    static CGRect Frame(nint self, nint selector) {
        try {
            return ObjCBridge.Target(self) is Probe probe ? probe.Frame : default;
        } catch {
            return default;
        }
    }
}
