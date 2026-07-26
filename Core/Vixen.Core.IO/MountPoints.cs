// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.IO;

/// <summary>The mounts every Vixen application has, and what each one promises.</summary>
/// <remarks>
///     The names are the contract. Engine code says <c>/app/textures/x.ktx2</c> and never learns that
///     on Android that is inside an APK, on iOS it is inside a signed bundle, in the browser it is an
///     HTTP fetch with an IndexedDB cache, and in the editor it is a directory. That translation is
///     the whole reason the layer exists, and it only works if the vocabulary is fixed.
/// </remarks>
public static class MountPoints {
    /// <summary>Read-only application content, shipped with the build.</summary>
    /// <remarks>
    ///     The APK's assets, the iOS bundle, <c>wwwroot</c>, the game's data directory. Writable
    ///     nowhere, on any platform — a build that writes here works on a developer's desktop and
    ///     fails on every device.
    /// </remarks>
    public static VirtualPath App { get; } = new("/app");

    /// <summary>Read-write application data that must survive a restart.</summary>
    /// <remarks>
    ///     Saves, settings, player profiles. Maps to the per-platform correct location — the one the
    ///     OS backs up and does not delete under storage pressure.
    /// </remarks>
    public static VirtualPath Data { get; } = new("/data");

    /// <summary>Read-write data the platform may delete at any time.</summary>
    /// <remarks>
    ///     Downloaded bundles, decoded textures, shader caches. Everything here must be
    ///     reconstructible, because on iOS and Android it will eventually be reconstructed whether
    ///     the application likes it or not.
    /// </remarks>
    public static VirtualPath Cache { get; } = new("/cache");

    /// <summary>Scratch space for the current session.</summary>
    public static VirtualPath Temp { get; } = new("/temp");

    /// <summary>The open project's folder. Editor only; unmounted in a shipped game.</summary>
    public static VirtualPath Project { get; } = new("/project");

    /// <summary>The content-addressed object database.</summary>
    public static VirtualPath Database { get; } = new("/db");

    /// <summary>Every mount the engine defines, in a fixed order.</summary>
    public static ReadOnlySpan<VirtualPath> All => AllMounts;

    static readonly VirtualPath[] AllMounts = [App, Data, Cache, Temp, Project, Database];
}
