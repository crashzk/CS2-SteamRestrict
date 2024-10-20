using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Admin;

using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Modules.Utils;

namespace KitsuneSteamRestrict;

public class PluginConfig : BasePluginConfig
{
    [JsonPropertyName("LogProfileInformations")]
    public bool LogProfileInformations { get; set; } = true;

    [JsonPropertyName("SteamWebAPI")]
    public string SteamWebAPI { get; set; } = "";

    [JsonPropertyName("MinimumCS2Level")]
    public int MinimumCS2Level { get; set; } = -1;

    [JsonPropertyName("MinimumHour")]
    public int MinimumHour { get; set; } = -1;

    [JsonPropertyName("MinimumLevel")]
    public int MinimumLevel { get; set; } = -1;

    [JsonPropertyName("MinimumSteamAccountAgeInDays")]
    public int MinimumSteamAccountAgeInDays { get; set; } = -1;

    [JsonPropertyName("BlockPrivateProfile")]
    public bool BlockPrivateProfile { get; set; } = false;

    [JsonPropertyName("BlockTradeBanned")]
    public bool BlockTradeBanned { get; set; } = false;

    [JsonPropertyName("BlockVACBanned")]
    public bool BlockVACBanned { get; set; } = false;

    [JsonPropertyName("SteamGroupID")]
    public string SteamGroupID { get; set; } = "";

    [JsonPropertyName("BlockGameBanned")]
    public bool BlockGameBanned { get; set; } = false;

    [JsonPropertyName("PrivateProfileWarningTime")]
    public int PrivateProfileWarningTime { get; set; } = 20;

    [JsonPropertyName("PrivateProfileWarningPrintSeconds")]
    public int PrivateProfileWarningPrintSeconds { get; set; } = 3;

    [JsonPropertyName("DatabaseSettings")]
    public DatabaseSettings DatabaseSettings { get; set; } = new DatabaseSettings();

    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 4;
}

public sealed class DatabaseSettings
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "localhost";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "root";

    [JsonPropertyName("database")]
    public string Database { get; set; } = "database";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "password";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 3306;

    [JsonPropertyName("sslmode")]
    public string Sslmode { get; set; } = "none";

    [JsonPropertyName("table-prefix")]
    public string TablePrefix { get; set; } = "";

    [JsonPropertyName("table-purge-days")]
    public int TablePurgeDays { get; set; } = 30;
}

[MinimumApiVersion(227)]
public class SteamRestrictPlugin : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "Steam Restrict";
    public override string ModuleVersion => "1.4.1";
    public override string ModuleAuthor => "K4ryuu, Cruze @ KitsuneLab";
    public override string ModuleDescription => "Restrict certain players from connecting to your server.";

    public readonly HttpClient Client = new HttpClient();
    private bool g_bSteamAPIActivated = false;

    private CounterStrikeSharp.API.Modules.Timers.Timer?[] g_hTimer = new CounterStrikeSharp.API.Modules.Timers.Timer?[65];
    private int[] g_iWarnTime = new int[65];

    private BypassConfig? _bypassConfig;
    public PluginConfig Config { get; set; } = new();

    public void OnConfigParsed(PluginConfig config)
    {
        if (config.Version < Config.Version)
            base.Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", this.Config.Version, config.Version);

        if (string.IsNullOrEmpty(config.SteamWebAPI))
            base.Logger.LogError("This plugin won't work because Web API is empty.");

        Config = config;
    }

    public override void Load(bool hotReload)
    {
        string bypassConfigFilePath = "bypass_config.json";
        var bypassConfigService = new BypassConfigService(Path.Combine(ModuleDirectory, bypassConfigFilePath));
        _bypassConfig = bypassConfigService.LoadConfig();

        if (!IsDatabaseConfigDefault())
        {
            var databaseService = new DatabaseService(Config.DatabaseSettings);
            Task.Run(databaseService.EnsureTablesExistAsync);
        }

        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(() => { g_bSteamAPIActivated = true; });
        RegisterListener<Listeners.OnClientConnect>((int slot, string name, string ipAddress) => { g_hTimer[slot]?.Kill(); });
        RegisterListener<Listeners.OnClientDisconnect>((int slot) => { g_hTimer[slot]?.Kill(); });
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);

        if (hotReload)
        {
            g_bSteamAPIActivated = true;

            foreach (var player in Utilities.GetPlayers().Where(m => m.Connected == PlayerConnectedState.PlayerConnected && !m.IsHLTV && !m.IsBot && m.SteamID.ToString().Length == 17))
            {
                OnPlayerConnectFull(player);
            }
        }
    }

    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
        if (player == null)
            return HookResult.Continue;

        OnPlayerConnectFull(player);
        return HookResult.Continue;
    }

    private void OnPlayerConnectFull(CCSPlayerController player)
    {
        if (string.IsNullOrEmpty(Config.SteamWebAPI))
            return;

        if (player.IsBot || player.IsHLTV)
            return;

        if (player.AuthorizedSteamID == null)
        {
            g_hTimer[player.Slot] = AddTimer(1.0f, () =>
            {
                if (player.AuthorizedSteamID != null)
                {
                    g_hTimer[player.Slot]?.Kill();
                    OnPlayerConnectFull(player);
                    return;
                }
            }, TimerFlags.REPEAT);
            return;
        }

        if (!g_bSteamAPIActivated)
            return;

        ulong authorizedSteamID = player.AuthorizedSteamID.SteamId64;
        nint handle = player.Handle;

        Task.Run(async () =>
        {
            if (!IsDatabaseConfigDefault())
            {
                var databaseService = new DatabaseService(Config.DatabaseSettings);
                if (await databaseService.IsSteamIdAllowedAsync(authorizedSteamID))
                {
                    Server.NextWorldUpdate(() => Logger.LogInformation($"{player.PlayerName} ({authorizedSteamID}) was allowed to join without validations because they were found in the database."));
                    return;
                }
            }

            Server.NextWorldUpdate(() =>
            {
                CheckUserViolations(handle, authorizedSteamID);
            });
        });
    }

    private void CheckUserViolations(nint handle, ulong authorizedSteamID)
    {
        SteamUserInfo UserInfo = new SteamUserInfo
        {
            CS2Level = new CCSPlayerController_InventoryServices(handle).PersonaDataPublicLevel
        };

        SteamService steamService = new SteamService(this, UserInfo);

        Task.Run(async () =>
        {
            await steamService.FetchSteamUserInfo(authorizedSteamID.ToString());

            SteamUserInfo? userInfo = steamService.UserInfo;

            Server.NextWorldUpdate(() =>
            {
                CCSPlayerController? player = Utilities.GetPlayerFromSteamId(authorizedSteamID);

                if (player?.IsValid == true && userInfo != null)
                {
                    if (Config.LogProfileInformations)
                    {
                        Logger.LogInformation($"{player.PlayerName} info:");
                        Logger.LogInformation($"CS2Playtime: {userInfo.CS2Playtime}");
                        Logger.LogInformation($"CS2Level: {userInfo.CS2Level}");
                        Logger.LogInformation($"SteamLevel: {userInfo.SteamLevel}");
                        if ((DateTime.Now - userInfo.SteamAccountAge).TotalSeconds > 30)
                            Logger.LogInformation($"Steam Account Creation Date: {userInfo.SteamAccountAge:dd-MM-yyyy} ({(int)(DateTime.Now - userInfo.SteamAccountAge).TotalDays} days ago)");
                        else
                            Logger.LogInformation($"Steam Account Creation Date: N/A");
                        //Logger.LogInformation($"HasPrime: {userInfo.HasPrime}"); Removed due to people bought prime after CS2 cannot be detected sadly (or atleast not yet)
                        Logger.LogInformation($"HasPrivateProfile: {userInfo.IsPrivate}");
                        Logger.LogInformation($"HasPrivateGameDetails: {userInfo.IsGameDetailsPrivate}");
                        Logger.LogInformation($"IsTradeBanned: {userInfo.IsTradeBanned}");
                        Logger.LogInformation($"IsGameBanned: {userInfo.IsGameBanned}");
                        Logger.LogInformation($"IsInSteamGroup: {userInfo.IsInSteamGroup}");
                    }

                    if (IsRestrictionViolatedVIP (player, userInfo))
                    {
                        if (Config.PrivateProfileWarningTime > 0 && (userInfo.IsPrivate || userInfo.IsGameDetailsPrivate))
                        {
                            int playerSlot = player.Slot;
                            g_iWarnTime[playerSlot] = Config.PrivateProfileWarningTime;
                            int printInterval = Config.PrivateProfileWarningPrintSeconds;
                            int remainingPrintTime = printInterval;

                            g_hTimer[playerSlot] = AddTimer(1.0f, () =>
                            {
                                if (player?.IsValid == true)
                                {
                                    g_iWarnTime[playerSlot]--;
                                    remainingPrintTime--;

                                    if (remainingPrintTime <= 0)
                                    {
                                        player.PrintToChat($" {ChatColors.Silver}[ {ChatColors.Lime}SteamRestrict {ChatColors.Silver}] {ChatColors.LightRed}Your Steam profile or Game details are private. You will be kicked in {g_iWarnTime[playerSlot]} seconds.");
                                        remainingPrintTime = printInterval;
                                    }

                                    if (g_iWarnTime[playerSlot] <= 0)
                                    {
                                        Server.ExecuteCommand($"kickid {player.UserId} \"You have been kicked for not meeting the minimum requirements.\"");
                                        g_hTimer[playerSlot]?.Kill();
                                        g_hTimer[playerSlot] = null;
                                    }
                                }
                                else
                                {
                                    g_hTimer[playerSlot]?.Kill();
                                    g_hTimer[playerSlot] = null;
                                }
                            }, TimerFlags.REPEAT);
                        }
                        else
                            Server.ExecuteCommand($"kickid {player.UserId} \"You have been kicked for not meeting the minimum requirements.\"");
                    }
					
                    if (IsRestrictionViolated (player, userInfo))
                    {
                        if (Config.PrivateProfileWarningTime > 0 && (userInfo.IsPrivate || userInfo.IsGameDetailsPrivate))
                        {
                            int playerSlot = player.Slot;
                            g_iWarnTime[playerSlot] = Config.PrivateProfileWarningTime;
                            int printInterval = Config.PrivateProfileWarningPrintSeconds;
                            int remainingPrintTime = printInterval;

                            g_hTimer[playerSlot] = AddTimer(1.0f, () =>
                            {
                                if (player?.IsValid == true)
                                {
                                    g_iWarnTime[playerSlot]--;
                                    remainingPrintTime--;

                                    if (remainingPrintTime <= 0)
                                    {
                                        player.PrintToChat($" {ChatColors.Silver}[ {ChatColors.Lime}SteamRestrict {ChatColors.Silver}] {ChatColors.LightRed}Your Steam profile or Game details are private. You will be kicked in {g_iWarnTime[playerSlot]} seconds.");
                                        remainingPrintTime = printInterval;
                                    }

                                    if (g_iWarnTime[playerSlot] <= 0)
                                    {
                                        Server.ExecuteCommand($"kickid {player.UserId} \"You have been kicked for not meeting the minimum requirements.\"");
                                        g_hTimer[playerSlot]?.Kill();
                                        g_hTimer[playerSlot] = null;
                                    }
                                }
                                else
                                {
                                    g_hTimer[playerSlot]?.Kill();
                                    g_hTimer[playerSlot] = null;
                                }
                            }, TimerFlags.REPEAT);
                        }
                        else
                            Server.ExecuteCommand($"kickid {player.UserId} \"You have been kicked for not meeting the minimum requirements.\"");
                    }

                    else if (!IsDatabaseConfigDefault())
                    {
                        ulong steamID = player.AuthorizedSteamID?.SteamId64 ?? 0;

                        if (steamID != 0)
                        {
                            var databaseService = new DatabaseService(Config.DatabaseSettings);
                            Task.Run(async () => await databaseService.AddAllowedUserAsync(steamID, Config.DatabaseSettings.TablePurgeDays));
                        }
                    }
                }
            });
        });
    }

    private bool IsRestrictionViolatedVIP(CCSPlayerController player, SteamUserInfo userInfo)
    {
        if (AdminManager.PlayerHasPermissions(player, "@css/vip"))
            return false;

        BypassConfig bypassConfig = _bypassConfig ?? new BypassConfig();
        PlayerBypassConfig? playerBypassConfig = bypassConfig.GetPlayerConfig(player.AuthorizedSteamID?.SteamId64 ?? 0);

        if (!(playerBypassConfig?.BypassMinimumCS2Level ?? false) && Config.MinimumCS2Level != -1 && userInfo.CS2Level < Config.MinimumCS2Level)
            return true;

        if (!(playerBypassConfig?.BypassMinimumHours ?? false) && Config.MinimumHour != -1 && userInfo.CS2Playtime < Config.MinimumHour)
            return true;

        if (!(playerBypassConfig?.BypassMinimumLevel ?? false) && Config.MinimumLevel != -1 && userInfo.SteamLevel < Config.MinimumLevel)
            return true;

        if (!(playerBypassConfig?.BypassMinimumSteamAccountAge ?? false) && Config.MinimumSteamAccountAgeInDays != -1 && (DateTime.Now - userInfo.SteamAccountAge).TotalDays < Config.MinimumSteamAccountAgeInDays)
            return true;

        if (Config.BlockPrivateProfile && !(playerBypassConfig?.BypassPrivateProfile ?? false) && (userInfo.IsPrivate || userInfo.IsGameDetailsPrivate))
            return true;

        return false;
    }
	
	private bool IsRestrictionViolated(CCSPlayerController player, SteamUserInfo userInfo)
    {
        if (AdminManager.PlayerHasPermissions(player, "@css/bypasspremiumcheck"))
            return false;

        BypassConfig bypassConfig = _bypassConfig ?? new BypassConfig();
        PlayerBypassConfig? playerBypassConfig = bypassConfig.GetPlayerConfig(player.AuthorizedSteamID?.SteamId64 ?? 0);

        if (!(playerBypassConfig?.BypassMinimumCS2Level ?? false) && Config.MinimumCS2Level != -1 && userInfo.CS2Level < Config.MinimumCS2Level)
            return true;

        if (!(playerBypassConfig?.BypassMinimumHours ?? false) && Config.MinimumHour != -1 && userInfo.CS2Playtime < Config.MinimumHour)
            return true;

        if (!(playerBypassConfig?.BypassMinimumLevel ?? false) && Config.MinimumLevel != -1 && userInfo.SteamLevel < Config.MinimumLevel)
            return true;

        if (!(playerBypassConfig?.BypassMinimumSteamAccountAge ?? false) && Config.MinimumSteamAccountAgeInDays != -1 && (DateTime.Now - userInfo.SteamAccountAge).TotalDays < Config.MinimumSteamAccountAgeInDays)
            return true;

        if (Config.BlockPrivateProfile && !(playerBypassConfig?.BypassPrivateProfile ?? false) && (userInfo.IsPrivate || userInfo.IsGameDetailsPrivate))
            return true;

        if (Config.BlockTradeBanned && !(playerBypassConfig?.BypassTradeBanned ?? false) && userInfo.IsTradeBanned)
            return true;

        if (Config.BlockGameBanned && !(playerBypassConfig?.BypassGameBanned ?? false) && userInfo.IsGameBanned)
            return true;

        if (!string.IsNullOrEmpty(Config.SteamGroupID) && !(playerBypassConfig?.BypassSteamGroupCheck ?? false) && !userInfo.IsInSteamGroup)
            return true;

        if (Config.BlockVACBanned && !(playerBypassConfig?.BypassVACBanned ?? false) && userInfo.IsVACBanned)
            return true;

        return false;
    }

    public bool IsDatabaseConfigDefault()
    {
        DatabaseSettings settings = Config.DatabaseSettings;
        return settings.Host == "localhost" &&
            settings.Username == "root" &&
            settings.Database == "database" &&
            settings.Password == "password" &&
            settings.Port == 3306 &&
            settings.Sslmode == "none" &&
            settings.TablePrefix == "" &&
            settings.TablePurgeDays == 30;
    }
}
