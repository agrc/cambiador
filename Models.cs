
namespace cambiador.Models {
  public class FieldMetadata {
    public string Table { get; set; }
    public string Field { get; set; }
    public string FieldType { get; set; }
  }

  public class TableMetadata {
    public string Table { get; set; }
    public string Id { get; set; }
    public string Shape { get; set; }
  }
}
