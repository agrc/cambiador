using System.Configuration;
using System.Data.SqlClient;

namespace ChangeDetection.Databases {
  internal static class SourceDatabase {
    public static SqlConnection GetConnection() => new SqlConnection(ConfigurationManager.ConnectionStrings["source"].ConnectionString);
  }

  internal static class HashDatabase {
    public static SqlConnection GetConnection() => new SqlConnection(ConfigurationManager.ConnectionStrings["hash"].ConnectionString);
  }
}
