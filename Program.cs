using cambiador.Extensions;
using cambiador.Models;
using Dapper;
using HashDepot;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace cambiador {
  internal class Program {
    private static readonly string changeTable = "CHANGEDETECTION";
    private static readonly string schema = "sde.";
    private static readonly string updateHashSql = "UPDATE ChangeDetection SET last_modified=GETDATE(), [hash]=@hash WHERE LOWER(table_name)=LOWER(@tableName)";
    private static readonly string insertHashSql = "INSERT INTO ChangeDetection (table_name, last_modified, [hash]) VALUES (@tableName,  GETDATE(), @hash)";
    private static readonly string getHashSql = $"SELECT [hash] FROM {changeTable} WHERE LOWER(table_name)=LOWER(@tableName)";

    private static async Task Main() {
      var totalTime = Stopwatch.StartNew();
      Console.WriteLine($"Connecting to the { "source".AsMagenta() } database");
      using var connection = Databases.SourceDatabase.GetConnection();

      if (!await EnsureChangeDetectionTableExists(connection)) {
        Console.WriteLine($"{changeTable.AsRed()} table does not exist. Please create it");

        return;
      }

      Console.WriteLine($"Discovering {"tables".AsMagenta()} and {"schemas".AsMagenta()}");

      var tableFieldMap = await DiscoverAndGroupTablesWithFields(connection);

      foreach (var table in tableFieldMap) {
        var tableTime = Stopwatch.StartNew();
        var tableName = table.Key;
        var fields = table.Value;

        var hashAsOfLastRun = await connection.QuerySingleOrDefaultAsync<string>(getHashSql, new { tableName });
        var hashAsOfNow = await CreateHashFromTableRows(tableName, fields, connection);

        if (string.IsNullOrEmpty(hashAsOfLastRun) || hashAsOfLastRun != hashAsOfNow) {
          await UpsertHash(connection, tableName, hashAsOfNow);

          Console.WriteLine($"Total table time: {tableTime.ElapsedMilliseconds.FriendlyFormat().AsYellow()}");
          Console.WriteLine();

          continue;
        }

        Console.WriteLine($"Total table time: {tableTime.ElapsedMilliseconds.FriendlyFormat().AsYellow()}");
        tableTime.Stop();
        Console.WriteLine("  No changes since last run  ".AsBlack().AsWhiteBg());
        Console.WriteLine();
      }

      Console.WriteLine();
      Console.WriteLine($"Total process time: {totalTime.ElapsedMilliseconds.FriendlyFormat().AsYellow()}");
    }

    private static async Task UpsertHash(SqlConnection connection, string tableName, string hashAsOfNow) {
      Console.WriteLine($"    Changes detected    ".AsRedBg());

      var recordExists = await connection.QuerySingleOrDefaultAsync<bool>($"SELECT 1 FROM {changeTable} WHERE LOWER(table_name)=LOWER(@tableName)",
        new { tableName });

      if (recordExists) {
        await connection.ExecuteAsync(updateHashSql, new { hash = hashAsOfNow, tableName });

        Console.WriteLine($"{"Updated".AsMagenta()} the hash for {tableName.AsBlue()}");

        return;
      }

      await connection.ExecuteAsync(insertHashSql, new { hash = hashAsOfNow, tableName });

      Console.WriteLine($"{"Inserted".AsMagenta()} a hash for {tableName.AsBlue()}");
    }

    // Creates the table on he first run or returns the name of the table if it already exists
    private static async Task<bool> EnsureChangeDetectionTableExists(SqlConnection connection) => await connection.QueryFirstOrDefaultAsync<bool>($"SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE UPPER(TABLE_NAME)=@changeTable", new { changeTable });

    private static async Task<Dictionary<string, IList<string>>> DiscoverAndGroupTablesWithFields(SqlConnection connection) {
      var skipFields = new List<string> { "gdb_geomattr_data", "globalid", "global_id", "objectid_" };

      var tableMetaQuery = "SELECT LOWER(registry.table_name) as [table], LOWER(registry.rowid_column) as [id], LOWER(shapes.f_geometry_column) as [shape] " +
        $"FROM {schema}sde_table_registry registry " +
        $"INNER JOIN {schema}sde_geometry_columns shapes ON registry.table_name = shapes.f_table_name " +
        "WHERE NOT (registry.table_name like 'SDE_%' OR table_name like 'GDB_%')";
        "WHERE NOT (table_name like 'SDE_%' OR table_name like 'GDB_%')";
      var fieldMetaQuery = "SELECT LOWER(table_schema) as [schema], LOWER(table_name) as [table], LOWER(column_name) as [field], LOWER(data_type) as fieldType " +
        "FROM INFORMATION_SCHEMA.COLUMNS " +
        "WHERE table_name IN @tables AND LOWER(column_name) NOT IN @skipFields";

      var tableMeta = await connection.QueryAsync<TableMetadata>(tableMetaQuery);
      var fieldMeta = await connection.QueryAsync<FieldMetadata>(fieldMetaQuery, new {
        tables = tableMeta.Select(x => x.Table),
        skipFields
      });

      var tableFieldMap = new Dictionary<string, IList<string>>(tableMeta.Count());

      foreach (var meta in fieldMeta) {
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
      Console.WriteLine($"Querying {table.AsBlue()} for all data");
      var timer = Stopwatch.StartNew();

      var rows = await connection.QueryAsync($"SELECT {string.Join(',', fields)} FROM {table} ORDER BY OBJECTID");

      Console.WriteLine($"Query completed: {timer.ElapsedMilliseconds.FriendlyFormat().AsYellow()}");
      timer.Stop();

      var numberOfRecords = ((List<dynamic>)rows).Count;
      var hashesAsOfNow = new StringBuilder();

      Console.WriteLine($"Hashing {numberOfRecords.ToString("#,##").AsCyan()} records");
      timer = Stopwatch.StartNew();
      foreach (var row in rows.Cast<IDictionary<string, object>>()) {
        var hashMe = new List<string>(row.Keys.Count);
        byte[] shapeBinary;

        foreach (var field in row.Keys) {
          if (field == "shape") {
            shapeBinary = row["shape"] as byte[];
            hashMe.Add(Encoding.UTF8.GetString(shapeBinary, 0, shapeBinary.Length));

            continue;
          }

          hashMe.Add(Convert.ToString(row[field]));
        }

        var hash = XXHash.Hash64(Encoding.UTF8.GetBytes(string.Join(string.Empty, hashMe))).ToString();

        hashesAsOfNow.Append(hash);
      }

      var result = XXHash.Hash64(Encoding.UTF8.GetBytes(hashesAsOfNow.ToString()));

      Console.WriteLine($"Hashing completed: {timer.ElapsedMilliseconds.FriendlyFormat().AsYellow()}");
      timer.Stop();

      return Convert.ToString(result);
    }
  }
}
