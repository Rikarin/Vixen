// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Engine.Scenes;
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

    /// <summary>What the scenes-in-build manifest is called, beside the catalog.</summary>
    /// <remarks>
    ///     Matches <c>ContentPipeline.SceneManifestFileName</c>, which is where a content build writes
    ///     it — the fourth name in this file spelled twice across a build and a run, and for the
    ///     reason the other three give. A sibling of the catalog rather than an addressed chunk
    ///     because it is what says <em>which</em> address to ask for: a build that answered that
    ///     question with an address would need one to find it.
    /// </remarks>
    public const string SceneManifestFileName = "scenes.bin";

    readonly IDisposable? bundles;

    ContentMount(
        AssetManager? assets,
        VirtualPath root,
        bool isLoose,
        IDisposable? bundles,
        string? reason,
        EffectStore? shaders = null,
        string? shaderReason = null,
        IReadOnlyList<string>? scenes = null,
        string? sceneReason = null
    ) {
        Assets = assets;
        Root = root;
        IsLoose = isLoose;
        Reason = reason;
        Shaders = shaders;
        ShaderReason = shaderReason;
        Scenes = scenes ?? [];
        SceneReason = sceneReason;
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

    /// <summary>
    ///     The addresses of the scenes this build shipped, first one first, or empty if it shipped
    ///     none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The runtime end of the editor's Build Settings scene list. What is committed under
    ///         <c>ProjectSettings/</c> is project-relative paths, because a person merges them; what
    ///         reaches a player is this, because a player has no asset database to resolve a path
    ///         with. <c>ContentPipeline</c> is where the two meet.
    ///     </para>
    ///     <para>
    ///         Read by the host to fill in <see cref="AppConfig.StartupScene" /> when a game did not
    ///         name one, and public for the same reason the catalog is: a game that builds its own
    ///         level select wants the list rather than only the first of it.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<string> Scenes { get; }

    /// <summary>Why there is no scene manifest, when there is none.</summary>
    public string? SceneReason { get; }

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

        // And before the catalog for the same reason: which scenes a build ships is a fact about the
        // build, and a mount whose catalog failed to read still wants to be able to say what was
        // listed beside it.
        var scenes = OpenScenes(files, root, out var sceneReason);

        var catalogPath = root / CatalogFileName;

        if (!files.Exists(catalogPath)) {
            return new(
                null,
                root,
                loose,
                null,
                $"There is no {CatalogFileName} at {root}.",
                shaders,
                shaderReason,
                scenes,
                sceneReason
            );
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
                shaderReason,
                scenes,
                sceneReason
            );
        }

        var source = new LocalBundleSource(files, root);

        return new(new(catalog, source), root, loose, source, null, shaders, shaderReason, scenes, sceneReason);
    }

    /// <summary>Reads the scenes-in-build manifest sitting beside the catalog, if the build wrote one.</summary>
    /// <remarks>
    ///     <para>
    ///         Recorded rather than thrown, for the reason the catalog's and the shader bundle's own
    ///         failures are: a file written by a newer build or truncated by a failed download is a
    ///         thing that happens in the field, and an application that refused to start over it could
    ///         not draw the message saying why. What it costs instead is a game that opens no scene,
    ///         which the host says once at startup.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A manifest from a newer build is refused rather than half-read.</b> Its version is
    ///         the one thing here that cannot be recovered from by ignoring: a later format may mean
    ///         something different by the order of the list, and opening the wrong level is worse than
    ///         opening none and saying so.
    ///     </para>
    /// </remarks>
    static IReadOnlyList<string>? OpenScenes(VirtualFileSystem files, VirtualPath root, out string? reason) {
        var path = root / SceneManifestFileName;

        if (!files.Exists(path)) {
            reason = $"There is no {SceneManifestFileName} at {root}.";
            return null;
        }

        SceneManifest manifest;

        try {
            using var stream = files.OpenRead(path);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            manifest = Serializer.Read<SceneManifest>(buffer.ToArray());
        }

        // Broad, for OpenShaders' reason: a generated reader reports a version it cannot migrate, a
        // length it cannot honour and a member count it did not expect through three exception types,
        // and a file cut mid-download produces whichever of them the cut landed in.
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) {
            reason = $"{path} could not be read: {failure.Message}";
            return null;
        }

        if (manifest.Version > SceneManifest.Current) {
            reason = $"{path} was written as version {manifest.Version} and this build reads "
                + $"{SceneManifest.Current}.";

            return null;
        }

        reason = null;
        return manifest.Scenes;
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
