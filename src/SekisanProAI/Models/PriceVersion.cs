namespace SekisanProAI.Models;

public sealed class PriceVersion
{
    public long Id { get; set; }
    public string Label { get; set; } = "";
    public string ImportedAt { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string FileHash { get; set; } = "";
    public int RecordCount { get; set; }
}