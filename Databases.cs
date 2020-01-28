using System.Configuration;
using System.Data.SqlClient;

namespace cambiador.Databases {
  internal static class SourceDatabase {
    public static SqlConnection GetConnection() => new SqlConnection(ConfigurationManager.ConnectionStrings["source"].ConnectionString);
  }
}
