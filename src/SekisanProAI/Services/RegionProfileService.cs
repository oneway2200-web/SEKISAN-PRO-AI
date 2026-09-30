using SekisanProAI.Models;

namespace SekisanProAI.Services;

public sealed class RegionProfileService
{
    private readonly List<RegionProfile> _profiles =
    [
        new() { Agency="長野県", RegionCode="IIDA", RegionName="飯田地区", PriceArea="04：飯田", Notes="Golden Riverの飯田地区単価を優先。対象地域設定ID 5・6・20・21を参照。" },
        new() { Agency="飯田市", RegionCode="IIDA_CITY", RegionName="飯田市", PriceArea="04：飯田", Notes="飯田市案件。Golden River市町単価と飯田地区物価を優先。" },
        new() { Agency="国土交通省", RegionCode="MLIT_CHUBU", RegionName="中部地方整備局", PriceArea="飯田周辺", Notes="中部地方整備局単価・基準を優先し、地域材料は飯田地区を照合。" }
    ];

    public IReadOnlyList<RegionProfile> GetProfiles() => _profiles;
    public RegionProfile Default => _profiles[0];
}