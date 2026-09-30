namespace SekisanProAI.Services;

public sealed class FolderWatchService : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly GoldenRiverImporter _importer;
    public event Action<string>? StatusChanged;

    public FolderWatchService(GoldenRiverImporter importer) => _importer = importer;

    public void Start(string folder)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusChanged?.Invoke("監視フォルダが未設定です。");
            return;
        }

        Scan(folder);
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        _watcher.Created += OnFile;
        _watcher.Changed += OnFile;
        _watcher.Renamed += (_,e)=>TryImport(e.FullPath);
        StatusChanged?.Invoke($"Golden River監視中: {folder}");
    }

    private void OnFile(object sender, FileSystemEventArgs e) => TryImport(e.FullPath);

    public void Scan(string folder)
    {
        foreach (var f in Directory.EnumerateFiles(folder))
            if(IsSupported(f)) TryImport(f);
    }

    private async void TryImport(string path)
    {
        if(!IsSupported(path)) return;
        await Task.Delay(800);
        for(int i=0;i<5;i++)
        {
            try
            {
                var r=_importer.Import(path);
                StatusChanged?.Invoke($"{Path.GetFileName(path)}: {r.Message} {r.Count:N0}件");
                return;
            }
            catch(IOException){ await Task.Delay(800); }
            catch(Exception ex){ StatusChanged?.Invoke($"{Path.GetFileName(path)}: {ex.Message}"); return; }
        }
    }

    private static bool IsSupported(string path)
        => new[]{".csv",".xlsx",".xlsm"}.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void Stop()
    {
        if(_watcher is null) return;
        _watcher.EnableRaisingEvents=false;
        _watcher.Dispose();
        _watcher=null;
    }

    public void Dispose()=>Stop();
}