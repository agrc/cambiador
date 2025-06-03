using System.Diagnostics;
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
  private const string getHashSql = $"SELECT [hash] FROM {changeSchema}{changeTable} WHERE LOWER(table_name)=LOWER(@tableName)";
  private const string changeTableExistSql = "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE LOWER(TABLE_NAME)=LOWER(@changeTable)";
  private const string updateHashSql = $"UPDATE {changeSchema}{changeTable} SET last_modified=GETDATE(), [hash]=@hash WHERE LOWER(table_name)=LOWER(@tableName)";
  private const string insertHashSql = $"INSERT INTO {changeSchema}{changeTable} (table_name, last_modified, [hash]) VALUES (@tableName, GETDATE(), @hash)";
  private const string hashExistsSql = $"SELECT 1 FROM {changeSchema}{changeTable} WHERE LOWER(table_name)=LOWER(@tableName)";
  private const string extraTablesSql = $"SELECT table_name as TableName, id FROM {changeSchema}{changeTable} cd WHERE NOT EXISTS (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES s WHERE lower(s.TABLE_CATALOG) + '.' + lower(s.TABLE_SCHEMA) + '.' + lower(s.TABLE_NAME) = cd.table_name)";
  private const string trimTablesSql = $"DELETE FROM {changeSchema}{changeTable} WHERE id IN @ids";

  public static async Task<IEnumerable<string?>> TrimChangeDetectionTablesNotInSource(SqlConnection connection) {
    var tables = await connection.QueryAsync<TrimData>(extraTablesSql);

    if (!tables.Any()) {
      return [];
    }

    await connection.ExecuteAsync(trimTablesSql, new { ids = tables.Select(x => x.Id) });

    return tables.Select(x => x.TableName);
  }

  public static async Task UpsertHash(SqlConnection connection, string tableName, string hashAsOfNow) {
    var recordExists = await connection.QuerySingleOrDefaultAsync<bool>(hashExistsSql, new { tableName });

    if (recordExists) {
      await connection.ExecuteAsync(updateHashSql, new { hash = hashAsOfNow, tableName });

      Log.Debug($"Updated the hash for {tableName}");

      return;
    }

    await connection.ExecuteAsync(insertHashSql, new { hash = hashAsOfNow, tableName });

    Log.Debug($"Inserted a hash for {tableName}");
  }

  // Creates the table on the first run or returns the name of the table if it already exists
  public static async Task<bool> EnsureChangeDetectionTableExists(SqlConnection connection) =>
    await connection.QueryFirstOrDefaultAsync<bool>(changeTableExistSql, new { changeTable });

  public static async Task<Dictionary<string, IList<string>>> DiscoverAndGroupTablesWithFields(SqlConnection connection) {
    var skipFields = new List<string> { "gdb_geomattr_data", "objectid_" };
    var skipSchemas = new[] { "demographic", "elevation", "meta" };

    const string? tableMetaQuery = "SELECT LOWER(table_name) " +
      $"FROM {schema}sde_table_registry registry " +
      "WHERE NOT (table_name like 'SDE_%' OR table_name like 'GDB_%' OR table_name like '%_temp' OR LOWER(owner) in ('demographic', 'elevation', 'meta'))";
    const string fieldMetaQuery = "SELECT LOWER(table_cataLog) as [db], LOWER(table_schema) as [schema], LOWER(table_name) as [table], LOWER(column_name) as [field], LOWER(data_type) as fieldType " +
      "FROM INFORMATION_SCHEMA.COLUMNS " +
      "WHERE table_name IN @tables AND LOWER(column_name) NOT IN @skipFields " +
      "ORDER BY [db], [schema], [table], [field] ASC";

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

      if (meta.FieldType == "geometry") {
        meta.Field = $"{meta.Field}.STAsBinary() as {meta.Field}";
      }

      if (!tableFieldMap.ContainsKey(meta.TableName())) {
        tableFieldMap.Add(meta.TableName(), [meta.Field]);

        continue;
      }

      tableFieldMap[meta.TableName()].Add(meta.Field);
    }

    return tableFieldMap;
  }

  public static async Task<string> CreateHashFromTableRows(string table, IEnumerable<string> fields, SqlConnection connection, Stats stats) {
    // get all of the data from the table
    var timer = Stopwatch.StartNew();

    var rows = await connection
      .QueryAsync($"SELECT {string.Join(',', fields.OrderBy(x => x))} FROM {table} ORDER BY OBJECTID", commandTimeout: 600);

    stats.QueryTime += timer.ElapsedMilliseconds;
    Log.Information($"Query completed: {timer.ElapsedMilliseconds.FriendlyFormat()}");
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
    Log.Information($"Hashing completed: {timer.ElapsedMilliseconds.FriendlyFormat()}, {result}");
    timer.Stop();

    return Convert.ToString(result);
  }

  public static async Task<string> GetHashOfLastRun(SqlConnection connection, string tableName) =>
    await connection.QuerySingleOrDefaultAsync<string>(getHashSql, new { tableName }) ?? string.Empty;
}
