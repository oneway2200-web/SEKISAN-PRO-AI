namespace SekisanProAI.Models;

public sealed class EstimateLine
{
    public int No { get; set; }
    public int Page { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Spec { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount => Math.Round(Quantity * UnitPrice, 0, MidpointRounding.AwayFromZero);
    public string PriceSource { get; set; } = "";
    public string MatchStatus { get; set; } = "未確定";
    public double Confidence { get; set; }
    public string Note { get; set; } = "";
}