#if REVIT2024_OR_GREATER
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Commands
{
    /// <summary>
    /// 新整地方案對話框：模式中英對照、參數可編輯下拉（常用值＋自由輸入）、
    /// 方案名隨輸入即時自動組（使用者改過即停止自動覆蓋）。
    /// </summary>
    public class NewGradingSchemeDialog : Window
    {
        private readonly RadioButton _footprintRadio;
        private readonly RadioButton _offsetRadio;
        private readonly RadioButton _slopeRadio;
        private readonly ComboBox _offsetCombo;
        private readonly ComboBox _slopeCombo;
        private readonly TextBox _nameBox;
        private TextBox _looseFactorBox;
        private TextBox _compactionFactorBox;
        private bool _nameEdited;
        private bool _updatingName;

        public string Mode { get; private set; }
        public JObject ExtraParameters { get; private set; }

        public NewGradingSchemeDialog(string selectionSummary)
        {
            Title = "新整地方案";
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

            panel.Children.Add(new TextBlock { Text = "方案名稱（隨輸入值自動組，可改）", Margin = new Thickness(0, 8, 0, 2) });
            _nameBox = new TextBox();
            panel.Children.Add(_nameBox);

            panel.Children.Add(new TextBlock { Text = "鬆方係數／壓實係數（選填，成對；供鬆實方三本帳）", Margin = new Thickness(0, 8, 0, 2) });
            var factorRow = new StackPanel { Orientation = Orientation.Horizontal };
            _looseFactorBox = new TextBox { Width = 70 };
            _compactionFactorBox = new TextBox { Width = 70, Margin = new Thickness(8, 0, 0, 0) };
            factorRow.Children.Add(_looseFactorBox);
            factorRow.Children.Add(_compactionFactorBox);
            panel.Children.Add(factorRow);

            panel.Children.Add(new TextBlock { Text = selectionSummary, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7 });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = "取消", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "執行並記錄", Width = 96, IsDefault = true };
            ok.Click += OnOk;
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            Content = panel;

            RoutedEventHandler refresh = (s, e) => RefreshEnabledAndName();
            _footprintRadio.Checked += refresh;
            _offsetRadio.Checked += refresh;
            _slopeRadio.Checked += refresh;
            _offsetCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => RefreshEnabledAndName()));
            _slopeCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => RefreshEnabledAndName()));
            _nameBox.TextChanged += (s, e) => { if (!_updatingName) { _nameEdited = true; } };
            RefreshEnabledAndName();
        }

        private void RefreshEnabledAndName()
        {
            if (_offsetCombo == null || _slopeCombo == null) { return; }
            _offsetCombo.IsEnabled = _offsetRadio.IsChecked == true;
            _slopeCombo.IsEnabled = _slopeRadio.IsChecked == true;
            if (_nameEdited) { return; }

            _updatingName = true;
            if (_footprintRadio.IsChecked == true) { _nameBox.Text = "投影直貼"; }
            else if (_offsetRadio.IsChecked == true) { _nameBox.Text = $"外延 {_offsetCombo.Text.Trim()}m"; }
            else { _nameBox.Text = $"放坡 {_slopeCombo.Text.Trim()}"; }
            _updatingName = false;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var extra = new JObject { ["schemeName"] = _nameBox.Text.Trim() };
            if (_offsetRadio.IsChecked == true)
            {
                if (!double.TryParse(_offsetCombo.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) || distance <= 0)
                {
                    MessageBox.Show("外延距離必須是正數。", "新整地方案"); return;
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
                    MessageBox.Show("坡率格式須為 1:n（n 為正數）。", "新整地方案"); return;
                }

                Mode = "slope_transition";
                extra["slopeRatio"] = ratio;
            }
            else
            {
                Mode = "footprint_only";
            }

            var looseText = _looseFactorBox.Text.Trim();
            var compactionText = _compactionFactorBox.Text.Trim();
            if (looseText.Length > 0 || compactionText.Length > 0)
            {
                if (!double.TryParse(looseText, NumberStyles.Float, CultureInfo.InvariantCulture, out var loose) || loose <= 0
                    || !double.TryParse(compactionText, NumberStyles.Float, CultureInfo.InvariantCulture, out var compaction) || compaction <= 0)
                {
                    MessageBox.Show("鬆方係數與壓實係數須成對填寫且為正數（例：1.25／0.9）。", "新整地方案"); return;
                }

                extra["looseFactor"] = loose;
                extra["compactionFactor"] = compaction;
            }

            ExtraParameters = extra;
            DialogResult = true;
        }
    }
}
#endif
