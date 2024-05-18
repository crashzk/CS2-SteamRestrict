using Dapper;
using KitsuneSteamRestrict;
using MySqlConnector;

public class DatabaseService
{
	private readonly string _tablePrefix;
	private readonly string _connectionString;

	public DatabaseService(DatabaseSettings settings)
	{
		_tablePrefix = settings.TablePrefix;
		_connectionString = $"Server={settings.Host};Port={settings.Port};Database={settings.Database};Uid={settings.Username};Pwd={settings.Password};SslMode={Enum.Parse<MySqlSslMode>(settings.Sslmode, true)};";
	}

	public async Task<bool> IsSteamIdAllowedAsync(ulong steamId)
	{
		using var connection = new MySqlConnection(_connectionString);
		var result = await connection.QueryFirstOrDefaultAsync<DateTime?>(
			$"SELECT `expiration_date` FROM `{_tablePrefix}allowed_users` WHERE `steam_id` = @steamId AND `expiration_date` > NOW()",
			new { steamId });

		return result.HasValue;
	}

	public async Task AddAllowedUserAsync(ulong steamId, int daysValid)
	{
		using var connection = new MySqlConnection(_connectionString);
		await connection.ExecuteAsync(
			$"INSERT INTO `{_tablePrefix}allowed_users` (`steam_id`, `expiration_date`) VALUES (@steamId, DATE_ADD(NOW(), INTERVAL @daysValid DAY))",
			new { steamId, daysValid });
	}

	public async Task EnsureTablesExistAsync()
	{
		using var connection = new MySqlConnection(_connectionString);
		await connection.ExecuteAsync($@"
            CREATE TABLE IF NOT EXISTS `{_tablePrefix}allowed_users` (
                `steam_id` BIGINT UNSIGNED PRIMARY KEY,
                `expiration_date` DATETIME
            )");
	}

	public async Task PurgeExpiredSavesAsync()
	{
		using var connection = new MySqlConnection(_connectionString);
		await connection.ExecuteAsync($"DELETE FROM `{_tablePrefix}allowed_users` WHERE `expiration_date` < NOW()");
	}
}
