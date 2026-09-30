namespace SekisanProAI.Models;

public sealed class UnitPriceRecord
{
    public long Id { get; set; }
    public long VersionId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Spec { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Price { get; set; }
    public string Region { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceFile { get; set; } = "";
}