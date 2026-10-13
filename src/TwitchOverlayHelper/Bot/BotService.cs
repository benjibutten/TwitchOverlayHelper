using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Pets;
using TwitchOverlayHelper.Settings;
using TwitchOverlayHelper.Speech;

namespace TwitchOverlayHelper.Bot;

/// <summary>What the bot has to be able to look up that it does not own itself.</summary>
/// <param name="DynamicCommand">
/// First claim on a chat line, for the commands whose answers depend on data (the spin's "!mina").
/// Returns true when the line is handled, which keeps the static commands and the welcome out of it;
/// the answer itself comes back through <see cref="BotService.Announce"/>.
/// </param>
public sealed record BotContext(
    Func<IReadOnlyList<TtsEntry>> ReadingQueue,
    Func<string> BotLogin,
    Func<ChatMessage, bool>? DynamicCommand = null);

/// <summary>
/// Decides what the bot says and when. Everything that reaches chat goes through here and then
/// through <see cref="BotSender"/>; nothing else in the app writes a bot line.
///
/// <para>It tells viewers what only the app knows: that a redemption was paid back and why, that a
/// reading is waiting for a yes, that the overlay is down. It batches a burst of refunds into one
/// line, honours per-flow cooldowns and never answers its own messages.</para>
/// </summary>
public sealed class BotService : IDisposable
{
    /// <summary>How often the waiting readings, the refund batch and the overlay are looked at.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long every pet overlay may be gone before the bot mentions it. A scene change in OBS drops
    /// the socket for a second or two.
    /// </summary>
    private static readonly TimeSpan OverlayGrace = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings;
    private readonly BotSender _sender;
    private readonly BotContext _context;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    // When each flow last spoke, for its cooldown. The viewer part of the key is the empty string
    // (one channel-wide cooldown) except for the flows in PerViewerCooldownFlows.
    private readonly Dictionary<(BotFlow Flow, string Viewer), DateTimeOffset> _lastSaid = [];

    // The same for the streamer's own commands, keyed by the command word.
    private readonly Dictionary<string, DateTimeOffset> _commandsSaid = new(StringComparer.OrdinalIgnoreCase);

    // Refunds waiting to be announced, held for the batch window so a burst becomes one line.
    private readonly List<RedemptionNotice> _pendingRefunds = [];
    private DateTimeOffset _refundsSince;

    // Ids of the readings the bot has already nudged about.
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
    /// True when the message was written by the bot's own account and the bot is set to ignore its
    /// own lines.
    /// </summary>
    public bool IsOwnMessage(ChatMessage message)
    {
        if (!Bot.IsActive || !Bot.IgnoreOwnMessages) return false;
        string login = _context.BotLogin();
        return login.Length > 0 && string.Equals(message.UserLogin, login, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A verdict reached the viewer's points. A fulfilment is announced at once; a refund is held
    /// for the batch window, so a sweep that settles many at once becomes one line.
    /// </summary>
    public void OnRedemptionAnswered(RedemptionNotice notice)
    {
        if (!Bot.IsActive) return;

        if (!notice.Refunded)
        {
            // A fulfilled spin was already announced by SpinWin when the reel stopped.
            if (notice.Subject == "spin") return;
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

    /// <summary>A pet redemption that never became a pet.</summary>
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

    /// <summary>
    /// How many pet overlays are connected. Zero starts the <see cref="OverlayGrace"/> clock rather
    /// than a message.
    /// </summary>
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
    /// A reading reached an ending, whatever the ending was. A refundable ending is not announced
    /// here; that waits for <see cref="OnRedemptionAnswered"/>.
    /// </summary>
    public void OnReadingFinished(TtsRequest request, TtsState state, string reason)
    {
        if (!Bot.IsActive) return;
        lock (_gate) _nudged.Remove(request.Id);

        // Two channel-wide warnings, said whoever paid: they are for the next viewer about to spend.
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

        // A refundable ending is announced by OnRedemptionAnswered, once Twitch has actually taken the
        // verdict. Announcing it here would promise points back before the refund call has been made,
        // and say it a second time when the ledger's notice arrives.
        if (request.Refundable) return;

        // Without a refund to promise, a failed reading has nothing worth saying.
        if (state == TtsState.Spoken) Say(BotFlow.TtsSpoken, Viewer(request.DisplayName, request.Cost));
    }

    /// <summary>Acknowledges a moderator's call.</summary>
    public void OnModCall(ChatMessage message)
    {
        if (!Bot.IsActive) return;
        Say(BotFlow.ModCallAck, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["viewer"] = message.DisplayName
        });
    }

    /// <summary>
    /// Somebody wrote the call command and nothing happened: the alert is switched off, or they are
    /// not a moderator.
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
    /// One chat line, for everything the bot answers rather than announces: the commands and the
    /// welcome. The bot's own lines are ignored.
    /// </summary>
    public void OnChatMessage(ChatMessage message)
    {
        if (!Bot.IsActive || IsOwnMessage(message)) return;

        // The data-driven commands go first, so a streamer's own "!mina" row cannot shadow the spin's
        // answer with a fixed text.
        if (_context.DynamicCommand?.Invoke(message) == true) return;

        // A first-ever line that is a command gets its answer and no welcome on top.
        if (AnswerCommand(message)) return;

        if (message.IsFirstMessage)
            Say(BotFlow.Welcome, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["viewer"] = message.DisplayName });
    }

    /// <summary>
    /// A line another service asked for – the spin announcing a win, prompting for a gift recipient,
    /// answering a command. The flow's own switch and template still decide whether and how it is
    /// said.
    /// </summary>
    /// <param name="ignoreCooldown">
    /// True for lines that answer one person who paid; false for the ones viewers can set off on
    /// purpose, like the list command.
    /// </param>
    public void Announce(BotFlow flow, IReadOnlyDictionary<string, string>? values, bool ignoreCooldown = true) =>
        Say(flow, values, ignoreCooldown);

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

    /// <summary>Drops everything waiting to be said and all cooldowns – the channel changed, or the bot was switched off.</summary>
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

    /// <summary>Answers one of the streamer's own commands. Returns true when the line was one.</summary>
    private bool AnswerCommand(ChatMessage message)
    {
        string text = message.Text.Trim();
        if (text.Length < 2 || text[0] != '!' || Bot.Commands.Count == 0) return false;

        foreach (BotCommand command in Bot.Commands)
        {
            if (!command.IsUsable || !Matches(text, command.Command)) continue;

            // Matched but not for this person: silence rather than a refusal, which chat would learn
            // to set off on purpose.
            if (command.ModeratorsOnly && !message.IsBroadcaster && !message.IsModerator) return true;

            if (!OnCooldown(command))
            {
                string line = BotTemplate.Render(command.Response, Bot,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["viewer"] = message.DisplayName });
                if (line.Length > 0) _sender.Enqueue(line);
            }
            // A command inside its cooldown is still handled, not ordinary chat.
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

    /// <summary>How many cooldown entries are being kept.</summary>
    internal int CooldownEntryCount
    {
        get { lock (_gate) return _lastSaid.Count; }
    }

    /// <summary>
    /// Handles what depends on time rather than on an event: a reading nobody has answered yet, a
    /// batch of refunds old enough to be counted, an overlay gone past its grace, expired cooldowns.
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
            PruneExpiredCooldowns(now);
        }
        catch (Exception ex)
        {
            // A timer callback that throws takes the process with it.
            AppLog.Error("Bot: fel i botens rond", ex);
        }
    }

    /// <summary>
    /// Drops the cooldown entries whose window has passed. A per-viewer flow leaves one entry per
    /// person who has asked, which would otherwise grow for as long as the stream runs.
    /// </summary>
    private void PruneExpiredCooldowns(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_lastSaid.Count == 0) return;
            List<(BotFlow Flow, string Viewer)>? expired = null;
            foreach ((BotFlow Flow, string Viewer) key in _lastSaid.Keys)
            {
                int cooldown = Bot.Rule(key.Flow).CooldownSeconds;
                if (cooldown <= 0 || now - _lastSaid[key] >= TimeSpan.FromSeconds(cooldown))
                    (expired ??= []).Add(key);
            }
            if (expired is null) return;
            foreach ((BotFlow Flow, string Viewer) key in expired) _lastSaid.Remove(key);
        }
    }

    private void NudgeWaitingReadings(DateTimeOffset now)
    {
        if (!Bot.Speaks(BotFlow.TtsWaiting)) return;

        IReadOnlyList<TtsEntry> queue = _context.ReadingQueue();
        var waiting = new List<TtsEntry>();
        lock (_gate)
        {
            // An id that has left the queue can come round again on a later request.
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
            // With the summary switched off this stays silent; it does not fall back to one line per
            // refund.
            Say(BotFlow.RefundBatch, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = due.Length.ToString(),
                ["total"] = due.Sum(notice => notice.Cost).ToString(),
                // Refunds that arrive together almost always share a reason; the first one's is used.
                ["reason"] = BotTemplate.Reason(due[0].Reason, "de kunde inte levereras", Bot)
            }, ignoreCooldown: true);
            return;
        }

        foreach (RedemptionNotice notice in due)
        {
            Say(notice.Subject switch { "tts" => BotFlow.TtsRefund, "spin" => BotFlow.SpinRefund, _ => BotFlow.PetRefund },
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

    // The flows whose cooldown is counted per viewer, so one viewer's answer does not block the next
    // person who asks. Every other flow shares one channel-wide cooldown.
    private static readonly HashSet<BotFlow> PerViewerCooldownFlows = [BotFlow.SpinList, BotFlow.SpinListEmpty];

    /// <summary>
    /// Writes one line, if this flow is switched on and has not just spoken. Callers pass
    /// <paramref name="ignoreCooldown"/> for lines owed to one viewer, such as a refund notice.
    /// </summary>
    private void Say(BotFlow flow, IReadOnlyDictionary<string, string>? values, bool ignoreCooldown = false)
    {
        BotMessageRule rule = Bot.Rule(flow);
        if (!Bot.IsActive || !rule.Enabled) return;

        if (!ignoreCooldown && rule.CooldownSeconds > 0)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            (BotFlow, string) key = (flow, PerViewerCooldownFlows.Contains(flow)
                && values is not null && values.TryGetValue("viewer", out string? viewer)
                ? viewer.ToLowerInvariant()
                : string.Empty);
            lock (_gate)
            {
                if (_lastSaid.TryGetValue(key, out DateTimeOffset last)
                    && now - last < TimeSpan.FromSeconds(rule.CooldownSeconds))
                    return;
                _lastSaid[key] = now;
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
