using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using cambiador.Extensions;
using cambiador.Models;
using Dapper;
using HashDepot;
using Pastel;

namespace cambiador {
  internal class Program {
    private static readonly string changeTable = "CHANGEDETECTION";
    private static readonly string changeSchema = "META.";
    private static readonly string schema = "sde.";
    private static readonly string changeTableExistSql = $"SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE LOWER(TABLE_NAME)=LOWER(@changeTable)";
    private static readonly string updateHashSql = $"UPDATE {changeSchema}{changeTable} SET last_modified=GETDATE(), [hash]=@hash WHERE LOWER(table_name)=LOWER(@tableName)";
    private static readonly string insertHashSql = $"INSERT INTO {changeSchema}{changeTable} (table_name, last_modified, [hash]) VALUES (@tableName, GETDATE(), @hash)";
    private static readonly string getHashSql = $"SELECT [hash] FROM {changeSchema}{changeTable} WHERE LOWER(table_name)=LOWER(@tableName)";
    private static readonly string hashExistsSql = $"SELECT 1 FROM {changeSchema}{changeTable} WHERE LOWER(table_name)=LOWER(@tableName)";
    private static readonly Stats stats = new();


    private static async Task Main() {
      var cambiador = new (string color, string letter)[] {
          ("#185C58", "c"),
          ("#1E736E", "a"),
          ("#248A84", "m"),
          ("#20B2AA", "b"),
          ("#3FBDB6", "i"),
          ("#5EC8C2", "a"),
          ("#7DD3CE", "d"),
          ("#9CDEDA", "o"),
          ("#BBE9E6", "r")
      };
      var totalTime = Stopwatch.StartNew();
      Console.WriteLine($"{string.Join(string.Empty, cambiador.Select(x => x.letter.Pastel(x.color)))} {"v".AsRed() + Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion.AsRed()}");
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
        Console.WriteLine($"Gathering all rows from {tableName.AsBlue()}");
        var hashAsOfNow = await CreateHashFromTableRows(tableName, fields, connection);

        if (string.IsNullOrEmpty(hashAsOfLastRun) || hashAsOfLastRun != hashAsOfNow) {
          await UpsertHash(connection, tableName, hashAsOfNow);

          stats.Changed.Add(tableName);

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
      Console.WriteLine($"Total rows processed: {stats.TotalRows.ToString("#,##").AsBlue()}");
      Console.WriteLine($"Total query time: {stats.QueryTime.FriendlyFormat().AsBlue()}");
      Console.WriteLine($"Total hasing time: {stats.HashTime.FriendlyFormat().AsBlue()}");
      Console.WriteLine($"Total tables changed: {stats.Changed.Count.ToString().AsBlue()}");

      foreach (var table in stats.Changed) {
        Console.WriteLine($"  {table.AsCyan()} updated");
      }
    }

    private static async Task UpsertHash(SqlConnection connection, string tableName, string hashAsOfNow) {
      Console.WriteLine($"    Changes detected    ".AsRedBg());

      var recordExists = await connection.QuerySingleOrDefaultAsync<bool>(hashExistsSql, new { tableName });

      if (recordExists) {
        await connection.ExecuteAsync(updateHashSql, new { hash = hashAsOfNow, tableName });

        Console.WriteLine($"{"Updated".AsMagenta()} the hash for {tableName.AsBlue()}");

        return;
      }

      await connection.ExecuteAsync(insertHashSql, new { hash = hashAsOfNow, tableName });

      Console.WriteLine($"{"Inserted".AsMagenta()} a hash for {tableName.AsBlue()}");
    }

    // Creates the table on he first run or returns the name of the table if it already exists
    private static async Task<bool> EnsureChangeDetectionTableExists(SqlConnection connection) => await connection.QueryFirstOrDefaultAsync<bool>(changeTableExistSql, new { changeTable });

    private static async Task<Dictionary<string, IList<string>>> DiscoverAndGroupTablesWithFields(SqlConnection connection) {
      var skipFields = new List<string> { "gdb_geomattr_data", "objectid_" };

      var tableMetaQuery = "SELECT LOWER(table_name) " +
        $"FROM {schema}sde_table_registry registry " +
        "WHERE NOT (table_name like 'SDE_%' OR table_name like 'GDB_%')";
      var fieldMetaQuery = "SELECT LOWER(table_catalog) as [db], LOWER(table_schema) as [schema], LOWER(table_name) as [table], LOWER(column_name) as [field], LOWER(data_type) as fieldType " +
        "FROM INFORMATION_SCHEMA.COLUMNS " +
        "WHERE table_name IN @tables AND LOWER(column_name) NOT IN @skipFields";

      var tables = await connection.QueryAsync<string>(tableMetaQuery);
      var fieldMeta = await connection.QueryAsync<FieldMetadata>(fieldMetaQuery, new {
        tables,
        skipFields
      });

      var tableFieldMap = new Dictionary<string, IList<string>>(tables.Count());

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
      var timer = Stopwatch.StartNew();

      var rows = await connection.QueryAsync($"SELECT {string.Join(',', fields)} FROM {table} ORDER BY OBJECTID", commandTimeout: 600);

      stats.QueryTime += timer.ElapsedMilliseconds;
      Console.WriteLine($"Query completed: {timer.ElapsedMilliseconds.FriendlyFormat().AsYellow()}");
      timer.Stop();

      var numberOfRecords = ((List<dynamic>)rows).Count;
      stats.TotalRows += numberOfRecords;
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

      stats.HashTime += timer.ElapsedMilliseconds;
      Console.WriteLine($"Hashing completed: {timer.ElapsedMilliseconds.FriendlyFormat().AsYellow()}");
      timer.Stop();

      return Convert.ToString(result);
    }
  }
}
