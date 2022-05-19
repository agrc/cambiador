using System.Diagnostics;
using System.Reflection;
using System.Text;
using Dapper;
using HashDepot;
using Microsoft.Data.SqlClient;

namespace cambiador;

internal class TrimData {
  public string? TableName { get; set; }
  public int Id { get; set; }
}
internal static class ChangeDetection {
  private const string changeTable = "CHANGEDETECTION";
  private const string changeSchema = "META.";
  private const string schema = "sde.";
  private const string changeTableExistSql = "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE LOWER(TABLE_NAME)=LOWER(@changeTable)";
  private static readonly string updateHashSql = $"UPDATE {changeSchema}{changeTable} SET last_modified=GETDATE(), [hash]=@hash WHERE LOWER(table_name)=LOWER(@tableName)";
  private static readonly string insertHashSql = $"INSERT INTO {changeSchema}{changeTable} (table_name, last_modified, [hash]) VALUES (@tableName, GETDATE(), @hash)";
  private static readonly string getHashSql = $"SELECT [hash] FROM {changeSchema}{changeTable} WHERE LOWER(table_name)=LOWER(@tableName)";
  private static readonly string hashExistsSql = $"SELECT 1 FROM {changeSchema}{changeTable} WHERE LOWER(table_name)=LOWER(@tableName)";
  private static readonly string extraTablesSql = $"SELECT table_name as TableName, id FROM {changeSchema}{changeTable} cd WHERE NOT EXISTS (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES s WHERE lower(s.TABLE_CATALOG) + '.' + lower(s.TABLE_SCHEMA) + '.' + lower(s.TABLE_NAME) = cd.table_name)";
  private static readonly string trimTablesSql = $"DELETE FROM {changeSchema}{changeTable} WHERE id IN @ids";
  private static readonly Stats stats = new();

  public static async Task<bool> DetectChanges(string connectionString) {
    var totalTime = Stopwatch.StartNew();
    var version = Assembly
      .GetEntryAssembly()?
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
      .InformationalVersion ?? "0.0.0";

    Log.Debug("Connecting to the source database");
    using var connection = new SqlConnection(connectionString);

    if (!await EnsureChangeDetectionTableExists(connection)) {
      Log.Debug("changeTable table does not exist. Please create it");

      return false;
    }

    Log.Information("Managing change detection table");
    var tables = await TrimChangeDetectionTablesNotInSource(connection);
    Log.Information("{tables} were removed from the change detection table", tables);

    Log.Information($"Starting Change Detection: {version}");

    Log.Debug("Discovering tables and schemas");

    var tableFieldMap = await DiscoverAndGroupTablesWithFields(connection);

    foreach (var table in tableFieldMap) {
      var tableTime = Stopwatch.StartNew();
      var tableName = table.Key;
      var fields = table.Value;

      var hashAsOfLastRun = await connection.QuerySingleOrDefaultAsync<string>(getHashSql, new { tableName });
      Log.Information($"Gathering all rows from {tableName}");

      var hashAsOfNow = string.Empty;
      try {
        hashAsOfNow = await CreateHashFromTableRows(tableName, fields, connection);
      } catch (Exception ex) {
        Log.Error(ex, $"Error while hashing {tableName}");
        continue;
      }

      if (string.IsNullOrEmpty(hashAsOfLastRun) || hashAsOfLastRun != hashAsOfNow) {
        await UpsertHash(connection, tableName, hashAsOfNow);

        stats.Changed.Add(tableName);

        Log.Debug($"Total table time: ({tableName}) {tableTime.ElapsedMilliseconds.FriendlyFormat()}");

        continue;
      }

      Log.Information($"Total table time: ({tableName}) {tableTime.ElapsedMilliseconds.FriendlyFormat()}");
      tableTime.Stop();
      Log.Debug("  No changes since last run");
    }

    Log.Information($"Total process time: {totalTime.ElapsedMilliseconds.FriendlyFormat()}");
    Log.Information($"Total rows processed: {stats.TotalRows}");
    Log.Information($"Total query time: {stats.QueryTime.FriendlyFormat()}");
    Log.Information($"Total hashing time: {stats.HashTime.FriendlyFormat()}");
    Log.Information($"Total tables changed: {stats.Changed.Count}");

    foreach (var table in stats.Changed) {
      Log.Information($"  {table} updated");
    }

    return true;
  }

  private static async Task<IEnumerable<string?>> TrimChangeDetectionTablesNotInSource(SqlConnection connection) {
    var tables = await connection.QueryAsync<TrimData>(extraTablesSql);

    if (!tables.Any()) {
      return Enumerable.Empty<string>();
    }

    await connection.ExecuteAsync(trimTablesSql, new { ids = tables.Select(x => x.Id) });

    return tables.Select(x => x.TableName);
  }

  private static async Task UpsertHash(SqlConnection connection, string tableName, string hashAsOfNow) {
    Log.Debug("    Changes detected");

    var recordExists = await connection.QuerySingleOrDefaultAsync<bool>(hashExistsSql, new { tableName });

    if (recordExists) {
      await connection.ExecuteAsync(updateHashSql, new { hash = hashAsOfNow, tableName });

      Log.Debug($"Updated the hash for {tableName}");

      return;
    }

    await connection.ExecuteAsync(insertHashSql, new { hash = hashAsOfNow, tableName });

    Log.Debug($"Inserted a hash for {tableName}");
  }

  // Creates the table on he first run or returns the name of the table if it already exists
  private static async Task<bool> EnsureChangeDetectionTableExists(SqlConnection connection) =>
    await connection.QueryFirstOrDefaultAsync<bool>(changeTableExistSql, new { changeTable });

  private static async Task<Dictionary<string, IList<string>>> DiscoverAndGroupTablesWithFields(SqlConnection connection) {
    var skipFields = new List<string> { "gdb_geomattr_data", "objectid_" };
    var skipSchemas = new[] { "demographic", "elevation", "meta" };
    var skipTables = new[] {
        "boundaries.usstates", "boundaries.utah", "boundaries.utahinlandportauthority_hb2001",
        "boundaries.utahinlandportauthority_hb2002", "boundaries.utahinlandportauthority_hb2003",
        "boundaries.wilderness_blm98reinventory", "boundaries.wilderness_blmsuitability", "boundaries.wilderness_blmwsas",
        "energy.coaldepositareas1988", "health.healthsmallstatisticalareas2017", "health.healthsmallstatisticalareas2018",
        "health.healthsmallstatisticalareas2020", "indices.nationalgrid", "indices.usgs100kquads", "indices.usgs24kquads",
        "indices.usgs24kquarterquads", "indices.usgs250kquads1x1", "indices.usgs250kquads1x2", "indices.usgs_dem_extents",
        "location.lucablockaddresscounts2017", "planning.publiclandsinitiativehr5780areas_blm2016",
        "planning.publiclandsinitiativehr5780lines_blm2016", "planning.utahpli_areas_proposal_jan16",
        "planning.utahpli_lines_proposal_jan16", "planning.wildernessprop_hr1500", "planning.wildernessprop_hr1745",
        "planning.wildernessprop_nineco1995", "planning.wildernessprop_redrock", "planning.wildernessprop_uwa1995",
        "planning.wildernessprop_uwc1989", "planning.wildernessprop_uwc1995", "planning.wildernessprop_uwc2008",
        "planning.wildernessprop_washingtonco", "planning.wildernessprop_wdesert1999", "political.districtcombinationareas2012",
        "political.districtcombinationareas2022", "political.judicialdistricts", "political.uniquehousesenate2002",
        "political.uscongressdistricts2002", "political.uscongressdistricts2012", "political.uscongressdistricts2022to2032",
        "political.utahhousedistricts2002", "political.utahhousedistricts2012", "political.utahhousedistricts2022to2032",
        "political.utahschoolboarddistricts2012", "political.utahschoolboarddistricts2015", "political.utahschoolboarddistricts2022to2032",
        "political.utahsenatedistricts2002", "political.utahsenatedistricts2012", "political.utahsenatedistricts2022to2032",
      };

    var orderBy = "ASC";
    if (DateTime.Now.Hour > 10) {
      orderBy = "DESC";
    }

    Log.Information("Using {order}, because time is {time}", orderBy, DateTime.Now.Hour);

    const string? tableMetaQuery = "SELECT LOWER(table_name) " +
      $"FROM {schema}sde_table_registry registry " +
      "WHERE NOT (table_name like 'SDE_%' OR table_name like 'GDB_%')";
    var fieldMetaQuery = "SELECT LOWER(table_cataLog) as [db], LOWER(table_schema) as [schema], LOWER(table_name) as [table], LOWER(column_name) as [field], LOWER(data_type) as fieldType " +
      "FROM INFORMATION_SCHEMA.COLUMNS " +
      "WHERE table_name IN @tables AND LOWER(column_name) NOT IN @skipFields " +
      $"ORDER BY [db], [schema], [table], [field] {orderBy}";

    var tables = await connection.QueryAsync<string>(tableMetaQuery);
    var fieldMeta = await connection.QueryAsync<FieldMetadata>(fieldMetaQuery, new {
      tables,
      skipFields
    });

    var tableFieldMap = new Dictionary<string, IList<string>>(tables.Count());

    foreach (var meta in fieldMeta) {
      if (skipSchemas.Contains(meta.Schema)) {
        Log.Information($"Treating as static, skipping: {meta.TableName()}");

        continue;
      }

      if (meta.Table.EndsWith("_temp")) {
        Log.Information($"Found swapping temp table, skipping: {meta.TableName()}");

        continue;
      }

      if (skipTables.Contains($"{meta.Schema}.{meta.Table}")) {
        Log.Information($"Treating as static, skipping: {meta.TableName()}");

        continue;
      }

      if (meta.FieldType == "geometry") {
        meta.Field = $"{meta.Field}.STAsBinary() as {meta.Field}";
      }

      if (!tableFieldMap.ContainsKey(meta.TableName())) {
        tableFieldMap.Add(meta.TableName(), new List<string> { meta.Field });

        continue;
      }

      tableFieldMap[meta.TableName()].Add(meta.Field);
    }

    return tableFieldMap;
  }

  private static async Task<string> CreateHashFromTableRows(string table, IEnumerable<string> fields, SqlConnection connection) {
    // get all of the data from the table
    var timer = Stopwatch.StartNew();

    var rows = await connection
      .QueryAsync($"SELECT {string.Join(',', fields)} FROM {table} ORDER BY OBJECTID", commandTimeout: 600);

    stats.QueryTime += timer.ElapsedMilliseconds;
    Log.Debug($"Query completed: {timer.ElapsedMilliseconds.FriendlyFormat()}");
    timer.Stop();

    var numberOfRecords = ((List<dynamic>)rows).Count;
    stats.TotalRows += numberOfRecords;
    var hashesAsOfNow = new StringBuilder();

    Log.Debug($"Hashing {numberOfRecords} records");
    timer = Stopwatch.StartNew();
    foreach (var row in rows.Cast<IDictionary<string, object>>()) {
      var hashMe = new List<string>(row.Keys.Count);
      byte[]? shapeBinary;

      foreach (var field in row.Keys) {
        if (field == "shape") {
          shapeBinary = row["shape"] as byte[];

          if (shapeBinary is null) {
            continue;
          }

          hashMe.Add(Encoding.UTF8.GetString(shapeBinary, 0, shapeBinary.Length));

          continue;
        }

        var value = Convert.ToString(row[field]);

        if (value is null) {
          continue;
        }

        hashMe.Add(value);
      }

      var hash = XXHash.Hash64(Encoding.UTF8.GetBytes(string.Concat(hashMe))).ToString();

      hashesAsOfNow.Append(hash);
    }

    var result = XXHash.Hash64(Encoding.UTF8.GetBytes(hashesAsOfNow.ToString()));

    stats.HashTime += timer.ElapsedMilliseconds;
    Log.Debug($"Hashing completed: {timer.ElapsedMilliseconds.FriendlyFormat()}");
    timer.Stop();

    return Convert.ToString(result);
  }
}
