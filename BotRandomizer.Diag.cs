using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace BotRandomizer;

public sealed partial class BotRandomizerPlugin
{
    private bool _diagWatch;
    private bool _diagWatchOnce;

    // Read-only observability for the team-intro agent path. `check` dumps the current
    // state on demand; `watch` replays the same dump on a timeline after each
    // round_prestart so a whole intro window can be captured unattended.
    [ConsoleCommand("css_botrandomizer", "BotRandomizer diagnostics: check | watch [on|off|once]")]
    public void OnDiagCommand(CCSPlayerController? player, CommandInfo command)
    {
        var sub = command.ArgCount >= 2 ? command.GetArg(1).ToLowerInvariant() : "check";
        switch (sub)
        {
            case "check":
                foreach (var line in BuildIntroReport("check"))
                    command.ReplyToCommand(line);
                foreach (var line in BuildInternalsReport())
                    command.ReplyToCommand(line);
                break;
            case "watch":
                switch (command.ArgCount >= 3 ? command.GetArg(2).ToLowerInvariant() : "")
                {
                    case "on":
                        _diagWatch = true;
                        _diagWatchOnce = false;
                        break;
                    case "off":
                        _diagWatch = false;
                        _diagWatchOnce = false;
                        break;
                    case "once":
                        _diagWatchOnce = true;
                        break;
                    default:
                        _diagWatch = !_diagWatch;
                        break;
                }
                command.ReplyToCommand(
                    _diagWatchOnce
                        ? "BotRandomizer watch: armed for the next round_prestart"
                        : $"BotRandomizer watch: {(_diagWatch ? "on" : "off")}");
                break;
            default:
                command.ReplyToCommand("Usage: css_botrandomizer [check|watch [on|off|once]]");
                break;
        }
    }

    private void DiagWatchTimeline()
    {
        EmitDiag("prestart+0");
        Server.NextFrame(() => EmitDiag("prestart+1f"));
        foreach (var (delay, tag) in new[]
        {
            (0.10f, "prestart+0.10s"),
            (0.25f, "prestart+0.25s"),
            (0.50f, "prestart+0.50s"),
            (1.0f, "prestart+1s"),
            (2.0f, "prestart+2s"),
            (4.0f, "prestart+4s"),
            (7.0f, "prestart+7s"),
            (10.0f, "prestart+10s"),
        })
        {
            AddTimer(delay, () => EmitDiag(tag), TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    private void EmitDiag(string tag)
    {
        try
        {
            foreach (var line in BuildIntroReport(tag))
                Logger.LogInformation("{Line}", line);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "[BotRandomizer] diag dump failed at {Tag}", tag);
        }
    }

    private List<string> BuildIntroReport(string tag)
    {
        var lines = new List<string>();
        var gameRules = Utilities
            .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()
            ?.GameRules;

        var bots = Utilities.GetPlayers()
            .Where(p => p.IsValid && p is { IsBot: true, IsHLTV: false } && IsPlayableTeam(p.TeamNum))
            .OrderBy(p => p.Slot)
            .ToList();
        var botByXuid = bots
            .Where(p => p.SteamID != 0)
            .GroupBy(p => p.SteamID)
            .ToDictionary(g => g.Key, g => g.First());

        var tPrev = CollectPreviews("team_intro_terrorist");
        var ctPrev = CollectPreviews("team_intro_counterterrorist");

        lines.Add(
            $"diag {tag}: introPeriod={FmtOpt(gameRules?.TeamIntroPeriod)} " +
            $"ctVar={FmtOpt(gameRules?.CTTeamIntroVariant)} tVar={FmtOpt(gameRules?.TTeamIntroVariant)} " +
            $"bots={bots.Count} tPrev={tPrev.Count} ctPrev={ctPrev.Count}");

        foreach (var (name, previews) in new[] { ("t", tPrev), ("ct", ctPrev) })
        {
            foreach (var preview in previews)
            {
                lines.Add(
                    $"  {name}/idx{preview.Index} ord={preview.Ordinal} var={preview.Variant} " +
                    $"xuid={FmtXuid(preview.Xuid, botByXuid)} agent={preview.AgentItem.ItemDefinitionIndex}");
            }
        }

        foreach (var bot in bots)
        {
            var expected = _states.TryGet(bot.Slot, out var state)
                ? state.Loadout.AgentDefIndex.ToString()
                : "-";
            lines.Add(
                $"  bot s{bot.Slot} {TeamLetter(bot.TeamNum)} \"{bot.PlayerName}\" " +
                $"xuid={bot.SteamID} wantAgent={expected}");
        }

        return lines;
    }

    private IEnumerable<string> BuildInternalsReport()
    {
        yield return $"plugin: v{ModuleVersion} catalog={(_catalog is null
            ? "null"
            : $"{_catalog.WeaponCount}w/{_catalog.WeaponPaintCount}p/{_catalog.StickerKits.Count}s/{_catalog.KeychainDefinitions.Count}c")}";
        yield return $"native: attrWriter={_applicator?.NativeAvailable} itemViews={_weaponItemViews?.NativeAvailable} gniHook={_giveNamedItemHooked}";
        yield return $"state: slots={_states.States.Count} pendingRerolls={_pendingRerolls.Count} " +
            $"watch={(_diagWatch ? "on" : _diagWatchOnce ? "once-armed" : "off")}";
    }

    private static List<CCSGO_TeamPreviewCharacterPosition> CollectPreviews(string designerName)
        => Utilities
            .FindAllEntitiesByDesignerName<CCSGO_TeamPreviewCharacterPosition>(designerName)
            .Where(preview => preview.IsValid)
            .OrderBy(preview => preview.Variant)
            .ThenBy(preview => preview.Ordinal)
            .ToList();

    private static string FmtXuid(ulong xuid, IReadOnlyDictionary<ulong, CCSPlayerController> botByXuid)
    {
        if (xuid == 0)
            return "0(open)";
        return botByXuid.TryGetValue(xuid, out var bot)
            ? $"{xuid}=s{bot.Slot}"
            : $"{xuid}(foreign)";
    }

    private static string FmtOpt<T>(T? value) where T : struct
        => value?.ToString() ?? "null";

    private static string TeamLetter(int team)
        => team == RandomizerAssets.CounterTerroristTeam ? "CT" : "T";
}
