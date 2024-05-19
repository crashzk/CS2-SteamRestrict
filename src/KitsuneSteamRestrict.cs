using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Admin;

using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace KitsuneSteamRestrict;

public class PluginConfig : BasePluginConfig
{
    [JsonPropertyName("SteamWebAPI")]
    public string SteamWebAPI { get; set; } = "";

    [JsonPropertyName("MinimumCS2LevelPrime")]
    public int MinimumCS2LevelPrime { get; set; } = -1;

    [JsonPropertyName("MinimumCS2LevelNonPrime")]
    public int MinimumCS2LevelNonPrime { get; set; } = -1;

    [JsonPropertyName("MinimumHourPrime")]
    public int MinimumHourPrime { get; set; } = -1;

    [JsonPropertyName("MinimumHourNonPrime")]
    public int MinimumHourNonPrime { get; set; } = -1;

    [JsonPropertyName("MinimumLevelPrime")]
    public int MinimumLevelPrime { get; set; } = -1;

    [JsonPropertyName("MinimumLevelNonPrime")]
    public int MinimumLevelNonPrime { get; set; } = -1;

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

    [JsonPropertyName("DatabaseSettings")]
    public DatabaseSettings DatabaseSettings { get; set; } = new DatabaseSettings();

    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 2;
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
    public override string ModuleVersion => "1.3.1";
    public override string ModuleAuthor => "K4ryuu, Cruze @ KitsuneLab";
    public override string ModuleDescription => "Restrict certain players from connecting to your server.";

    public readonly HttpClient Client = new HttpClient();
    private bool g_bSteamAPIActivated = false;

    private CounterStrikeSharp.API.Modules.Timers.Timer?[] g_hAuthorize = new CounterStrikeSharp.API.Modules.Timers.Timer?[65];

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
            _ = databaseService.EnsureTablesExistAsync();
        }

        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(() => { g_bSteamAPIActivated = true; });
        RegisterListener<Listeners.OnClientConnect>((int slot, string name, string ipAddress) => { g_hAuthorize[slot]?.Kill(); });
        RegisterListener<Listeners.OnClientDisconnect>((int slot) => { g_hAuthorize[slot]?.Kill(); });
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
            g_hAuthorize[player.Slot] = AddTimer(1.0f, () =>
            {
                if (player.AuthorizedSteamID != null)
                {
                    g_hAuthorize[player.Slot]?.Kill();
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
        SteamService steamService = new SteamService(this);
        steamService.FetchSteamUserInfo(handle, authorizedSteamID);

        SteamUserInfo? userInfo = steamService.UserInfo;

        CCSPlayerController? player = Utilities.GetPlayerFromSteamId(authorizedSteamID);

        if (player?.IsValid == true && userInfo != null)
        {
            Logger.LogInformation($"{player.PlayerName} info:");
            Logger.LogInformation($"CS2Playtime: {userInfo.CS2Playtime}");
            Logger.LogInformation($"CS2Level: {userInfo.CS2Level}");
            Logger.LogInformation($"SteamLevel: {userInfo.SteamLevel}");
            if ((DateTime.Now - userInfo.SteamAccountAge).TotalSeconds > 30)
                Logger.LogInformation($"Steam Account Creation Date: {userInfo.SteamAccountAge:dd-MM-yyyy}");
            else
                Logger.LogInformation($"Steam Account Creation Date: N/A");
            Logger.LogInformation($"HasPrime: {userInfo.HasPrime}");
            Logger.LogInformation($"HasPrivateProfile: {userInfo.IsPrivate}");
            Logger.LogInformation($"IsTradeBanned: {userInfo.IsTradeBanned}");
            Logger.LogInformation($"IsGameBanned: {userInfo.IsGameBanned}");
            Logger.LogInformation($"IsInSteamGroup: {userInfo.IsInSteamGroup}");

            if (IsRestrictionViolated(player, userInfo))
            {
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
    }

    private bool IsRestrictionViolated(CCSPlayerController player, SteamUserInfo userInfo)
    {
        if (AdminManager.PlayerHasPermissions(player, "@css/bypasspremiumcheck"))
            return false;

        BypassConfig bypassConfig = _bypassConfig ?? new BypassConfig();
        PlayerBypassConfig? playerBypassConfig = bypassConfig.GetPlayerConfig(player.AuthorizedSteamID?.SteamId64 ?? 0);

        bool isPrime = userInfo.HasPrime;
        var configChecks = new[]
        {
            (isPrime && (playerBypassConfig?.BypassMinimumCS2Level ?? false), Config.MinimumCS2LevelPrime, userInfo.CS2Level),
            (!isPrime && (playerBypassConfig?.BypassMinimumCS2Level ?? false), Config.MinimumCS2LevelNonPrime, userInfo.CS2Level),
            (isPrime && (playerBypassConfig?.BypassMinimumHours ?? false), Config.MinimumHourPrime, userInfo.CS2Playtime),
            (!isPrime && (playerBypassConfig?.BypassMinimumHours ?? false), Config.MinimumHourNonPrime, userInfo.CS2Playtime),
            (isPrime && (playerBypassConfig?.BypassMinimumLevel ?? false), Config.MinimumLevelPrime, userInfo.SteamLevel),
            (!isPrime && (playerBypassConfig?.BypassMinimumLevel ?? false), Config.MinimumLevelNonPrime, userInfo.SteamLevel),
            (playerBypassConfig?.BypassMinimumSteamAccountAge ?? false, Config.MinimumSteamAccountAgeInDays, (DateTime.Now - userInfo.SteamAccountAge).TotalDays),
            (Config.BlockPrivateProfile && (playerBypassConfig?.BypassPrivateProfile ?? false), 1, userInfo.IsPrivate ? 0 : 1),
            (Config.BlockTradeBanned && (playerBypassConfig?.BypassTradeBanned ?? false), 1, userInfo.IsTradeBanned ? 0 : 1),
            (Config.BlockGameBanned && (playerBypassConfig?.BypassGameBanned ?? false), 1, userInfo.IsGameBanned ? 0 : 1),
            (!string.IsNullOrEmpty(Config.SteamGroupID) && (playerBypassConfig?.BypassSteamGroupCheck ?? false), 1, userInfo.IsInSteamGroup ? 1 : 0),
            (Config.BlockVACBanned && (playerBypassConfig?.BypassVACBanned ?? false), 1, userInfo.IsVACBanned ? 0 : 1),
        };

        return configChecks.Any(check => check.Item1 && check.Item2 != -1 && check.Item3 < check.Item2);
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
