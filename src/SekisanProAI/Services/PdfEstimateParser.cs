using SekisanProAI.Models;
using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace SekisanProAI.Services;

public sealed class PdfEstimateParser
{
    private static readonly string[] Units =
    [
        "m3","m³","m2","m²","m","kg","t","本","個","基","式","箇所","ヶ所","日","台","枚","袋","組","人","時間"
    ];

    public sealed record ParseResult(List<EstimateLine> Lines, string FullText, int PageCount);

    public ParseResult Parse(string path)
    {
        var lines = new List<EstimateLine>();
        var full = new System.Text.StringBuilder();
        int no = 1;
        using var doc = PdfDocument.Open(path);
        foreach (var page in doc.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page, true);
            full.AppendLine($"--- PAGE {page.Number} ---");
            full.AppendLine(text);
            foreach (var raw in text.Replace("\r\n","\n").Split('\n'))
            {
                var parsed = TryParseLine(raw, page.Number, no);
                if (parsed is null) continue;
                lines.Add(parsed);
                no++;
            }
        }
        return new(lines, full.ToString(), doc.NumberOfPages);
    }

    private static EstimateLine? TryParseLine(string raw, int page, int no)
    {
        var line = Regex.Replace(raw ?? "", @"\s+", " ").Trim();
        if (line.Length < 4) return null;

        // 例: 再生クラッシャラン RC-40 120 m3
        var unitPattern = string.Join("|", Units.Select(Regex.Escape));
        var m = Regex.Match(line, $@"^(?<desc>.+?)\s+(?<qty>[0-9][0-9,]*(?:\.[0-9]+)?)\s*(?<unit>{unitPattern})\b", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            // 例: 再生クラッシャラン RC-40 m3 120
            m = Regex.Match(line, $@"^(?<desc>.+?)\s+(?<unit>{unitPattern})\s+(?<qty>[0-9][0-9,]*(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
        }
        if (!m.Success) return null;

        if (!decimal.TryParse(m.Groups["qty"].Value.Replace(",",""), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty))
            return null;
        if (qty <= 0) return null;

        var desc = m.Groups["desc"].Value.Trim();
        if (desc.Length < 2) return null;

        var (name,spec)=SplitNameSpec(desc);
        return new EstimateLine
        {
            No=no, Page=page, Name=name, Spec=spec,
            Unit=m.Groups["unit"].Value, Quantity=qty,
            MatchStatus="🔴 未照合", Note="PDFから自動抽出"
        };
    }

    private static (string Name,string Spec) SplitNameSpec(string desc)
    {
        // 規格らしい後半を緩く分離。完全一致はAI/Golden River照合で補正する。
        var tokens=desc.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if(tokens.Length<=1) return (desc,"");
        int idx=-1;
        for(int i=1;i<tokens.Length;i++)
        {
            var t=tokens[i];
            if(Regex.IsMatch(t, @"[0-9]|φ|Φ|A$|K$|mm|cm|MPa|RC-|7\.5", RegexOptions.IgnoreCase))
            { idx=i; break; }
        }
        if(idx<0) return (desc,"");
        return (string.Join(" ",tokens.Take(idx)), string.Join(" ",tokens.Skip(idx)));
    }
}