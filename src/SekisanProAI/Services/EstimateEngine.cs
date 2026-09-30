using SekisanProAI.Models;
using System.Text;

namespace SekisanProAI.Services;

public sealed class EstimateEngine
{
    private readonly DatabaseService _db;
    public EstimateEngine(DatabaseService db) => _db = db;

    public void ApplyGoldenRiverPrices(IList<EstimateLine> lines, RegionProfile region)
    {
        var latest = _db.GetVersions().FirstOrDefault();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Name)) continue;
            var candidates = _db.Search(line.Name, 250)
                .Where(x => latest is null || x.VersionId == latest.Id)
                .ToList();

            var scored = candidates
                .Select(x => (Price:x, Score:Score(line, x, region)))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Price.VersionId)
                .FirstOrDefault();

            if (scored.Price is null || scored.Score < .40)
            {
                line.UnitPrice = 0;
                line.MatchStatus = "🔴 未確定";
                line.Confidence = 0;
                line.PriceSource = "";
                line.Note = "Golden River最新単価に十分な候補がありません。";
                continue;
            }

            line.Code = scored.Price.Code;
            line.UnitPrice = scored.Price.Price;
            line.PriceSource = $"Golden River / {scored.Price.Source}";
            line.Confidence = Math.Round(scored.Score, 2);
            if (scored.Score >= .82)
            {
                line.MatchStatus = "🟢 確定候補";
                line.Note = "名称・規格・単位の一致度が高い候補です。";
            }
            else
            {
                line.MatchStatus = "🟡 要確認";
                line.Note = $"候補: {scored.Price.Name} {scored.Price.Spec} / {scored.Price.Region}";
            }
        }
    }

    private static double Score(EstimateLine line, UnitPriceRecord p, RegionProfile region)
    {
        string a=N(line.Name), b=N(p.Name), sa=N(line.Spec), sb=N(p.Spec);
        double score=0;
        if(a==b && a.Length>0) score += .62;
        else if(a.Length>1 && b.Length>1 && (a.Contains(b)||b.Contains(a))) score += .50;
        else score += .42 * TokenOverlap(a,b);

        if(!string.IsNullOrWhiteSpace(sa) && !string.IsNullOrWhiteSpace(sb))
        {
            if(sa==sb) score += .25;
            else if(sa.Contains(sb)||sb.Contains(sa)) score += .16;
            else score += .12 * TokenOverlap(sa,sb);
        }

        if(!string.IsNullOrWhiteSpace(line.Unit) && !string.IsNullOrWhiteSpace(p.Unit))
            score += N(line.Unit)==N(p.Unit) ? .10 : -.05;

        if(!string.IsNullOrWhiteSpace(p.Region) &&
           (p.Region.Contains("飯田") || region.PriceArea.Contains("飯田"))) score += .05;

        return Math.Clamp(score,0,1);
    }

    private static string N(string s)
    {
        var sb=new StringBuilder();
        foreach(var c in (s??"").Normalize(NormalizationForm.FormKC).ToUpperInvariant())
            if(char.IsLetterOrDigit(c) || c=='φ' || c=='Φ') sb.Append(c);
        return sb.ToString();
    }

    private static double TokenOverlap(string a,string b)
    {
        if(a.Length==0||b.Length==0) return 0;
        var shortS=a.Length<=b.Length?a:b;
        var longS=a.Length>b.Length?a:b;
        if(shortS.Length<=2) return longS.Contains(shortS)?1:0;
        int hit=0,total=0;
        for(int i=0;i<=shortS.Length-2;i++)
        {
            total++;
            if(longS.Contains(shortS.Substring(i,2))) hit++;
        }
        return total==0?0:(double)hit/total;
    }
}