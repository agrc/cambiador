
using System.Collections.Generic;

namespace cambiador.Models {
  public class FieldMetadata {
    public string Table { get; set; }
    public string Field { get; set; }
    public string FieldType { get; set; }
    public string Schema { get; set; }
    public string TableName() {
      if (string.IsNullOrEmpty(Schema)) {
        return Table;
      }

      return $"{Schema}.{Table}";
    }
  }

  public class Stats {
    public int TotalRows { get; set; } = 0;
    public ICollection<string> Changed { get; set; } = new List<string>(10);
    public long QueryTime { get; set; } = 0;
    public long HashTime { get; set; } = 0;
  }
}
