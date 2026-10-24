#if REVIT2024_OR_GREATER
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Commands
{
    /// <summary>
    /// 平衡高程試算對話框：模式與參數比照「新整地方案」，另加目標淨土方與是否套用落成方案。
    /// 進階試算參數（maxAdjustMeters／maxIterations）維持工具預設值，不在此對話框開放調整；
    /// 有更細需求時請走 MCP／AI 呼叫 solve_balanced_elevation。
    /// </summary>
    public class BalancedElevationDialog : Window
    {
        private readonly RadioButton _footprintRadio;
        private readonly RadioButton _offsetRadio;
        private readonly RadioButton _slopeRadio;
        private readonly ComboBox _offsetCombo;
        private readonly ComboBox _slopeCombo;
        private readonly TextBox _targetNetBox;
        private readonly TextBox _toleranceBox;
        private readonly CheckBox _applyCheck;
        private readonly TextBox _nameBox;

        public string Mode { get; private set; }
        public JObject ExtraParameters { get; private set; }

        public BalancedElevationDialog(string selectionSummary)
        {
            Title = "平衡高程試算";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "模式", FontWeight = FontWeights.Bold });
            _footprintRadio = new RadioButton { Content = "投影直貼 Footprint", GroupName = "mode", Margin = new Thickness(0, 2, 0, 0) };
            _offsetRadio = new RadioButton { Content = "外延漸變 Offset", GroupName = "mode", IsChecked = true, Margin = new Thickness(0, 2, 0, 0) };
            _slopeRadio = new RadioButton { Content = "放坡銜接 Slope", GroupName = "mode", Margin = new Thickness(0, 2, 0, 0) };
            panel.Children.Add(_footprintRadio);
            panel.Children.Add(_offsetRadio);
            panel.Children.Add(_slopeRadio);

            panel.Children.Add(new TextBlock { Text = "外延距離（m）：常用 3／5／10／15／20，可自行輸入", Margin = new Thickness(0, 8, 0, 2) });
            _offsetCombo = new ComboBox { IsEditable = true, Text = "5" };
            foreach (var preset in new[] { "3", "5", "10", "15", "20" }) { _offsetCombo.Items.Add(preset); }
            panel.Children.Add(_offsetCombo);

            panel.Children.Add(new TextBlock { Text = "坡率 1:n：常用 1:1／1:1.5／1:2／1:3，可自行輸入", Margin = new Thickness(0, 8, 0, 2) });
            _slopeCombo = new ComboBox { IsEditable = true, Text = "1:2", IsEnabled = false };
            foreach (var preset in new[] { "1:1", "1:1.5", "1:2", "1:3" }) { _slopeCombo.Items.Add(preset); }
            panel.Children.Add(_slopeCombo);

            panel.Children.Add(new TextBlock { Text = "目標淨土方 m³（Fill−Cut，負值＝餘土；預設 0＝挖填平衡）", Margin = new Thickness(0, 8, 0, 2) });
            _targetNetBox = new TextBox { Text = "0" };
            panel.Children.Add(_targetNetBox);

            panel.Children.Add(new TextBlock { Text = "收斂容差 m³（預設 10）", Margin = new Thickness(0, 8, 0, 2) });
            _toleranceBox = new TextBox { Text = "10" };
            panel.Children.Add(_toleranceBox);

            _applyCheck = new CheckBox { Content = "收斂時套用並落成方案（不勾＝僅試算，不動模型）", Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(_applyCheck);

            panel.Children.Add(new TextBlock { Text = "方案名稱（僅套用時使用，留空用預設「平衡方案（±X.XXm）」）", Margin = new Thickness(0, 8, 0, 2) });
            _nameBox = new TextBox();
            panel.Children.Add(_nameBox);

            panel.Children.Add(new TextBlock { Text = selectionSummary, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7 });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = "取消", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "試算", Width = 96, IsDefault = true };
            ok.Click += OnOk;
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            Content = panel;

            RoutedEventHandler refresh = (s, e) => RefreshEnabled();
            _footprintRadio.Checked += refresh;
            _offsetRadio.Checked += refresh;
            _slopeRadio.Checked += refresh;
            _offsetCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => RefreshEnabled()));
            _slopeCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => RefreshEnabled()));
            RefreshEnabled();
        }

        private void RefreshEnabled()
        {
            if (_offsetCombo == null || _slopeCombo == null) { return; }
            _offsetCombo.IsEnabled = _offsetRadio.IsChecked == true;
            _slopeCombo.IsEnabled = _slopeRadio.IsChecked == true;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var extra = new JObject();
            if (_offsetRadio.IsChecked == true)
            {
                if (!double.TryParse(_offsetCombo.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) || distance <= 0)
                {
                    MessageBox.Show("外延距離必須是正數。", "平衡高程試算"); return;
                }

                Mode = "offset_transition";
                extra["offsetDistance"] = distance;
            }
            else if (_slopeRadio.IsChecked == true)
            {
                var ratio = _slopeCombo.Text.Trim();
                var parts = ratio.Split(':');
                if (parts.Length != 2
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0)
                {
                    MessageBox.Show("坡率格式須為 1:n（n 為正數）。", "平衡高程試算"); return;
                }

                Mode = "slope_transition";
                extra["slopeRatio"] = ratio;
            }
            else
            {
                Mode = "footprint_only";
            }

            if (!double.TryParse(_targetNetBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var targetNet))
            {
                MessageBox.Show("目標淨土方必須是數字。", "平衡高程試算"); return;
            }

            if (!double.TryParse(_toleranceBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tolerance) || tolerance <= 0)
            {
                MessageBox.Show("收斂容差必須是正數。", "平衡高程試算"); return;
            }

            extra["targetNetCubicMeters"] = targetNet;
            extra["toleranceCubicMeters"] = tolerance;
            extra["apply"] = _applyCheck.IsChecked == true;
            var name = _nameBox.Text.Trim();
            if (name.Length > 0) { extra["schemeName"] = name; }

            ExtraParameters = extra;
            DialogResult = true;
        }
    }
}
#endif
