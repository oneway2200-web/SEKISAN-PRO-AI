using System.IO;
using ClosedXML.Excel;
using SekisanProAI.Models;
using System.Globalization;
using System.Text;

namespace SekisanProAI.Services;

public sealed class HistoricalDataService
{
    private readonly string _db;
    public HistoricalDataService(DatabaseService db)
    {
        _db=db.DatabasePath;
        EnsureTable();
    }

    private void EnsureTable()
    {
        using var con=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}");
        con.Open();
        using var cmd=con.CreateCommand();
        cmd.CommandText="""
        CREATE TABLE IF NOT EXISTS historical_projects(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          bid_date TEXT, agency TEXT, project_name TEXT, category TEXT, location TEXT,
          planned_price REAL, award_price REAL, source_file TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_hist_agency ON historical_projects(agency);
        CREATE INDEX IF NOT EXISTS idx_hist_name ON historical_projects(project_name);
        """;
        cmd.ExecuteNonQuery();
    }

    public int Import(string path)
    {
        var rows=ReadTable(path);
        if(rows.Count<2) return 0;
        int header=FindHeader(rows);
        var h=rows[header].Select(N).ToList();
        int cDate=Col(h,["入札日","開札日","日付","年度"]);
        int cAgency=Col(h,["発注者","機関","発注機関"]);
        int cName=Col(h,["工事名","件名","工事件名"]);
        int cCat=Col(h,["工種","工事種別","業種"]);
        int cLoc=Col(h,["施工場所","場所","工事場所"]);
        int cPlan=Col(h,["予定価格","設計価格","予定額"]);
        int cAward=Col(h,["落札額","落札価格","契約額"]);
        if(cName<0 || cPlan<0 || cAward<0) return 0;

        int count=0;
        using var con=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}");
        con.Open();
        using var tx=con.BeginTransaction();
        for(int r=header+1;r<rows.Count;r++)
        {
            string G(int i)=>i>=0&&i<rows[r].Count?rows[r][i].Trim():"";
            if(string.IsNullOrWhiteSpace(G(cName))) continue;
            if(!D(G(cPlan),out var plan) || !D(G(cAward),out var award)) continue;
            using var cmd=con.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO historical_projects(bid_date,agency,project_name,category,location,planned_price,award_price,source_file) VALUES($d,$a,$n,$c,$l,$p,$w,$s)";
            cmd.Parameters.AddWithValue("$d",G(cDate)); cmd.Parameters.AddWithValue("$a",G(cAgency));
            cmd.Parameters.AddWithValue("$n",G(cName)); cmd.Parameters.AddWithValue("$c",G(cCat));
            cmd.Parameters.AddWithValue("$l",G(cLoc)); cmd.Parameters.AddWithValue("$p",plan);
            cmd.Parameters.AddWithValue("$w",award); cmd.Parameters.AddWithValue("$s",path);
            cmd.ExecuteNonQuery(); count++;
        }
        tx.Commit();
        return count;
    }

    public List<HistoricalProject> SearchSimilar(string keyword, string location, decimal currentAmount, int limit=100)
    {
        var all=new List<HistoricalProject>();
        using var con=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}");
        con.Open();
        using var cmd=con.CreateCommand();
        cmd.CommandText="""
        SELECT id,bid_date,agency,project_name,category,location,planned_price,award_price,source_file
        FROM historical_projects
        WHERE ($q='' OR project_name LIKE $like OR category LIKE $like)
          AND ($loc='' OR location LIKE $loclike)
        ORDER BY id DESC LIMIT 1000
        """;
        cmd.Parameters.AddWithValue("$q",keyword??""); cmd.Parameters.AddWithValue("$like",$"%{keyword}%");
        cmd.Parameters.AddWithValue("$loc",location??""); cmd.Parameters.AddWithValue("$loclike",$"%{location}%");
        using var rd=cmd.ExecuteReader();
        while(rd.Read())
        {
            all.Add(new HistoricalProject{
                Id=rd.GetInt64(0), BidDate=rd.IsDBNull(1)?"":rd.GetString(1), Agency=rd.IsDBNull(2)?"":rd.GetString(2),
                ProjectName=rd.IsDBNull(3)?"":rd.GetString(3), Category=rd.IsDBNull(4)?"":rd.GetString(4),
                Location=rd.IsDBNull(5)?"":rd.GetString(5), PlannedPrice=Convert.ToDecimal(rd.GetDouble(6)),
                AwardPrice=Convert.ToDecimal(rd.GetDouble(7)), SourceFile=rd.IsDBNull(8)?"":rd.GetString(8)
            });
        }
        return all.OrderBy(x=>Distance(x,currentAmount,keyword)).Take(limit).ToList();
    }

    public string Summary(IEnumerable<HistoricalProject> items)
    {
        var list=items.Where(x=>x.PlannedPrice>0&&x.AwardPrice>0).ToList();
        if(list.Count==0) return "比較できる過去工事がありません。";
        var rates=list.Select(x=>x.BidRate).OrderBy(x=>x).ToList();
        var median=rates[rates.Count/2];
        var planMedian=list.Select(x=>x.PlannedPrice).OrderBy(x=>x).ElementAt(list.Count/2);
        return $"類似工事 {list.Count:N0}件 / 落札率中央値 {median:P1} / 予定価格中央値 {planMedian:N0}円";
    }

    private static double Distance(HistoricalProject x, decimal amount, string keyword)
    {
        double d=0;
        if(amount>0&&x.PlannedPrice>0) d+=Math.Abs(Math.Log((double)(x.PlannedPrice/amount)));
        if(!string.IsNullOrWhiteSpace(keyword) &&
           (x.ProjectName.Contains(keyword,StringComparison.OrdinalIgnoreCase)||x.Category.Contains(keyword,StringComparison.OrdinalIgnoreCase))) d-=1;
        return d;
    }

    private static List<List<string>> ReadTable(string path)
    {
        var ext=Path.GetExtension(path).ToLowerInvariant();
        if(ext is ".xlsx" or ".xlsm")
        {
            using var wb=new XLWorkbook(path);
            var used=wb.Worksheets.First().RangeUsed();
            return used is null?[]:used.RowsUsed().Select(r=>r.Cells().Select(c=>c.GetFormattedString()).ToList()).ToList();
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes=File.ReadAllBytes(path); string text;
        try{text=new UTF8Encoding(false,true).GetString(bytes);}catch{text=Encoding.GetEncoding(932).GetString(bytes);}
        return text.Replace("\r\n","\n").Split('\n').Where(x=>!string.IsNullOrWhiteSpace(x)).Select(ParseCsv).ToList();
    }

    private static List<string> ParseCsv(string line)
    {
        var list=new List<string>(); var sb=new StringBuilder(); bool q=false;
        for(int i=0;i<line.Length;i++){var c=line[i]; if(c=='"'){if(q&&i+1<line.Length&&line[i+1]=='"'){sb.Append('"');i++;}else q=!q;}
        else if(c==','&&!q){list.Add(sb.ToString());sb.Clear();}else sb.Append(c);} list.Add(sb.ToString()); return list;
    }
    private static int FindHeader(List<List<string>> rows){for(int r=0;r<Math.Min(rows.Count,30);r++){var s=string.Join("|",rows[r]);if(s.Contains("工事名")&&(s.Contains("予定価格")||s.Contains("設計価格")))return r;}return 0;}
    private static string N(string s)=>s.Replace(" ","").Replace("　","").ToLowerInvariant();
    private static int Col(List<string> h,string[] keys){for(int i=0;i<h.Count;i++)foreach(var k in keys)if(h[i].Contains(N(k)))return i;return -1;}
    private static bool D(string s,out decimal v){s=s.Replace(",","").Replace("円","").Replace("￥","").Trim();return decimal.TryParse(s,NumberStyles.Any,CultureInfo.GetCultureInfo("ja-JP"),out v);}
}