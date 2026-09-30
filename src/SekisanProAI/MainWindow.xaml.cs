using Microsoft.Win32;
using SekisanProAI.Services;
using System.Windows;
using System.Windows.Input;
using WinForms = System.Windows.Forms;

namespace SekisanProAI;

public partial class MainWindow : Window
{
    private readonly DatabaseService _db = new();
    private readonly GoldenRiverImporter _importer;
    private readonly FolderWatchService _watcher;

    public MainWindow()
    {
        InitializeComponent();
        _importer = new GoldenRiverImporter(_db);
        _watcher = new FolderWatchService(_importer);
        _watcher.StatusChanged += s => Dispatcher.Invoke(() => {
            SyncStatus.Text=s; SidebarStatus.Text=s; RefreshVersion();
        });

        FolderBox.Text = _db.GetSetting("goldenriver_folder", "");
        if(!string.IsNullOrWhiteSpace(FolderBox.Text) && Directory.Exists(FolderBox.Text))
            _watcher.Start(FolderBox.Text);
        RefreshVersion();
    }

    private void Show(UIElement panel)
    {
        HomePanel.Visibility=Visibility.Collapsed;
        PricePanel.Visibility=Visibility.Collapsed;
        SyncPanel.Visibility=Visibility.Collapsed;
        HistoryPanel.Visibility=Visibility.Collapsed;
        panel.Visibility=Visibility.Visible;
    }

    private void Home_Click(object sender,RoutedEventArgs e){ Show(HomePanel); RefreshVersion(); }
    private void Price_Click(object sender,RoutedEventArgs e){ Show(PricePanel); Search(); }
    private void Sync_Click(object sender,RoutedEventArgs e)=>Show(SyncPanel);
    private void History_Click(object sender,RoutedEventArgs e){ Show(HistoryPanel); VersionGrid.ItemsSource=_db.GetVersions(); }

    private void Search_Click(object sender,RoutedEventArgs e)=>Search();
    private void SearchBox_KeyDown(object sender,System.Windows.Input.KeyEventArgs e){ if(e.Key==Key.Enter) Search(); }
    private void Search()=>PriceGrid.ItemsSource=_db.Search(SearchBox.Text.Trim());

    private void ChooseFolder_Click(object sender,RoutedEventArgs e)
    {
        using var dlg=new WinForms.FolderBrowserDialog{Description="Golden RiverのCSV/Excel出力フォルダを選択"};
        if(dlg.ShowDialog()==WinForms.DialogResult.OK) FolderBox.Text=dlg.SelectedPath;
    }

    private void SaveFolder_Click(object sender,RoutedEventArgs e)
    {
        var folder=FolderBox.Text.Trim();
        _db.SetSetting("goldenriver_folder",folder);
        _watcher.Start(folder);
        SyncStatus.Text="設定を保存しました。";
    }

    private void Scan_Click(object sender,RoutedEventArgs e)
    {
        var folder=FolderBox.Text.Trim();
        if(!Directory.Exists(folder)){ MessageBox.Show("有効なフォルダを選択してください。"); return; }
        _watcher.Scan(folder);
        RefreshVersion();
    }

    private void ImportFile_Click(object sender,RoutedEventArgs e)
    {
        var dlg=new OpenFileDialog{Filter="Golden River出力 (*.csv;*.xlsx;*.xlsm)|*.csv;*.xlsx;*.xlsm|すべてのファイル|*.*"};
        if(dlg.ShowDialog()!=true) return;
        try
        {
            var r=_importer.Import(dlg.FileName);
            SyncStatus.Text=$"{r.Message} {r.Count:N0}件";
            SidebarStatus.Text=SyncStatus.Text;
            RefreshVersion();
        }
        catch(Exception ex){ MessageBox.Show(ex.Message,"取込エラー",MessageBoxButton.OK,MessageBoxImage.Error); }
    }

    private void RefreshVersion()
    {
        var v=_db.GetVersions().FirstOrDefault();
        CurrentVersionText.Text = v is null ? "まだ単価データがありません。" :
            $"最新: {v.ImportedAt} / {v.RecordCount:N0}件 / {Path.GetFileName(v.SourceFile)}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher.Dispose();
        base.OnClosed(e);
    }
}