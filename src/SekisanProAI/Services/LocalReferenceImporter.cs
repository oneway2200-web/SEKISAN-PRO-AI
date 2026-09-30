using Microsoft.Data.Sqlite;
using SekisanProAI.Models;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SekisanProAI.Services;

public sealed class LocalReferenceImporter
{
    private readonly string _dbPath;

    public LocalReferenceImporter(DatabaseService db)
    {
        _dbPath = db.DatabasePath;
        EnsureTables();
    }

    private void EnsureTables()
    {
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS local_reference_files(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source_zip TEXT NOT NULL,
            entry_name TEXT NOT NULL,
            entry_type TEXT NOT NULL,
            imported_at TEXT NOT NULL,
            UNIQUE(source_zip, entry_name)
        );
        CREATE TABLE IF NOT EXISTS local_reference_rules(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source_zip TEXT NOT NULL,
            entry_name TEXT NOT NULL,
            rule_key TEXT,
            rule_value TEXT,
            category TEXT,
            imported_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_local_rules_key ON local_reference_rules(rule_key);
        CREATE INDEX IF NOT EXISTS idx_local_rules_category ON local_reference_rules(category);

        CREATE TABLE IF NOT EXISTS grz_catalog(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source_zip TEXT NOT NULL,
            entry_name TEXT NOT NULL,
            file_name TEXT NOT NULL,
            inferred_date TEXT,
            inferred_category TEXT,
            imported_at TEXT NOT NULL,
            UNIQUE(source_zip, entry_name)
        );
        """;
        cmd.ExecuteNonQuery();
    }

    public LocalReferenceStatus ImportSystemZip(string zipPath)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("system.zip が見つかりません。", zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        int fileCount = 0, ruleCount = 0;

        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using var tx = con.BeginTransaction();

        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext is not (".xml" or ".json" or ".csv" or ".txt" or ".ini" or ".config" or ".dat")) continue;

            string text;
            using (var sr = new StreamReader(entry.Open(), DetectEncoding(entry), true))
                text = sr.ReadToEnd();

            InsertFile(con, tx, zipPath, entry.FullName, ext);
            fileCount++;

            foreach (var kv in ExtractRules(entry.FullName, ext, text))
            {
                InsertRule(con, tx, zipPath, entry.FullName, kv.Key, kv.Value, kv.Category);
                ruleCount++;
            }
        }

        tx.Commit();
        return new LocalReferenceStatus
        {
            RuleFiles = fileCount,
            RuleEntries = ruleCount,
            LastImportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            SourceFile = zipPath
        };
    }

    public LocalReferenceStatus ImportUnitPriceZipCatalog(string zipPath)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("単価ZIPが見つかりません。", zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        int grz = 0;
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using var tx = con.BeginTransaction();

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith(".grz", StringComparison.OrdinalIgnoreCase)) continue;
            var fileName = Path.GetFileName(entry.FullName);
            var date = InferDate(fileName);
            var category = InferCategory(fileName);
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
            INSERT INTO grz_catalog(source_zip,entry_name,file_name,inferred_date,inferred_category,imported_at)
            VALUES($s,$e,$f,$d,$c,$i)
            ON CONFLICT(source_zip,entry_name) DO UPDATE SET
              file_name=excluded.file_name,
              inferred_date=excluded.inferred_date,
              inferred_category=excluded.inferred_category,
              imported_at=excluded.imported_at
            """;
            cmd.Parameters.AddWithValue("$s", zipPath);
            cmd.Parameters.AddWithValue("$e", entry.FullName);
            cmd.Parameters.AddWithValue("$f", fileName);
            cmd.Parameters.AddWithValue("$d", date);
            cmd.Parameters.AddWithValue("$c", category);
            cmd.Parameters.AddWithValue("$i", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
            grz++;
        }
        tx.Commit();

        var status = GetStatus();
        status.GrzFiles = grz;
        status.SourceFile = zipPath;
        status.LastImportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return status;
    }

    public LocalReferenceStatus GetStatus()
    {
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();

        int Scalar(string sql)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        var status = new LocalReferenceStatus
        {
            RuleFiles = Scalar("SELECT COUNT(*) FROM local_reference_files"),
            RuleEntries = Scalar("SELECT COUNT(*) FROM local_reference_rules"),
            GrzFiles = Scalar("SELECT COUNT(*) FROM grz_catalog")
        };

        using var last = con.CreateCommand();
        last.CommandText = """
        SELECT imported_at, source_zip FROM (
          SELECT imported_at, source_zip FROM local_reference_files
          UNION ALL
          SELECT imported_at, source_zip FROM grz_catalog
        ) ORDER BY imported_at DESC LIMIT 1
        """;
        using var rd = last.ExecuteReader();
        if (rd.Read())
        {
            status.LastImportedAt = rd.IsDBNull(0) ? "" : rd.GetString(0);
            status.SourceFile = rd.IsDBNull(1) ? "" : rd.GetString(1);
        }
        return status;
    }

    public List<(string Key,string Value,string Category,string Source)> SearchRules(string keyword, int limit = 300)
    {
        var result = new List<(string,string,string,string)>();
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        SELECT rule_key,rule_value,category,entry_name
        FROM local_reference_rules
        WHERE $q='' OR rule_key LIKE $like OR rule_value LIKE $like OR category LIKE $like
        ORDER BY id DESC LIMIT $limit
        """;
        cmd.Parameters.AddWithValue("$q", keyword ?? "");
        cmd.Parameters.AddWithValue("$like", $"%{keyword}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            result.Add((rd.IsDBNull(0)?"":rd.GetString(0), rd.IsDBNull(1)?"":rd.GetString(1),
                        rd.IsDBNull(2)?"":rd.GetString(2), rd.IsDBNull(3)?"":rd.GetString(3)));
        return result;
    }

    private static void InsertFile(SqliteConnection con, SqliteTransaction tx, string zip, string entry, string type)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
        INSERT INTO local_reference_files(source_zip,entry_name,entry_type,imported_at)
        VALUES($s,$e,$t,$i)
        ON CONFLICT(source_zip,entry_name) DO UPDATE SET imported_at=excluded.imported_at
        """;
        cmd.Parameters.AddWithValue("$s", zip);
        cmd.Parameters.AddWithValue("$e", entry);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$i", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    private static void InsertRule(SqliteConnection con, SqliteTransaction tx, string zip, string entry, string key, string value, string category)
    {
        if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(value)) return;
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
        INSERT INTO local_reference_rules(source_zip,entry_name,rule_key,rule_value,category,imported_at)
        VALUES($s,$e,$k,$v,$c,$i)
        """;
        cmd.Parameters.AddWithValue("$s", zip);
        cmd.Parameters.AddWithValue("$e", entry);
        cmd.Parameters.AddWithValue("$k", key ?? "");
        cmd.Parameters.AddWithValue("$v", value ?? "");
        cmd.Parameters.AddWithValue("$c", category ?? "");
        cmd.Parameters.AddWithValue("$i", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    private sealed record RuleRow(string Key,string Value,string Category);

    private static IEnumerable<RuleRow> ExtractRules(string name, string ext, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        string category = InferCategory(name);

        if (ext == ".xml")
        {
            XDocument? doc = null;
            try { doc = XDocument.Parse(text); } catch { }
            if (doc is not null)
            {
                foreach (var el in doc.Descendants())
                {
                    if (!el.HasElements)
                    {
                        var value = el.Value?.Trim() ?? "";
                        if (value.Length > 0 && value.Length < 2000)
                            yield return new RuleRow(el.Name.LocalName, value, category);
                    }
                    foreach (var a in el.Attributes())
                    {
                        if (!string.IsNullOrWhiteSpace(a.Value))
                            yield return new RuleRow($"{el.Name.LocalName}.{a.Name.LocalName}", a.Value, category);
                    }
                }
                yield break;
            }
        }

        if (ext == ".json")
        {
            List<RuleRow>? jsonRows = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                jsonRows = FlattenJson(doc.RootElement, "", category).ToList();
            }
            catch { }

            if (jsonRows is not null)
            {
                foreach (var row in jsonRows)
                    yield return row;
                yield break;
            }
        }

        foreach (var raw in text.Split((char)10))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.Length > 2000) continue;
            int sep = line.IndexOf('=');
            if (sep < 0) sep = line.IndexOf(':');
            if (sep < 0) sep = line.IndexOf(',');
            if (sep > 0)
                yield return new RuleRow(line[..sep].Trim(), line[(sep+1)..].Trim(), category);
            else if (line.Any(char.IsDigit))
                yield return new RuleRow("", line, category);
        }
    }

    private static IEnumerable<RuleRow> FlattenJson(JsonElement e, string prefix, string category)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                foreach (var x in FlattenJson(p.Value, string.IsNullOrEmpty(prefix)?p.Name:$"{prefix}.{p.Name}", category))
                    yield return x;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            int i=0;
            foreach (var v in e.EnumerateArray())
            {
                foreach (var x in FlattenJson(v, $"{prefix}[{i++}]", category))
                    yield return x;
            }
        }
        else
        {
            var v = e.ToString();
            if (!string.IsNullOrWhiteSpace(v))
                yield return new RuleRow(prefix, v, category);
        }
    }

    private static Encoding DetectEncoding(ZipArchiveEntry entry)
    {
        // Golden River/国内積算データはShift-JIS系の可能性があるため登録しておく。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.UTF8;
    }

    private static string InferDate(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        if (digits.Length >= 6)
        {
            for (int i=0;i<=digits.Length-6;i++)
            {
                var s=digits.Substring(i,6);
                if (int.TryParse(s[..4],out var y) && int.TryParse(s.Substring(4,2),out var m) &&
                    y>=2000 && y<=2100 && m>=1 && m<=12) return $"{y:D4}-{m:D2}";
            }
        }
        return "";
    }

    private static string InferCategory(string name)
    {
        string n = name;
        string[] keys = ["飯田","市町","建設物価","積算資料","中部地方整備局","北陸地方整備局","関東農政局","歩掛","経費","週休","端数","展開","予測","地域","単価"];
        return string.Join(" / ", keys.Where(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)));
    }
}