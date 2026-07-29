// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;
using Vixen.Ui.Text;

namespace Vixen.Editor.AssetEditors.Fonts;

/// <summary>A font asset: a face, the faces behind it, and how it is put in an atlas.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A document beside the <c>.ttf</c> rather than settings on it, and the reason is the
///         fallback chain.</b> A chain is a property of *this use* of a face: the same
///         <c>NotoSans.ttf</c> is the primary face of one font asset and the CJK fallback of another,
///         and import settings on the file could only express one of those. Doc 11's row names three
///         things — coverage, atlas preview, fallback chain — and the third is what makes this an
///         asset of its own.
///     </para>
///     <para>
///         ⚠ <b>The faces are GUIDs, so moving a <c>.ttf</c> needs nothing done to this file</b> —
///         doc 08's rule, and it is what lets <c>ReferenceIndex</c> report that deleting a font
///         breaks three chains.
///     </para>
/// </remarks>
[DataContract("FontAsset")]
public sealed class FontAsset {
    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

    /// <summary>What a font asset is written as.</summary>
    public const string Extension = ".vxfont";

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the font is called, in a style sheet.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The face it is mostly made of.</summary>
    public AssetId Face { get; set; }

    /// <summary>Which face inside a collection, for a <c>.ttc</c>.</summary>
    public int FaceIndex { get; set; }

    /// <summary>The faces consulted, in order, for a code point the primary does not have.</summary>
    public List<AssetId> Fallbacks { get; set; } = [];

    /// <summary>How large a glyph is rasterised, in pixels.</summary>
    public float PixelSize { get; set; } = 48f;

    /// <summary>How wide the atlas page is, in pixels.</summary>
    public int AtlasWidth { get; set; } = 1024;

    /// <summary>And how tall.</summary>
    public int AtlasHeight { get; set; } = 1024;

    /// <summary>How many pixels of margin each glyph gets.</summary>
    /// <remarks>
    ///     ⚠ <b>Not decoration: it is what a distance field needs to have somewhere to fall off
    ///     into.</b> A field packed with no padding clips its own gradient at the glyph's edge, which
    ///     shows up as a hard stair-step exactly where the antialiasing was supposed to be.
    /// </remarks>
    public int Padding { get; set; } = 4;

    /// <summary>Whether glyphs are stored as a distance field rather than as coverage.</summary>
    public bool DistanceField { get; set; } = true;

    /// <summary>The code-point ranges the atlas is pre-populated with.</summary>
    /// <remarks>
    ///     Empty means "whatever gets drawn", which is right for a game whose text is data and wrong
    ///     for one whose text is a fixed HUD — a range list is how somebody says the second.
    /// </remarks>
    public List<FontRangeData> Ranges { get; set; } = [];

    /// <summary>Reads YAML into a font asset.</summary>
    /// <param name="yaml">The text.</param>
    /// <returns>The asset.</returns>
    /// <exception cref="NotSupportedException">The file is from a newer editor.</exception>
    public static FontAsset FromYaml(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        if (yaml.Trim().Length == 0) {
            return new();
        }

        var font = YamlSerializer.Parse<FontAsset>(yaml);

        return font.Version <= Current
            ? font
            : throw new NotSupportedException(
                $"This font is version {font.Version} and this build reads {Current}."
            );
    }

    /// <summary>Writes it as YAML.</summary>
    /// <returns>The text.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(this);
}

/// <summary>A range of code points the atlas carries.</summary>
[DataContract("FontRange")]
public sealed class FontRangeData {
    /// <summary>The first code point.</summary>
    public int First { get; set; }

    /// <summary>The last, inclusive.</summary>
    public int Last { get; set; }
}

/// <summary>One block of Unicode, and how much of it a face has.</summary>
/// <param name="Name">What the block is called.</param>
/// <param name="First">Its first code point.</param>
/// <param name="Last">Its last, inclusive.</param>
/// <param name="Covered">How many of them the face has a glyph for.</param>
/// <param name="Assigned">How many of them are assigned characters at all.</param>
/// <remarks>
///     ⚠ <b>Coverage is reported against <i>assigned</i> code points rather than against the block's
///     width.</b> Most blocks have unassigned holes, and a font that has every character in Latin-1
///     Supplement would otherwise report 87 % and read as incomplete. What a person wants to know is
///     "is anything missing", and the answer has to be able to be yes-nothing.
/// </remarks>
public readonly record struct FontCoverage(string Name, int First, int Last, int Covered, int Assigned) {
    /// <summary>The fraction covered, in <c>[0, 1]</c>.</summary>
    public float Fraction => Assigned == 0 ? 0f : (float) Covered / Assigned;
}

/// <summary>A font asset, open for editing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The face is loaded here and disposed with the document.</b> <see cref="FontFace" />
///         owns native memory, a panel's factory runs again on every reopen, and a face loaded by a
///         view would leak one per reopen — which for a font is megabytes.
///     </para>
///     <para>
///         ⚠ <b>A face that will not load is reported and the document still opens.</b> Editing the
///         chain of a font whose primary has been deleted is exactly when somebody needs this panel.
///     </para>
/// </remarks>
public sealed class FontDocument : EditorDocument {
    /// <summary>What a font asset is written as.</summary>
    public const string Extension = FontAsset.Extension;

    readonly List<FontFace> loaded = [];

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The asset.</summary>
    public FontAsset Font { get; private set; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; private set; }

    /// <summary>The primary face, or <see langword="null" /> when it would not load.</summary>
    public FontFace? Face { get; private set; }

    /// <summary>The fallback faces that loaded, in order.</summary>
    public IReadOnlyList<FontFace> Fallbacks => fallbacks;

    readonly List<FontFace> fallbacks = [];

    /// <summary>What loading the faces had to say.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>Raised after anything changes the asset.</summary>
    public event Action<FontDocument>? Changed;

    /// <summary>Opens a font asset.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public FontDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        try {
            Font = FontAsset.FromYaml(AssetFile.Read(path));
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            Font = new();
            LoadError = exception.Message;
        }

        if (Font.Name.Length == 0) {
            Font.Name = Path.GetFileNameWithoutExtension(path);
        }

        Resolve();
    }

    /// <summary>Loads the faces the asset names, replacing whatever was loaded before.</summary>
    /// <remarks>
    ///     ⚠ <b>Called on every edit that could have changed a face, and it disposes first.</b>
    ///     Changing the primary face four times while looking for the right one would otherwise be
    ///     four faces held for the life of the document.
    /// </remarks>
    public void Resolve() {
        foreach (var face in loaded) {
            face.Dispose();
        }

        loaded.Clear();
        fallbacks.Clear();

        List<string> problems = [];

        Face = Load(Font.Face, Font.FaceIndex, problems);

        foreach (var fallback in Font.Fallbacks) {
            if (Load(fallback, 0, problems) is { } face) {
                fallbacks.Add(face);
            }
        }

        Problems = problems;
    }

    FontFace? Load(AssetId asset, int index, List<string> problems) {
        if (asset.IsEmpty) {
            return null;
        }

        if (!Project.Assets.TryGetByGuid(asset, out var entry)) {
            problems.Add($"{asset} is not in this project.");

            return null;
        }

        try {
            var face = FontFace.Load(File.ReadAllBytes(Project.Paths.Absolute(entry.Path)), index, entry.Path);

            loaded.Add(face);
            return face;
        } catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or InvalidDataException or NotSupportedException) {
            problems.Add($"{entry.Path}: {exception.Message}");

            return null;
        }
    }

    /// <summary>Replaces the asset, undoably.</summary>
    /// <param name="name">What the undo history calls the edit.</param>
    /// <param name="change">What to do to it.</param>
    /// <param name="reloads">Whether the faces have to be loaded again.</param>
    public void Edit(string name, Action<FontAsset> change, bool reloads = false) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(change);

        var before = Font.ToYaml();

        change(Font);

        var after = Font.ToYaml();

        if (string.Equals(before, after, StringComparison.Ordinal)) {
            return;
        }

        // ⚠ Undo by reparsing the text rather than by remembering which member moved. The asset is a
        // small mutable record with a dozen members, and a command per member would be twelve
        // commands that each have to agree about what "changed" means — the YAML *is* the state, and
        // it is the state the file has anyway.
        Stack.Execute(
            new DelegateCommand(
                name,
                _ => Apply(after, reloads),
                _ => Apply(before, reloads)
            )
        );
    }

    void Apply(string yaml, bool reloads) {
        Font = FontAsset.FromYaml(yaml);

        if (reloads) {
            Resolve();
        }

        Changed?.Invoke(this);
    }

    /// <summary>How much of each Unicode block the chain covers.</summary>
    /// <param name="includeFallbacks">Whether a fallback counts as covering a code point.</param>
    /// <returns>One row per block this editor lists.</returns>
    public IReadOnlyList<FontCoverage> Coverage(bool includeFallbacks = true) {
        List<FontCoverage> rows = [];

        foreach (var (name, first, last) in Blocks) {
            var covered = 0;
            var assigned = 0;

            for (var code = first; code <= last; code++) {
                if (char.GetUnicodeCategory((char) code) == System.Globalization.UnicodeCategory.OtherNotAssigned) {
                    continue;
                }

                assigned++;

                if (Supports(code, includeFallbacks)) {
                    covered++;
                }
            }

            rows.Add(new(name, first, last, covered, assigned));
        }

        return rows;
    }

    /// <summary>Whether the chain has a glyph for a code point.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <param name="includeFallbacks">Whether a fallback counts.</param>
    /// <returns>Whether anything draws it.</returns>
    public bool Supports(int codePoint, bool includeFallbacks = true) {
        if (Face?.Supports(codePoint) == true) {
            return true;
        }

        return includeFallbacks && fallbacks.Any(face => face.Supports(codePoint));
    }

    /// <summary>Which face in the chain draws a code point, or −1 for none.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>0 for the primary, 1.. for a fallback, −1 for nothing.</returns>
    /// <remarks>
    ///     What makes the chain worth showing rather than merely storing: "which font is this
    ///     character actually coming from" is the question a fallback chain exists to answer, and it
    ///     is unanswerable from the file alone.
    /// </remarks>
    public int Resolves(int codePoint) {
        if (Face?.Supports(codePoint) == true) {
            return 0;
        }

        for (var index = 0; index < fallbacks.Count; index++) {
            if (fallbacks[index].Supports(codePoint)) {
                return index + 1;
            }
        }

        return -1;
    }

    /// <summary>The blocks this editor reports on.</summary>
    /// <remarks>
    ///     ⚠ <b>A chosen list rather than every block in Unicode.</b> There are over three hundred,
    ///     most of which no game ships, and a panel that listed them all would bury the four rows
    ///     somebody is checking. These are the ones a Latin, Cyrillic, Greek or symbol-using project
    ///     asks about; a font that covers something not in the list still works, and the code-point
    ///     probe below answers for any character.
    /// </remarks>
    static readonly (string Name, int First, int Last)[] Blocks = [
        ("Basic Latin", 0x0020, 0x007E),
        ("Latin-1 Supplement", 0x00A0, 0x00FF),
        ("Latin Extended-A", 0x0100, 0x017F),
        ("Latin Extended-B", 0x0180, 0x024F),
        ("Greek and Coptic", 0x0370, 0x03FF),
        ("Cyrillic", 0x0400, 0x04FF),
        ("General Punctuation", 0x2000, 0x206F),
        ("Currency Symbols", 0x20A0, 0x20BF),
        ("Arrows", 0x2190, 0x21FF),
        ("Mathematical Operators", 0x2200, 0x22FF),
        ("Box Drawing", 0x2500, 0x257F),
        ("Geometric Shapes", 0x25A0, 0x25FF),
        ("Miscellaneous Symbols", 0x2600, 0x26FF)
    ];

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, Font.ToYaml());

    /// <inheritdoc />
    protected override void OnClosed() {
        base.OnClosed();

        foreach (var face in loaded) {
            face.Dispose();
        }

        loaded.Clear();
        fallbacks.Clear();

        Face = null;
    }
}
