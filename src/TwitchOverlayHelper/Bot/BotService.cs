using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;

namespace TwitchOverlayHelper.Bot;

/// <summary>What the bot has to be able to look up that it does not own itself.</summary>
public sealed record BotContext(
    Func<IReadOnlyList<TtsEntry>> ReadingQueue,
    Func<string> BotLogin);

/// <summary>
/// Decides what the bot says and when. Everything that reaches chat goes through here and then
/// through <see cref="BotSender"/>; nothing else in the app writes a bot line.
///
/// <para><b>Why the app needs this at all.</b> The app already knows a great deal that only it knows –
/// that a redemption was paid back and why, that a reading is still waiting for a yes, that the
/// overlay is down and points spent right now would come straight back. Until now all of it went to
/// the streamer's own status line, where the person it concerns cannot see it. The viewer paid, and
/// the viewer is the one left guessing.</para>
///
/// <para><b>What it refuses to do.</b> Say the same thing twice, say a dozen things at once because
/// a sweep settled a whole stream's redemptions in one second, repeat a reason that was really an
/// error message from a synthesis API, or answer its own messages. Each of those is a way a helpful
/// bot becomes the loudest thing in the channel, and each is handled here rather than left to the
/// templates.</para>
/// </summary>
public sealed class BotService : IDisposable
{
    /// <summary>How often the waiting readings, the refund batch and the overlay are looked at.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long every pet overlay may be gone before the bot mentions it. The same reasoning as the
    /// ledger's own grace: a scene change in OBS drops the socket for a second or two, and a bot that
    /// announced that would be announcing the streamer's scene changes to the channel.
    /// </summary>
    private static readonly TimeSpan OverlayGrace = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings;
    private readonly BotSender _sender;
    private readonly BotContext _context;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    /// <summary>When each flow last spoke, so its cooldown can be honoured.</summary>
    private readonly Dictionary<BotFlow, DateTimeOffset> _lastSaid = [];

    /// <summary>The same for the streamer's own commands, keyed by the word that sets them off.</summary>
    private readonly Dictionary<string, DateTimeOffset> _commandsSaid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Refunds waiting to be announced, held long enough to notice they are a crowd.</summary>
    private readonly List<RedemptionNotice> _pendingRefunds = [];
    private DateTimeOffset _refundsSince;

    /// <summary>Readings the bot has already nudged about, so it nudges once and not every tick.</summary>
    private readonly HashSet<string> _nudged = new(StringComparer.Ordinal);

    private DateTimeOffset? _overlayGoneSince;
    private bool _overlayReportedDown;
    private bool _disposed;

    public BotService(AppSettings settings, BotSender sender, BotContext context)
    {
        _settings = settings;
        _sender = sender;
        _context = context;
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    private BotSettings Bot => _settings.Bot;

    /// <summary>
    /// Whether a line from this account should be treated as chat rather than as the bot talking to
    /// itself. Asked by the app before a message triggers a pet, an edge alert or a welcome.
    /// </summary>
    public bool IsOwnMessage(ChatMessage message)
    {
        if (!Bot.IsActive || !Bot.IgnoreOwnMessages) return false;
        string login = _context.BotLogin();
        return login.Length > 0 && string.Equals(message.UserLogin, login, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A verdict reached the viewer's points. Held for a moment rather than announced: the sweep
    /// after a restart answers everything left over from last time, and one line per redemption
    /// would be the bot's loudest minute of the stream for something nobody is waiting on.
    /// </summary>
    public void OnRedemptionAnswered(RedemptionNotice notice)
    {
        if (!Bot.IsActive) return;

        if (!notice.Refunded)
        {
            BotFlow flow = notice.Subject == "tts" ? BotFlow.TtsSpoken : BotFlow.PetFulfilled;
            Say(flow, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["viewer"] = notice.ViewerName,
                ["cost"] = notice.Cost.ToString()
            });
            return;
        }

        lock (_gate)
        {
            if (_pendingRefunds.Count == 0) _refundsSince = DateTimeOffset.UtcNow;
            _pendingRefunds.Add(notice);
        }
    }

    /// <summary>A pet redemption that never became a pet, so the viewer knows what to expect.</summary>
    public void OnPetOutcome(string viewer, int cost, PetSpawnOutcome outcome)
    {
        if (!Bot.IsActive) return;
        BotFlow? flow = outcome switch
        {
            PetSpawnOutcome.Full => BotFlow.PetLawnFull,
            PetSpawnOutcome.Disabled => BotFlow.PetsDisabled,
            PetSpawnOutcome.NoOverlay => BotFlow.PetOverlayDown,
            _ => null
        };
        if (flow is not { } chosen) return;
        Say(chosen, Viewer(viewer, cost));
    }

    /// <summary>How many pet overlays are connected. Zero starts a clock rather than a message.</summary>
    public void OnPetOverlayCountChanged(int count)
    {
        lock (_gate)
        {
            if (count > 0)
            {
                _overlayGoneSince = null;
                if (!_overlayReportedDown) return;
                _overlayReportedDown = false;
            }
            else
            {
                _overlayGoneSince ??= DateTimeOffset.UtcNow;
                return;
            }
        }
        Say(BotFlow.PetOverlayBack, null);
    }

    /// <summary>A reading was accepted and is waiting for the streamer's yes.</summary>
    public void OnReadingPending(TtsRequest request)
    {
        if (!Bot.IsActive) return;
        Say(BotFlow.TtsAccepted, Viewer(request.DisplayName, request.Cost));
    }

    /// <summary>
    /// A reading reached an ending, whatever the ending was. One entry point for four flows,
    /// because the difference between them is the reason and whether the points came back – and
    /// deciding that here keeps the app from having to know which template answers which failure.
    /// </summary>
    public void OnReadingFinished(TtsRequest request, TtsState state, string reason)
    {
        if (!Bot.IsActive) return;
        lock (_gate) _nudged.Remove(request.Id);

        // Two channel-wide warnings that are worth saying whoever paid and however it was paid: the
        // next viewer about to spend on a queue that is full, or on a feature that is not running, is
        // the one who benefits from hearing it. Their cooldowns keep a busy minute to one line.
        if (string.Equals(reason, TtsService.QueueFullReason, StringComparison.Ordinal))
        {
            Say(BotFlow.TtsQueueFull, Viewer(request.DisplayName, request.Cost));
            return;
        }

        if (string.Equals(reason, TtsService.DisabledReason, StringComparison.Ordinal)
            || string.Equals(reason, TtsService.NotConfiguredReason, StringComparison.Ordinal))
        {
            Say(BotFlow.TtsUnavailable, Viewer(request.DisplayName, request.Cost));
            return;
        }

        // Everything left is this one reading's own ending, and a refundable one is not this method's
        // to announce.
        //
        // <para><b>Why not.</b> A refundable ending raises TtsService.Answered as well, which hands
        // the verdict to the ledger – and the ledger only reports back once Twitch has actually taken
        // it, retrying for as long as that takes and giving up out loud if it never does. This event
        // fires the moment the reading ended, before that call has even been attempted. Announcing
        // here would tell a viewer their points are back while the refund is still being retried, and
        // would go on saying it after the ledger had given up. It would also say it twice, because
        // the ledger's own notice arrives later and reads the same – which is exactly what the
        // sender's duplicate guard was quietly hiding.</para>
        if (request.Refundable) return;

        // Nothing was refundable here, so a reading that failed has no refund to promise and nothing
        // worth saying. One that was read out loud is its own good news.
        if (state == TtsState.Spoken) Say(BotFlow.TtsSpoken, Viewer(request.DisplayName, request.Cost));
    }

    /// <summary>A moderator's call landed, so the mod can see that it did.</summary>
    public void OnModCall(ChatMessage message)
    {
        if (!Bot.IsActive) return;
        Say(BotFlow.ModCallAck, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["viewer"] = message.DisplayName
        });
    }

    /// <summary>
    /// Somebody wrote the call command and nothing happened. The two reasons are worded for the
    /// person who wrote it rather than for the log: one of them is something they can do nothing
    /// about, and the other is that they are not a moderator.
    /// </summary>
    public void OnModCallMissed(ChatMessage message, bool alertDisabled)
    {
        if (!Bot.IsActive) return;
        Say(BotFlow.ModCallMissed, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["viewer"] = message.DisplayName,
            ["reason"] = alertDisabled
                ? "det ljuset är avstängt just nu"
                : "bara moderatorer kan använda det"
        });
    }

    /// <summary>
    /// One chat line, for everything the bot answers rather than announces: the welcome and the two
    /// commands. Returns quietly for its own lines, which is what keeps a bot that greets people from
    /// greeting itself.
    /// </summary>
    public void OnChatMessage(ChatMessage message)
    {
        if (!Bot.IsActive || IsOwnMessage(message)) return;

        // A command answers and nothing else follows. Someone whose first ever line is a command is
        // asking a question, not saying hello, and answering both would be two lines at a stranger.
        if (AnswerCommand(message)) return;

        if (message.IsFirstMessage)
            Say(BotFlow.Welcome, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["viewer"] = message.DisplayName });
    }

    /// <summary>A raid, a shoutout, a sub, a hype train – the things worth a thank you.</summary>
    public void OnChatEvent(ChatEvent chatEvent)
    {
        if (!Bot.IsActive) return;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["viewer"] = chatEvent.DisplayName,
            ["viewers"] = (chatEvent.ViewerCount ?? 0).ToString(),
            ["level"] = (chatEvent.HypeLevel ?? 0).ToString(),
            ["link"] = chatEvent.UserLogin.Length > 0 ? "twitch.tv/" + chatEvent.UserLogin : "twitch.tv"
        };

        BotFlow? flow = chatEvent.Type switch
        {
            ChatEventType.Raid => BotFlow.Raid,
            ChatEventType.ShoutoutReceived => BotFlow.ShoutoutReceived,
            ChatEventType.Subscription or ChatEventType.SubGift or ChatEventType.CommunityGift or ChatEventType.SubUpgrade => BotFlow.Subscription,
            ChatEventType.HypeTrainBegin => BotFlow.HypeTrainBegin,
            ChatEventType.HypeTrainEnd => BotFlow.HypeTrainEnd,
            _ => null
        };
        if (flow is { } chosen) Say(chosen, values);
    }

    /// <summary>Everything waiting goes without being said – the channel changed, or the bot was switched off.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pendingRefunds.Clear();
            _nudged.Clear();
            _lastSaid.Clear();
            _commandsSaid.Clear();
            _overlayGoneSince = null;
            _overlayReportedDown = false;
        }
        _sender.Clear();
    }

    /// <summary>
    /// Answers one of the streamer's own commands, and says whether it did.
    ///
    /// <para>The cheap test comes first: chat is mostly not commands, and a busy room should not cost
    /// a walk through the command list per line.</para>
    /// </summary>
    private bool AnswerCommand(ChatMessage message)
    {
        string text = message.Text.Trim();
        if (text.Length < 2 || text[0] != '!' || Bot.Commands.Count == 0) return false;

        foreach (BotCommand command in Bot.Commands)
        {
            if (!command.IsUsable || !Matches(text, command.Command)) continue;

            // Matched but not for this person. Answered with silence rather than with a refusal: a
            // viewer who was never told the command exists does not need to be told off for trying
            // it, and a bot that announces every refusal is a bot chat learns to set off on purpose.
            if (command.ModeratorsOnly && !message.IsBroadcaster && !message.IsModerator) return true;

            if (!OnCooldown(command))
            {
                string line = BotTemplate.Render(command.Response, Bot,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["viewer"] = message.DisplayName });
                if (line.Length > 0) _sender.Enqueue(line);
            }
            // Handled either way. A command still inside its cooldown is not a line that should then
            // go on to be treated as ordinary chat.
            return true;
        }
        return false;
    }

    /// <summary>Whether this command has spoken too recently, marking it as having spoken if not.</summary>
    private bool OnCooldown(BotCommand command)
    {
        if (command.CooldownSeconds <= 0) return false;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_commandsSaid.TryGetValue(command.Command, out DateTimeOffset last)
                && now - last < TimeSpan.FromSeconds(command.CooldownSeconds))
                return true;
            _commandsSaid[command.Command] = now;
            return false;
        }
    }

    /// <summary>
    /// The three things that are true of a moment rather than of an event: a reading nobody has
    /// answered yet, a batch of refunds old enough to be counted, and an overlay that has been gone
    /// long enough to mean it.
    /// </summary>
    internal void Tick()
    {
        try
        {
            if (!Bot.IsActive) return;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            NudgeWaitingReadings(now);
            FlushRefunds(now);
            ReportOverlayDown(now);
        }
        catch (Exception ex)
        {
            // A timer callback that throws takes the process with it, and this one runs while the
            // streamer is live.
            AppLog.Error("Bot: fel i botens rond", ex);
        }
    }

    private void NudgeWaitingReadings(DateTimeOffset now)
    {
        if (!Bot.Speaks(BotFlow.TtsWaiting)) return;

        IReadOnlyList<TtsEntry> queue = _context.ReadingQueue();
        var waiting = new List<TtsEntry>();
        lock (_gate)
        {
            // Anything no longer in the queue has been answered, spoken or let go, and its id can
            // come round again on a later request from the same viewer.
            _nudged.IntersectWith(queue.Select(entry => entry.Id));
            foreach (TtsEntry entry in queue)
            {
                if (entry.State != "pending") continue;
                if (now - DateTimeOffset.FromUnixTimeMilliseconds(entry.At) < TimeSpan.FromSeconds(Bot.TtsWaitingSeconds)) continue;
                if (!_nudged.Add(entry.Id)) continue;
                waiting.Add(entry);
            }
        }

        foreach (TtsEntry entry in waiting)
            Say(BotFlow.TtsWaiting, Viewer(entry.Viewer, entry.Cost), ignoreCooldown: true);
    }

    private void FlushRefunds(DateTimeOffset now)
    {
        RedemptionNotice[] due;
        lock (_gate)
        {
            if (_pendingRefunds.Count == 0) return;
            if (now - _refundsSince < TimeSpan.FromSeconds(Bot.RefundBatchWindowSeconds)) return;
            due = _pendingRefunds.ToArray();
            _pendingRefunds.Clear();
        }

        if (due.Length >= Bot.RefundBatchThreshold)
        {
            // Switched off, this stays silent rather than falling back to one line per refund. A
            // streamer who turns off the summary is saying a pile of refunds is not worth a message,
            // and answering that with thirty messages would be the opposite of what they asked for.
            Say(BotFlow.RefundBatch, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = due.Length.ToString(),
                ["total"] = due.Sum(notice => notice.Cost).ToString(),
                // They are a batch because they happened together, which almost always means they
                // happened for the same reason; the first one speaks for the rest.
                ["reason"] = BotTemplate.Reason(due[0].Reason, "de kunde inte levereras", Bot)
            }, ignoreCooldown: true);
            return;
        }

        foreach (RedemptionNotice notice in due)
        {
            Say(notice.Subject == "tts" ? BotFlow.TtsRefund : BotFlow.PetRefund,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["viewer"] = notice.ViewerName,
                    ["cost"] = notice.Cost.ToString(),
                    ["reason"] = BotTemplate.Reason(notice.Reason, "det gick inte den här gången", Bot)
                },
                ignoreCooldown: true);
        }
    }

    private void ReportOverlayDown(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_overlayReportedDown) return;
            if (_overlayGoneSince is not { } since || now - since < OverlayGrace) return;
            _overlayReportedDown = true;
        }
        Say(BotFlow.PetOverlayDown, null);
    }

    /// <summary>
    /// Writes one line, if this flow is switched on and has not just spoken. The cooldown is skipped
    /// for the flows that name a person: two viewers owed their points back are two answers, and
    /// silencing the second one leaves somebody who paid with nothing at all.
    /// </summary>
    private void Say(BotFlow flow, IReadOnlyDictionary<string, string>? values, bool ignoreCooldown = false)
    {
        BotMessageRule rule = Bot.Rule(flow);
        if (!Bot.IsActive || !rule.Enabled) return;

        if (!ignoreCooldown && rule.CooldownSeconds > 0)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                if (_lastSaid.TryGetValue(flow, out DateTimeOffset last)
                    && now - last < TimeSpan.FromSeconds(rule.CooldownSeconds))
                    return;
                _lastSaid[flow] = now;
            }
        }

        string line = BotTemplate.Render(rule.Template, Bot, values);
        if (line.Length > 0) _sender.Enqueue(line);
    }

    private static Dictionary<string, string> Viewer(string name, int cost) =>
        new(StringComparer.OrdinalIgnoreCase) { ["viewer"] = name, ["cost"] = cost.ToString() };

    private static bool Matches(string text, string command) =>
        text.Equals(command, StringComparison.OrdinalIgnoreCase)
        || text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
