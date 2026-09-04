using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Admin;
using System.Collections.Generic;
using System.Reflection;
using System.Xml.Linq;
using System.Threading;
using CounterStrikeSharp.API.Modules.Entities;
using System.Data.SqlTypes;
using System.Numerics;

namespace HideAndSeekPlugin;

public class HideAndSeekPlugin : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "HideAndSeekPlugin";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "ShookEagle";

    public Config Config { get; set; } = new Config();

    public static HideAndSeekPlugin Instance { get; private set; }

    public List<CCSPlayerController> Winners = new List<CCSPlayerController>();

    public Random Random = new Random();

    public override void Load(bool hotReload)
    {
        Instance = this;
        AddCommandListener("jointeam", Command_Jointeam, HookMode.Pre);
    }

    public void OnConfigParsed(Config config)
    {
        Config = config;
    }

    public HookResult Command_Jointeam(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null || !int.TryParse(commandInfo.GetArg(1), out int arg))
            return HookResult.Continue;

        // Sucher (T) darf man sich nie selbst aussuchen, das macht Find(). Statt den Spieler
        // hängen zu lassen (altes Verhalten: Command wurde einfach geblockt), schicken wir ihn
        // stattdessen zu den Versteckern (CT).
        if (arg == (int)CsTeam.Terrorist)
        {
            player.PrintToChat($"[{ChatColors.Blue}{Instance.Config.Prefix}{ChatColors.White}] You cannot join the Seeker team directly.");
            if (player.Team != CsTeam.CounterTerrorist)
            {
                player.SwitchTeam(CsTeam.CounterTerrorist);
            }
            return HookResult.Handled;
        }

        if (arg == (int)CsTeam.CounterTerrorist)
        {
            if (player.Team == CsTeam.Terrorist)
            {
                player.PrintToChat($"[{ChatColors.Blue}{Instance.Config.Prefix}{ChatColors.White}] You may not leave Seekers");
                return HookResult.Handled;
            }

            player.SwitchTeam(CsTeam.CounterTerrorist);
            if (player.PlayerPawn?.Value != null)
            {
                player.PlayerPawn.Value.MaxHealth = 100;
                player.PlayerPawn.Value.Health = 100;
            }
            return HookResult.Continue;
        }

        // Alles andere (z.B. Spectate) unangetastet lassen - vorher wurde das hier fälschlich
        // geblockt und Spieler blieben in einem komischen Zustand hängen.
        return HookResult.Continue;
    }

    public void Find()
    {
        List<CCSPlayerController> allPlayers = Utilities.GetPlayers()
            .Where(p => p.IsValid && p.Team != CsTeam.Spectator)
            .ToList();

        if (allPlayers.Count < Instance.Config.MinPlayers)
        {
            Server.PrintToChatAll($"[{ChatColors.Blue}{Instance.Config.Prefix}{ChatColors.White}] Minimum {ChatColors.LightBlue}{Instance.Config.MinPlayers}{ChatColors.Default} required to start.");
            return;
        }

        // Immer mindestens 1 Sucher und mindestens 1 Versteck-Spieler übrig lassen,
        // egal was in der Config für hns_starting_ts steht.
        int maxSeekers = Math.Max(1, allPlayers.Count - 1);
        int desiredSeekers = Math.Clamp(Instance.Config.StartingTs, 1, maxSeekers);

        List<CCSPlayerController> seekers = Winners
            .Where(w => w != null && w.IsValid && allPlayers.Contains(w))
            .Distinct()
            .Take(desiredSeekers)
            .ToList();
        Winners.Clear();

        if (seekers.Count < desiredSeekers)
        {
            List<CCSPlayerController> candidates = allPlayers.Where(p => !seekers.Contains(p)).ToList();
            seekers.AddRange(RandomSeeker(candidates, desiredSeekers - seekers.Count));
        }

        foreach (var player in allPlayers)
        {
            if (player == null || !player.IsValid)
                continue;

            if (seekers.Contains(player))
            {
                player.SwitchTeam(CsTeam.Terrorist);
                player.Respawn();
                if (player.PlayerPawn?.Value != null)
                {
                    player.PlayerPawn.Value.MaxHealth = 9999;
                    player.PlayerPawn.Value.Health = 9999;
                    player.GiveNamedItem("weapon_knife"); // <- Messer beim Rundenstart
                }
                Server.PrintToChatAll($"[{ChatColors.Blue}{Instance.Config.Prefix}{ChatColors.White}] {ChatColors.Green}{player.PlayerName}{ChatColors.White} is now seeking.");
            }
            else if (player.Team != CsTeam.CounterTerrorist)
            {
                // Absicherung gegen komische Teamzuordnung (z.B. erste Runde nach Mapstart).
                player.SwitchTeam(CsTeam.CounterTerrorist);
            }
        }
    }

    public List<CCSPlayerController> RandomSeeker(List<CCSPlayerController> candidates, int num)
    {
        List<CCSPlayerController> pool = new List<CCSPlayerController>(candidates);
        List<CCSPlayerController> picked = new List<CCSPlayerController>();

        num = Math.Min(num, pool.Count);
        for (int i = 0; i < num; i++)
        {
            int index = Random.Next(pool.Count);
            picked.Add(pool[index]);
            pool.RemoveAt(index);
        }

        return picked;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        var seeker = @event.Attacker;
        List<CCSPlayerController> allPlayers = Utilities.GetPlayers();

        if (player != null)
        {
            if (player.Team == CsTeam.Terrorist)
            {
                Server.RunOnTickAsync(Server.TickCount + 32, () =>
                {
                    player.Respawn();
                    if (player.PlayerPawn?.Value != null)
                    {
                        player.PlayerPawn.Value.MaxHealth = 9999;
                        player.PlayerPawn.Value.Health = 9999;
                        player.GiveNamedItem("weapon_knife"); // <- Messer beim Rundenstart
                    }
                });
                return HookResult.Continue;
            }
            else if (player.Team == CsTeam.CounterTerrorist)
            {
                Server.RunOnTickAsync(Server.TickCount + 32, () =>
                {
                    player.SwitchTeam(CsTeam.Terrorist);
                    player.Respawn();
                    if (player.PlayerPawn?.Value != null)
                    {
                        player.PlayerPawn.Value.MaxHealth = 9999;
                        player.PlayerPawn.Value.Health = 9999;
                        player.GiveNamedItem("weapon_knife"); // <- Messer beim Rundenstart
                    }

                    if (seeker != null && seeker.IsValid)
                    {
                        Server.PrintToChatAll($"[{ChatColors.Blue}{Instance.Config.Prefix}{ChatColors.White}] {ChatColors.Green}{player.PlayerName}{ChatColors.White} was found by {ChatColors.Red}{seeker.PlayerName}{ChatColors.White}");
                    }
                    else
                    {
                        Server.PrintToChatAll($"[{ChatColors.Blue}{Instance.Config.Prefix}{ChatColors.White}] {ChatColors.Green}{player.PlayerName}{ChatColors.White} was found by the Seekers");
                    }

                    List<CCSPlayerController> CtPlayers = allPlayers.Where(c => c.Team == CsTeam.CounterTerrorist).ToList();
                    if (CtPlayers.Count == 1)
                    {
                        var lastCT = CtPlayers.First();
                        if (!Winners.Contains(lastCT))
                        {
                            Winners.Clear();
                            Winners.Add(lastCT);
                            Server.PrintToChatAll($"[{ChatColors.Blue}{Instance.Config.Prefix}{ChatColors.White}] {ChatColors.Purple}{lastCT.PlayerName}{ChatColors.White} is the last survivor and may become the next Seeker.");
                        }
                    }
                    else if (CtPlayers.Count == 0)
                    {
                        Server.PrintToChatAll($"[{ChatColors.Blue}{Instance.Config.Prefix}{ChatColors.White}] {ChatColors.LightRed}All CTs have been found! Ending round...");

                        Server.RunOnTickAsync(Server.TickCount + 64, () =>
                        {
                            GameRules?.TerminateRound(1.0f, RoundEndReason.RoundDraw);
                        });
                    }
                });
                return HookResult.Continue;
            }
        }
        return HookResult.Continue;
    }

    // Holt die aktuellen GameRules, um z.B. den Warmup-Status zu prüfen/beenden.
    public CCSGameRules? GameRules =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

    // Beendet den Warmup automatisch, sobald genug Spieler (inkl. Bots) verbunden sind.
    public void CheckAutoStart()
    {
        var rules = GameRules;
        if (rules == null || !rules.WarmupPeriod)
            return;

        int connected = Utilities.GetPlayers().Count(p => p.IsValid && !p.IsHLTV);
        if (connected >= Instance.Config.MinPlayers)
        {
            Server.PrintToChatAll($"[{ChatColors.Blue}{Instance.Config.Prefix}{ChatColors.White}] {ChatColors.LightBlue}{connected}{ChatColors.Default} players ready - starting the round!");
            Server.ExecuteCommand("mp_warmup_end");
        }
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        CheckAutoStart();
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        // Ungültige/verlassene Spieler aus der Winners-Liste (nächster Sucher) entfernen,
        // damit Find() nie versucht, jemanden zu switchen, der nicht mehr da ist.
        Winners.RemoveAll(w => w == null || !w.IsValid);

        int connected = Utilities.GetPlayers().Count(p => p.IsValid && !p.IsHLTV);
        if (connected == 0)
        {
            // Server ist leer -> alles zurücksetzen und wieder in den Warmup, damit die
            // nächste Runde sauber neu startet, sobald wieder genug Spieler da sind.
            Winners.Clear();

            var rules = GameRules;
            if (rules != null && !rules.WarmupPeriod)
            {
                Server.ExecuteCommand("mp_warmup_start");
            }
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundPreStart(EventRoundPrestart @event, GameEventInfo info)
    {
        Find();
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        List<CCSPlayerController> allPlayers = Utilities.GetPlayers().Where(p => p.Team == CsTeam.Terrorist).ToList();
        foreach (var player in allPlayers)
        {
            if (player.PlayerPawn?.Value != null)
            {
                player.PlayerPawn.Value.MaxHealth = 9999;
                player.PlayerPawn.Value.Health = 9999;
                player.GiveNamedItem("weapon_knife"); // <- Messer beim Rundenstart
            }
        }
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        List<CCSPlayerController> allPlayers = Utilities.GetPlayers().Where(p => p.Team != CsTeam.Spectator).ToList();
        List<CCSPlayerController> ctPlayers = allPlayers.Where(p => p.Team == CsTeam.CounterTerrorist).ToList();

        if (ctPlayers.Count > 1)
        {
            var randomCT = ctPlayers[Random.Next(ctPlayers.Count)];
            Winners.Clear();
            Winners.Add(randomCT);
            Server.PrintToChatAll($"[{ChatColors.Blue}{Instance.Config.Prefix}{ChatColors.White}] {ChatColors.Green}{randomCT.PlayerName}{ChatColors.White} survived and will be the next Seeker!");
        }

        foreach (var player in allPlayers)
        {
            if (player != null)
            {
                player.SwitchTeam(CsTeam.CounterTerrorist);
                if (player.PlayerPawn?.Value != null)
                {
                    player.PlayerPawn.Value.MaxHealth = 100;
                    player.PlayerPawn.Value.Health = 100;
                }
            }
        }

        return HookResult.Continue;
    }

    [ConsoleCommand("css_decoy", "Give all Ct's A Decoy")]
    [RequiresPermissions("@css/rcon")]
    public void Ondecoy(CCSPlayerController player, CommandInfo info)
    {
        var allPlayers = Utilities.GetPlayers().Where(p => p.Team == CsTeam.CounterTerrorist);
        foreach (var t in allPlayers)
        {
            t.GiveNamedItem("weapon_decoy");
        }
    }
}
