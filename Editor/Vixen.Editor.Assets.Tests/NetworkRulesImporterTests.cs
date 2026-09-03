// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets.Net;
using Vixen.Net.Engine;
using Vixen.Net.Rules;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>The one asset kind networking adds — [docs/plan/16 § Rules], and its importer.</summary>
/// <remarks>
///     ⚠ <b>What is asserted throughout is the artefact read back the way the <em>runtime</em> reads
///     it</b>, rather than that the import succeeded. A policy that imports and produces a chunk
///     nothing can deserialise is an object governed by the registry's default — server-authoritative
///     and therefore safe, and therefore invisible: the symptom is a game rule that does not work,
///     with a policy file in the project that reads exactly right.
/// </remarks>
public sealed class NetworkRulesImporterTests {
    [Fact]
    public void ItClaimsTheExtensionAndNamesTheTypeItWrites() {
        var importer = new NetworkRulesImporter();

        Assert.Equal("NetworkRulesImporter", importer.Name);
        Assert.Contains(".vxnetrules", importer.Extensions);
        Assert.Equal("NetworkRulesAsset", NetworkRulesImporter.RulesType);
    }

    /// <summary>And that the build's own registry hands a <c>.vxnetrules</c> to it.</summary>
    /// <remarks>
    ///     ⚠ <b>The gap every other test in this file would step over.</b> They construct the
    ///     importer and drive it, which asserts that it works and nothing about whether anything
    ///     reaches it — and <c>[Importer]</c> is a declaration the registry does not scan for.
    ///     <see cref="BuiltInImporters.Create()" /> is a hand-written list, and an importer absent
    ///     from it means the file falls through to <c>RawImporter</c>: a byte blob under a name no
    ///     runtime reader resolves, with no error anywhere. That has now happened six times.
    /// </remarks>
    [Fact]
    public void TheBuildsOwnRegistryHandsAPolicyFileToIt() {
        // ⚠ A contribution set of its own, not ImporterContributions.Default: the default is
        // process-wide and ImporterContributionTests mutates it, so reading it here would race.
        var registry = BuiltInImporters.Create(new ImporterContributions());

        Assert.True(registry.TryGetForFile("Assets/Rules/pickup.vxnetrules", out var importer));
        Assert.IsType<NetworkRulesImporter>(importer);
    }

    /// <summary>
    ///     ⚠ <b>Every field of a policy survives the document, the import and the chunk.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Non-default on purpose, all seven of them, because a default is what a value that
    ///         never arrived looks like.</b> A round trip asserted with the type's own defaults is a
    ///         round trip that passes when the binder binds nothing at all — this repository's
    ///         standing example being an asset type that forgot <c>MathScalars.Register</c> and read
    ///         every <c>Vector3</c> back as zero, which only shows up when the suite happens to run
    ///         it before anything scene-shaped.
    ///     </para>
    ///     <para>
    ///         ⚠ There is no <c>MathScalars.Register</c> here and none is needed: a policy is six
    ///         enums and a name, with no geometry anywhere in it. This test is what makes that a
    ///         checked statement rather than a hope.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ANonDefaultPolicySurvivesTheDocument() {
        var (_, result) = await Import(
            "pickup.vxnetrules",
            """
            name: Pickup
            rules:
              spawn: Owner
              despawn: Everyone
              callServerRpc: Owner
              write: Owner
              changeOwner: Everyone
              claim: WhenUnowned
              onOwnerDisconnect: Destroy
            """
        );

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(message => message.Message)));

        var artefact = Assert.Single(result.Artifacts);
        var written = Serializer.Read<NetworkRulesAsset>(artefact.Content.Span);

        Assert.Equal("NetworkRulesAsset", artefact.Type);
        Assert.Equal("Pickup", written.Name);

        Assert.Equal(RuleAudience.Owner, written.Rules.Spawn);
        Assert.Equal(RuleAudience.Everyone, written.Rules.Despawn);
        Assert.Equal(RuleAudience.Owner, written.Rules.CallServerRpc);
        Assert.Equal(RuleAudience.Owner, written.Rules.Write);
        Assert.Equal(RuleAudience.Everyone, written.Rules.ChangeOwner);
        Assert.Equal(OwnershipClaim.WhenUnowned, written.Rules.Claim);
        Assert.Equal(DisconnectBehaviour.Destroy, written.Rules.OnOwnerDisconnect);

        // ⚠ And that the values above are not what a policy nobody filled in would have. Every one
        // of the seven differs from the default, so a binder that bound nothing fails all seven.
        Assert.NotEqual(NetworkRules.ServerAuthoritative, written.Rules);
    }

    /// <summary>An unnamed policy takes the file's own stem, because a prefab names it by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this an author who left the field blank gets an asset nothing can refer
    ///     to.</b> A prefab naming the empty string resolves to nothing — see
    ///     <c>NetworkSpawner.Govern</c> — so the symptom is not "asset not found", it is an object
    ///     silently governed by the default, with no diagnostic anywhere.
    /// </remarks>
    [Fact]
    public async Task AnUnnamedPolicyTakesTheFilesOwnName() {
        var (_, result) = await Import("vehicle.vxnetrules", "rules:\n  changeOwner: Owner\n");

        Assert.True(result.Succeeded);

        var written = Serializer.Read<NetworkRulesAsset>(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal("vehicle", written.Name);
    }

    /// <summary>
    ///     ⚠ A claim rule nobody can ever exercise is an error, not a warning.
    /// </summary>
    /// <remarks>
    ///     <c>claim: WhenUnowned</c> constrains clients taking things from each other, so it decides
    ///     nothing at all when <c>changeOwner</c> admits no client. An author who wrote both lines
    ///     meant the first to do something, and at run time the symptom is a pick-up that never
    ///     happens beside a policy file that reads exactly right.
    /// </remarks>
    [Fact]
    public async Task AClaimRuleNoClientCanReachIsRefused() {
        var (_, result) = await Import(
            "unreachable.vxnetrules",
            """
            name: Unreachable
            rules:
              changeOwner: ServerOnly
              claim: WhenUnowned
            """
        );

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            message => message.Severity == ImportSeverity.Error
                && message.Message.Contains("decides nothing", StringComparison.Ordinal)
        );
    }

    /// <summary>The policy that is legal, loads, and gives up server authority entirely.</summary>
    /// <remarks>
    ///     ⚠ <b>A warning and not an error, because a trusted prototype is a real reason to want
    ///     it</b> — and doc 16's whole argument for a policy file is that relaxing authority should
    ///     be a decision somebody wrote down rather than one they inherited from an example.
    /// </remarks>
    [Fact]
    public async Task APolicyThatLetsAnyClientWriteIsWarnedAbout() {
        var (_, result) = await Import("trusting.vxnetrules", "name: Trusting\nrules:\n  write: Everyone\n");

        Assert.True(result.Succeeded);
        Assert.Contains(result.Diagnostics, message => message.Message.Contains("server authority", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, message => message.Severity == ImportSeverity.Error);
    }

    /// <summary>
    ///     ⚠ A misspelled field is said out loud, because the alternative is a policy that reads right
    ///     and is not.
    /// </summary>
    /// <remarks>
    ///     <b><c>YamlSerializer</c> ignores an unknown key unless the caller asks</b> — deliberately,
    ///     and no importer in the tree asked. So <c>onOwnerDisconect</c> bound to nothing, the file
    ///     imported clean, and the object stayed on <c>TransferToServer</c>: a game rule that does not
    ///     work with no diagnostic anywhere. The value here is <c>Destroy</c> and the assertion is
    ///     that the record does <i>not</i> carry it, so the test names the consequence rather than the
    ///     message.
    /// </remarks>
    [Fact]
    public async Task AMisspelledFieldIsWarnedAboutRatherThanDroppedInSilence() {
        var (_, result) = await Import(
            "typo.vxnetrules",
            """
            name: Typo
            rules:
              onOwnerDisconect: Destroy
            """
        );

        Assert.True(result.Succeeded);

        var written = Serializer.Read<NetworkRulesAsset>(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(DisconnectBehaviour.TransferToServer, written.Rules.OnOwnerDisconnect);
        Assert.Contains(
            result.Diagnostics,
            message => message.Severity == ImportSeverity.Warning
                && message.Message.Contains("onOwnerDisconect", StringComparison.Ordinal)
        );
    }

    /// <summary>And a file with nothing misspelled in it says nothing, so the warning means something.</summary>
    [Fact]
    public async Task AWellSpelledPolicyWarnsAboutNoField() {
        var (_, result) = await Import(
            "clean.vxnetrules",
            """
            name: Clean
            rules:
              changeOwner: Everyone
              claim: WhenUnowned
            """
        );

        Assert.True(result.Succeeded);
        Assert.Empty(result.Diagnostics);
    }

    /// <summary>
    ///     ⚠ A value that is not a member of its enum is an error, which the guide's examples were not.
    /// </summary>
    /// <remarks>
    ///     <c>Enum.Parse</c> throws and <c>BindScalar</c> rewraps it as a <c>YamlBindingException</c>,
    ///     so this half was never silent — but the shipped guide offered <c>claim: Never</c> and
    ///     <c>onOwnerDisconnect: ReleaseToUnowned</c>, neither of which exists, so a reader copying an
    ///     example got a file the importer refuses. Both are corrected; this is what holds the
    ///     behaviour they were corrected against.
    /// </remarks>
    [Fact]
    public async Task AValueThatIsNotAMemberOfItsEnumIsRefused() {
        var (_, result) = await Import("invented.vxnetrules", "name: Invented\nrules:\n  claim: Never\n");

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            message => message.Severity == ImportSeverity.Error
                && message.Message.Contains("OwnershipClaim", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task BrokenYamlIsReportedRatherThanThrown() {
        var (_, result) = await Import("broken.vxnetrules", "name: [unclosed\n");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, message => message.Severity == ImportSeverity.Error);
    }

    static async Task<(ImportContext Context, ImportResult Result)> Import(string name, string text) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();

        files.Seed(path, text);

        var importer = new NetworkRulesImporter();
        var context = new ImportContext(
            AssetId.New(),
            path,
            importer.CreateSettings(),
            files,
            importer.Name,
            "Windows"
        );

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
