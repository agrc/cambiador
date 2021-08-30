
using System.Collections.Generic;

namespace cambiador.Models {
  public class FieldMetadata {
    public string Db { get; set; } = default!;
    public string Table { get; set; } = default!;
    public string Field { get; set; } = default!;
    public string FieldType { get; set; } = default!;
    public string? Schema { get; set; }
    public string TableName() {
      if (string.IsNullOrEmpty(Schema)) {
        return Table;
      }

      return $"{Db}.{Schema}.{Table}";
    }
  }

  public class Stats {
    public int TotalRows { get; set; }
    public ICollection<string> Changed { get; set; } = new List<string>(10);
    public long QueryTime { get; set; } = 0;
    public long HashTime { get; set; } = 0;
  }

  public class SendGridSettings {
    public string? ApiKey { get; set; }
  }
}
