// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Content;

namespace Vixen.Samples.AddressablesRemote;

/// <summary>An asset with a serializer, so there is a real object at the far end.</summary>
/// <remarks>
///     Deliberately trivial. What this sample demonstrates is the path bytes take from a build to a
///     device, and a texture would only make the interesting part harder to see.
/// </remarks>
[DataContract("Greeting")]
public sealed class Greeting {
    /// <summary>What it says.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Padding, so a bundle has a size worth reporting.</summary>
    public byte[] Payload { get; set; } = [];
}

/// <summary>Builds a content build, the way an editor would, and writes it where a CDN would.</summary>
/// <remarks>
///     <para>
///         Two groups, each packed on its own, so that changing one asset changes one bundle and the
///         demonstration has something to be about. That is the whole reason
///         <see cref="BundlePacking" /> exists: a build where everything is in one bundle makes every
///         update a full download.
///     </para>
///     <para>
///         The bundles are named by their <b>content hash</b>. Two builds that produce identical
///         bytes for a group produce the same file name, and a client that has it already is finished
///         before it starts — which is what makes the second run below cheap, and is why a CDN cannot
///         serve a stale one.
///     </para>
/// </remarks>
static class Content {
    /// <summary>Builds one version of the content and writes it to a directory.</summary>
    /// <param name="directory">Where to write. Everything in it is replaced.</param>
    /// <param name="baseUrl">Where the bundles will be served from. Recorded in the catalog.</param>
    /// <param name="propsText">What the props asset says — the thing that differs between versions.</param>
    /// <returns>The catalog that was written.</returns>
    /// <remarks>
    ///     <b>The build has to know its own CDN URL</b>, which is why this takes one. A group's
    ///     <c>RemoteUrl</c> is what turns a bundle name into something a client can fetch, and a build
    ///     without it produces a catalog whose bundles are relative paths — which fails at the first
    ///     download with "an invalid request URI was provided" and not before.
    /// </remarks>
    public static ContentCatalog Publish(string directory, string baseUrl, string propsText) {
        var files = new VirtualFileSystem();
        files.Mount(new("/odb"), new MemoryFileProvider());

        var backend = new FileOdbBackend(files, new("/odb"));
        var database = new ObjectDatabase(backend);

        // Deterministic payloads: the point is that an unchanged group produces an unchanged bundle,
        // and random bytes would make that impossible to see.
        var hero = database.Write(new Greeting { Text = "Hello from the hero", Payload = Filled(96 * 1024, 0x5EED_C0DE_1234_0001) });
        var props = database.Write(new Greeting { Text = propsText, Payload = Filled(48 * 1024, 0x5EED_C0DE_1234_0002) });

        var built = new ContentBuilder(RuntimeName()).Build(
            [
                Group("Characters", baseUrl),
                Group("Props", baseUrl)
            ],
            [
                new("characters/hero", hero, "Characters", [], []),
                new("props/torch", props, "Props", [], [])
            ],
            backend
        );

        Directory.CreateDirectory(directory);

        foreach (var stale in Directory.EnumerateFiles(directory)) {
            File.Delete(stale);
        }

        // Written under the name the builder chose, not one composed here. With FilenameHash naming
        // that is `<group>_<hash16>.bundle`, and it is the same string the catalog puts in the URL —
        // so inventing it a second time on this side is how a build serves 404s for files that are
        // sitting right there.
        foreach (var bundle in built.Bundles) {
            File.WriteAllBytes(Path.Combine(directory, bundle.FileName), bundle.Bytes.ToArray());
        }

        var catalog = CatalogFormat.Write(built.Catalog);
        File.WriteAllBytes(Path.Combine(directory, "catalog.bin"), catalog);

        // The hash file beside the catalog is step 1 of doc 08's boot sequence: a few bytes that say
        // whether the whole catalog is worth fetching. `vixen content build` writes one for the same
        // reason — Vixen.ContentServer can synthesise it, and a CDN synthesises nothing.
        //
        // It is the hash of the catalog *file*, not the catalog's own BuildHash. Those are different
        // numbers and using the wrong one is not a silent failure: the client fetches the catalog,
        // hashes what arrived, finds it disagrees with what was advertised, and reports Rejected —
        // which is exactly what a tampered or half-published CDN looks like, and is what this sample
        // did on its first run.
        File.WriteAllText(
            Path.Combine(directory, "catalog.bin.hash"),
            ContentHash.Compute(catalog).ToString()
        );

        return built.Catalog;
    }

    static AddressableGroup Group(string name, string baseUrl) =>
        new() {
            Name = name,
            LoadPath = ContentProvider.Remote,
            RemoteUrl = baseUrl,

            // Separately, so one changed asset costs one bundle rather than the whole build.
            Packing = BundlePacking.PackSeparately,
            BundleNaming = BundleNaming.FilenameHash
        };

    /// <summary>Deterministic bytes that do not compress.</summary>
    /// <remarks>
    ///     <para>
    ///         Both halves matter. <b>Deterministic</b>, because the whole demonstration rests on an
    ///         unchanged asset producing an identical bundle, and a random payload would change the
    ///         content hash on every run and make every bundle a fresh download.
    ///     </para>
    ///     <para>
    ///         <b>Incompressible</b>, because a run of one repeated byte is what the first version of
    ///         this used, and LZ4 turned ninety-six kilobytes into four hundred — leaving the catalog
    ///         as the largest thing on the wire and the saving invisible. A payload that compresses
    ///         to nothing measures the compressor, not the update.
    ///     </para>
    ///     <para>
    ///         A 64-bit xorshift, written out rather than taken from <c>Random</c>: the sequence has
    ///         to be the same on every machine and every framework version, and <c>Random</c>'s is
    ///         explicitly not promised to be.
    ///     </para>
    /// </remarks>
    static byte[] Filled(int length, ulong seed) {
        var bytes = new byte[length];
        var state = seed;

        for (var index = 0; index < length; index++) {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            bytes[index] = (byte)state;
        }

        return bytes;
    }

    /// <summary>
    ///     The build's target name, which the catalog records and a client checks.
    /// </summary>
    /// <remarks>
    ///     A catalog built for another platform is one of the outcomes <see cref="ContentUpdate" />
    ///     refuses rather than throws on, so it has to be the real one here or the sample would
    ///     demonstrate that refusal instead.
    /// </remarks>
    static string RuntimeName() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsAndroid() ? "Android"
        : OperatingSystem.IsIOS() ? "iOS"
        : "Linux";
}
