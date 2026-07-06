#if REVIT2024_OR_GREATER
using System;
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
    /// <summary>方案總覽（非模態）：登記簿清單＋數字＋動作；Revit API 動作經 ExternalEventManager 排隊。</summary>
    public class SchemeOverviewWindow : Window
    {
        private static SchemeOverviewWindow _instance;
        private readonly ObservableCollection<SchemeRow> _rows = new ObservableCollection<SchemeRow>();
        private readonly ListView _list;

        public class SchemeRow
        {
            public string Name { get; set; }
            public string Mode { get; set; }
            public string Cut { get; set; }
            public string Fill { get; set; }
            public string Total { get; set; }
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
            Width = 620;
            Height = 380;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _list = new ListView { ItemsSource = _rows, Margin = new Thickness(8) };
            var view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = "方案", Width = 130, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
            view.Columns.Add(new GridViewColumn { Header = "模式", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("Mode") });
            view.Columns.Add(new GridViewColumn { Header = "挖方 m³", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Cut") });
            view.Columns.Add(new GridViewColumn { Header = "填方 m³", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Fill") });
            view.Columns.Add(new GridViewColumn { Header = "總量 m³", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Total") });
            _list.View = view;
            Grid.SetRow(_list, 0);
            grid.Children.Add(_list);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            buttons.Children.Add(MakeButton("重新整理", (s, e) => Reload()));
            buttons.Children.Add(MakeButton("開啟方案視圖", (s, e) => OpenSchemeView()));
            buttons.Children.Add(MakeButton("還原方案", (s, e) => RunOnSelected("restore_grading_scheme", new JObject(), "還原方案")));
            buttons.Children.Add(MakeButton("刪除方案", (s, e) => DeleteSelected()));
            buttons.Children.Add(MakeButton("匯出 Excel", (s, e) => RunCommand("export_grading_comparison", new JObject(), "匯出 Excel")));
            buttons.Children.Add(MakeButton("比較出圖", (s, e) => RunCommand("create_grading_comparison_sheet", new JObject(), "比較出圖")));
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
                var rows = new System.Collections.Generic.List<SchemeRow>();
                if (response.Success)
                {
                    var data = JObject.FromObject(response.Data);
                    foreach (var scheme in data["Schemes"] as JArray ?? new JArray())
                    {
                        var cut = scheme.Value<double?>("CutCubicMeters") ?? 0;
                        var fill = scheme.Value<double?>("FillCubicMeters") ?? 0;
                        rows.Add(new SchemeRow
                        {
                            Name = scheme.Value<string>("SchemeName"),
                            Mode = scheme.Value<string>("Mode"),
                            Cut = cut.ToString("0"),
                            Fill = fill.ToString("0"),
                            Total = (cut + fill).ToString("0"),
                            DesignId = scheme.Value<long?>("DesignToposolidId") ?? 0
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    _rows.Clear();
                    foreach (var row in rows) { _rows.Add(row); }
                });
            });
        }

        private SchemeRow Selected => _list.SelectedItem as SchemeRow;

        private void OpenSchemeView()
        {
            var row = Selected;
            if (row == null) { MessageBox.Show("先選一列方案。", Title); return; }
            ExternalEventManager.Instance.ExecuteCommand(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var json = RevitMCP.Core.Grading.RevitToposolidGradingAdapter.TryReadSchemeRecord(doc, row.DesignId);
                var viewId = json == null ? 0 : JObject.Parse(json).Value<long?>("SchemeViewId") ?? 0;
                if (viewId > 0 && doc.GetElement(new ElementId(checked((int)viewId))) is View view)
                {
                    app.ActiveUIDocument.RequestViewChange(view);
                }
            });
        }

        private void DeleteSelected()
        {
            var row = Selected;
            if (row == null) { MessageBox.Show("先選一列方案。", Title); return; }
            if (MessageBox.Show($"確定刪除方案「{row.Name}」？（設計地形與方案視圖都會刪除）",
                Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) { return; }
            RunOnSelected("remove_grading_scheme", new JObject { ["confirm"] = true }, "刪除方案");
        }

        private void RunOnSelected(string commandName, JObject parameters, string title)
        {
            var row = Selected;
            if (row == null) { MessageBox.Show("先選一列方案。", Title); return; }
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
