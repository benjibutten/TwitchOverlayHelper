using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper.Fight;

/// <summary>What a viewer did for a corner: cheered it on, or patched it up.</summary>
public enum FightAssistKind
{
    Cheer,
    Heal
}

/// <summary>One viewer taking a side, as the wait screen is told about it.</summary>
public sealed record FightAssist(int Player, FightAssistKind Kind, string Viewer, string? Color);

/// <summary>
/// The chat's say in the fight. A viewer names a corner – <c>!heja 1</c>, <c>!hela p2</c> – and
/// the wait screen does the rest; this decides only whether the line was a command at all and
/// whether its writer is still waiting out the last one.
///
/// <para>Corners rather than characters, so the words stay the same when the streamer puts somebody
/// else in the ring. The fighter's current name is accepted as well, since that is what is written
/// on the screen next to the bar.</para>
/// </summary>
public sealed class FightService(AppSettings settings, FightCatalog catalog, Action<FightAssist> publish)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastUsed = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether anybody is watching. A command with no wait screen open would only start a cooldown
    /// for nothing, so it is ignored instead; null treats every screen as open.
    /// </summary>
    public Func<bool>? IsShowing { get; set; }

    /// <summary>Acts on a chat line if it is a fight command. Answers whether it was.</summary>
    public bool HandleMessage(ChatMessage message) => HandleMessage(message, DateTimeOffset.UtcNow);

    internal bool HandleMessage(ChatMessage message, DateTimeOffset now)
    {
        FightSettings fight = settings.Fight;
        if (!fight.CommandsEnabled) return false;
        if (Parse(message.Text, fight) is not { } parsed) return false;
        if (IsShowing?.Invoke() == false) return false;

        string who = message.UserId.Length > 0 ? message.UserId : message.UserLogin;
        string key = $"{who}:{parsed.Kind}";
        lock (_lock)
        {
            if (_lastUsed.TryGetValue(key, out DateTimeOffset last) && now - last < TimeSpan.FromSeconds(fight.CooldownSeconds))
                return true;
            _lastUsed[key] = now;
            // A long stream meets thousands of viewers; the ones whose cooldown is over are forgotten.
            if (_lastUsed.Count > 2000)
                foreach (string stale in _lastUsed.Where(p => now - p.Value > TimeSpan.FromSeconds(fight.CooldownSeconds)).Select(p => p.Key).ToArray())
                    _lastUsed.Remove(stale);
        }

        string viewer = message.DisplayName.Length > 0 ? message.DisplayName : message.UserLogin;
        publish(new FightAssist(parsed.Player, parsed.Kind, viewer, message.NameColor));
        return true;
    }

    /// <summary>The command and the corner in a line, or null when it is ordinary chat.</summary>
    internal (FightAssistKind Kind, int Player)? Parse(string? text, FightSettings fight)
    {
        string[] words = (text ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !words[0].StartsWith('!')) return null;

        FightAssistKind kind;
        string rest;
        if (StartsWithCommand(words[0], fight.CheerCommand, out rest)) kind = FightAssistKind.Cheer;
        else if (StartsWithCommand(words[0], fight.HealCommand, out rest)) kind = FightAssistKind.Heal;
        else return null;

        // "!heja1" as well as "!heja 1": the space is the first thing a hurried viewer leaves out.
        string target = rest.Length > 0 ? rest : words.Length > 1 ? words[1] : string.Empty;
        int player = PlayerFrom(target, fight);
        return player == 0 ? null : (kind, player);
    }

    private static bool StartsWithCommand(string word, string command, out string rest)
    {
        rest = string.Empty;
        if (!word.StartsWith(command, StringComparison.OrdinalIgnoreCase)) return false;
        rest = word[command.Length..];
        return rest.Length == 0 || rest is "1" or "2";
    }

    private int PlayerFrom(string target, FightSettings fight)
    {
        string t = target.Trim().TrimStart('@').ToLowerInvariant();
        switch (t)
        {
            case "1" or "p1" or "spelare1": return 1;
            case "2" or "p2" or "spelare2": return 2;
        }
        if (t.Length == 0) return 0;
        if (Names(catalog.FighterOrFallback(fight.Player1, 0)).Contains(t)) return 1;
        if (Names(catalog.FighterOrFallback(fight.Player2, 1)).Contains(t)) return 2;
        return 0;
    }

    private static string[] Names(FighterDefinition? fighter) =>
        fighter is null ? [] : [fighter.Id.ToLowerInvariant(), fighter.Name.ToLowerInvariant()];
}
