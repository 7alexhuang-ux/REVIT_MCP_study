#if REVIT2024_OR_GREATER
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace RevitMCP.Commands
{
    /// <summary>
    /// 整地檢視選單：列出「原地形＋各方案（編號＋名稱）」讓使用者挑一個要在專用視圖顯示。
    /// </summary>
    public class TerrainViewPickerDialog : Window
    {
        private readonly ComboBox _combo;
        private readonly List<long> _designIds = new List<long>();

        /// <summary>選中項的設計地形 ID；0 代表原地形。</summary>
        public long SelectedDesignId { get; private set; }

        public TerrainViewPickerDialog(IList<KeyValuePair<string, long>> options)
        {
            Title = "整地檢視 — 選擇要顯示的地形";
            Width = 340;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = "要在「整地檢視」視圖顯示哪一個？（樓板等會保留）",
                Margin = new Thickness(0, 0, 0, 6)
            });

            _combo = new ComboBox();
            foreach (var option in options)
            {
                _combo.Items.Add(option.Key);
                _designIds.Add(option.Value);
            }

            _combo.SelectedIndex = 0;
            panel.Children.Add(_combo);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var cancel = new Button { Content = "取消", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "顯示", Width = 80, IsDefault = true };
            ok.Click += (sender, args) =>
            {
                var index = _combo.SelectedIndex < 0 ? 0 : _combo.SelectedIndex;
                SelectedDesignId = _designIds[index];
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
