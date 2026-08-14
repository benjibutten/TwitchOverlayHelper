using TwitchOverlayHelper.Bot;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// Collects what the bot wrote, without a socket. The sender is asynchronous on purpose – it spaces
/// messages out to stay inside Twitch's allowance – so the tests wait for a line to arrive rather
/// than assuming it already has.
/// </summary>
internal sealed class ChatSpy
{
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();

    public Task Send(string text, CancellationToken token)
    {
        lock (_gate) _lines.Add(text);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return _lines.ToArray(); }
    }

    /// <summary>Waits for the queue to have written this many lines, or gives up and says what it saw.</summary>
    public async Task<IReadOnlyList<string>> WaitFor(int count, int millisecondsTimeout = 3000)
    {
        for (int waited = 0; waited < millisecondsTimeout; waited += 20)
        {
            if (Lines.Count >= count) return Lines;
            await Task.Delay(20);
        }
        return Lines;
    }

    /// <summary>Gives the queue a moment to write something it should not have written.</summary>
    public async Task<IReadOnlyList<string>> Settle()
    {
        await Task.Delay(120);
        return Lines;
    }
}

public sealed class BotTemplateTests
{
    private static BotSettings Settings(Action<BotSettings>? tweak = null)
    {
        var settings = new BotSettings();
        settings.Normalize();
        tweak?.Invoke(settings);
        return settings;
    }

    [Fact]
    public void Fills_in_the_values_it_is_given()
    {
        BotSettings settings = Settings();
        string line = BotTemplate.Render("@{viewer} fick tillbaka {cost} poäng.", settings,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["viewer"] = "Kajsa", ["cost"] = "500" });

        Assert.Equal("@Kajsa fick tillbaka 500 poäng.", line);
    }

    [Fact]
    public void Uses_the_channels_own_word_for_its_pets()
    {
        BotSettings settings = Settings(bot =>
        {
            bot.PetWord = "on screen-vän";
            bot.PetWordPlural = "on screen-vänner";
            bot.PetWordDefinite = "on screen-vännen";
        });

        Assert.Equal(
            "on screen-vännen gick hem, och alla on screen-vänner är avstängda.",
            BotTemplate.Render("{peten} gick hem, och alla {pets} är avstängda.", settings, null));
    }

    [Fact]
    public void Says_streamern_until_a_name_is_given()
    {
        Assert.Equal("väntar på streamern", BotTemplate.Render("väntar på {streamer}", Settings(), null));
        Assert.Equal("väntar på Benji", BotTemplate.Render("väntar på {streamer}", Settings(bot => bot.StreamerName = "Benji"), null));
    }

    /// <summary>
    /// A misspelled placeholder stays visible rather than vanishing. The settings window previews
    /// every template, so a mistake that shows up there is one the streamer fixes before chat sees it
    /// – whereas a silently dropped word would only be noticed as a sentence that reads oddly.
    /// </summary>
    [Fact]
    public void Leaves_an_unknown_placeholder_standing()
    {
        Assert.Equal("hej {viewrs}", BotTemplate.Render("hej {viewrs}", Settings(), null));
    }

    [Fact]
    public void Reasons_the_app_knows_are_reworded_with_the_channels_words()
    {
        BotSettings settings = Settings(bot => bot.PetWordDefinite = "on screen-vännen");

        Assert.Equal("on screen-vännen kom aldrig upp på skärmen",
            BotTemplate.Reason("overlayen ritade aldrig peten", "annars", settings));
    }

    /// <summary>
    /// The reason on a failed reading is whatever the synthesis threw – an HTTP status, a quota
    /// message, the word "Unauthorized". None of that is the channel's business, so anything the app
    /// does not recognise becomes the caller's own wording instead of being passed through.
    /// </summary>
    [Fact]
    public void An_unrecognised_reason_never_reaches_chat()
    {
        Assert.Equal("det gick inte den här gången",
            BotTemplate.Reason("ElevenLabs svarade 401: Unauthorized (api key)", "det gick inte den här gången", Settings()));
    }

    [Fact]
    public void A_half_filled_template_does_not_leave_double_spaces()
    {
        Assert.Equal("@Kajsa fick tillbaka poäng.",
            BotTemplate.Render("@{viewer} fick tillbaka {cost} poäng.", Settings(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["viewer"] = "Kajsa", ["cost"] = "" }));
    }
}

public sealed class BotSettingsTests
{
    /// <summary>Every flow but the placeholder for the ones this version does not have.</summary>
    private static IEnumerable<BotFlow> RealFlows =>
        Enum.GetValues<BotFlow>().Where(flow => flow != BotFlow.Unknown);

    [Fact]
    public void Normalize_gives_every_flow_a_rule()
    {
        var settings = new BotSettings();
        settings.Normalize();

        foreach (BotFlow flow in RealFlows)
            Assert.Contains(settings.Messages, rule => rule.Flow == flow);
    }

    /// <summary>
    /// Unknown is the one flow that must never get a rule: it is what an unrecognised name from the
    /// settings file becomes, and a default for it would put a message in the list that nothing
    /// raises and that the window would offer to edit.
    /// </summary>
    [Fact]
    public void The_placeholder_flow_never_becomes_a_message()
    {
        var settings = new BotSettings { Messages = [new BotMessageRule { Flow = BotFlow.Unknown, Template = "spöke" }] };
        settings.Normalize();

        Assert.DoesNotContain(settings.Messages, rule => rule.Flow == BotFlow.Unknown);
        Assert.DoesNotContain(BotSettings.Defaults, rule => rule.Flow == BotFlow.Unknown);
    }

    [Fact]
    public void Normalize_keeps_what_was_saved_and_drops_nothing_else()
    {
        var settings = new BotSettings
        {
            Messages = [new BotMessageRule { Flow = BotFlow.Welcome, Enabled = false, Template = "Hej {viewer}!" }]
        };
        settings.Normalize();

        BotMessageRule welcome = settings.Rule(BotFlow.Welcome);
        Assert.False(welcome.Enabled);
        Assert.Equal("Hej {viewer}!", welcome.Template);
        Assert.Equal(RealFlows.Count(), settings.Messages.Count);
    }

    [Fact]
    public void An_emptied_template_falls_back_to_the_shipped_wording()
    {
        var settings = new BotSettings
        {
            Messages = [new BotMessageRule { Flow = BotFlow.Welcome, Enabled = true, Template = "   " }]
        };
        settings.Normalize();

        Assert.Equal(BotSettings.Defaults.First(rule => rule.Flow == BotFlow.Welcome).Template,
            settings.Rule(BotFlow.Welcome).Template);
    }

    [Fact]
    public void Commands_come_out_usable_however_they_were_typed()
    {
        var settings = new BotSettings
        {
            Commands =
            [
                new BotCommand { Command = "vänner", Response = "Hej" },
                new BotCommand { Command = "  !läsupp nu ", Response = "Hej" }
            ]
        };
        settings.Normalize();

        Assert.Equal("!vänner", settings.Commands[0].Command);
        Assert.Equal("!läsupp", settings.Commands[1].Command);
    }

    [Fact]
    public void Numbers_are_clamped_to_what_Twitch_and_reason_allow()
    {
        var settings = new BotSettings { MessagesPer30Seconds = 5000, TtsWaitingSeconds = 1, RefundBatchThreshold = 0 };
        settings.Normalize();

        Assert.Equal(90, settings.MessagesPer30Seconds);
        Assert.Equal(10, settings.TtsWaitingSeconds);
        Assert.Equal(2, settings.RefundBatchThreshold);
    }

    [Fact]
    public void Speaks_needs_both_the_bot_and_the_flow_to_be_on()
    {
        var settings = new BotSettings { Mode = BotMode.Off };
        settings.Normalize();
        Assert.False(settings.Speaks(BotFlow.Welcome));

        settings.Mode = BotMode.Streamer;
        Assert.True(settings.Speaks(BotFlow.Welcome));

        settings.Rule(BotFlow.Welcome).Enabled = false;
        Assert.False(settings.Speaks(BotFlow.Welcome));
    }
}

public sealed class BotRateLimiterTests
{
    [Fact]
    public void Lets_the_allowance_through_and_then_holds_the_rest()
    {
        var limiter = new BotRateLimiter();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(TimeSpan.Zero, limiter.TimeUntilSlot(3, now));
            limiter.Record(now);
        }

        Assert.True(limiter.TimeUntilSlot(3, now) > TimeSpan.Zero);
    }

    [Fact]
    public void The_window_rolls_rather_than_resets()
    {
        var limiter = new BotRateLimiter();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        limiter.Record(start);
        limiter.Record(start);

        Assert.True(limiter.TimeUntilSlot(2, start) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, limiter.TimeUntilSlot(2, start + TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void The_same_line_twice_inside_the_window_is_a_duplicate()
    {
        var guard = new BotDuplicateGuard();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.False(guard.IsDuplicate("hej", now));
        guard.Record("hej", now);
        Assert.True(guard.IsDuplicate("hej", now + TimeSpan.FromSeconds(5)));
        Assert.False(guard.IsDuplicate("hej", now + TimeSpan.FromSeconds(31)));
        Assert.False(guard.IsDuplicate("hej då", now));
    }
}

public sealed class BotSenderTests
{
    [Fact]
    public async Task Writes_what_it_is_given()
    {
        var spy = new ChatSpy();
        await using var sender = new BotSender(spy.Send, () => 20);

        sender.Enqueue("hej");
        Assert.Equal(["hej"], await spy.WaitFor(1));
    }

    /// <summary>
    /// Twitch drops a repeat of the same line within thirty seconds without a word, so a bot that
    /// sent one would look like it had stopped working. It is skipped here instead, where it can be
    /// written down.
    /// </summary>
    [Fact]
    public async Task Skips_a_repeat_Twitch_would_have_swallowed()
    {
        var spy = new ChatSpy();
        await using var sender = new BotSender(spy.Send, () => 20);

        sender.Enqueue("samma rad");
        await spy.WaitFor(1);
        sender.Enqueue("samma rad");

        Assert.Single(await spy.Settle());
    }

    [Fact]
    public async Task A_send_that_fails_does_not_stop_the_queue()
    {
        var spy = new ChatSpy();
        bool first = true;
        await using var sender = new BotSender(
            (text, token) =>
            {
                if (first) { first = false; return Task.FromException(new InvalidOperationException("inte ansluten")); }
                return spy.Send(text, token);
            },
            () => 20);

        sender.Enqueue("den som försvinner");
        sender.Enqueue("den som kommer fram");

        Assert.Equal(["den som kommer fram"], await spy.WaitFor(1));
    }
}


/// <summary>
/// The bot, the queue it writes through and a chat that only remembers – built and torn down as one,
/// because the queue is a running task and a test that left it behind would have the next test's
/// lines arriving in its own list.
/// </summary>
internal sealed class BotHarness : IAsyncDisposable
{
    private readonly BotSender _sender;

    public BotHarness(
        Action<BotSettings>? tweak = null,
        Func<IReadOnlyList<TtsEntry>>? readings = null,
        string botLogin = "kanalbotten")
    {
        Settings = new AppSettings();
        Settings.Normalize();
        Settings.Bot.Mode = BotMode.Bot;
        // The holding window exists for the sweep after a restart; the tests that are not about it
        // want their line now rather than four seconds from now. Set past Normalize on purpose.
        Settings.Bot.RefundBatchWindowSeconds = 0;
        tweak?.Invoke(Settings.Bot);

        _sender = new BotSender(Chat.Send, () => Settings.Bot.MessagesPer30Seconds);
        Bot = new BotService(Settings, _sender, new BotContext(readings ?? (() => []), () => botLogin));
    }

    public AppSettings Settings { get; }

    public BotService Bot { get; }

    public ChatSpy Chat { get; } = new();

    public async ValueTask DisposeAsync()
    {
        Bot.Dispose();
        await _sender.DisposeAsync();
    }
}

public sealed class BotServiceTests
{
    private static ChatMessage Message(string text, string login = "kajsa", string name = "Kajsa", bool first = false) =>
        new(Guid.NewGuid().ToString("N"), name, text, null, [], first, false, DateTimeOffset.Now)
        {
            UserLogin = login,
            UserId = "42"
        };

    [Fact]
    public async Task Says_nothing_at_all_while_it_is_switched_off()
    {
        await using var app = new BotHarness(bot => bot.Mode = BotMode.Off);

        app.Bot.OnRedemptionAnswered(new RedemptionNotice(true, "Kajsa", 500, "appen var inte igång"));
        app.Bot.Tick();
        app.Bot.OnChatMessage(Message("hej", first: true));

        Assert.Empty(await app.Chat.Settle());
        Assert.False(app.Settings.Bot.IsActive);
    }

    [Fact]
    public async Task Tells_the_viewer_their_points_came_back_and_why()
    {
        await using var app = new BotHarness();

        app.Bot.OnRedemptionAnswered(new RedemptionNotice(true, "Kajsa", 500, "overlayen ritade aldrig peten"));
        app.Bot.Tick();

        IReadOnlyList<string> lines = await app.Chat.WaitFor(1);
        Assert.Single(lines);
        Assert.Contains("Kajsa", lines[0]);
        Assert.Contains("500", lines[0]);
        Assert.Contains("kom aldrig upp på skärmen", lines[0]);
    }

    /// <summary>
    /// The sweep after a restart answers everything left over from last time, which can be a whole
    /// stream's redemptions inside a second. One line about all of them, rather than the bot's
    /// loudest minute of the day about something nobody is waiting on.
    /// </summary>
    [Fact]
    public async Task Several_refunds_at_once_become_one_line()
    {
        await using var app = new BotHarness(bot => bot.RefundBatchThreshold = 3);

        foreach (string viewer in new[] { "Kajsa", "Ove", "Pia", "Nils" })
            app.Bot.OnRedemptionAnswered(new RedemptionNotice(true, viewer, 100, "appen var inte igång"));
        app.Bot.Tick();

        IReadOnlyList<string> lines = await app.Chat.WaitFor(1);
        Assert.Single(lines);
        Assert.Contains("4", lines[0]);
        Assert.DoesNotContain("Kajsa", lines[0]);
    }

    [Fact]
    public async Task Two_refunds_are_still_two_answers()
    {
        await using var app = new BotHarness(bot => bot.RefundBatchThreshold = 3);

        app.Bot.OnRedemptionAnswered(new RedemptionNotice(true, "Kajsa", 100, "det var fullt på gräsmattan"));
        app.Bot.OnRedemptionAnswered(new RedemptionNotice(true, "Ove", 100, "det var fullt på gräsmattan"));
        app.Bot.Tick();

        IReadOnlyList<string> lines = await app.Chat.WaitFor(2);
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, line => line.Contains("Kajsa"));
        Assert.Contains(lines, line => line.Contains("Ove"));
    }

    [Fact]
    public async Task Nudges_a_reading_nobody_has_answered_and_does_it_once()
    {
        TtsEntry waiting = new("r1", "Kajsa", "läs det här", 500, "reward", "pending", true,
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(), null);
        await using var app = new BotHarness(readings: () => [waiting]);

        app.Bot.Tick();
        app.Bot.Tick();
        app.Bot.Tick();

        IReadOnlyList<string> lines = await app.Chat.WaitFor(1);
        Assert.Single(lines);
        Assert.Contains("Kajsa", lines[0]);
    }

    [Fact]
    public async Task Leaves_a_reading_alone_until_the_wait_is_up()
    {
        TtsEntry fresh = new("r1", "Kajsa", "läs det här", 500, "reward", "pending", true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
        await using var app = new BotHarness(bot => bot.TtsWaitingSeconds = 600, readings: () => [fresh]);

        app.Bot.Tick();

        Assert.Empty(await app.Chat.Settle());
    }

    /// <summary>
    /// A reading that failed is not announced as a refund unless the points actually went back, and
    /// the reading queue is not what knows that. It hands the verdict to the ledger, which keeps
    /// trying until Twitch takes it and can still give up – so a refund announced when the reading
    /// ended would be a promise made before the call was even attempted, and would stand after it
    /// had failed for good.
    /// </summary>
    [Fact]
    public async Task A_refundable_ending_says_nothing_until_the_ledger_says_so()
    {
        await using var app = new BotHarness();
        var request = new TtsRequest("r1", TtsSource.Reward, "reward-1", Refundable: true, "42", "Kajsa", "hej", 500, DateTimeOffset.Now);

        app.Bot.OnReadingFinished(request, TtsState.Expired, "ingen hann svara");
        Assert.Empty(await app.Chat.Settle());

        // What the ledger raises once Twitch has taken the refund.
        app.Bot.OnRedemptionAnswered(new RedemptionNotice(true, "Kajsa", 500, "ingen hann svara", "tts"));
        app.Bot.Tick();

        IReadOnlyList<string> lines = await app.Chat.WaitFor(1);
        Assert.Single(lines);
        Assert.Contains("ingen hann svara i tid", lines[0]);
        Assert.Contains("500", lines[0]);
    }

    /// <summary>
    /// The other half of the same rule: nothing went back, so there is nothing to say. Both paths
    /// staying quiet is what stops the viewer being told twice – which the sender's duplicate guard
    /// was hiding rather than preventing.
    /// </summary>
    [Fact]
    public async Task A_reading_that_could_never_pay_back_is_not_announced_as_a_refund()
    {
        await using var app = new BotHarness();
        var request = new TtsRequest("r1", TtsSource.Reward, "reward-1", Refundable: false, "42", "Kajsa", "hej", 500, DateTimeOffset.Now);

        app.Bot.OnReadingFinished(request, TtsState.Expired, "ingen hann svara");

        Assert.Empty(await app.Chat.Settle());
    }

    [Fact]
    public async Task A_full_queue_is_its_own_answer_rather_than_a_refund()
    {
        await using var app = new BotHarness();
        var request = new TtsRequest("r1", TtsSource.Reward, "reward-1", true, "42", "Kajsa", "hej", 500, DateTimeOffset.Now);

        app.Bot.OnReadingFinished(request, TtsState.Rejected, TtsService.QueueFullReason);

        IReadOnlyList<string> lines = await app.Chat.WaitFor(1);
        Assert.Single(lines);
        Assert.Contains("full", lines[0]);
        Assert.DoesNotContain("tillbaka", lines[0]);
    }

    [Fact]
    public async Task Welcomes_a_first_time_chatter()
    {
        await using var app = new BotHarness();

        app.Bot.OnChatMessage(Message("hej allihop", first: true));

        IReadOnlyList<string> lines = await app.Chat.WaitFor(1);
        Assert.Contains("Kajsa", lines[0]);
    }

    /// <summary>
    /// The bot's own greeting is a first message from an account too. Answering it would have the
    /// channel watch a bot welcome itself for as long as the stream lasted.
    /// </summary>
    [Fact]
    public async Task Never_answers_its_own_lines()
    {
        await using var app = new BotHarness();
        ChatMessage own = Message("Välkommen till chatten!", login: "kanalbotten", name: "Kanalbotten", first: true);

        Assert.True(app.Bot.IsOwnMessage(own));
        app.Bot.OnChatMessage(own);

        Assert.Empty(await app.Chat.Settle());
    }

    [Fact]
    public async Task Answers_a_command_the_streamer_wrote()
    {
        await using var app = new BotHarness(bot => bot.Commands.Add(
            new BotCommand { Command = "!discord", Response = "Häng med i vår Discord: exempel.se/d" }));

        app.Bot.OnChatMessage(Message("!discord"));

        Assert.Equal(["Häng med i vår Discord: exempel.se/d"], await app.Chat.WaitFor(1));
    }

    [Fact]
    public async Task A_command_answer_is_a_template_like_every_other()
    {
        await using var app = new BotHarness(bot =>
        {
            bot.StreamerName = "Benji";
            bot.PetWordPlural = "on screen-vänner";
            bot.Commands.Add(new BotCommand { Command = "!info", Response = "{viewer}: {streamer} har {pets} på skärmen." });
        });

        app.Bot.OnChatMessage(Message("!info"));

        Assert.Equal(["Kajsa: Benji har on screen-vänner på skärmen."], await app.Chat.WaitFor(1));
    }

    [Fact]
    public async Task A_word_nobody_configured_is_left_alone()
    {
        await using var app = new BotHarness(bot => bot.Commands.Add(
            new BotCommand { Command = "!discord", Response = "Häng med!" }));

        app.Bot.OnChatMessage(Message("!nånting"));
        app.Bot.OnChatMessage(Message("discord"));

        Assert.Empty(await app.Chat.Settle());
    }

    [Fact]
    public async Task A_command_switched_off_or_half_written_never_answers()
    {
        await using var app = new BotHarness(bot =>
        {
            bot.Commands.Add(new BotCommand { Command = "!av", Response = "Hej", Enabled = false });
            bot.Commands.Add(new BotCommand { Command = "!tomt", Response = "" });
        });

        app.Bot.OnChatMessage(Message("!av"));
        app.Bot.OnChatMessage(Message("!tomt"));

        Assert.Empty(await app.Chat.Settle());
    }

    /// <summary>
    /// A command is the one thing here viewers can set off on purpose, so it is also the one way a
    /// room could spend the bot's whole write allowance by agreeing to type the same word.
    /// </summary>
    [Fact]
    public async Task A_command_keeps_its_cooldown()
    {
        await using var app = new BotHarness(bot => bot.Commands.Add(
            new BotCommand { Command = "!discord", Response = "Häng med!", CooldownSeconds = 300 }));

        app.Bot.OnChatMessage(Message("!discord"));
        app.Bot.OnChatMessage(Message("!discord", login: "ove", name: "Ove"));

        Assert.Single(await app.Chat.WaitFor(1));
        Assert.Single(await app.Chat.Settle());
    }

    [Fact]
    public async Task A_zero_cooldown_answers_every_time()
    {
        await using var app = new BotHarness(bot => bot.Commands.Add(
            new BotCommand { Command = "!vem", Response = "Hej {viewer}!", CooldownSeconds = 0 }));

        app.Bot.OnChatMessage(Message("!vem"));
        app.Bot.OnChatMessage(Message("!vem", login: "ove", name: "Ove"));

        Assert.Equal(2, (await app.Chat.WaitFor(2)).Count);
    }

    /// <summary>
    /// Answered with silence rather than with a refusal: a viewer who was never told the command
    /// exists does not need telling off for trying it, and a bot that announces every refusal is one
    /// chat learns to set off on purpose.
    /// </summary>
    [Fact]
    public async Task A_moderators_only_command_says_nothing_to_anybody_else()
    {
        await using var app = new BotHarness(bot => bot.Commands.Add(
            new BotCommand { Command = "!kö", Response = "Kön är öppen.", ModeratorsOnly = true, CooldownSeconds = 0 }));

        app.Bot.OnChatMessage(Message("!kö"));
        Assert.Empty(await app.Chat.Settle());

        ChatMessage fromMod = Message("!kö", login: "ove", name: "Ove") with
        {
            Badges = [new ChatBadge("moderator", "1")]
        };
        app.Bot.OnChatMessage(fromMod);

        Assert.Single(await app.Chat.WaitFor(1));
    }

    /// <summary>
    /// Someone whose very first line is a command is asking a question, not saying hello. Two lines
    /// at a stranger is one too many.
    /// </summary>
    [Fact]
    public async Task A_command_does_not_also_earn_a_welcome()
    {
        await using var app = new BotHarness(bot => bot.Commands.Add(
            new BotCommand { Command = "!discord", Response = "Häng med!" }));

        app.Bot.OnChatMessage(Message("!discord", first: true));

        Assert.Single(await app.Chat.WaitFor(1));
        Assert.DoesNotContain("Välkommen", (await app.Chat.Settle())[0]);
    }

    [Fact]
    public async Task The_cooldown_keeps_a_channel_wide_line_from_repeating()
    {
        await using var app = new BotHarness();

        app.Bot.OnPetOutcome("Kajsa", 100, PetSpawnOutcome.Full);
        app.Bot.OnPetOutcome("Ove", 100, PetSpawnOutcome.Full);

        Assert.Single(await app.Chat.WaitFor(1));
        Assert.Single(await app.Chat.Settle());
    }

    /// <summary>
    /// A scene change in OBS drops every lawn for a second or two. Announcing that would be
    /// announcing the streamer's scene changes to the channel.
    /// </summary>
    [Fact]
    public async Task A_brief_overlay_drop_is_not_worth_mentioning()
    {
        await using var app = new BotHarness();

        app.Bot.OnPetOverlayCountChanged(0);
        app.Bot.Tick();
        app.Bot.OnPetOverlayCountChanged(1);
        app.Bot.Tick();

        Assert.Empty(await app.Chat.Settle());
    }

    [Fact]
    public async Task Thanks_a_raid_with_a_link_to_the_channel_that_sent_it()
    {
        await using var app = new BotHarness();

        app.Bot.OnChatEvent(new ChatEvent(ChatEventType.Raid, "e1", "Ove", DateTimeOffset.Now)
        {
            UserLogin = "ove",
            ViewerCount = 37
        });

        IReadOnlyList<string> lines = await app.Chat.WaitFor(1);
        Assert.Contains("Ove", lines[0]);
        Assert.Contains("twitch.tv/ove", lines[0]);
    }

    [Fact]
    public async Task Tells_a_moderator_why_their_call_did_nothing()
    {
        await using var app = new BotHarness();

        app.Bot.OnModCallMissed(Message("!psst"), alertDisabled: false);

        IReadOnlyList<string> lines = await app.Chat.WaitFor(1);
        Assert.Contains("moderatorer", lines[0]);
    }

    [Fact]
    public async Task Reset_drops_everything_that_was_about_to_be_said()
    {
        await using var app = new BotHarness(bot => bot.RefundBatchWindowSeconds = 30);

        app.Bot.OnRedemptionAnswered(new RedemptionNotice(true, "Kajsa", 500, "appen var inte igång"));
        app.Bot.Reset();
        app.Bot.Tick();

        Assert.Empty(await app.Chat.Settle());
    }
}

/// <summary>
/// The six things a review found once the feature worked: each of them a way the bot could say
/// something that was not true, say it twice, or say it in a channel nobody was in.
/// </summary>
public sealed class BotRegressionTests
{
    private static ChatMessage Message(string text, string login = "kajsa", string name = "Kajsa") =>
        new(Guid.NewGuid().ToString("N"), name, text, null, [], false, false, DateTimeOffset.Now)
        {
            UserLogin = login,
            UserId = "42"
        };

    /// <summary>
    /// Two rows answering the same word means only the first can ever fire, so the second is dropped
    /// rather than left in the list looking as though it works. A row still being filled in has no
    /// word yet and must survive – otherwise adding a command would delete it again.
    /// </summary>
    [Fact]
    public void A_command_word_claimed_twice_keeps_only_the_first()
    {
        var settings = new BotSettings
        {
            Commands =
            [
                new BotCommand { Command = "!discord", Response = "först" },
                new BotCommand { Command = "discord", Response = "sedan" },
                new BotCommand { Command = "", Response = "en rad som inte är ifylld än" }
            ]
        };

        settings.Normalize();

        Assert.Equal(2, settings.Commands.Count);
        Assert.Equal("!discord", settings.Commands[0].Command);
        Assert.Equal("först", settings.Commands[0].Response);
        Assert.Equal(string.Empty, settings.Commands[1].Command);
    }

    /// <summary>
    /// A flow name this version has never heard of – one an earlier version wrote and this one
    /// dropped. The stock string converter throws on it, and that exception reaches SettingsStore,
    /// which answers by handing back a blank AppSettings. Upgrading past a removed flow would
    /// silently reset the overlay, the channel and everything else in the file.
    /// </summary>
    [Fact]
    public void A_settings_file_naming_a_flow_this_version_dropped_still_loads()
    {
        // Property names as SettingsStore actually writes them – its serializer has no naming policy,
        // so the file on disk is PascalCase and reading it back is case-sensitive.
        const string json = """
        {
          "Bot": {
            "Mode": "Bot",
            "StreamerName": "Benji",
            "Messages": [
              { "Flow": "CommandPets", "Enabled": true, "Template": "På skärmen: {list}" },
              { "Flow": "Welcome", "Enabled": false, "Template": "Tjena {viewer}" }
            ]
          },
          "Channel": "benjibutten"
        }
        """;

        AppSettings settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        settings.Normalize();

        // Nothing was lost on the way past the unknown name.
        Assert.Equal("benjibutten", settings.Channel);
        Assert.Equal("Benji", settings.Bot.StreamerName);
        Assert.Equal(BotMode.Bot, settings.Bot.Mode);
        // The dropped flow is gone, and the flow beside it kept what it said.
        Assert.DoesNotContain(settings.Bot.Messages, rule => rule.Flow == BotFlow.Unknown);
        Assert.Equal("Tjena {viewer}", settings.Bot.Rule(BotFlow.Welcome).Template);
        Assert.False(settings.Bot.Rule(BotFlow.Welcome).Enabled);
    }

    /// <summary>
    /// A send that never reached Twitch is not one Twitch will swallow as a repeat. Writing it down
    /// as said would block the same perfectly good sentence for the next thirty seconds – exactly
    /// when the bot has just reconnected and could finally deliver it.
    /// </summary>
    [Fact]
    public async Task A_failed_send_does_not_block_the_same_line_from_being_tried_again()
    {
        var spy = new ChatSpy();
        bool failing = true;
        await using var sender = new BotSender(
            (text, token) => failing
                ? Task.FromException(new InvalidOperationException("Chatten är inte inloggad"))
                : spy.Send(text, token),
            () => 20);

        sender.Enqueue("@Kajsa fick tillbaka 500 poäng.");
        await Task.Delay(150);
        Assert.Empty(spy.Lines);

        failing = false;
        sender.Enqueue("@Kajsa fick tillbaka 500 poäng.");

        Assert.Single(await spy.WaitFor(1));
    }

    /// <summary>
    /// Twitch counts duplicates per channel, so a line already said in the channel we have left is a
    /// new line in the one we are joining.
    /// </summary>
    [Fact]
    public async Task What_was_said_in_the_old_channel_is_forgotten_with_it()
    {
        var spy = new ChatSpy();
        await using var sender = new BotSender(spy.Send, () => 20);

        sender.Enqueue("Välkommen till chatten!");
        await spy.WaitFor(1);

        sender.Enqueue("Välkommen till chatten!");
        Assert.Single(await spy.Settle());

        sender.Clear();
        sender.Enqueue("Välkommen till chatten!");

        Assert.Equal(2, (await spy.WaitFor(2)).Count);
    }

    /// <summary>
    /// settings.json is a file a person can open. A null that reached a dereference in Normalize
    /// would take the app down at startup – SettingsStore only catches malformed JSON – with no way
    /// out except finding and editing the file by hand.
    /// </summary>
    [Fact]
    public void A_hand_edited_settings_file_full_of_nulls_still_loads()
    {
        var settings = new BotSettings
        {
            StreamerName = null!,
            PetWord = null!,
            PetWordPlural = null!,
            PetWordDefinite = null!,
            Messages = [null!, new BotMessageRule { Flow = BotFlow.Welcome, Enabled = true, Template = null! }],
            Commands = [null!, new BotCommand { Command = null!, Response = null! }]
        };

        settings.Normalize();

        Assert.Single(settings.Commands);
        Assert.Equal(string.Empty, settings.Commands[0].Command);
        Assert.Equal("pet", settings.PetWord);
        Assert.Equal(string.Empty, settings.StreamerName);
        Assert.Equal(BotSettings.Defaults.Count, settings.Messages.Count);
        Assert.Equal(
            BotSettings.Defaults.First(rule => rule.Flow == BotFlow.Welcome).Template,
            settings.Rule(BotFlow.Welcome).Template);
    }

    /// <summary>
    /// Every property that can arrive as null from disk is one AppSettings.Normalize walks into on
    /// the way to the first window being drawn.
    /// </summary>
    [Fact]
    public void The_whole_settings_file_normalizes_with_a_null_bot()
    {
        var settings = new AppSettings { Bot = null! };

        settings.Normalize();

        Assert.NotNull(settings.Bot);
        Assert.Equal(BotMode.Off, settings.Bot.Mode);
    }
}

/// <summary>
/// The bot's own Twitch account, joining and leaving. No socket is opened here – what is under test
/// is which of two overlapping intentions wins, which is decided before anything reaches the wire.
/// </summary>
public sealed class BotAccountTests
{
    /// <summary>
    /// Applying and disconnecting are both asked for from UI handlers that do not wait for them, and
    /// fetching a token is a network round trip in between. Without the generation check a connect
    /// paused in the middle of one comes back and joins a channel the user has already left.
    /// </summary>
    [Fact]
    public async Task A_disconnect_during_a_connect_wins()
    {
        using var http = new System.Net.Http.HttpClient();
        string tokenPath = Path.Combine(Path.GetTempPath(), "twitchoverlayhelper-tests", Guid.NewGuid().ToString("N"));
        await using var account = new BotAccount(http, tokenPath);

        // Not logged in, so ApplyAsync can never connect – what matters is that both calls complete
        // and leave the account settled rather than deadlocked against each other.
        Task apply = account.ApplyAsync("kanalen", wanted: true);
        Task disconnect = account.DisconnectAsync();

        await Task.WhenAll(apply, disconnect).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(account.CanSend);
    }

    [Fact]
    public async Task Applying_without_a_login_never_joins_anonymously()
    {
        using var http = new System.Net.Http.HttpClient();
        string tokenPath = Path.Combine(Path.GetTempPath(), "twitchoverlayhelper-tests", Guid.NewGuid().ToString("N"));
        await using var account = new BotAccount(http, tokenPath);

        await account.ApplyAsync("kanalen", wanted: true).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(account.IsLoggedIn);
        Assert.False(account.CanSend);
    }
}
