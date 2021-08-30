using System.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace cambiador.Databases {
  internal static class SourceDatabase {
    public static SqlConnection GetConnection(IConfiguration configuration) => new(configuration.GetConnectionString("source"));
  }
}
