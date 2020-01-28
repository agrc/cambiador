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
    private static async Task Main() {
      Console.WriteLine("Connecting to the databases");
      using var source_connection = Databases.SourceDatabase.GetConnection();
      using var hash_connection = Databases.HashDatabase.GetConnection();
      var timer = new Stopwatch();

      if (!await EnsureChangeDetectinTableExists(source_connection)) {
        Console.WriteLine("ChangeDetection table does not exist. Please create it");

        return;
      }

      Console.WriteLine("Discovering tables and schemas");
      var tableFieldMap = await DiscoverAndGroupTablesWithFields(source_connection);

      foreach (var table in tableFieldMap) {
        var tableName = table.Key;
        var fields = table.Value;

        // create the database table if it doesn't exist yet
        Console.WriteLine($"Ensuring {tableName} exists");
        var tableOfHashes = await EnsureHashTableExists(tableName, hash_connection);

        var hashesAsOfLastRun = new HashSet<string>(await source_connection.QueryAsync<string>($"SELECT [hash] from {tableOfHashes}"));
        var hashesAsOfNow = await CreateHashesFromTable(tableName, fields, source_connection);

        // have we hashed this dataset already?
        if (hashesAsOfLastRun.Count < 1) {
          Console.WriteLine($"Assuming first run for {tableOfHashes}, inserting hashes");

          var inserts = hashesAsOfNow.Select(x => new Insert() { Hash = x });

          timer = Stopwatch.StartNew();
          await BulkInsert(tableOfHashes, hashesAsOfNow, hash_connection);
          Console.WriteLine($"Insert: {timer.ElapsedMilliseconds}");

          await source_connection.ExecuteAsync($"UPDATE ChangeDetection SET last_modified=GETDATE() WHERE table_name=@tableName", new { tableName });

          continue;
        }

        Console.WriteLine("Finding differences");
        timer = Stopwatch.StartNew();
        hashesAsOfLastRun.SymmetricExceptWith(hashesAsOfNow);
        Console.WriteLine($"SymmetricExceptWith: {timer.ElapsedMilliseconds}");
        timer.Stop();

        var numberOfDifferences = hashesAsOfLastRun.Count;
        if (numberOfDifferences > 0) {
          Console.WriteLine($"{numberOfDifferences} differences detected");
          timer = Stopwatch.StartNew();

          await hash_connection.ExecuteAsync($"TRUNCATE TABLE {tableOfHashes}");

          Console.WriteLine($"Truncate: {timer.ElapsedMilliseconds}");

          timer = Stopwatch.StartNew();

          await BulkInsert(tableOfHashes, hashesAsOfNow, hash_connection);

          Console.WriteLine($"Insert: {timer.ElapsedMilliseconds}");

          timer.Stop();

          await source_connection.ExecuteAsync($"UPDATE ChangeDetection SET last_modified=GETDATE() WHERE table_name=@tableName", new { tableName });

          continue;
        }

        Console.WriteLine("No changes since last hash");
      }
    }

    // Creates the table on he first run or returns the name of the table if it already exists
    private static async Task<bool> EnsureChangeDetectinTableExists(SqlConnection connection) {
      const string changeTable = "CHANGEDETECTION";

      return await connection.QueryFirstOrDefaultAsync<bool>($"SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE UPPER(TABLE_NAME)=@changeTable", new { changeTable });
    }

    private static async Task<Dictionary<string, IList<string>>> DiscoverAndGroupTablesWithFields(SqlConnection connection) {
      var skipFields = new List<string> { "gdb_geomattr_data", "globalid", "global_id", "objectid_" };

      var tableMetaQuery = "SELECT LOWER(registry.table_name) as [table], LOWER(registry.rowid_column) as [id], LOWER(shapes.f_geometry_column) as [shape] " +
        "FROM sde_table_registry registry " +
        "INNER JOIN sde_geometry_columns shapes ON registry.table_name = shapes.f_table_name " +
        "WHERE NOT (registry.table_name like 'SDE_%' OR table_name like 'GDB_%')";
      var fieldMetaQuery = "SELECT LOWER(table_name) as [table], LOWER(column_name) as [field], LOWER(data_type) as fieldType " +
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

        if (!tableFieldMap.ContainsKey(meta.Table)) {
          tableFieldMap.Add(meta.Table, new List<string> { meta.Field });

          continue;
        }

        tableFieldMap[meta.Table].Add(meta.Field);
      }

      return tableFieldMap;
    }

    private static async Task<string> EnsureHashTableExists(string table, SqlConnection connection) {
      var tableHashes = $"{table.ToUpperInvariant()}_HASH";
      var tableExists = await connection.QueryFirstOrDefaultAsync<bool>($"SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE UPPER(TABLE_NAME)=@tableHashes", new { tableHashes });

      if (tableExists) {
        return tableHashes;
      }

      var createTableSql = $"CREATE TABLE [{tableHashes}]([id] [int] IDENTITY(1,1) NOT NULL, [hash] [nvarchar](50) NOT NULL)";

      await connection.ExecuteAsync(createTableSql);

      return tableHashes;
    }

    private static async Task<HashSet<string>> CreateHashesFromTable(string table, IEnumerable<string> fields, SqlConnection connection) {
      // get all of the data from the table
      Console.WriteLine($"Querying {table} for all data");
      var timer = Stopwatch.StartNew();
      var rows = await connection.QueryAsync($"SELECT {string.Join(',', fields)} FROM {table}");
      Console.WriteLine($"Query completed: {timer.ElapsedMilliseconds}");
      timer.Stop();

      var numberOfRecords = ((List<dynamic>)rows).Count;
      var hashesAsOfNow = new HashSet<string>(numberOfRecords);

      Console.WriteLine($"Hashing {numberOfRecords} records");
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

        var result = XXHash.Hash64(Encoding.UTF8.GetBytes(string.Join(string.Empty, hashMe))).ToString();

        hashesAsOfNow.Add(result);
      }

      Console.WriteLine($"Hashing completed: {timer.ElapsedMilliseconds}");
      timer.Stop();

      return hashesAsOfNow;
    }

    public static async Task BulkInsert(string table, HashSet<string> hashes, SqlConnection connection) {
      var statements = GenerateBatchInsertStatements(table, hashes);

      foreach (var statement in statements) {
        await connection.ExecuteAsync(statement);
      }
    }

    private static IList<string> GenerateBatchInsertStatements(string table, IReadOnlyCollection<string> values) {
      var insertSql = $"INSERT INTO [{table}] ([hash]) VALUES ";
      var batchSize = 1000;

      var sql = new List<string>(values.Count);
      var numberOfBatches = (int)Math.Ceiling((double)values.Count / batchSize);

      for (var i = 0; i < numberOfBatches; i++) {
        var batch = values.Skip(i * batchSize).Take(batchSize);

        var valuesToInsert = batch.Select(x => $"('{x}')");

        sql.Add(insertSql + string.Join(',', valuesToInsert));
      }

      return sql;
    }
  }
}
