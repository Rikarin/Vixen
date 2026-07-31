// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Shaders;

namespace Vixen.App;

/// <summary>The content build a running application reads from, and where it came from.</summary>
/// <remarks>
///     <para>
///         The seam that was missing. Every piece of the content pipeline worked — the catalog, the
///         bundle reader, the asset manager, the build that writes them — and nothing in the boot
///         path opened any of it, so a game could not ask for an address without standing up all
///         three itself. `Vixen.Sdk` has been copying a content build beside the binary since Phase 3
///         and nothing has ever read it.
///     </para>
///     <para>
///         <b>Read through the virtual file system, not through a path.</b> The obvious version takes
///         <c>IFileSystemHost.ApplicationDirectory</c> and appends <c>Content</c>, and it is wrong on
///         the two platforms this phase exists for: that property is documented as empty where
///         content is not a directory at all, which is an APK's assets and an iOS bundle. Going
///         through <c>/app</c> means the Android provider's <c>AAssetManager</c> answers the same
///         call the desktop's directory does.
///     </para>
///     <para>
///         <b>No content is not an error.</b> A sample that draws a triangle, a batch tool, a test —
///         each is an ordinary application with nothing to load, and a host that refused to start
///         without a catalog would make the smallest possible program the hardest one to write.
///         <see cref="Assets" /> is null and the host says so once at startup.
///     </para>
/// </remarks>
public sealed class ContentMount : IDisposable {
    /// <summary>Where loose content is mounted, when there is any.</summary>
    /// <remarks>
    ///     Its own mount rather than a path under <c>/app</c>, because loose content is by definition
    ///     a directory somewhere else — the whole point is to point a shipped build at content it did
    ///     not ship with.
    /// </remarks>
    public static VirtualPath LooseMountPoint { get; } = new("/content");

    /// <summary>The folder inside the application's output that a content build is copied into.</summary>
    /// <remarks>
    ///     Matches <c>VixenContentFolderName</c> in <c>Vixen.Sdk</c>, which is where the copy that
    ///     puts it there is written. Two spellings of one name is how a build that produced content
    ///     and an application that found none end up in the same release.
    /// </remarks>
    public const string FolderName = "Content";

    /// <summary>What a catalog file is called.</summary>
    public const string CatalogFileName = "catalog.bin";

    /// <summary>
    ///     What the shader bundle is called, beside the catalog.
    /// </summary>
    /// <remarks>
    ///     Matches <c>ShaderBuildRunner.BundleFileName</c>, which is where <c>vixen build</c> writes
    ///     it — the third name in this file that is spelled twice across a build and a run, and for
    ///     the reason the other two give. A sibling of the catalog rather than an addressed chunk
    ///     because it has to be loadable <em>before</em> anything addressable is: an address is a
    ///     thing the catalog provides, and resolving one needs a shader.
    /// </remarks>
    public const string ShaderBundleFileName = "shaders.effects";

    readonly IDisposable? bundles;

    ContentMount(
        AssetManager? assets,
        VirtualPath root,
        bool isLoose,
        IDisposable? bundles,
        string? reason,
        EffectStore? shaders = null,
        string? shaderReason = null
    ) {
        Assets = assets;
        Root = root;
        IsLoose = isLoose;
        Reason = reason;
        Shaders = shaders;
        ShaderReason = shaderReason;
        this.bundles = bundles;
    }

    /// <summary>The manager over the content that was found, or <see langword="null" /> if there was none.</summary>
    public AssetManager? Assets { get; }

    /// <summary>Where it was read from.</summary>
    public VirtualPath Root { get; }

    /// <summary>
    ///     Whether this is loose content rather than what the application shipped with.
    /// </summary>
    /// <remarks>
    ///     [Doc 17](../../docs/plan/17-app-heads-and-shipping.md) Q5b: a release build may be pointed
    ///     at loose files so that a bug reproducible only in a shipping configuration can be poked at,
    ///     and the trade is that "release reads only bundles" stops being an invariant. That is why
    ///     this is a property rather than a detail — the host warns on a timer while it is true, and
    ///     a diagnostic overlay and a crash report will both want to stamp it.
    /// </remarks>
    public bool IsLoose { get; }

    /// <summary>Why there is no content, when there is none.</summary>
    public string? Reason { get; }

    /// <summary>
    ///     The variants this build baked, or <see langword="null" /> if it baked none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A shipping build's only effect source: the code that could compile one is in
    ///         <c>Tools/Vixen.ShaderCompiler</c> and is never linked into a game. Until this was read
    ///         here, <c>vixen build</c> had been writing the file and nothing had ever opened it —
    ///         the same gap the catalog was in, one layer along.
    ///     </para>
    ///     <para>
    ///         Null is ordinary rather than broken. A project that has not written a
    ///         <c>Shaders.effects.json</c> yet ships no bundle and runs against whatever provider it
    ///         adds itself — a development build's compiler, a test's fake — which is exactly the
    ///         arrangement the samples use.
    ///     </para>
    /// </remarks>
    public EffectStore? Shaders { get; }

    /// <summary>Why there are no baked variants, when there are none.</summary>
    public string? ShaderReason { get; }

    /// <summary>Finds the application's content and opens it.</summary>
    /// <param name="files">The virtual file system, with the standard locations already mounted.</param>
    /// <param name="loosePath">The directory from <c>--vixen-loose-content</c>, or <see langword="null" />.</param>
    /// <returns>The mount. Never null; its <see cref="Assets" /> may be.</returns>
    /// <remarks>
    ///     Failures are recorded rather than thrown. A catalog written by a newer build, truncated by
    ///     a failed download or corrupted on a phone's flash is a thing that happens in the field, and
    ///     an application that refuses to start over it cannot even show the message saying why.
    /// </remarks>
    public static ContentMount Open(VirtualFileSystem files, string? loosePath = null) {
        ArgumentNullException.ThrowIfNull(files);

        var loose = loosePath is { Length: > 0 };
        var root = MountPoints.App / FolderName;

        if (loose) {
            var directory = Path.GetFullPath(loosePath!);

            if (!Directory.Exists(directory)) {
                return new(null, LooseMountPoint, true, null, $"'{directory}' is not a directory.");
            }

            files.Mount(LooseMountPoint, new PhysicalFileProvider(directory, isReadOnly: true));
            root = LooseMountPoint;
        }

        // Before the catalog and independently of it, because the two are separate products of one
        // build: a project may bake its variants before it has any addressable content, and a build
        // whose catalog failed to read still wants to say what it found beside it.
        var shaders = OpenShaders(files, root, out var shaderReason);

        var catalogPath = root / CatalogFileName;

        if (!files.Exists(catalogPath)) {
            return new(null, root, loose, null, $"There is no {CatalogFileName} at {root}.", shaders, shaderReason);
        }

        ContentCatalog catalog;

        try {
            using var stream = files.OpenRead(catalogPath);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            catalog = CatalogFormat.Read(buffer.ToArray());
        } catch (Exception failure) when (failure is IOException or InvalidDataException or CatalogFormatException) {
            return new(
                null,
                root,
                loose,
                null,
                $"{catalogPath} could not be read: {failure.Message}",
                shaders,
                shaderReason
            );
        }

        var source = new LocalBundleSource(files, root);

        return new(new(catalog, source), root, loose, source, null, shaders, shaderReason);
    }

    /// <summary>Reads the baked variants sitting beside the catalog, if the build wrote any.</summary>
    /// <remarks>
    ///     Recorded rather than thrown, for the reason the catalog's own failures are: a bundle
    ///     written by a newer build or truncated by a failed download is a thing that happens in the
    ///     field, and a game that refused to start over it could not draw the message saying why.
    ///     What it costs instead is every material resolving to a miss, which the effect system
    ///     already counts and names.
    /// </remarks>
    static EffectStore? OpenShaders(VirtualFileSystem files, VirtualPath root, out string? reason) {
        var path = root / ShaderBundleFileName;

        if (!files.Exists(path)) {
            reason = $"There is no {ShaderBundleFileName} at {root}.";
            return null;
        }

        try {
            using var stream = files.OpenRead(path);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            reason = null;
            return new(Serializer.Read<EffectBundle>(buffer.ToArray()));
        }

        // ⚠ Broad, unlike the catalog's named CatalogFormatException, and deliberately: a bundle is
        // read by a generated serializer that reports a version it cannot migrate, a length it cannot
        // honour and a member count it did not expect through three different exception types — and a
        // file truncated mid-download produces whichever of them the cut landed in. Naming a set here
        // would be naming the failures somebody has seen so far.
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) {
            reason = $"{path} could not be read: {failure.Message}";
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => bundles?.Dispose();
}
