#if REVIT2024_OR_GREATER
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace RevitMCP.Commands
{
    /// <summary>板高程對話框的單板意圖。</summary>
    public sealed class FloorElevationIntent
    {
        public long FloorId { get; set; }
        public string Action { get; set; } // keep | offset | slope | reset
        public double? OffsetMeters { get; set; }
        public string SlopeRatio { get; set; }
    }

    /// <summary>
    /// V3-S1 板高程/斜板對話框：每片選取樓板一列（編號＋目前偏移），
    /// 動作＝維持／設定平板偏移／改為斜板（關閉後再點低側邊）／還原平板。
    /// </summary>
    public class FloorElevationDialog : Window
    {
        private readonly List<(long FloorId, ComboBox Action, TextBox Offset, TextBox Rate)> _rows
            = new List<(long, ComboBox, TextBox, TextBox)>();

        public IReadOnlyList<FloorElevationIntent> Intents { get; private set; }

        public FloorElevationDialog(IReadOnlyList<(long FloorId, string Description)> floors)
        {
            Title = "板高程／斜板";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = "每片板選動作；「改為斜板」按確定後會請你到模型點該板的低側邊（板往那條邊斜下去）。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            foreach (var (floorId, description) in floors)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                row.Children.Add(new TextBlock
                {
                    Text = description,
                    Width = 210,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var action = new ComboBox { Width = 130, SelectedIndex = 0 };
                foreach (var option in new[] { "維持不動", "設定平板偏移", "改為斜板（點低側邊）", "還原平板" })
                {
                    action.Items.Add(option);
                }

                var offset = new TextBox { Width = 70, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false, ToolTip = "新偏移（公尺）" };
                var rate = new TextBox { Width = 60, Margin = new Thickness(8, 0, 0, 0), Text = "1:12", IsEnabled = false, ToolTip = "斜板坡率 1:n" };
                action.SelectionChanged += (sender, args) =>
                {
                    offset.IsEnabled = action.SelectedIndex == 1;
                    rate.IsEnabled = action.SelectedIndex == 2;
                };

                row.Children.Add(action);
                row.Children.Add(offset);
                row.Children.Add(rate);
                panel.Children.Add(row);
                _rows.Add((floorId, action, offset, rate));
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var cancel = new Button { Content = "取消", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "確定", Width = 88, IsDefault = true };
            ok.Click += OnOk;
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            Content = panel;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var intents = new List<FloorElevationIntent>();
            foreach (var (floorId, action, offset, rate) in _rows)
            {
                switch (action.SelectedIndex)
                {
                    case 1:
                        if (!double.TryParse(offset.Text.Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var offsetMeters))
                        {
                            MessageBox.Show($"板 {floorId} 的偏移必須是數字（公尺）。", Title);
                            return;
                        }

                        intents.Add(new FloorElevationIntent
                        { FloorId = floorId, Action = "offset", OffsetMeters = offsetMeters });
                        break;
                    case 2:
                        var ratio = rate.Text.Trim();
                        var parts = ratio.Split(':');
                        if (parts.Length != 2
                            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                            || n <= 0)
                        {
                            MessageBox.Show($"板 {floorId} 的坡率格式須為 1:n。", Title);
                            return;
                        }

                        intents.Add(new FloorElevationIntent
                        { FloorId = floorId, Action = "slope", SlopeRatio = ratio });
                        break;
                    case 3:
                        intents.Add(new FloorElevationIntent { FloorId = floorId, Action = "reset" });
                        break;
                }
            }

            if (intents.Count == 0)
            {
                MessageBox.Show("沒有任何板指定動作。", Title);
                return;
            }

            Intents = intents;
            DialogResult = true;
        }
    }

    /// <summary>邊坡率輸入小框：可編輯下拉（常用值＋自由輸入 1:n）。</summary>
    public class EdgeRateDialog : Window
    {
        private readonly ComboBox _combo;

        public string SlopeRatio { get; private set; }

        public EdgeRateDialog(string prompt)
        {
            Title = "邊坡率";
            Width = 320;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
            _combo = new ComboBox { IsEditable = true, Text = "1:12", Margin = new Thickness(0, 6, 0, 0) };
            foreach (var preset in new[] { "1:10", "1:12", "1:15", "1:20" }) { _combo.Items.Add(preset); }
            panel.Children.Add(_combo);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var cancel = new Button { Content = "取消", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "確定", Width = 80, IsDefault = true };
            ok.Click += (sender, args) =>
            {
                var ratio = _combo.Text.Trim();
                var parts = ratio.Split(':');
                if (parts.Length != 2
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    || n <= 0)
                {
                    MessageBox.Show("坡率格式須為 1:n（例 1:12）。", Title);
                    return;
                }

                SlopeRatio = ratio;
                DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            Content = panel;
        }
    }
}
#endif
