namespace SekisanProAI.Models;

public sealed class HistoricalProject
{
    public long Id { get; set; }
    public string BidDate { get; set; } = "";
    public string Agency { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Location { get; set; } = "";
    public decimal PlannedPrice { get; set; }
    public decimal AwardPrice { get; set; }
    public decimal BidRate => PlannedPrice > 0 ? AwardPrice / PlannedPrice : 0;
    public string SourceFile { get; set; } = "";
}