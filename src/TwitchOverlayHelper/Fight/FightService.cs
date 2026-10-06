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

/// <summary>One viewer's vote in the character select: this fighter, in this outfit, for this corner.</summary>
public sealed record FightPick(int Player, string Fighter, int Outfit, string Viewer, string? UserKey, string? Color);

/// <summary>
/// The chat's say in the fight. A viewer names a corner – <c>!heja 1</c>, <c>!hela p2</c> – and
/// the wait screen does the rest; this decides only whether the line was a command at all and
/// whether its writer is still waiting out the last one.
///
/// <para>Corners rather than characters, so the words stay the same when the streamer puts somebody
/// else in the ring. The fighter's current name is accepted as well, since that is what is written
/// on the screen next to the bar.</para>
///
/// <para>The character select's votes come through here too – <c>!p1 my2</c> – and are only checked
/// against the roster: counting them, and deciding when the vote is over, is the wait screen's.</para>
/// </summary>
public sealed class FightService(AppSettings settings, FightCatalog catalog, Action<FightAssist> publish,
    Action<FightPick>? publishPick = null)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastUsed = new(StringComparer.Ordinal);
    private (string? P1, string? P2) _lineup;

    /// <summary>
    /// Who the wait screen says is in the ring right now. With a character select that is the
    /// chat's choice rather than the settings', and a cheer by name has to find the fighter there.
    /// </summary>
    public void SetLineup(string? p1, string? p2)
    {
        lock (_lock) _lineup = (p1, p2);
    }

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
        if (HandlePick(message, fight)) return true;
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

    /// <summary>A vote in the character select. No cooldown: a later vote simply replaces the earlier one.</summary>
    private bool HandlePick(ChatMessage message, FightSettings fight)
    {
        if (!fight.CharacterSelect || publishPick is null) return false;
        if (ParsePick(message.Text, fight) is not { } pick) return false;
        if (IsShowing?.Invoke() == false) return true;

        string viewer = message.DisplayName.Length > 0 ? message.DisplayName : message.UserLogin;
        string key = message.UserId.Length > 0 ? message.UserId : message.UserLogin;
        publishPick(new FightPick(pick.Player, pick.Fighter.Id, pick.Outfit, viewer, key, message.NameColor));
        return true;
    }

    /// <summary>
    /// "!p1 my2" as a corner, a fighter and an outfit, or null. The fighter is named by id or display
    /// name, spaces and dashes optional, and a number straight after it is the outfit – unless the
    /// whole thing is itself a fighter's name, so an "r2d2" is still R2D2. An outfit the fighter does
    /// not have is the ordinary look rather than no vote at all.
    /// </summary>
    internal (int Player, FighterDefinition Fighter, int Outfit)? ParsePick(string? text, FightSettings fight)
    {
        string[] words = (text ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;
        int player = string.Equals(words[0], fight.Pick1Command, StringComparison.OrdinalIgnoreCase) ? 1
            : string.Equals(words[0], fight.Pick2Command, StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        if (player == 0) return null;

        string wanted = Squash(string.Concat(words.Skip(1)));
        if (wanted.Length == 0) return null;
        IReadOnlyList<FighterDefinition> roster = catalog.Fighters;
        if (Lookup(roster, wanted) is { } exact) return (player, exact, 1);

        // A digit or two at the end is the outfit: "zelda2", "my 12".
        for (int digits = 1; digits <= 2 && digits < wanted.Length; digits++)
        {
            string tail = wanted[^digits..];
            if (!tail.All(char.IsAsciiDigit) || !int.TryParse(tail, out int code)) continue;
            if (Lookup(roster, wanted[..^digits]) is { } fighter)
                return (player, fighter, fighter.Outfits.Any(o => o.Code == code) ? code : 1);
        }
        return null;
    }

    private static FighterDefinition? Lookup(IReadOnlyList<FighterDefinition> roster, string squashed) =>
        roster.FirstOrDefault(f => Squash(f.Id) == squashed) ?? roster.FirstOrDefault(f => Squash(f.Name) == squashed);

    /// <summary>A name as it can be typed: lower case, with no spaces, dashes or underscores.</summary>
    internal static string Squash(string name) =>
        new(name.ToLowerInvariant().Where(ch => ch is not (' ' or '-' or '_' or '@')).ToArray());

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
        // A name with a space in it – "!heja dj jenni" – is the rest of the line, not its first word.
        if (player == 0 && rest.Length == 0 && words.Length > 2) player = PlayerFrom(string.Concat(words.Skip(1)), fight);
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
        (string? P1, string? P2) lineup;
        lock (_lock) lineup = _lineup;
        // The fighters actually in the ring, as the wait screen reported them; the settings until then.
        FighterDefinition? first = catalog.FindFighter(lineup.P1) ?? catalog.FighterOrFallback(fight.Player1, 0);
        FighterDefinition? second = catalog.FindFighter(lineup.P2) ?? catalog.FighterOrFallback(fight.Player2, 1);
        if (Names(first).Contains(Squash(t))) return 1;
        if (Names(second).Contains(Squash(t))) return 2;
        return 0;
    }

    private static string[] Names(FighterDefinition? fighter) =>
        fighter is null ? [] : [Squash(fighter.Id), Squash(fighter.Name)];
}
