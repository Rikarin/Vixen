// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Animation;

/// <summary>Settings for the importer that takes a shape vocabulary.</summary>
/// <remarks>Empty, for <c>AnimationClipImportSettings</c>'s reason: the file is engine data already.</remarks>
[DataContract("ShapeVocabularyImporter")]
public sealed record ShapeVocabularyImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Settings for the importer that takes a proxy shape set.</summary>
[DataContract("ProxyShapeSetImporter")]
public sealed record ProxyShapeSetImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxshapevocab</c>.</summary>
/// <remarks>
///     The names and tags a project's proxy shapes may use, and the classes it declares. What the
///     importer adds over reading the file is the two checks a text file invites — a name declared
///     twice, and a class member naming a shape the vocabulary itself does not.
/// </remarks>
[Importer(ShapeVocabularyContent.Extension)]
public sealed class ShapeVocabularyImporter : AssetImporter<ShapeVocabularyImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    public const string VocabularyType = "ShapeVocabularyContent";

    static ShapeVocabularyImporter() => MathScalars.Register();

    /// <inheritdoc />
    public override int Version => ShapeVocabularyContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        ShapeVocabularyImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        if (await ShapeYaml.ReadAsync<ShapeVocabularyContent>(context, cancellationToken).ConfigureAwait(false)
            is not { } vocabulary) {
            return context.Finish();
        }

        Check(context, vocabulary);
        context.Write(SubAssetId.Main, VocabularyType, Serializer.ToBytes(vocabulary));

        return context.Finish();
    }

    /// <summary>Reports what the vocabulary says is wrong with itself.</summary>
    /// <remarks>
    ///     ⚠ <b>The rules live on the content type, because the editor asks the same question.</b> A
    ///     second copy here would be the one that goes out of step with the panel, and the way that
    ///     shows up is a file the editor calls clean and the build refuses.
    /// </remarks>
    static void Check(ImportContext context, ShapeVocabularyContent vocabulary) {
        foreach (var problem in vocabulary.Problems()) {
            context.Report(problem.Fatal ? ImportSeverity.Error : ImportSeverity.Warning, problem.Message);
        }
    }
}

/// <summary>Compiles a <c>.vxproxyshapes</c>, and checks it against the vocabulary it names.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the payoff D13 claims for a declared vocabulary, and it only exists because
///         the check runs here.</b> A clip's constraint refers to a shape by name, and the clip is
///         portable exactly as far as that name is present and means the same thing on every body it
///         might play on. Without the check the failure is a clip that silently does nothing on one
///         character, discovered by a player. With it, it is an error at import naming the set and the
///         missing name.
///     </para>
///     <para>
///         The vocabulary is read through <see cref="ImportContext.Files" /> and declared as a file
///         dependency, so adding a shape name re-checks every set in the project rather than the ones
///         somebody remembered to touch.
///     </para>
/// </remarks>
[Importer(ProxyShapeSetContent.Extension)]
public sealed class ProxyShapeSetImporter : AssetImporter<ProxyShapeSetImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    public const string SetType = "ProxyShapeSetContent";

    // A shape is mostly vectors, so the scalar converters have to be in the process-wide table before
    // the first file is bound. Same static-constructor placement, and same reason, as MaterialImporter.
    static ProxyShapeSetImporter() => MathScalars.Register();

    /// <inheritdoc />
    public override int Version => ProxyShapeSetContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        ProxyShapeSetImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        if (await ShapeYaml.ReadAsync<ProxyShapeSetContent>(context, cancellationToken).ConfigureAwait(false)
            is not { } set) {
            return context.Finish();
        }

        if (!Structure(context, set)) {
            return context.Finish();
        }

        await AgainstVocabularyAsync(context, set, cancellationToken).ConfigureAwait(false);

        context.Write(SubAssetId.Main, SetType, Serializer.ToBytes(set));
        return context.Finish();
    }

    /// <summary>The mistakes a text file invites, whether or not a vocabulary is named.</summary>
    static bool Structure(ImportContext context, ProxyShapeSetContent set) {
        HashSet<string> seen = [];
        var ok = true;

        foreach (var shape in set.Shapes) {
            if (string.IsNullOrWhiteSpace(shape.Name)) {
                context.Report(ImportSeverity.Error, "One of its shapes has no name. A contact names a shape.");
                ok = false;

                continue;
            }

            if (!seen.Add(shape.Name)) {
                context.Report(
                    ImportSeverity.Error,
                    $"It has two shapes called '{shape.Name}'. A contact naming it would land on whichever one "
                    + "was written first, and the two are usually the left and the right."
                );

                ok = false;
            }

            if (string.IsNullOrWhiteSpace(shape.Joint)) {
                context.Report(
                    ImportSeverity.Error,
                    $"'{shape.Name}' hangs off no joint. A shape that is not attached to the rig never moves."
                );

                ok = false;
            }

            if (shape.Extents.LengthSquared() <= 0f) {
                // A warning rather than an error: a shape somebody has just added and not sized yet
                // is the ordinary way of making one, and the surface of a zero-size primitive is a
                // point rather than an exception.
                context.Report(
                    ImportSeverity.Warning,
                    $"'{shape.Name}' has no size. Every coordinate on it resolves to the same point."
                );
            }
        }

        return ok;
    }

    /// <summary>The check the vocabulary exists for.</summary>
    static async ValueTask AgainstVocabularyAsync(
        ImportContext context,
        ProxyShapeSetContent set,
        CancellationToken cancellationToken
    ) {
        if (string.IsNullOrWhiteSpace(set.Vocabulary)) {
            return;
        }

        var path = new VirtualPath(set.Vocabulary);

        context.DependsOnFile(path);

        if (!context.Files.Exists(path)) {
            context.Report(
                ImportSeverity.Error,
                $"It says it implements '{set.Vocabulary}', and there is no such file. An unchecked set is one "
                + "whose names are only right by luck."
            );

            return;
        }

        ShapeVocabularyContent declared;

        try {
            await using var source = await context.Files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(source);

            declared = YamlSerializer.Parse<ShapeVocabularyContent>(
                await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)
            );
        } catch (Exception failure) when (failure is YamlParseException or YamlBindingException or IOException) {
            context.Report(ImportSeverity.Error, $"Its vocabulary '{set.Vocabulary}' could not be read: {failure.Message}");
            return;
        }

        // Baked against a skeleton-free set: the check is about names, kinds and tags, none of which
        // need a rig. Whether a joint exists is the rig's question and is answered at load.
        List<ShapeValidation> findings = [];

        declared.Bake().Validate(Bake(set), findings, Symbol.Intern(set.Class));

        foreach (var finding in findings) {
            context.Report(ImportSeverity.Error, finding.Message);
        }
    }

    /// <summary>The set as the checker sees it, with every joint at the root.</summary>
    static ProxyShapeSet Bake(ProxyShapeSetContent set) {
        List<ProxyShape> shapes = [];

        foreach (var record in set.Shapes) {
            shapes.Add(
                new() {
                    Name = Symbol.Intern(record.Name),
                    Kind = record.Kind,
                    Joint = 0,
                    Dimensions = new(record.Extents, record.TopExtents),
                    Tags = ShapeTags.Parse(record.Tags),
                    Coarse = record.Coarse
                }
            );
        }

        return ProxyShapeSet.Of(set.Name, set.Vocabulary, [.. shapes]);
    }
}

/// <summary>Reading one of these files, which both do the same way.</summary>
static class ShapeYaml {
    public static async ValueTask<T?> ReadAsync<T>(ImportContext context, CancellationToken cancellationToken)
        where T : class, new() {
        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (text.Trim().Length == 0) {
            // An empty file is a new one rather than an error, for `AssetFile.Read`'s reason: the
            // ordinary way to make one of these is to create the file and open it.
            return new();
        }

        YamlNode document;

        try {
            document = YamlReader.Read(text);
        } catch (YamlParseException failure) {
            context.Report(ImportSeverity.Error, $"It is not valid YAML: {failure.Message}");
            return null;
        }

        if (document is not YamlMapping root) {
            context.Report(ImportSeverity.Error, "Its root is not a mapping.");
            return null;
        }

        if (AssetReferenceScan.Declare(root, context) > 0) {
            return null;
        }

        try {
            return YamlSerializer.Parse<T>(text);
        } catch (Exception failure) when (failure is YamlBindingException or FormatException or NotSupportedException) {
            context.Report(ImportSeverity.Error, failure.Message);
            return null;
        }
    }
}
