using System.Text.Json;

namespace KitsuneSteamRestrict
{
	public class BypassConfig
	{
		public ulong SteamID { get; set; }
		public bool BypassMinimumCS2Level { get; set; } = false;
		public bool BypassMinimumHours { get; set; } = false;
		public bool BypassMinimumLevel { get; set; } = false;
		public bool BypassMinimumSteamAccountAge { get; set; } = false;
		public bool BypassPrivateProfile { get; set; } = false;
		public bool BypassTradeBanned { get; set; } = false;
		public bool BypassVACBanned { get; set; } = false;
		public bool BypassSteamGroupCheck { get; set; } = false;
		public bool BypassGameBanned { get; set; } = false;
	}

	public class BypassConfigService
	{
		private readonly string _configFilePath;

		public BypassConfigService(string configFilePath)
		{
			_configFilePath = configFilePath;
		}

		public BypassConfig LoadConfig()
		{
			if (File.Exists(_configFilePath))
			{
				string json = File.ReadAllText(_configFilePath);
				return JsonSerializer.Deserialize<BypassConfig>(json)!;
			}
			else
			{
				BypassConfig defaultConfig = new BypassConfig
				{
					SteamID = 76561198345583467,
					BypassMinimumCS2Level = true,
					BypassMinimumHours = false,
					BypassMinimumLevel = true,
					BypassMinimumSteamAccountAge = false,
					BypassPrivateProfile = true,
					BypassTradeBanned = false,
					BypassVACBanned = true,
					BypassSteamGroupCheck = false,
					BypassGameBanned = true
				};

				string json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
				File.WriteAllText(_configFilePath, json);

				return defaultConfig;
			}
		}
	}
}
