#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Core;
using RevitMCP.Models;
// Autodesk.Revit.DB.Grid（軸線）與 WPF 版面 Grid 同名，明確指向 WPF。
using Grid = System.Windows.Controls.Grid;

namespace RevitMCP.Commands
{
    /// <summary>
    /// 方案儀表板（非模態）：登記簿清單（含虛擬「原地形」列）＋狀態＋動作；
    /// Revit API 動作經 ExternalEventManager 排隊。2026-07-10 從純清單升級為方案總管介面——
    /// 「整地檢視」「產生分析圖」「出圖」全部併入清單動作，主 Ribbon 面板只留
    /// 「新整地方案」「方案總覽」兩個核心入口（見 2026-07-10-grading-ribbon-consolidation-design.md）。
    /// </summary>
    public class SchemeOverviewWindow : Window
    {
        private static SchemeOverviewWindow _instance;
        private readonly ObservableCollection<SchemeRow> _rows = new ObservableCollection<SchemeRow>();
        private readonly ListView _list;
        private Button _viewSchemeButton;
        private Button _restoreButton;
        private Button _deleteButton;

        public class SchemeRow
        {
            public bool IsOriginal { get; set; }
            public string Name { get; set; }
            public string Mode { get; set; }
            public string Cut { get; set; }
            public string Fill { get; set; }
            public string Total { get; set; }
            public string AnalysisStatus { get; set; }
            public long DesignId { get; set; }
        }

        public static void ShowOrActivate(UIApplication uiApp)
        {
            if (_instance == null)
            {
                _instance = new SchemeOverviewWindow();
                _instance.Closed += (s, e) => _instance = null;
                _instance.Show();
            }
            else
            {
                _instance.Activate();
            }

            _instance.Reload();
        }

        private SchemeOverviewWindow()
        {
            Title = "整地方案總覽";
            Width = 760;
            Height = 420;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _list = new ListView { ItemsSource = _rows, Margin = new Thickness(8), SelectionMode = SelectionMode.Extended };
            var gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn { Header = "方案", Width = 150, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
            gridView.Columns.Add(new GridViewColumn { Header = "模式", Width = 100, DisplayMemberBinding = new System.Windows.Data.Binding("Mode") });
            gridView.Columns.Add(new GridViewColumn { Header = "挖方 m³", Width = 75, DisplayMemberBinding = new System.Windows.Data.Binding("Cut") });
            gridView.Columns.Add(new GridViewColumn { Header = "填方 m³", Width = 75, DisplayMemberBinding = new System.Windows.Data.Binding("Fill") });
            gridView.Columns.Add(new GridViewColumn { Header = "總量 m³", Width = 75, DisplayMemberBinding = new System.Windows.Data.Binding("Total") });
            gridView.Columns.Add(new GridViewColumn { Header = "分析圖", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("AnalysisStatus") });
            _list.View = gridView;
            _list.SelectionChanged += (s, e) => RefreshButtonEnabled();
            Grid.SetRow(_list, 0);
            grid.Children.Add(_list);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            buttons.Children.Add(MakeButton("重新整理", (s, e) => Reload()));
            buttons.Children.Add(MakeButton("看地形", (s, e) => ShowTerrain()));
            _viewSchemeButton = MakeButton("開啟方案視圖", (s, e) => OpenSchemeView());
            buttons.Children.Add(_viewSchemeButton);
            buttons.Children.Add(MakeButton("產生分析圖", (s, e) => GenerateAnalysis()));
            buttons.Children.Add(MakeButton("出圖", (s, e) => ShowOutputMenu()));
            _restoreButton = MakeButton("還原方案", (s, e) => RunOnSelectedSingle("restore_grading_scheme", new JObject(), "還原方案"));
            buttons.Children.Add(_restoreButton);
            _deleteButton = MakeButton("刪除方案", (s, e) => DeleteSelected());
            buttons.Children.Add(_deleteButton);
            buttons.Children.Add(MakeButton("匯出 Excel", (s, e) => RunCommand("export_grading_comparison", new JObject(), "匯出 Excel")));
            Grid.SetRow(buttons, 1);
            grid.Children.Add(buttons);
            Content = grid;
        }

        private static Button MakeButton(string text, RoutedEventHandler onClick)
        {
            var button = new Button { Content = text, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            button.Click += onClick;
            return button;
        }

        private void Reload()
        {
            ExternalEventManager.Instance.ExecuteCommand(app =>
            {
                var response = new CommandExecutor(app).ExecuteCommand(new RevitCommandRequest
                { CommandName = "list_grading_schemes", Parameters = new JObject(), RequestId = "ui-" + Guid.NewGuid().ToString("N") });
                var rows = new List<SchemeRow>();
                if (response.Success)
                {
                    var data = JObject.FromObject(response.Data);
                    var schemes = (data["Schemes"] as JArray ?? new JArray()).ToList();
                    if (schemes.Count > 0)
                    {
                        rows.Add(new SchemeRow
                        {
                            IsOriginal = true,
                            Name = "原地形（拆除前）",
                            Mode = string.Empty,
                            Cut = string.Empty,
                            Fill = string.Empty,
                            Total = string.Empty,
                            AnalysisStatus = string.Empty,
                            DesignId = 0
                        });
                    }

                    foreach (var scheme in schemes)
                    {
                        var cut = scheme.Value<double?>("CutCubicMeters") ?? 0;
                        var fill = scheme.Value<double?>("FillCubicMeters") ?? 0;
                        var analysisAt = scheme.Value<string>("AnalysisGeneratedAt");
                        rows.Add(new SchemeRow
                        {
                            IsOriginal = false,
                            Name = scheme.Value<string>("SchemeName"),
                            Mode = scheme.Value<string>("Mode"),
                            Cut = cut.ToString("0"),
                            Fill = fill.ToString("0"),
                            Total = (cut + fill).ToString("0"),
                            AnalysisStatus = string.IsNullOrEmpty(analysisAt) ? "尚未產生" : "已產生",
                            DesignId = scheme.Value<long?>("DesignToposolidId") ?? 0
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    _rows.Clear();
                    foreach (var row in rows) { _rows.Add(row); }
                    RefreshButtonEnabled();
                });
            });
        }

        private List<SchemeRow> SelectedRows => _list.SelectedItems.Cast<SchemeRow>().ToList();

        private SchemeRow Selected => SelectedRows.FirstOrDefault();

        /// <summary>單選/多選狀態決定哪些動作按鈕可用：開啟方案視圖／還原／刪除僅支援單一真實方案。</summary>
        private void RefreshButtonEnabled()
        {
            var selected = SelectedRows;
            var singleReal = selected.Count == 1 && !selected[0].IsOriginal;
            if (_viewSchemeButton != null) { _viewSchemeButton.IsEnabled = singleReal; }
            if (_restoreButton != null) { _restoreButton.IsEnabled = singleReal; }
            if (_deleteButton != null) { _deleteButton.IsEnabled = singleReal; }
        }

        /// <summary>看地形（原「整地檢視」併入）：對選中列（含原地形）呼叫 show_grading_terrain。</summary>
        private void ShowTerrain()
        {
            var row = Selected;
            if (row == null) { MessageBox.Show("先選一列（方案或原地形）。", Title); return; }
            var parameters = new JObject();
            if (!row.IsOriginal) { parameters["designToposolidId"] = row.DesignId; }
            RunCommand("show_grading_terrain", parameters, "看地形");
        }

        private void OpenSchemeView()
        {
            var row = Selected;
            if (row == null || row.IsOriginal)
            {
                MessageBox.Show("請選一個方案（原地形沒有方案視圖，請用「看地形」）。", Title);
                return;
            }

            ExternalEventManager.Instance.ExecuteCommand(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var json = RevitMCP.Core.Grading.RevitToposolidGradingAdapter.TryReadSchemeRecord(doc, row.DesignId);
                var viewId = json == null ? 0 : JObject.Parse(json).Value<long?>("SchemeViewId") ?? 0;
                if (viewId > 0 && doc.GetElement(new ElementId(checked((int)viewId))) is View view)
                {
                    app.ActiveUIDocument.RequestViewChange(view);
                }
                else
                {
                    Dispatcher.Invoke(() =>
                        MessageBox.Show("這個方案還沒有方案視圖，請先按「產生分析圖」。", Title));
                }
            });
        }

        /// <summary>對選中的方案（支援多選）逐一產生分析圖；原地形不需要分析圖，選到會被忽略。</summary>
        private void GenerateAnalysis()
        {
            var targets = SelectedRows.Where(r => !r.IsOriginal).ToList();
            if (targets.Count == 0) { MessageBox.Show("請至少選一個方案（原地形不需要分析圖）。", Title); return; }
            foreach (var row in targets)
            {
                RunCommand("generate_grading_analysis",
                    new JObject { ["designToposolidId"] = row.DesignId }, $"產生分析圖（{row.Name}）");
            }
        }

        /// <summary>出圖：跳選單選各別出圖／比較出圖；有選列只出選中方案，未選＝全部方案（比照工具既有預設）。</summary>
        private void ShowOutputMenu()
        {
            var kindDialog = new TaskDialog("出圖")
            {
                MainInstruction = "要出哪一種圖？",
                MainContent = "各別出圖＝登記簿方案一案一張排圖；比較出圖＝全部方案排成一張總比較圖紙（C-00c）。"
                    + "清單未選任何列＝出全部方案；有選列＝只出選中的方案。",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                AllowCancellation = true
            };
            kindDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "各別出圖");
            kindDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "比較出圖");
            var kindResult = kindDialog.Show();
            if (kindResult != TaskDialogResult.CommandLink1 && kindResult != TaskDialogResult.CommandLink2)
            {
                return;
            }

            var targets = SelectedRows.Where(r => !r.IsOriginal).Select(r => r.DesignId).ToList();
            var parameters = new JObject { ["export"] = true };
            if (targets.Count > 0) { parameters["designToposolidIds"] = new JArray(targets); }

            if (kindResult == TaskDialogResult.CommandLink1)
            {
                RunCommand("create_grading_presentation", parameters, "各別出圖");
                return;
            }

            // 2026-07-10 使用者裁決：每次比較出圖可選比較表樣式（內建明細表／自訂排版表格）。
            var styleDialog = new TaskDialog("比較出圖")
            {
                MainInstruction = "比較表要用哪種樣式？",
                MainContent = "內建明細表＝Revit 原生 Schedule（可在專案內持續編輯）；"
                    + "自訂排版表格＝分組底色、雙線組界、+餘/-缺 格式的繪製版。",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                AllowCancellation = true
            };
            styleDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "內建明細表");
            styleDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "自訂排版表格");
            var styleResult = styleDialog.Show();
            if (styleResult != TaskDialogResult.CommandLink1 && styleResult != TaskDialogResult.CommandLink2)
            {
                return;
            }

            parameters["scheduleStyle"] = styleResult == TaskDialogResult.CommandLink1 ? "native" : "custom";
            RunCommand("create_grading_comparison_sheet", parameters, "比較出圖");
        }

        private void DeleteSelected()
        {
            var row = Selected;
            if (row == null || row.IsOriginal) { MessageBox.Show("先選一個方案。", Title); return; }
            if (MessageBox.Show($"確定刪除方案「{row.Name}」？（設計地形與方案視圖都會刪除）",
                Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) { return; }
            RunOnSelectedSingle("remove_grading_scheme", new JObject { ["confirm"] = true }, "刪除方案");
        }

        private void RunOnSelectedSingle(string commandName, JObject parameters, string title)
        {
            var row = Selected;
            if (row == null || row.IsOriginal) { MessageBox.Show("先選一個方案。", Title); return; }
            parameters["designToposolidId"] = row.DesignId;
            RunCommand(commandName, parameters, title);
        }

        private void RunCommand(string commandName, JObject parameters, string title)
        {
            ExternalEventManager.Instance.ExecuteCommand(app =>
            {
                var response = new CommandExecutor(app).ExecuteCommand(new RevitCommandRequest
                { CommandName = commandName, Parameters = parameters, RequestId = "ui-" + Guid.NewGuid().ToString("N") });
                Dispatcher.Invoke(() =>
                {
                    if (!response.Success) { MessageBox.Show($"{title}失敗：{response.Error}", Title); }
                    Reload();
                });
            });
        }
    }
}
#endif
