using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.SqlClient;
using SendGrid;
using Serilog.Sinks.Email;

var env = Environment.GetEnvironmentVariable("CAMBIADOR_CONFIGURATION");

IConfiguration configuration = new ConfigurationBuilder()
  .AddJsonFile("appsettings.json")
  .AddJsonFile("appsettings.Release.json")
  .Build();

var client = new SendGridClient(configuration["SendGrid:ApiKey"]);
var emailConnectionInfo = new EmailConnectionInfo {
  EmailSubject = "Cambiador on test app5 error",
  FromEmail = "noreply@utah.gov",
  ToEmail = "sgourley@utah.gov",
  SendGridClient = client,
  FromName = "Cambiador"
};

var logger = new LoggerConfiguration()
  .ReadFrom.Configuration(configuration)
  .WriteTo.Email(emailConnectionInfo, restrictedToMinimumLevel: LogEventLevel.Error)
  .CreateLogger();

var totalTime = Stopwatch.StartNew();
var version = Assembly
  .GetEntryAssembly()?
  .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
  .InformationalVersion ?? "0.0.0";

logger.Debug("Connecting to the source database");
using var connection = new SqlConnection(configuration["ConnectionStrings:source"]);

if (!await ChangeDetection.EnsureChangeDetectionTableExists(connection)) {
  logger.Debug("changeTable table does not exist. Please create it");

  return 4;
}

logger.Information($"Starting Change Detection: {version}");

logger.Information("Managing change detection table");
var tables = await ChangeDetection.TrimChangeDetectionTablesNotInSource(connection);
logger.Information("{tables} were removed from the change detection table", tables);

logger.Debug("Discovering tables and schemas");

var tableFieldMap = await ChangeDetection.DiscoverAndGroupTablesWithFields(connection);

Stats stats = new();

foreach (var table in tableFieldMap) {
  var tableTime = Stopwatch.StartNew();
  var tableName = table.Key;
  var fields = table.Value;

  var hashAsOfLastRun = await ChangeDetection.GetHashOfLastRun(connection, tableName);
  logger.Information($"Gathering all rows from {tableName}");

  var hashAsOfNow = string.Empty;
  try {
    hashAsOfNow = await ChangeDetection.CreateHashFromTableRows(tableName, fields, connection, stats);
  } catch (Exception ex) {
    logger.Error(ex, $"Error while hashing {tableName}");
    continue;
  }

  if (string.IsNullOrEmpty(hashAsOfLastRun) || hashAsOfLastRun != hashAsOfNow) {
    logger.Information($"{tableName} has changed. Hashes: {hashAsOfLastRun} -> {hashAsOfNow}");

    if (env == "Development") {
      logger.Debug("Development skipping update");
      continue;
    }

    await ChangeDetection.UpsertHash(connection, tableName, hashAsOfNow);

    stats.Changed.Add(tableName);

    logger.Information($"Total table time: ({tableName}) {tableTime.ElapsedMilliseconds.FriendlyFormat()}");

    continue;
  }

  logger.Information($"{tableName} has not changed. Hashes: {hashAsOfLastRun} -> {hashAsOfNow}");
  logger.Information($"Total table time: ({tableName}) {tableTime.ElapsedMilliseconds.FriendlyFormat()}");
  tableTime.Stop();
  logger.Debug("No changes since last run");
}

logger.Information($"Total process time: {totalTime.ElapsedMilliseconds.FriendlyFormat()}");
logger.Information($"Total rows processed: {stats.TotalRows}");
logger.Information($"Total query time: {stats.QueryTime.FriendlyFormat()}");
logger.Information($"Total hashing time: {stats.HashTime.FriendlyFormat()}");
logger.Information($"Total tables changed: {stats.Changed.Count}");

foreach (var table in stats.Changed) {
  logger.Information($"{table} updated");
}

return 0;
