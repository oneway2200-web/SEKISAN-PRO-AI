namespace SekisanProAI.Models;

public sealed class RegionProfile
{
    public string Agency { get; set; } = "";
    public string RegionCode { get; set; } = "";
    public string RegionName { get; set; } = "";
    public string PriceArea { get; set; } = "";
    public string Notes { get; set; } = "";
    public override string ToString() => $"{Agency} / {RegionName} ({PriceArea})";
}