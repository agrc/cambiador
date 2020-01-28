
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
}
