using ClosedXML.Excel;
using SekisanProAI.Models;
using SekisanProAI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using WinForms = System.Windows.Forms;

namespace SekisanProAI;

public partial class MainWindow : Window
{
    private readonly DatabaseService _db = new();
    private readonly GoldenRiverImporter _importer;
    private readonly FolderWatchService _watcher;
    private readonly PdfEstimateParser _pdfParser = new();
    private readonly EstimateEngine _estimateEngine;
    private readonly HistoricalDataService _historical;
    private readonly RegionProfileService _regions = new();
    private readonly SecretStore _secrets;
    private readonly AiCrossCheckService _ai;
    private readonly LocalReferenceImporter _localReferences;
    private readonly PeriodicSyncService _periodicSync;
    private readonly ObservableCollection<EstimateLine> _estimateLines = new();
    private List<HistoricalProject> _lastHistorical = [];
    private string _lastPdfPath = "";

    public MainWindow()
    {
        InitializeComponent();
        _importer = new GoldenRiverImporter(_db);
        _watcher = new FolderWatchService(_importer);
        _estimateEngine = new EstimateEngine(_db);
        _historical = new HistoricalDataService(_db);
        _secrets = new SecretStore(_db);
        _ai = new AiCrossCheckService(_secrets);
        _localReferences = new LocalReferenceImporter(_db);
        _periodicSync = new PeriodicSyncService(_db, _watcher);

        _watcher.StatusChanged += s => Dispatcher.Invoke(() =>
        {
            SyncStatus.Text = s;
            SidebarStatus.Text = s;
            RefreshVersion();
        });
        _periodicSync.StatusChanged += s => Dispatcher.Invoke(() =>
        {
            SidebarStatus.Text = s;
            if (SyncStatus is not null) SyncStatus.Text = s;
        });

        EstimateGrid.ItemsSource = _estimateLines;
        RegionCombo.ItemsSource = _regions.GetProfiles();
        SettingsRegionCombo.ItemsSource = _regions.GetProfiles();
        RegionCombo.SelectedItem = _regions.Default;
        SettingsRegionCombo.SelectedItem = _regions.Default;

        FolderBox.Text = _db.GetSetting("goldenriver_folder", "");
        if (!string.IsNullOrWhiteSpace(FolderBox.Text) && Directory.Exists(FolderBox.Text))
            _watcher.Start(FolderBox.Text);

        RefreshVersion();
        RefreshAiKeyStatus();
        RefreshLocalReferenceStatus();
        UpdateRegionRule();
        _periodicSync.Start();
    }

    private void Show(UIElement panel)
    {
        HomePanel.Visibility = Visibility.Collapsed;
        EstimatePanel.Visibility = Visibility.Collapsed;
        PricePanel.Visibility = Visibility.Collapsed;
        SyncPanel.Visibility = Visibility.Collapsed;
        HistoricalPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
    }

    private RegionProfile SelectedRegion =>
        RegionCombo.SelectedItem as RegionProfile ?? _regions.Default;

    private void Home_Click(object sender, RoutedEventArgs e) { Show(HomePanel); RefreshVersion(); }
    private void Estimate_Click(object sender, RoutedEventArgs e) { Show(EstimatePanel); UpdateEstimateSummary(); }
    private void Price_Click(object sender, RoutedEventArgs e) { Show(PricePanel); Search(); }
    private void Sync_Click(object sender, RoutedEventArgs e) => Show(SyncPanel);
    private void Historical_Click(object sender, RoutedEventArgs e) => Show(HistoricalPanel);
    private void Settings_Click(object sender, RoutedEventArgs e) { Show(SettingsPanel); RefreshAiKeyStatus(); }
    private void History_Click(object sender, RoutedEventArgs e) { Show(HistoryPanel); VersionGrid.ItemsSource = _db.GetVersions(); }

    private void Search_Click(object sender, RoutedEventArgs e) => Search();
    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) Search(); }
    private void Search() => PriceGrid.ItemsSource = _db.Search(SearchBox.Text.Trim());

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog { Description = "Golden RiverのCSV/Excel出力フォルダを選択" };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK) FolderBox.Text = dlg.SelectedPath;
    }

    private void SaveFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        _db.SetSetting("goldenriver_folder", folder);
        _watcher.Start(folder);
        SyncStatus.Text = "設定を保存しました。";
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            System.Windows.MessageBox.Show("有効なフォルダを選択してください。");
            return;
        }
        _watcher.Scan(folder);
        RefreshVersion();
    }

    private void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Golden River出力 (*.csv;*.xlsx;*.xlsm)|*.csv;*.xlsx;*.xlsm|すべてのファイル|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var r = _importer.Import(dlg.FileName);
            SyncStatus.Text = $"{r.Message} {r.Count:N0}件";
            SidebarStatus.Text = SyncStatus.Text;
            RefreshVersion();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "取込エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PDF設計書 (*.pdf)|*.pdf" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            EstimateStatus.Text = "PDFを解析しています...";
            var result = _pdfParser.Parse(dlg.FileName);
            _lastPdfPath = dlg.FileName;
            _estimateLines.Clear();
            foreach (var line in result.Lines) _estimateLines.Add(line);
            ProjectNameBox.Text = string.IsNullOrWhiteSpace(ProjectNameBox.Text)
                ? Path.GetFileNameWithoutExtension(dlg.FileName) : ProjectNameBox.Text;
            EstimateStatus.Text = $"PDF {result.PageCount}ページから {_estimateLines.Count:N0} 行を抽出しました。";
            if (_estimateLines.Count == 0)
                EstimateStatus.Text += " 文字PDFでない場合はAI/PDF画像解析機能の追加確認が必要です。";
            SidebarStatus.Text = EstimateStatus.Text;
            UpdateEstimateSummary();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "PDF解析エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MatchPrices_Click(object sender, RoutedEventArgs e)
    {
        if (_estimateLines.Count == 0)
        {
            System.Windows.MessageBox.Show("先に設計書PDFを読み込んでください。");
            return;
        }
        _estimateEngine.ApplyGoldenRiverPrices(_estimateLines, SelectedRegion);
        EstimateGrid.Items.Refresh();
        var green = _estimateLines.Count(x => x.MatchStatus.Contains("確定"));
        var yellow = _estimateLines.Count(x => x.MatchStatus.Contains("要確認"));
        var red = _estimateLines.Count(x => x.MatchStatus.Contains("未確定"));
        EstimateStatus.Text = $"Golden River照合完了：🟢 {green}件 / 🟡 {yellow}件 / 🔴 {red}件";
        SidebarStatus.Text = EstimateStatus.Text;
        UpdateEstimateSummary();
    }

    private void CompareHistory_Click(object sender, RoutedEventArgs e)
    {
        var amount = _estimateLines.Sum(x => x.Amount);
        var keyword = ProjectNameBox.Text.Trim();
        var loc = ProjectLocationBox.Text.Trim();
        _lastHistorical = _historical.SearchSimilar(keyword, loc, amount, 80);
        if (_lastHistorical.Count == 0 && !string.IsNullOrWhiteSpace(keyword))
            _lastHistorical = _historical.SearchSimilar("", loc, amount, 80);
        HistoricalGrid.ItemsSource = _lastHistorical;
        HistoricalSummaryText.Text = "過去比較：" + _historical.Summary(_lastHistorical);
        HistorySummaryText.Text = _historical.Summary(_lastHistorical);
    }

    private async void AiCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_estimateLines.Count == 0)
        {
            System.Windows.MessageBox.Show("照合する積算行がありません。");
            return;
        }
        AiCheckButton.IsEnabled = false;
        AiResultBox.Text = "AI照合中...";
        try
        {
            var results = await _ai.CheckAsync(_estimateLines, SelectedRegion);
            AiResultBox.Text = string.Join("\n\n", results.Select(r =>
                r.Success ? $"【{r.Provider}】\n{r.Result}" : $"【{r.Provider}】エラー\n{r.Error}"));
        }
        finally
        {
            AiCheckButton.IsEnabled = true;
        }
    }

    private void ExportEstimate_Click(object sender, RoutedEventArgs e)
    {
        if (_estimateLines.Count == 0)
        {
            System.Windows.MessageBox.Show("出力する積算結果がありません。");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = $"{SafeName(ProjectNameBox.Text)}_積算結果.xlsx"
        };
        if (dlg.ShowDialog() != true) return;

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("積算結果");
        string[] headers = ["No","頁","判定","コード","名称","規格","単位","数量","単価","金額","一致度","根拠"];
        for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        int row = 2;
        foreach (var x in _estimateLines)
        {
            ws.Cell(row,1).Value=x.No; ws.Cell(row,2).Value=x.Page; ws.Cell(row,3).Value=x.MatchStatus;
            ws.Cell(row,4).Value=x.Code; ws.Cell(row,5).Value=x.Name; ws.Cell(row,6).Value=x.Spec;
            ws.Cell(row,7).Value=x.Unit; ws.Cell(row,8).Value=x.Quantity; ws.Cell(row,9).Value=x.UnitPrice;
            ws.Cell(row,10).Value=x.Amount; ws.Cell(row,11).Value=x.Confidence; ws.Cell(row,12).Value=x.Note;
            row++;
        }
        ws.Cell(row+1,9).Value="直接工事費（抽出分）";
        ws.Cell(row+1,10).Value=_estimateLines.Sum(x=>x.Amount);
        ws.Columns().AdjustToContents();

        var aiws=wb.Worksheets.Add("AI照合");
        aiws.Cell(1,1).Value=AiResultBox.Text;
        aiws.Column(1).Width=120;
        aiws.Cell(1,1).Style.Alignment.WrapText=true;

        var hws=wb.Worksheets.Add("過去工事比較");
        string[] hh=["日付","発注者","工事名","工種","場所","予定価格","落札額","落札率"];
        for(int i=0;i<hh.Length;i++)hws.Cell(1,i+1).Value=hh[i];
        row=2;
        foreach(var h in _lastHistorical)
        {
            hws.Cell(row,1).Value=h.BidDate; hws.Cell(row,2).Value=h.Agency; hws.Cell(row,3).Value=h.ProjectName;
            hws.Cell(row,4).Value=h.Category; hws.Cell(row,5).Value=h.Location; hws.Cell(row,6).Value=h.PlannedPrice;
            hws.Cell(row,7).Value=h.AwardPrice; hws.Cell(row,8).Value=h.BidRate; row++;
        }
        hws.Columns().AdjustToContents();
        wb.SaveAs(dlg.FileName);
        EstimateStatus.Text = $"Excel出力しました：{dlg.FileName}";
    }

    private void HistoryImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "入札結果 (*.csv;*.xlsx;*.xlsm)|*.csv;*.xlsx;*.xlsm|すべてのファイル|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var count = _historical.Import(dlg.FileName);
            HistorySummaryText.Text = $"{Path.GetFileName(dlg.FileName)} から {count:N0}件を登録しました。";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "過去工事取込エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void HistorySearch_Click(object sender, RoutedEventArgs e)
    {
        _lastHistorical = _historical.SearchSimilar(HistoryKeywordBox.Text.Trim(), HistoryLocationBox.Text.Trim(), 0, 200);
        HistoricalGrid.ItemsSource = _lastHistorical;
        HistorySummaryText.Text = _historical.Summary(_lastHistorical);
    }


    private void ImportSystemZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "system.zip / ZIP (*.zip)|*.zip|すべてのファイル|*.*",
            Title = "Golden River / 長野県の system.zip を選択"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var r = _localReferences.ImportSystemZip(dlg.FileName);
            LocalReferenceStatusText.Text = $"system.zip取込完了：対象ファイル {r.RuleFiles:N0} / 設定候補 {r.RuleEntries:N0}件";
            SidebarStatus.Text = LocalReferenceStatusText.Text;
            RefreshLocalReferenceStatus();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "system.zip取込エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportUnitZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Golden River単価ZIP (*.zip)|*.zip|すべてのファイル|*.*",
            Title = "Golden Riverの単価ZIPを選択"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var r = _localReferences.ImportUnitPriceZipCatalog(dlg.FileName);
            LocalReferenceStatusText.Text = $"単価ZIP一覧取込完了：GRZ {r.GrzFiles:N0}件。金額はCSV/Excel同期を使用します。";
            SidebarStatus.Text = LocalReferenceStatusText.Text;
            RefreshLocalReferenceStatus();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "単価ZIP取込エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SearchLocalRules_Click(object sender, RoutedEventArgs e)
    {
        var rows = _localReferences.SearchRules(LocalRuleSearchBox.Text.Trim(), 200);
        LocalRuleResultBox.Text = rows.Count == 0
            ? "該当するローカル設定がありません。"
            : string.Join("\n", rows.Select(x =>
                $"[{x.Category}] {x.Key} = {x.Value}  ({x.Source})"));
    }

    private void RefreshLocalReferenceStatus()
    {
        if (LocalReferenceStatusText is null) return;
        var r = _localReferences.GetStatus();
        LocalReferenceStatusText.Text =
            $"ローカル参照：設定ファイル {r.RuleFiles:N0} / 設定候補 {r.RuleEntries:N0} / GRZ一覧 {r.GrzFiles:N0}" +
            (string.IsNullOrWhiteSpace(r.LastImportedAt) ? "" : $" / 最終取込 {r.LastImportedAt}");
    }

    private void SaveAiKeys_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(OpenAiKeyBox.Password)) _secrets.Save("openai_key", OpenAiKeyBox.Password.Trim());
        if (!string.IsNullOrWhiteSpace(AnthropicKeyBox.Password)) _secrets.Save("anthropic_key", AnthropicKeyBox.Password.Trim());
        if (!string.IsNullOrWhiteSpace(GeminiKeyBox.Password)) _secrets.Save("gemini_key", GeminiKeyBox.Password.Trim());
        OpenAiKeyBox.Clear(); AnthropicKeyBox.Clear(); GeminiKeyBox.Clear();
        RefreshAiKeyStatus();
    }

    private void RefreshAiKeyStatus()
    {
        string S(string key) => string.IsNullOrWhiteSpace(_secrets.Load(key)) ? "未設定" : "設定済";
        AiKeyStatusText.Text = $"OpenAI: {S("openai_key")} / Claude: {S("anthropic_key")} / Gemini: {S("gemini_key")}";
    }

    private void RegionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateRegionRule();
        if (RegionCombo.SelectedItem is RegionProfile r && SettingsRegionCombo is not null)
            SettingsRegionCombo.SelectedItem = r;
    }

    private void SettingsRegionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SettingsRegionCombo.SelectedItem is RegionProfile r)
        {
            SettingsRegionText.Text = $"{r.Agency} / {r.RegionName}\n物価地区：{r.PriceArea}\n{r.Notes}";
            if (RegionCombo is not null) RegionCombo.SelectedItem = r;
        }
    }

    private void UpdateRegionRule()
    {
        if (RegionRuleText is null) return;
        var r = SelectedRegion;
        RegionRuleText.Text = $"地域設定：{r.Agency} / {r.RegionName} / {r.PriceArea} — {r.Notes}";
    }

    private void UpdateEstimateSummary()
    {
        if (DirectCostText is null) return;
        DirectCostText.Text = $"直接工事費（抽出・照合分）：{_estimateLines.Sum(x => x.Amount):N0}円";
    }

    private void RefreshVersion()
    {
        var v = _db.GetVersions().FirstOrDefault();
        CurrentVersionText.Text = v is null ? "まだ単価データがありません。" :
            $"最新: {v.ImportedAt} / {v.RecordCount:N0}件 / {Path.GetFileName(v.SourceFile)}";
    }

    private static string SafeName(string name)
    {
        var s = string.IsNullOrWhiteSpace(name) ? "新規工事" : name;
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    protected override void OnClosed(EventArgs e)
    {
        _periodicSync.Dispose();
        _watcher.Dispose();
        base.OnClosed(e);
    }
}