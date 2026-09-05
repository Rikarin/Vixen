// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core.Imaging;

namespace Vixen.Testing;

/// <summary>
///     Writes a synthetic Vixen project to disk: so many textures, so many models, so many scenes,
///     and any number of opaque blobs.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/plan/12</c> § "Test infrastructure worth building early" asks for a
///         <c>FixtureProject</c>: <i>"a synthetic Vixen project generator (N textures, M models, K
///         scenes) for asset pipeline scale tests"</i>. This is that, and it is the last of the five
///         that section names.
///     </para>
///     <para>
///         <b>The counts are the product, not the files.</b> Every assertion a scale test makes is an
///         exact integer — <i>this many were imported, that many were cached, one changed</i> — and
///         the suite that wrote its own fixture had to compute those integers a second time, beside
///         the loop that wrote it. Two derivations of one number in one file is where they drift:
///         <see cref="Written.Entries" /> is counted off the filesystem the fixture just wrote, so a
///         test cannot assert against arithmetic the fixture did not actually perform.
///     </para>
///     <para>
///         ⚠ <b>And that is why <see cref="Write" /> refuses three things rather than returning a
///         project.</b> A fixture is an instrument like any other in this repository, and every one
///         of the three refusals replaces a form that is <i>green</i> when it should be red:
///     </para>
///     <list type="bullet">
///         <item>
///             A fixture asked for <b>nothing at all</b> — every count zero — writes an empty
///             project, over which "everything was imported" and "nothing failed" are both true and
///             neither means anything. A miscounted scale variable, a fixture whose files went to
///             another directory, a generator whose loop never ran: all three arrive as this.
///         </item>
///         <item>
///             A fixture written into a directory that <b>already holds assets</b> counts somebody
///             else's files into <see cref="Written.Entries" />, so every exact integer in the suite
///             is off by however many were already there — and off in the direction that still
///             passes, because the import will find them too.
///         </item>
///         <item>
///             A fixture whose <b>writes and arithmetic disagree</b>. The counts are read back off
///             the disk, but they are also computed, and the two are compared: a kind that silently
///             wrote no files (an encoder that threw into a swallowed <c>catch</c>, a count of zero
///             where the caller meant a hundred) is otherwise a smaller project that passes every
///             assertion derived from it.
///         </item>
///     </list>
///     <para>
///         <b>The assets are real assets and that is the whole point of the kinds.</b> The textures
///         are PNGs <see cref="PngCodec" /> encoded, the models are Wavefront OBJ and the scenes are
///         the YAML a <c>.vxscene</c> is, so the project drives <c>TextureImporter</c>,
///         <c>ModelImporter</c> and <c>SceneImporter</c> rather than the raw fallback. ⚠ A fixture of
///         ten thousand <c>.bin</c> files exercises exactly one importer, and the failure that hides
///         is the interesting one: a "texture" nothing claims is imported as a byte blob, succeeds,
///         is counted, and reads as a pass — which is the same shape as the <c>.vxwaves</c> that fell
///         through to <c>RawImporter</c> and became an asset no runtime reader resolves.
///         <see cref="Blobs" /> is still here because a scale test wants cheap files by the thousand;
///         it is the fixture's cheapest kind rather than its only one.
///     </para>
///     <para>
///         <b>Sources only, no <c>.meta</c> sidecars.</b> That is what a project written by hand or
///         checked out for the first time looks like, and minting them is the scan's job — which is
///         itself a thing a scale test is measuring.
///     </para>
/// </remarks>
sealed class FixtureProject {
    /// <summary>The project directory. <c>Assets/</c> goes under it.</summary>
    public required string Root { get; init; }

    /// <summary>How many PNG textures to write, under <c>Assets/Textures</c>.</summary>
    public int Textures { get; init; }

    /// <summary>How many OBJ models to write, under <c>Assets/Models</c>.</summary>
    public int Models { get; init; }

    /// <summary>How many <c>.vxscene</c> scenes to write, under <c>Assets/Scenes</c>.</summary>
    public int Scenes { get; init; }

    /// <summary>How many opaque files to write — the cheap kind, for size rather than for coverage.</summary>
    public int Blobs { get; init; }

    /// <summary>What the blobs are called, so a suite with its own importer can be given files it claims.</summary>
    public string BlobExtension { get; init; } = ".bin";

    /// <summary>
    ///     How many blobs go in one folder, or zero to put them straight in <c>Assets</c>.
    /// </summary>
    /// <remarks>
    ///     A hundred by default, which is the shape <c>ImportBudgetTests</c> already had: ten
    ///     thousand files in one directory is not a project anybody has, and the folders are entries
    ///     the scan imports too, so the layout is part of what is being measured rather than
    ///     decoration.
    /// </remarks>
    public int BlobsPerFolder { get; init; } = 100;

    /// <summary>Writes it.</summary>
    /// <returns>Where it is and how much of it there is.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The fixture asks for no assets, the assets directory already has some, or what reached the
    ///     disk is not what was asked for. See the remarks on the type for why each of those is a
    ///     refusal rather than a smaller project.
    /// </exception>
    public Written Write() {
        var assets = Path.Combine(Root, "Assets");

        if (Textures + Models + Scenes + Blobs <= 0) {
            throw new InvalidOperationException(
                "A fixture project with no assets in it is a project every assertion is vacuous over: "
                + "'everything imported' and 'nothing failed' are both true of an empty directory. Ask for "
                + "at least one texture, model, scene or blob."
            );
        }

        if (Directory.Exists(assets) && Directory.EnumerateFileSystemEntries(assets).Any()) {
            throw new InvalidOperationException(
                $"{assets} already has something in it, so the counts this returns would not describe the "
                + "project — an import finds what was there before as well. Write each fixture into its own "
                + "directory."
            );
        }

        Directory.CreateDirectory(assets);

        var folders = 0;

        folders += WriteBlobs(assets);
        folders += WriteKind(assets, "Textures", Textures, Texture);
        folders += WriteKind(assets, "Models", Models, Model);
        folders += WriteKind(assets, "Scenes", Scenes, Scene);

        var files = Textures + Models + Scenes + Blobs;

        // ⚠ Counted off the disk rather than returned from the arithmetic above, and then the two are
        // compared. What a fixture claims is what a suite asserts against, so a fixture that claims
        // more than it wrote makes the suite assert the fixture's bug — and a fixture that wrote
        // nothing at all claims nothing at all, which passes.
        var onDisk = Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories).Count();
        var foldersOnDisk = Directory.EnumerateDirectories(assets, "*", SearchOption.AllDirectories).Count();

        if (onDisk != files || foldersOnDisk != folders) {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the fixture asked for {files} files in {folders} folders and {assets} holds {onDisk} "
                    + $"in {foldersOnDisk}, so it is refused rather than described wrongly: every count a "
                    + $"scale test asserts is derived from the one it would have returned."
                )
            );
        }

        return new(Root, assets, files, folders, BlobPath);

        string BlobPath(int index) => Path.Combine(assets, BlobFolder(index), BlobName(index));
    }

    /// <summary>The pixels of texture <paramref name="index" />, no two alike.</summary>
    /// <remarks>
    ///     Four by four, because what is being measured is a project's size and not an image's, and
    ///     because <c>TextureImporter</c> compresses in four-by-four blocks. Distinct per index so
    ///     that the content-addressed artefact store holds one chunk per asset — a fixture of
    ///     identical files is a fixture in which a hundred assets share one artefact, which is a
    ///     different project from the one the caller asked for.
    /// </remarks>
    static byte[] Texture(int index) {
        var pixels = new byte[4 * 4 * 4];

        for (var pixel = 0; pixel < 4 * 4; pixel++) {
            pixels[(pixel * 4) + 0] = (byte)(index * 7);
            pixels[(pixel * 4) + 1] = (byte)(pixel * 17);
            pixels[(pixel * 4) + 2] = (byte)(index + pixel);
            pixels[(pixel * 4) + 3] = 255;
        }

        return PngCodec.Encode(new(4, 4, pixels));
    }

    /// <summary>Model <paramref name="index" />: one triangle, moved so that no two are the same bytes.</summary>
    static byte[] Model(int index) =>
        Encoding.UTF8.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"o Shape{index}\nv {index} 0 0\nv {index + 1} 0 0\nv {index} 1 0\nf 1 2 3\n"
            )
        );

    /// <summary>Scene <paramref name="index" />: one root entity, with an id nothing else has.</summary>
    /// <remarks>
    ///     The ids are hexadecimal and thirty-two characters because that is what an
    ///     <c>AssetId</c>-shaped entity id in a <c>.vxscene</c> is; a scene whose id does not parse is
    ///     reported as an error by <c>SceneImporter</c> rather than imported, which would make the
    ///     fixture's own files the thing a scale test measured.
    /// </remarks>
    static byte[] Scene(int index) =>
        Encoding.UTF8.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 version: 1
                 name: Level{index}
                 roots:
                   - id: {index:x32}
                     name: Root
                     position: {index} 0 0

                 """
            )
        );

    /// <summary>Writes one kind into its own folder.</summary>
    /// <returns>How many folders that took: one if anything was written, none otherwise.</returns>
    static int WriteKind(string assets, string folder, int count, Func<int, byte[]> content) {
        if (count <= 0) {
            return 0;
        }

        var directory = Path.Combine(assets, folder);

        Directory.CreateDirectory(directory);

        for (var index = 0; index < count; index++) {
            File.WriteAllBytes(Path.Combine(directory, Name(folder, index)), content(index));
        }

        return 1;
    }

    /// <summary>Writes the blobs, spread over folders the way a real project's bulk is.</summary>
    /// <returns>How many folders that took, the containing one included.</returns>
    int WriteBlobs(string assets) {
        if (Blobs <= 0) {
            return 0;
        }

        if (BlobsPerFolder <= 0) {
            for (var index = 0; index < Blobs; index++) {
                File.WriteAllBytes(Path.Combine(assets, BlobName(index)), Blob(index));
            }

            return 0;
        }

        var folders = ((Blobs - 1) / BlobsPerFolder) + 1;

        for (var folder = 0; folder < folders; folder++) {
            Directory.CreateDirectory(Path.Combine(assets, "Bulk", folder.ToString(CultureInfo.InvariantCulture)));
        }

        for (var index = 0; index < Blobs; index++) {
            File.WriteAllBytes(Path.Combine(assets, BlobFolder(index), BlobName(index)), Blob(index));
        }

        // The bulk folder itself, which is an entry the scan imports like any other.
        return folders + 1;
    }

    static byte[] Blob(int index) =>
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"asset {index}"));

    string BlobName(int index) => string.Create(CultureInfo.InvariantCulture, $"asset{index}{BlobExtension}");

    string BlobFolder(int index) =>
        BlobsPerFolder <= 0
            ? string.Empty
            : Path.Combine("Bulk", (index / BlobsPerFolder).ToString(CultureInfo.InvariantCulture));

    static string Name(string folder, int index) =>
        folder switch {
            "Textures" => string.Create(CultureInfo.InvariantCulture, $"texture{index}.png"),
            "Models" => string.Create(CultureInfo.InvariantCulture, $"model{index}.obj"),
            _ => string.Create(CultureInfo.InvariantCulture, $"scene{index}.vxscene")
        };

    /// <summary>A written fixture project: where it is, and how much of it there is.</summary>
    /// <param name="Root">The project directory.</param>
    /// <param name="Assets">Its <c>Assets</c> directory.</param>
    /// <param name="Files">How many source files were written.</param>
    /// <param name="Folders">How many folders they are in, all of which are entries too.</param>
    /// <param name="Blob">Where blob <c>n</c> is, for a test that needs to edit one.</param>
    public sealed record Written(string Root, string Assets, int Files, int Folders, Func<int, string> Blob) {
        /// <summary>Everything an import of this project will find: the files, and the folders.</summary>
        /// <remarks>
        ///     ⚠ A folder is an asset entry in this pipeline — it gets a <c>.meta</c> and a guid, so
        ///     that moving one is a rename rather than a re-import of everything under it. A suite
        ///     asserting a file count against an import summary is therefore short by the number of
        ///     folders, which is the arithmetic this member exists to stop being written twice.
        /// </remarks>
        public int Entries => Files + Folders;
    }
}
