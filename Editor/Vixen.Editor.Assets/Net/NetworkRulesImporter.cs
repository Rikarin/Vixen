// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Net.Engine;
using Vixen.Net.Rules;

namespace Vixen.Editor.Assets.Net;

/// <summary>How a network policy is imported.</summary>
/// <remarks>
///     Empty, like <c>WaterWavesImportSettings</c> and for the same reason: a <c>.vxnetrules</c>
///     already <em>is</em> engine data, so there is nothing about the conversion to decide.
/// </remarks>
[DataContract("NetworkRulesImporter")]
public sealed record NetworkRulesImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Imports a <c>.vxnetrules</c> — the one asset kind networking adds.</summary>
/// <remarks>
///     <para>
///         <b>[16 § Rules](../../../docs/plan/16-networking.md): "NetworkRules is a policy asset
///         referenced per prefab or set globally".</b> The registry that answers the questions was
///         built first and said in its own remarks that this half was not; this is that half.
///     </para>
///     <para>
///         <b>Written as its serialized record rather than carried forward as text</b>, on
///         <c>WaterWavesImporter</c>'s rule. There is a runtime reader — a dedicated server loads
///         the policy into <see cref="NetworkRulesRegistry" /> and every ownership decision goes
///         through it — and a game does not carry the YAML dialect, which is the editor's format. A
///         text chunk would be a policy that quietly never arrives, and the symptom of that is a
///         game rule that does not work rather than an error anybody sees.
///     </para>
///     <para>
///         ⚠ <b>No <c>MathScalars.Register</c>, deliberately, and it is worth writing down because
///         its absence is the shape of a bug this repository has had twice.</b> An asset type that
///         forgets that call reads a <c>Vector3</c> back as zero — and only when it runs before
///         anything scene-shaped, so it passes in a suite and fails alone. It is not needed here:
///         <see cref="NetworkRules" /> is six enums, and a policy has no geometry in it at all.
///         Calling it anyway would be cargo, and cargo is what makes the next reader unsure whether
///         the one that matters is load-bearing. <c>ANonDefaultPolicySurvivesTheDocument</c>
///         round-trips every field to prove it.
///     </para>
///     <para>
///         ⚠ <b>A key this build does not know is a warning, and until it was one it was nothing at
///         all.</b> <c>YamlSerializer</c> ignores an unknown key unless the caller asks — deliberately,
///         because "the caller decides whether an unknown key is news" — and no importer in the tree
///         asked. So <c>onOwnerDisconect: Destroy</c> imported cleanly, produced a chunk, resolved
///         onto a spawned node, and left the object on <see cref="DisconnectBehaviour.TransferToServer" />:
///         a policy file that reads exactly right and a rule that is not the one it says. That is the
///         same silence <see cref="NetworkRulesAsset.Validate" /> exists to break, one field over.
///     </para>
///     <para>
///         Warned rather than refused, on the same line <c>write: Everyone</c> is drawn on. A file
///         written by a newer engine carries keys this one does not have, and refusing it would make
///         an unknown field a downgrade failure rather than a typo — but a typo is by far the likelier
///         of the two, so it has to be said out loud.
///     </para>
///     <para>
///         ⚠ <b>What cannot be checked here is whether anything names it.</b> A prefab naming a
///         policy no asset carries falls back to the registry's default and counts into
///         <c>NetworkSpawner.UnresolvedRules</c> — a running session with the wrong rules. The
///         importer sees one file and cannot know which prefabs point at it; the count is where that
///         shows.
///     </para>
/// </remarks>
[Importer(NetworkRulesAsset.Extension)]
public sealed class NetworkRulesImporter : AssetImporter<NetworkRulesImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    /// <remarks>
    ///     ⚠ <b>The alias of the type actually written, which is what a chunk's reader resolves.</b>
    ///     <c>MaterialImporter</c>'s remarks record what the other spelling costs: the bytes of one
    ///     record handed to the reader of another, thrown from inside the asset manager about content
    ///     the build had just declared good.
    /// </remarks>
    public const string RulesType = nameof(NetworkRulesAsset);

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        NetworkRulesImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);

            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        NetworkRulesAsset policy;
        var unknown = new List<string>();

        try {
            policy = YamlSerializer.Parse<NetworkRulesAsset>(
                text,
                YamlSerializerOptions.Default with { OnUnknownKey = unknown.Add }
            );
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            // Reported rather than thrown, so an author gets every broken file in one pass.
            context.Report(ImportSeverity.Error, exception.Message);

            return context.Finish();
        }

        if (policy.Name.Length == 0) {
            policy.Name = Path.GetFileNameWithoutExtension(context.SourcePath.Value);
        }

        foreach (var key in unknown) {
            context.Report(
                ImportSeverity.Warning,
                $"'{key}' is not a field of a network policy, so nothing read it and the rule it was "
                + "meant to set is on its default. The fields are name, and under rules: spawn, "
                + "despawn, callServerRpc, write, changeOwner, claim and onOwnerDisconnect."
            );
        }

        if (policy.Validate() is { } problem) {
            context.Report(ImportSeverity.Error, problem);

            return context.Finish();
        }

        Advise(context, policy.Rules);

        context.Write(SubAssetId.Main, RulesType, Serializer.ToBytes(policy));

        return context.Finish();
    }

    /// <summary>The policy that is legal, loads, and is almost never what anybody meant.</summary>
    /// <remarks>
    ///     ⚠ <b>A warning and not an error, because it is a thing somebody may want</b> — a trusted
    ///     LAN prototype is a legitimate reason to let any client write any object's state. What it
    ///     has in common with a file nobody finished is everything else: it is the one setting that
    ///     turns off server authority entirely, and doc 16's whole argument for a policy file is that
    ///     relaxing authority should be a decision somebody wrote down rather than one they inherited
    ///     from an example they copied.
    /// </remarks>
    static void Advise(ImportContext context, NetworkRules rules) {
        if (rules.Write == RuleAudience.Everyone) {
            context.Report(
                ImportSeverity.Warning,
                "write: Everyone lets any client in the session overwrite this object's replicated "
                + "state, which is the one setting that gives up server authority completely. A "
                + "trusted prototype is a legitimate reason; `Owner` is what a co-operative game "
                + "usually means."
            );
        }
    }
}
