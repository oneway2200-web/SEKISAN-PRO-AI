using ClosedXML.Excel;
using SekisanProAI.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SekisanProAI.Services;

public sealed class GoldenRiverImporter
{
    private readonly DatabaseService _db;
    public GoldenRiverImporter(DatabaseService db) => _db = db;

    public sealed record ImportResult(bool Imported, string Message, int Count, long VersionId);

    public ImportResult Import(string path)
    {
        if (!File.Exists(path)) return new(false, "ファイルが見つかりません。", 0, 0);
        var hash = Sha256(path);
        if (_db.HasHash(hash)) return new(false, "同じファイルはすでに取り込み済みです。", 0, 0);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        List<UnitPriceRecord> rows = ext switch
        {
            ".csv" => ReadCsv(path),
            ".xlsx" => ReadXlsx(path),
            ".xlsm" => ReadXlsx(path),
            _ => throw new NotSupportedException("対応形式は CSV / XLSX / XLSM です。")
        };

        rows = rows.Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.Price > 0).ToList();
        if (rows.Count == 0) return new(false, "単価行を認識できませんでした。列見出しを確認してください。", 0, 0);

        var label = $"{DateTime.Now:yyyy-MM-dd HH:mm} {Path.GetFileName(path)}";
        var vid = _db.AddVersion(label, path, hash);
        foreach (var row in rows) row.VersionId = vid;
        _db.AddPrices(vid, rows);
        return new(true, "Golden River単価を取り込みました。", rows.Count, vid);
    }

    private static List<UnitPriceRecord> ReadXlsx(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var used = ws.RangeUsed();
        if (used is null) return [];
        var data = used.RowsUsed().Select(r => r.Cells().Select(c => c.GetFormattedString()).ToList()).ToList();
        return Normalize(data, path);
    }

    private static List<UnitPriceRecord> ReadCsv(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        try { text = new UTF8Encoding(false,true).GetString(bytes); }
        catch { text = Encoding.GetEncoding(932).GetString(bytes); }

        var rows = new List<List<string>>();
        foreach (var line in text.Replace("\r\n","\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            rows.Add(ParseCsvLine(line));
        }
        return Normalize(rows, path);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q=false;
        for(int i=0;i<line.Length;i++)
        {
            char c=line[i];
            if(c=='"')
            {
                if(q && i+1<line.Length && line[i+1]=='"'){ sb.Append('"'); i++; }
                else q=!q;
            }
            else if(c==',' && !q){ list.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        list.Add(sb.ToString());
        return list;
    }

    private static List<UnitPriceRecord> Normalize(List<List<string>> data, string sourceFile)
    {
        if (data.Count == 0) return [];
        int headerIndex = FindHeaderRow(data);
        if(headerIndex < 0) headerIndex = 0;
        var header = data[headerIndex].Select(NormalizeHeader).ToList();

        int cCode=FindCol(header, ["コード","単価コード","資材コード","code"]);
        int cName=FindCol(header, ["名称","品名","材料名","単価名称","name"]);
        int cSpec=FindCol(header, ["規格","摘要","仕様","spec"]);
        int cUnit=FindCol(header, ["単位","unit"]);
        int cPrice=FindCol(header, ["単価","価格","金額","price"]);
        int cRegion=FindCol(header, ["地区","地域","物価地区","region"]);
        int cSource=FindCol(header, ["出典","資料","単価種別","source"]);

        if (cName < 0) cName = data[headerIndex].Count > 1 ? 1 : 0;
        if (cPrice < 0)
        {
            for (int i=0;i<header.Count;i++)
                if(header[i].Contains("単価") || header[i].Contains("価格")) { cPrice=i; break; }
        }

        var result = new List<UnitPriceRecord>();
        for(int r=headerIndex+1;r<data.Count;r++)
        {
            var row=data[r];
            string Get(int i)=> i>=0 && i<row.Count ? row[i].Trim() : "";
            if (!TryDecimal(Get(cPrice), out var price)) continue;
            var name=Get(cName);
            if(string.IsNullOrWhiteSpace(name)) continue;
            result.Add(new UnitPriceRecord{
                Code=Get(cCode), Name=name, Spec=Get(cSpec), Unit=Get(cUnit),
                Price=price, Region=Get(cRegion), Source=string.IsNullOrWhiteSpace(Get(cSource))?"Golden River":Get(cSource),
                SourceFile=sourceFile
            });
        }
        return result;
    }

    private static int FindHeaderRow(List<List<string>> rows)
    {
        for(int r=0;r<Math.Min(rows.Count,30);r++)
        {
            var s=string.Join("|",rows[r]);
            if((s.Contains("名称")||s.Contains("品名")||s.Contains("材料名")) &&
               (s.Contains("単価")||s.Contains("価格"))) return r;
        }
        return -1;
    }

    private static string NormalizeHeader(string s)=>s.Trim().Replace(" ","").Replace("　","").ToLowerInvariant();

    private static int FindCol(List<string> h, string[] keys)
    {
        for(int i=0;i<h.Count;i++)
            foreach(var k in keys)
                if(h[i].Contains(NormalizeHeader(k))) return i;
        return -1;
    }

    private static bool TryDecimal(string raw, out decimal value)
    {
        raw = raw.Replace(",","").Replace("￥","").Replace("¥","").Trim();
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value)
            || decimal.TryParse(raw, NumberStyles.Any, CultureInfo.GetCultureInfo("ja-JP"), out value);
    }

    private static string Sha256(string path)
    {
        using var sha=SHA256.Create();
        using var fs=File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}