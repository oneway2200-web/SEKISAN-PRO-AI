using System.IO;
using Microsoft.Data.Sqlite;
using SekisanProAI.Models;

namespace SekisanProAI.Services;

public sealed class DatabaseService
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public DatabaseService()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SEKISAN_PRO_AI");
        Directory.CreateDirectory(root);
        _dbPath = Path.Combine(root, "sekisan.db");
        _connectionString = $"Data Source={_dbPath}";
        Initialize();
    }

    public string DatabasePath => _dbPath;

    private void Initialize()
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        var sql = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS price_versions(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            label TEXT NOT NULL,
            imported_at TEXT NOT NULL,
            source_file TEXT NOT NULL,
            file_hash TEXT NOT NULL UNIQUE,
            record_count INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS unit_prices(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            version_id INTEGER NOT NULL,
            code TEXT,
            name TEXT,
            spec TEXT,
            unit TEXT,
            price REAL NOT NULL,
            region TEXT,
            source TEXT,
            source_file TEXT,
            FOREIGN KEY(version_id) REFERENCES price_versions(id)
        );
        CREATE INDEX IF NOT EXISTS idx_unit_prices_code ON unit_prices(code);
        CREATE INDEX IF NOT EXISTS idx_unit_prices_name ON unit_prices(name);
        CREATE INDEX IF NOT EXISTS idx_unit_prices_version ON unit_prices(version_id);
        CREATE TABLE IF NOT EXISTS settings(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public string GetSetting(string key, string fallback = "")
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar()?.ToString() ?? fallback;
    }

    public void SetSetting(string key, string value)
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    public bool HasHash(string hash)
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM price_versions WHERE file_hash=$hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public long AddVersion(string label, string sourceFile, string hash)
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO price_versions(label,imported_at,source_file,file_hash,record_count) VALUES($l,$i,$s,$h,0); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$l", label);
        cmd.Parameters.AddWithValue("$i", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$s", sourceFile);
        cmd.Parameters.AddWithValue("$h", hash);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void AddPrices(long versionId, IEnumerable<UnitPriceRecord> rows)
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        using var tx = con.BeginTransaction();
        int count = 0;
        foreach (var r in rows)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
            INSERT INTO unit_prices(version_id,code,name,spec,unit,price,region,source,source_file)
            VALUES($v,$c,$n,$sp,$u,$p,$r,$so,$sf)
            """;
            cmd.Parameters.AddWithValue("$v", versionId);
            cmd.Parameters.AddWithValue("$c", r.Code);
            cmd.Parameters.AddWithValue("$n", r.Name);
            cmd.Parameters.AddWithValue("$sp", r.Spec);
            cmd.Parameters.AddWithValue("$u", r.Unit);
            cmd.Parameters.AddWithValue("$p", r.Price);
            cmd.Parameters.AddWithValue("$r", r.Region);
            cmd.Parameters.AddWithValue("$so", r.Source);
            cmd.Parameters.AddWithValue("$sf", r.SourceFile);
            cmd.ExecuteNonQuery();
            count++;
        }
        using var update = con.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE price_versions SET record_count=$c WHERE id=$id";
        update.Parameters.AddWithValue("$c", count);
        update.Parameters.AddWithValue("$id", versionId);
        update.ExecuteNonQuery();
        tx.Commit();
    }

    public List<PriceVersion> GetVersions()
    {
        var list = new List<PriceVersion>();
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id,label,imported_at,source_file,file_hash,record_count FROM price_versions ORDER BY id DESC";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new PriceVersion {
                Id = rd.GetInt64(0),
                Label = rd.GetString(1),
                ImportedAt = rd.GetString(2),
                SourceFile = rd.GetString(3),
                FileHash = rd.GetString(4),
                RecordCount = rd.GetInt32(5)
            });
        }
        return list;
    }

    public List<UnitPriceRecord> Search(string keyword, int limit = 300)
    {
        var list = new List<UnitPriceRecord>();
        using var con = new SqliteConnection(_connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        SELECT u.id,u.version_id,u.code,u.name,u.spec,u.unit,u.price,u.region,u.source,u.source_file
        FROM unit_prices u
        JOIN price_versions v ON v.id=u.version_id
        WHERE ($q='' OR u.code LIKE $like OR u.name LIKE $like OR u.spec LIKE $like)
        ORDER BY u.version_id DESC, u.name
        LIMIT $limit
        """;
        cmd.Parameters.AddWithValue("$q", keyword);
        cmd.Parameters.AddWithValue("$like", $"%{keyword}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new UnitPriceRecord {
                Id=rd.GetInt64(0), VersionId=rd.GetInt64(1),
                Code=rd.IsDBNull(2)?"":rd.GetString(2),
                Name=rd.IsDBNull(3)?"":rd.GetString(3),
                Spec=rd.IsDBNull(4)?"":rd.GetString(4),
                Unit=rd.IsDBNull(5)?"":rd.GetString(5),
                Price=Convert.ToDecimal(rd.GetDouble(6)),
                Region=rd.IsDBNull(7)?"":rd.GetString(7),
                Source=rd.IsDBNull(8)?"":rd.GetString(8),
                SourceFile=rd.IsDBNull(9)?"":rd.GetString(9)
            });
        }
        return list;
    }
}