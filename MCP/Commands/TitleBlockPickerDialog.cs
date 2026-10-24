#if REVIT2024_OR_GREATER
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace RevitMCP.Commands
{
    public sealed class TitleBlockPickerOption
    {
        public TitleBlockPickerOption(string label, long id)
        {
            Label = label;
            Id = id;
        }

        public string Label { get; }

        public long Id { get; }
    }

    /// <summary>出圖前每次選圖框；固定提供空白圖紙 fallback。</summary>
    public class TitleBlockPickerDialog : Window
    {
        private readonly ComboBox _combo;
        private readonly List<long> _ids = new List<long>();

        public long SelectedTitleBlockId { get; private set; }

        public TitleBlockPickerDialog(string purpose, IList<TitleBlockPickerOption> options, long preferredId)
        {
            Title = purpose + " — 選擇圖框";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = "請選擇本次出圖使用的圖框；沒有正式圖框時可用空白圖紙。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

            _combo = new ComboBox();
            var preferredIndex = 0;
            for (var index = 0; index < options.Count; index++)
            {
                _combo.Items.Add(options[index].Label);
                _ids.Add(options[index].Id);
                if (options[index].Id == preferredId)
                {
                    preferredIndex = index;
                }
            }

            _combo.SelectedIndex = options.Count == 0 ? -1 : preferredIndex;
            panel.Children.Add(_combo);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var cancel = new Button { Content = "取消", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "出圖", Width = 80, IsDefault = true };
            ok.Click += (sender, args) =>
            {
                var index = _combo.SelectedIndex < 0 ? 0 : _combo.SelectedIndex;
                SelectedTitleBlockId = _ids[index];
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
