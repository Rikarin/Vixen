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

    /// <summary>Where downloaded bundles are kept, under the platform's own cache directory.</summary>
    /// <remarks>
    ///     ⚠ <b><c>/cache</c> and not <c>/data</c>, which is the difference between an install that
    ///     can be reclaimed and one that cannot.</b> Every byte here is re-fetchable from the URL the
    ///     catalog names, so an operating system that clears the cache under storage pressure has
    ///     taken nothing but time — whereas the same policy applied to <c>/data</c> would delete a
    ///     save game. <c>BundleCache</c> names each file by content hash, so a build that replaced a
    ///     bundle leaves the old one to be evicted rather than serving it.
    /// </remarks>
    public static VirtualPath CacheRoot { get; } = MountPoints.Cache / FolderName;

    /// <summary>What is disposed with the mount: the bundle sources, and the transport if it is ours.</summary>
    IDisposable? Sources { get; init; }

    /// <inheritdoc cref="Sources" />
    IDisposable? OwnedTransport { get; init; }

    ContentMount(VirtualPath root, bool isLoose) {
        Root = root;
        IsLoose = isLoose;
    }

    /// <summary>The manager over the content that was found, or <see langword="null" /> if there was none.</summary>
    public AssetManager? Assets { get; private init; }

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
    public string? Reason { get; private init; }

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
    public EffectStore? Shaders { get; private init; }

    /// <summary>Why there are no baked variants, when there are none.</summary>
    public string? ShaderReason { get; private init; }

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
    public IReadOnlyList<string> Scenes { get; private init; } = [];

    /// <summary>Why there is no scene manifest, when there is none.</summary>
    public string? SceneReason { get; private init; }

    /// <summary>
    ///     Where downloaded bundles are kept, or <see langword="null" /> if this build downloads none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Public because managing a download is a thing a game has UI for. <c>TotalSize</c> is
    ///         what a "storage used" row reports and <c>Clear</c> is what the button beside it does;
    ///         the per-address forms — <see cref="AssetManager.DownloadSize" />,
    ///         <see cref="AssetManager.DownloadAsync" />, <see cref="AssetManager.ClearCache" /> —
    ///         are on the manager, because they take addresses and this takes bundles.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null is the ordinary case and costs nothing.</b> A project whose groups are all
    ///         local has no remote bundle in its catalog, so the host builds no cache, no
    ///         <see cref="RemoteBundleSource" /> and — the part that matters on a phone — no
    ///         <c>HttpClient</c>. <see cref="RemoteReason" /> says which it was.
    ///     </para>
    /// </remarks>
    public BundleCache? Cache { get; private init; }

    /// <summary>Why nothing can be downloaded, when nothing can.</summary>
    public string? RemoteReason { get; private init; }

    /// <summary>Finds the application's content and opens it.</summary>
    /// <param name="files">The virtual file system, with the standard locations already mounted.</param>
    /// <param name="loosePath">The directory from <c>--vixen-loose-content</c>, or <see langword="null" />.</param>
    /// <param name="transport">
    ///     How remote bundles are fetched, or <see langword="null" /> for plain HTTP. A transport
    ///     handed in is the caller's to dispose; one made here is not.
    /// </param>
    /// <returns>The mount. Never null; its <see cref="Assets" /> may be.</returns>
    /// <remarks>
    ///     <para>
    ///         Failures are recorded rather than thrown. A catalog written by a newer build, truncated
    ///         by a failed download or corrupted on a phone's flash is a thing that happens in the
    ///         field, and an application that refuses to start over it cannot even show the message
    ///         saying why.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Whether this build can download anything is read out of the catalog.</b> A bundle
    ///         with a URL is one a group declared <c>loadPath: Remote</c>, and it is the only thing
    ///         that makes the host build a <see cref="BundleCache" />, a
    ///         <see cref="RemoteBundleSource" /> and the <see cref="RoutedBundleSource" /> that picks
    ///         between them. Nothing has to be configured to turn downloading on, and a game that
    ///         ships everything in its package pays nothing for the option.
    ///     </para>
    /// </remarks>
    public static ContentMount Open(
        VirtualFileSystem files,
        string? loosePath = null,
        IContentTransport? transport = null
    ) {
        ArgumentNullException.ThrowIfNull(files);

        var loose = loosePath is { Length: > 0 };
        var root = MountPoints.App / FolderName;

        if (loose) {
            var directory = Path.GetFullPath(loosePath!);

            if (!Directory.Exists(directory)) {
                return new(LooseMountPoint, true) { Reason = $"'{directory}' is not a directory." };
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
            return Mount(reason: $"There is no {CatalogFileName} at {root}.");
        }

        ContentCatalog catalog;

        try {
            using var stream = files.OpenRead(catalogPath);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            catalog = CatalogFormat.Read(buffer.ToArray());
        } catch (Exception failure) when (failure is IOException or InvalidDataException or CatalogFormatException) {
            return Mount(reason: $"{catalogPath} could not be read: {failure.Message}");
        }

        var local = new LocalBundleSource(files, root);

        // ⚠ Decided from the catalog rather than from configuration, and only a catalog that names a
        // remote bundle gets any of it. `RoutedBundleSource` reads an empty URL as "this one shipped
        // with the application", so a project whose groups are all local has nothing to route — and
        // building the machinery anyway would mean every game that never downloads anything paying
        // for an HttpClient, a cache directory and a socket handle at boot.
        if (!catalog.Bundles.Any(bundle => bundle.Url.Length > 0)) {
            return Mount(
                assets: new(catalog, local),
                sources: local,
                remoteReason: "no group in this build's catalog is served from a URL."
            );
        }

        // Ours only if we made it. One handed in belongs to whoever handed it over — a game that
        // authenticates its CDN, a test — and outlives the mount, which is `WithGraphics`' rule for
        // the same reason.
        var ours = transport is null ? new HttpContentTransport() : null;
        var cache = new BundleCache(files, CacheRoot, transport ?? ours!);
        var routed = new RoutedBundleSource(local, new RemoteBundleSource(files, cache));

        return Mount(assets: new(catalog, routed), sources: routed, owned: ours, cache: cache);

        // Everything found beside the catalog belongs on the mount whether or not the catalog itself
        // read, so the four of them are filled in here rather than at each of the five returns.
        ContentMount Mount(
            string? reason = null,
            AssetManager? assets = null,
            IDisposable? sources = null,
            IDisposable? owned = null,
            BundleCache? cache = null,
            string? remoteReason = null
        ) =>
            new(root, loose) {
                Reason = reason,
                Shaders = shaders,
                ShaderReason = shaderReason,
                Scenes = scenes ?? [],
                SceneReason = sceneReason,
                Assets = assets,
                Sources = sources,
                OwnedTransport = owned,
                Cache = cache,
                RemoteReason = remoteReason
            };
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
    /// <inheritdoc />
    /// <remarks>
    ///     The sources first and the transport after, because a <see cref="RemoteBundleSource" />
    ///     closing its mapped files is the last thing that could still be reading what the transport
    ///     fetched. The transport is only disposed when this made it — see <see cref="Open" />.
    /// </remarks>
    public void Dispose() {
        Sources?.Dispose();
        OwnedTransport?.Dispose();
    }
}
