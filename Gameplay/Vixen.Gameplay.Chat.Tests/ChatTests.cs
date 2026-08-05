// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Chat.Tests;

/// <summary>Four channels: a spatial one, a guild one, a whisper and a rate-limited trade channel.</summary>
public static class Content {
    public const string Say = "chat/say";
    public const string Guild = "chat/guild";
    public const string Whisper = "chat/whisper";
    public const string Trade = "chat/trade";
    public const string Speak = "Guild.Permission.Speak";

    public static PlayerId Player(ulong who) => new(who);

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag(Speak)
            .Add(
                Say,
                new ChatChannelDefinition {
                    DisplayName = "Say",
                    Command = "/s",
                    Route = ChatRoute.Realm,
                    Audience = ChatAudienceKind.Scene,
                    Radius = 30f,
                    MaximumLength = 16
                }
            )
            .Add(
                Guild,
                new ChatChannelDefinition {
                    DisplayName = "Guild",
                    Command = "/g",
                    Route = ChatRoute.Gate,
                    Audience = ChatAudienceKind.Guild,
                    Permission = Speak
                }
            )
            .Add(
                Whisper,
                new ChatChannelDefinition {
                    DisplayName = "Whisper",
                    Command = "/w",
                    Route = ChatRoute.Gate,
                    Audience = ChatAudienceKind.Direct
                }
            )
            .Add(
                Trade,
                new ChatChannelDefinition {
                    DisplayName = "Trade",
                    Command = "/t",
                    Route = ChatRoute.Gate,
                    Audience = ChatAudienceKind.Global,
                    RateLimit = 2,
                    RateWindow = 10f,
                    Requirements = [new() { Kind = RequirementKind.Value, Subject = "Level", Comparison = RequirementComparison.AtLeast, Value = 10f }]
                }
            )
            .Build();
}

/// <summary>A world of five players who all hear everything, so the tests are about the rules.</summary>
sealed class Everybody : IChatAudience {
    public int Resolve(in ChatMessage message, ICollection<PlayerId> into) {
        if (message.Channel.Audience == ChatAudienceKind.Direct) {
            if (message.Recipient.IsSome) {
                into.Add(message.Recipient);

                return 1;
            }

            return 0;
        }

        for (ulong who = 1; who <= 5; who++) {
            into.Add(Content.Player(who));
        }

        return 5;
    }
}

/// <summary>A context a test drives by hand: a clock, a block list and one level per player.</summary>
sealed class World : IChatContext {
    readonly Dictionary<PlayerId, Subject> subjects = [];
    readonly HashSet<(PlayerId, PlayerId)> blocks = [];

    public float Now { get; set; }

    public bool IsSevered(PlayerId left, PlayerId right) =>
        blocks.Contains((left, right)) || blocks.Contains((right, left));

    public IRequirementContext? ContextOf(PlayerId player) => subjects.GetValueOrDefault(player);

    public void Block(PlayerId who, PlayerId whom) => blocks.Add((who, whom));

    public Subject Give(PlayerId player, float level, GameplayTag permission = default) {
        var subject = new Subject(level);

        if (permission.IsSome) {
            subject.Tags.Add(permission);
        }

        subjects[player] = subject;

        return subject;
    }

    internal sealed class Subject(float level) : IRequirementContext {
        public GameplayTagSet Tags { get; } = new();

        GameplayTagSet? IRequirementContext.Tags => Tags;

        public bool TryGetValue(AttributeId subject, out float value) {
            if (subject == AttributeId.From("Level")) {
                value = level;

                return true;
            }

            value = 0f;

            return false;
        }
    }
}

public class ChatLibraryTests {
    readonly ChatLibrary library = ChatLibrary.Compile(Content.Catalog());

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    [Fact]
    public void AChannelIsFoundByIdAndByCommand() {
        Assert.NotNull(library.Find(DefId.From(Content.Say)));
        Assert.Equal("Guild", library.FindCommand("/g")!.DisplayName);
        Assert.Equal("Guild", library.FindCommand("/G")!.DisplayName);
        Assert.Null(library.FindCommand("/nope"));
    }

    [Fact]
    public void ASpatialChannelWithNoRadiusIsAProblem() {
        var problems = ChatLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add("chat/odd", new ChatChannelDefinition { Audience = ChatAudienceKind.Scene })
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("radius", StringComparison.Ordinal));
    }

    [Fact]
    public void AWhisperRoutedThroughTheRealmIsAProblem() {
        // doc 27 § Chat: the recipient may be on another continent, or offline.
        var problems = ChatLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(
                        "chat/odd",
                        new ChatChannelDefinition { Audience = ChatAudienceKind.Direct, Route = ChatRoute.Realm }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("another shard", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoChannelsAnsweringOneCommandIsAProblem() {
        var problems = ChatLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add("chat/a", new ChatChannelDefinition { Command = "/x", Audience = ChatAudienceKind.Global })
                    .Add("chat/b", new ChatChannelDefinition { Command = "/x", Audience = ChatAudienceKind.Global })
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("'/x'", StringComparison.Ordinal));
    }
}

public class ChatFilterTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly ChatLibrary library;
    readonly World world = new();

    public ChatFilterTests() => library = ChatLibrary.Compile(catalog);

    ChatChannel Channel(string address) => library.Find(DefId.From(address))!;

    ChatDraft Draft(string text, string channel = Content.Say, ulong sender = 1, ulong recipient = 0) =>
        new(Content.Player(sender), Channel(channel), text, default, Content.Player(recipient));

    [Fact]
    public void AnEmptyMessageIsRefused() {
        var verdict = new ChatFilters.Empty().Apply(Draft("   "), world);

        Assert.Equal(ChatRejection.Empty, verdict.Rejection);
    }

    [Fact]
    public void AMessageLongerThanTheChannelTakesIsRefusedRatherThanTruncated() {
        // ⚠ A message cut in half says something its sender did not, and on trade that is a price.
        var draft = Draft(new string('a', 17));
        var verdict = new ChatFilters.Length().Apply(draft, world);

        Assert.Equal(ChatRejection.TooLong, verdict.Rejection);
        Assert.Equal(17, draft.Text.Length);
    }

    [Fact]
    public void ARateLimitCountsPerPlayerAndPerChannel() {
        var limiter = new ChatRateLimiter();
        var filter = new ChatFilters.RateLimit(limiter);

        Assert.True(filter.Apply(Draft("one", Content.Trade), world).IsAllowed);
        Assert.True(filter.Apply(Draft("two", Content.Trade), world).IsAllowed);
        Assert.Equal(ChatRejection.RateLimited, filter.Apply(Draft("three", Content.Trade), world).Rejection);

        // The same player on another channel, and another player on the same one, are both untouched.
        Assert.True(filter.Apply(Draft("hello"), world).IsAllowed);
        Assert.True(filter.Apply(Draft("four", Content.Trade, sender: 2), world).IsAllowed);
    }

    [Fact]
    public void ARefusedMessageDoesNotCountAgainstTheWindow() {
        // ⚠ Charging for the refusal turns a rate limit into a lockout for a client that retries.
        var limiter = new ChatRateLimiter();
        var filter = new ChatFilters.RateLimit(limiter);

        filter.Apply(Draft("one", Content.Trade), world);
        filter.Apply(Draft("two", Content.Trade), world);

        for (var attempt = 0; attempt < 20; attempt++) {
            Assert.Equal(ChatRejection.RateLimited, filter.Apply(Draft("more", Content.Trade), world).Rejection);
        }

        world.Now = 11f;

        Assert.True(filter.Apply(Draft("later", Content.Trade), world).IsAllowed);
    }

    [Fact]
    public void AChannelWithNoLimitIsNeverLimited() {
        var filter = new ChatFilters.RateLimit(new ChatRateLimiter());

        for (var attempt = 0; attempt < 100; attempt++) {
            Assert.True(filter.Apply(Draft("hello"), world).IsAllowed);
        }
    }

    [Fact]
    public void AMuteExpires() {
        var mutes = new MuteList();
        var filter = new ChatFilters.Muted(mutes);

        mutes.Mute(Content.Player(1), 60f);

        Assert.Equal(ChatRejection.Muted, filter.Apply(Draft("hello"), world).Rejection);

        world.Now = 61f;

        Assert.True(filter.Apply(Draft("hello"), world).IsAllowed);
        Assert.True(mutes.Unmute(Content.Player(1)));
        Assert.False(mutes.Unmute(Content.Player(1)));
    }

    [Fact]
    public void ABlockRefusesAWhisperEitherWayRoundAndSaysTheSameThing() {
        // ⚠ Telling the sender "they have blocked you" is how a block stops being invisible.
        var filter = new ChatFilters.Blocked();

        world.Block(Content.Player(2), Content.Player(1));

        var refused = filter.Apply(Draft("hi", Content.Whisper, 1, 2), world);
        var mirrored = filter.Apply(Draft("hi", Content.Whisper, 2, 1), world);

        Assert.Equal(ChatRejection.Blocked, refused.Rejection);
        Assert.Equal(ChatRejection.Blocked, mirrored.Rejection);
        Assert.Equal(refused.Message, mirrored.Message);
    }

    [Fact]
    public void ABlockDoesNotSilenceSomebodyInAZone() {
        var filter = new ChatFilters.Blocked();

        world.Block(Content.Player(2), Content.Player(1));

        Assert.True(filter.Apply(Draft("hello"), world).IsAllowed);
    }

    [Fact]
    public void TheSameMessageTwiceIsRefusedUntilTheWindowPasses() {
        var filter = new ChatFilters.Repeat(30f);

        Assert.True(filter.Apply(Draft("wts sword"), world).IsAllowed);
        Assert.Equal(ChatRejection.Repeated, filter.Apply(Draft("WTS SWORD"), world).Rejection);
        Assert.True(filter.Apply(Draft("wts shield"), world).IsAllowed);

        world.Now = 31f;

        Assert.True(filter.Apply(Draft("wts shield"), world).IsAllowed);
    }

    [Fact]
    public void AWordFilterCensorsAndLetsTheMessageThrough() {
        // ⚠ Rejecting for one word tells the sender which word is on the list.
        var filter = new ChatFilters.Words(["badger"]);
        var draft = Draft("you BADGER, badger");

        Assert.True(filter.Apply(draft, world).IsAllowed);
        Assert.Equal("you ******, ******", draft.Text);
    }

    [Fact]
    public void APipelineStopsAtTheFirstRefusalAndSaysWhichFilterItWas() {
        var pipeline = new ChatPipeline()
            .Add(new ChatFilters.Empty())
            .Add(new ChatFilters.Length())
            .Add(new ChatFilters.Words(["badger"]));

        var draft = Draft(new string('a', 40) + "badger");

        Assert.Equal(ChatRejection.TooLong, pipeline.Apply(draft, world).Rejection);
        Assert.Equal("length", pipeline.LastRefusedBy!.Name);

        // The word filter never ran, so the message is exactly as it was typed.
        Assert.EndsWith("badger", draft.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void APipelineRecordsThatAFilterRewroteTheWords() {
        var pipeline = new ChatPipeline().Add(new ChatFilters.Words(["badger"]));
        var draft = Draft("badger");

        Assert.True(pipeline.Apply(draft, world).IsAllowed);
        Assert.True(draft.WasRewritten);
    }
}

public class ChatRouterTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly ChatLibrary library;
    readonly World world = new();
    readonly ChatPipeline pipeline = new();
    readonly ChatRouter router;

    public ChatRouterTests() {
        library = ChatLibrary.Compile(catalog);
        pipeline.Add(new ChatFilters.Empty()).Add(new ChatFilters.Length()).Add(new ChatFilters.Blocked());
        router = new(library, pipeline, new Everybody());
        world.Give(Content.Player(1), 20f, catalog.Tags.Resolve(Content.Speak));
        world.Give(Content.Player(2), 5f);
    }

    ChatDelivery Say(string channel, string text, ulong sender = 1, ulong recipient = 0) =>
        router.Say(Content.Player(sender), DefId.From(channel), text, world, default, Content.Player(recipient));

    [Fact]
    public void AMessageReachesItsAudienceAndCarriesItsRoute() {
        var delivery = Say(Content.Say, "hello");

        Assert.True(delivery.IsDelivered);
        Assert.Equal(5, delivery.Audience.Count);
        Assert.Equal(ChatRoute.Realm, delivery.Route);
        Assert.Equal(1ul, delivery.Message.Sequence);
    }

    [Fact]
    public void AnUnknownChannelIsRefused() =>
        Assert.Equal(ChatRejection.UnknownChannel, Say("chat/nowhere", "hello").Rejection);

    [Fact]
    public void AChannelPermissionIsCheckedBeforeThePipeline() {
        Assert.True(Say(Content.Guild, "hello").IsDelivered);
        Assert.Equal(ChatRejection.NoPermission, Say(Content.Guild, "hello", sender: 2).Rejection);
    }

    [Fact]
    public void AChannelRequirementIsChecked() {
        Assert.True(Say(Content.Trade, "wts").IsDelivered);
        Assert.Equal(ChatRejection.Requirements, Say(Content.Trade, "wts", sender: 2).Rejection);
    }

    [Fact]
    public void ABlockDropsTheListenerRatherThanTheMessage() {
        // ⚠ The per-recipient half of the block rule, and the reason it is not a rejection.
        world.Block(Content.Player(3), Content.Player(1));

        var delivery = Say(Content.Say, "hello");

        Assert.True(delivery.IsDelivered);
        Assert.Equal(4, delivery.Audience.Count);
        Assert.DoesNotContain(Content.Player(3), delivery.Audience);
    }

    [Fact]
    public void ABlockDropsTheMessageOnAWhisper() {
        world.Block(Content.Player(3), Content.Player(1));

        Assert.Equal(ChatRejection.Blocked, Say(Content.Whisper, "hello", recipient: 3).Rejection);
    }

    [Fact]
    public void AWhisperToNobodyReachesNobody() =>
        Assert.Equal(ChatRejection.NoAudience, Say(Content.Whisper, "hello").Rejection);

    [Fact]
    public void ARefusedMessageDoesNotAdvanceTheSequence() {
        Say(Content.Say, "hello");
        Say(Content.Say, "");
        Say(Content.Say, new string('a', 40));

        Assert.Equal(2ul, Say(Content.Say, "again").Message.Sequence);
        Assert.Equal(2ul, router.Delivered);
    }

    [Fact]
    public void EverySentMessageIsAnnouncedOnce() {
        var seen = new List<ChatDelivery>();

        router.Sent += delivery => seen.Add(delivery);

        Say(Content.Say, "one");
        Say(Content.Say, "");
        Say(Content.Say, "two");

        Assert.Equal(2, seen.Count);
        Assert.Equal("two", seen[1].Message.Text);
    }

    [Fact]
    public void TheSenderStillHearsThemselvesWhenTheyHaveBlockedSomebody() {
        world.Block(Content.Player(1), Content.Player(3));

        var delivery = Say(Content.Say, "hello");

        Assert.Contains(Content.Player(1), delivery.Audience);
        Assert.DoesNotContain(Content.Player(3), delivery.Audience);
    }
}
