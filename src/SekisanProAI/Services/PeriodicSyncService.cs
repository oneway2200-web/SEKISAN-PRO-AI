using System.IO;
using System.Windows.Threading;

namespace SekisanProAI.Services;

public sealed class PeriodicSyncService : IDisposable
{
    private readonly DatabaseService _db;
    private readonly FolderWatchService _watcher;
    private readonly DispatcherTimer _timer;

    public event Action<string>? StatusChanged;

    public PeriodicSyncService(DatabaseService db, FolderWatchService watcher)
    {
        _db = db;
        _watcher = watcher;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _timer.Tick += (_,_) => Scan();
    }

    public void Start()
    {
        _timer.Start();
        Scan();
    }

    public void Scan()
    {
        var folder = _db.GetSetting("goldenriver_folder", "");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusChanged?.Invoke("Golden River定期同期：監視フォルダ未設定");
            return;
        }
        try
        {
            _watcher.Scan(folder);
            _db.SetSetting("goldenriver_last_periodic_scan", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            StatusChanged?.Invoke($"Golden River定期同期確認：{DateTime.Now:yyyy-MM-dd HH:mm}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Golden River定期同期エラー：{ex.Message}");
        }
    }

    public void Dispose() => _timer.Stop();
}