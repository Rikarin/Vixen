// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Gameplay;
using Vixen.Samples.Mmo.Rules;
using Xunit;

namespace Vixen.Samples.Mmo.Realms.Tests;

/// <summary>What <c>MmoRealm.Compose</c> does, driven without a shard.</summary>
/// <remarks>
///     ⚠ <b>A realm that could only be composed inside a running shard is a realm nobody tests</b> —
///     which is why <c>Compose</c> is a public method taking the compiled content rather than
///     something <c>OnRealmInitialise</c> does to itself. It is the same argument doc 27 makes for
///     keeping a grain a thin adapter over a plain class.
/// </remarks>
public sealed class RealmTests {
    static MmoLibraries Empty => MmoLibraries.Load([]);

    [Fact]
    public void ComposingBuildsTheBridges() {
        var realm = new MmoRealm();

        realm.Compose(Empty);

        Assert.NotNull(realm.Libraries);
        Assert.Equal(0, realm.Lockouts.Warm);
        Assert.Equal(0, realm.Social.Warm);
    }

    [Fact]
    public void ABridgeReadBeforeComposingSaysWhatIsMissing() {
        // Rather than a NullReferenceException three frames later, in a stack that names nothing.
        var realm = new MmoRealm();

        Assert.Contains("Compose", Assert.Throws<InvalidOperationException>(() => realm.Lockouts).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentThatDoesNotCompileCleanStopsTheShardStarting() {
        // ⚠ Refused rather than logged. A shard that starts with a broken loot table hands out
        // nothing for an evening and is diagnosed from a player complaint.
        var realm = new MmoRealm();
        var broken = MmoLibraries.Load([("chat/leaky", Bytes())]);

        Assert.NotEmpty(broken.Problems);

        var refusal = Assert.Throws<InvalidOperationException>(() => realm.Compose(broken));

        Assert.Contains("will not", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyCatalogStillComposesEveryLibrary() {
        // The composition is independent of the content, which is the property that lets a realm
        // start on a map whose content is still downloading and refuse players rather than crash.
        var libraries = Empty;

        Assert.Empty(libraries.Problems);
        Assert.Equal(21, libraries.Composition.Modules.Count);
        Assert.NotNull(libraries.Collections);
    }

    /// <summary>A whisper routed through the realm, as artefact bytes.</summary>
    /// <remarks>
    ///     ⚠ Deliberately <em>this</em> mistake rather than a dangling address. A loot entry naming an
    ///     item that is not in the catalog is not a content problem any library reports, because a
    ///     <c>DefId</c> is a hash and an id for nothing looks exactly like an id for something — which
    ///     is a real gap and one the sample's own content test cannot close either. A whisper on the
    ///     realm route is a mistake a library <em>can</em> see, because it is about two fields of one
    ///     definition disagreeing rather than about something absent.
    /// </remarks>
    static ReadOnlyMemory<byte> Bytes() =>
        DefinitionSerialization.ToBytes(
            new Vixen.Gameplay.Chat.ChatChannelDefinition {
                DisplayName = "Leaky",
                Command = "leak",
                Audience = Vixen.Gameplay.Chat.ChatAudienceKind.Direct,
                Route = Vixen.Gameplay.Chat.ChatRoute.Realm
            }
        );
}
