// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>One recorded EGL call.</summary>
/// <param name="Name">The entry point.</param>
/// <param name="Arguments">Its arguments, in order.</param>
public readonly record struct EglCall(string Name, object?[] Arguments) {
    /// <inheritdoc />
    public override string ToString() =>
        $"{Name}({string.Join(", ", Arguments.Select(argument => Convert.ToString(argument, CultureInfo.InvariantCulture)))})";
}

/// <summary>An EGL that records what it was asked to do, and answers however a test wants.</summary>
/// <remarks>
///     <para>
///         <b>What makes context bring-up checkable on a machine with no EGL</b> — which is every
///         machine this repository is developed on except an Android device, and every CI runner
///         without ANGLE. The calls are transcription and a driver is the only thing that can check
///         them; the <em>sequence</em> is a decision per step, and all of it is decidable from the
///         call stream.
///     </para>
///     <para>
///         The interesting knobs are the refusals. A driver that has GLES 3.0 and not 3.2 is the
///         common Android device and the one the version ladder exists for, and
///         <see cref="RefusedMinorVersions" /> is how a test is one.
///     </para>
/// </remarks>
public sealed class RecordingEglApi : IEglApi {
    readonly List<EglCall> calls = [];
    readonly Queue<uint> errors = new();

    nint current;

    /// <summary>Everything that has been asked for, in order.</summary>
    public IReadOnlyList<EglCall> Calls => calls;

    /// <summary>The names of every call, for an ordering assertion.</summary>
    public IReadOnlyList<string> Names => calls.Select(call => call.Name).ToList();

    /// <summary>What <c>eglGetDisplay</c> should return.</summary>
    public nint Display { get; set; } = 0x100;

    /// <summary>Whether <c>eglInitialize</c> should succeed.</summary>
    public bool Initialises { get; set; } = true;

    /// <summary>What EGL version the fake driver implements.</summary>
    public (int Major, int Minor) Version { get; set; } = (1, 5);

    /// <summary>Whether <c>eglBindAPI</c> should succeed.</summary>
    public bool BindsApi { get; set; } = true;

    /// <summary>Whether <c>eglChooseConfig</c> should succeed.</summary>
    public bool ChoosesConfig { get; set; } = true;

    /// <summary>How many configs it should report matching.</summary>
    public int ConfigCount { get; set; } = 1;

    /// <summary>What config handle it should hand back.</summary>
    public nint Config { get; set; } = 0x200;

    /// <summary>The GLES minor versions this driver refuses — <c>2</c> makes it a GLES 3.0 device.</summary>
    public HashSet<int> RefusedMinorVersions { get; } = [];

    /// <summary>What <c>eglCreateContext</c> should return when it does not refuse.</summary>
    public nint Context { get; set; } = 0x300;

    /// <summary>What the surface calls should return; zero makes them fail.</summary>
    public nint Surface { get; set; } = 0x400;

    /// <summary>Whether <c>eglMakeCurrent</c> should succeed.</summary>
    public bool MakesCurrent { get; set; } = true;

    /// <summary>Whether <c>eglSwapBuffers</c> should succeed.</summary>
    public bool Swaps { get; set; } = true;

    /// <summary>What <c>eglQuerySurface</c> should report for width and height.</summary>
    public (int Width, int Height) SurfaceSize { get; set; } = (1280, 720);

    /// <summary>Symbols the client library exports.</summary>
    public Dictionary<string, nint> ClientSymbols { get; } = [];

    /// <summary>Symbols only <c>eglGetProcAddress</c> knows.</summary>
    public Dictionary<string, nint> EglSymbols { get; } = [];

    /// <summary>Every call with a given name.</summary>
    public IReadOnlyList<EglCall> Named(string name) => calls.Where(call => call.Name == name).ToList();

    /// <summary>How many calls with a given name were made.</summary>
    public int Count(string name) => calls.Count(call => call.Name == name);

    /// <summary>The only call with a given name, failing if there is not exactly one.</summary>
    public EglCall Single(string name) => calls.Single(call => call.Name == name);

    /// <summary>Whether one call happened before another.</summary>
    /// <param name="first">The name that should come first.</param>
    /// <param name="second">The name that should come second.</param>
    public bool Precedes(string first, string second) {
        var a = calls.FindIndex(call => call.Name == first);
        var b = calls.FindIndex(call => call.Name == second);
        return a >= 0 && b >= 0 && a < b;
    }

    /// <summary>Queues an error for the next <c>eglGetError</c>.</summary>
    /// <param name="code">The code.</param>
    public void Fail(uint code) => errors.Enqueue(code);

    /// <inheritdoc />
    public override string ToString() => string.Join(Environment.NewLine, calls);

    /// <inheritdoc />
    public nint GetDisplay(nint nativeDisplay) {
        Record("GetDisplay", nativeDisplay);
        return Display;
    }

    /// <inheritdoc />
    public bool Initialise(nint display, out int major, out int minor) {
        Record("Initialise", display);
        (major, minor) = Initialises ? Version : (0, 0);
        return Initialises;
    }

    /// <inheritdoc />
    public bool Terminate(nint display) {
        Record("Terminate", display);
        return true;
    }

    /// <inheritdoc />
    public bool BindApi(uint api) {
        Record("BindApi", api);
        return BindsApi;
    }

    /// <inheritdoc />
    public string? QueryString(nint display, int name) {
        Record("QueryString", display, name);
        return null;
    }

    /// <inheritdoc />
    public bool ChooseConfig(nint display, ReadOnlySpan<int> attributes, Span<nint> configs, out int count) {
        Record("ChooseConfig", display, attributes.ToArray());
        count = 0;

        if (!ChoosesConfig) {
            return false;
        }

        count = Math.Min(ConfigCount, configs.Length);

        for (var index = 0; index < count; index++) {
            configs[index] = Config + index;
        }

        return true;
    }

    /// <inheritdoc />
    public nint CreateContext(nint display, nint config, nint share, ReadOnlySpan<int> attributes) {
        var list = attributes.ToArray();
        Record("CreateContext", display, config, share, list);

        return RefusedMinorVersions.Contains(MinorOf(list)) ? EglConstants.NoContext : Context;
    }

    /// <inheritdoc />
    public nint CreateWindowSurface(nint display, nint config, nint window, ReadOnlySpan<int> attributes) {
        Record("CreateWindowSurface", display, config, window, attributes.ToArray());
        return Surface;
    }

    /// <inheritdoc />
    public nint CreatePbufferSurface(nint display, nint config, ReadOnlySpan<int> attributes) {
        Record("CreatePbufferSurface", display, config, attributes.ToArray());
        return Surface;
    }

    /// <inheritdoc />
    public bool DestroySurface(nint display, nint surface) {
        Record("DestroySurface", display, surface);
        return true;
    }

    /// <inheritdoc />
    public bool DestroyContext(nint display, nint context) {
        Record("DestroyContext", display, context);
        return true;
    }

    /// <inheritdoc />
    public bool QuerySurface(nint display, nint surface, int attribute, out int value) {
        Record("QuerySurface", display, surface, attribute);

        value = attribute == EglConstants.Width ? SurfaceSize.Width : SurfaceSize.Height;
        return true;
    }

    /// <inheritdoc />
    public bool MakeCurrent(nint display, nint draw, nint read, nint context) {
        Record("MakeCurrent", display, draw, read, context);

        if (!MakesCurrent) {
            return false;
        }

        current = context;
        return true;
    }

    /// <inheritdoc />
    public nint GetCurrentContext() {
        Record("GetCurrentContext");
        return current;
    }

    /// <inheritdoc />
    public bool SwapBuffers(nint display, nint surface) {
        Record("SwapBuffers", display, surface);
        return Swaps;
    }

    /// <inheritdoc />
    public bool SwapInterval(nint display, int interval) {
        Record("SwapInterval", display, interval);
        return true;
    }

    /// <inheritdoc />
    public bool ReleaseThread() {
        Record("ReleaseThread");
        return true;
    }

    /// <inheritdoc />
    public nint GetProcAddress(string name) {
        Record("GetProcAddress", name);
        return EglSymbols.GetValueOrDefault(name);
    }

    /// <inheritdoc />
    public nint GetClientProcAddress(string name) {
        Record("GetClientProcAddress", name);
        return ClientSymbols.GetValueOrDefault(name);
    }

    /// <inheritdoc />
    public uint GetError() {
        Record("GetError");
        return errors.Count > 0 ? errors.Dequeue() : EglConstants.Success;
    }

    /// <summary>The GLES minor version an attribute list asks for; absent means zero.</summary>
    static int MinorOf(int[] attributes) {
        for (var index = 0; index + 1 < attributes.Length; index += 2) {
            if (attributes[index] == EglConstants.ContextMinorVersion) {
                return attributes[index + 1];
            }

            if (attributes[index] == EglConstants.None) {
                break;
            }
        }

        return 0;
    }

    void Record(string name, params object?[] arguments) => calls.Add(new(name, arguments));
}
