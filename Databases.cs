using System.Configuration;
using System.Data.SqlClient;

namespace cambiador.Databases {
  internal static class SourceDatabase {
    public static SqlConnection GetConnection() => new(ConfigurationManager.ConnectionStrings["source"].ConnectionString);
  }
}
