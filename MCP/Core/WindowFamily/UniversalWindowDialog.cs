using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
// Nice3point SDK 隱含 using Autodesk.Revit.DB，下列名稱與 WPF 衝突，明確指定
using Color = System.Windows.Media.Color;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace RevitMCP.Core.WindowFamily
{
    /// <summary>
    /// 萬用窗 add-in 對話框（SPEC §11、ADR D-008）。不需要 MCP 服務、不需要 AI：
    /// 使用者在這裡編輯格子與尺寸 → <see cref="WindowSpecBuilder"/> 組出規格 JSON →
    /// <see cref="WindowFamilyService.Generate"/>（與 MCP 工具共用的引擎進入點）生成並載入族。
    /// 本類別只處理介面與立面預覽，不自己畫任何 Revit 幾何。
    /// 純程式碼 WPF（與既有 LogViewerWindow 同風格，不用 XAML）。
    /// 注意：本檔刻意不 using Autodesk.Revit.UI／DB，因為兩者都有 TextBox／ComboBox／Grid 等同名型別。
    /// </summary>
    public class UniversalWindowDialog : Window
    {
        private static readonly SashType[] SashTypes =
        {
            SashType.Fixed, SashType.Casement, SashType.Sliding, SashType.Awning,
            SashType.Hopper, SashType.SlidingV, SashType.Screen
        };

        private static readonly string[] SashTypeNames =
        {
            "固定", "外推", "橫拉", "上懸", "下懸／內倒", "垂直上下拉", "紗窗"
        };

        private readonly Autodesk.Revit.UI.UIApplication _uiApp;
        private readonly WindowSpecBuilder _b = new WindowSpecBuilder();
        private bool _updating;
        private int _selRow;
        private int _selCol;
        private string _formError;

        // ── 控制項 ──
        private ComboBox _cboPreset;
        private ComboBox _cboRows;
        private ComboBox _cboCols;
        private TextBox _txtW, _txtH, _txtSill, _txtRowRatio, _txtColRatio;
        private Border _minimapHost;
        private TextBlock _selCellTitle;
        private ComboBox _cboSashCount;
        private StackPanel _sashEditor;
        private TextBox _txtFrameOuter, _txtMullion, _txtSash, _txtDepth, _txtGlass, _txtOverlap;
        private CheckBox _chkOverlapAuto;
        private CheckBox _chkAutoNames, _chkLoad, _chkOverwrite;
        private TextBox _txtFamily, _txtType, _txtOutDir;
        private Button _btnGenerate;
        private Canvas _canvas;
        private TextBlock _txtSummary;
        private TextBlock _txtError;
        private TextBox _txtResult;

        // 預覽用的最近一次有效佈局
        private WindowLayout _layout;
        private WindowFamilySpec _previewSpec;

        public UniversalWindowDialog(Autodesk.Revit.UI.UIApplication uiApp)
        {
            _uiApp = uiApp;

            Title = "萬用窗族生成器（不需 MCP 服務）";
            Width = 1060;
            Height = 800;
            MinWidth = 900;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontSize = 12;

            Content = BuildLayout();

            Loaded += (s, e) =>
            {
                // builder 預設狀態＝第一個範例，下拉同步顯示（不觸發套用）
                _updating = true;
                try { _cboPreset.SelectedIndex = 0; }
                finally { _updating = false; }
                SyncFromBuilder();
            };
        }

        // ────────────────────────────────────────────────
        // 版面
        // ────────────────────────────────────────────────

        private UIElement BuildLayout()
        {
            var root = new System.Windows.Controls.Grid { Margin = new Thickness(10) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ── 左：表單（可捲動）＋ 底部按鈕 ──
            var left = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 10, 0) };
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var form = new StackPanel();
            form.Children.Add(BuildPresetSection());
            form.Children.Add(BuildSizeSection());
            form.Children.Add(BuildCellSection());
            form.Children.Add(BuildFrameSection());
            form.Children.Add(BuildOutputSection());

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = form,
                Padding = new Thickness(0, 0, 6, 0)
            };
            System.Windows.Controls.Grid.SetRow(scroll, 0);
            left.Children.Add(scroll);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            _btnGenerate = new Button
            {
                Content = "產生族並載入專案",
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.Bold
            };
            _btnGenerate.Click += OnGenerate;
            var btnClose = new Button { Content = "關閉", Padding = new Thickness(16, 6, 16, 6) };
            btnClose.Click += (s, e) => Close();
            buttons.Children.Add(_btnGenerate);
            buttons.Children.Add(btnClose);
            System.Windows.Controls.Grid.SetRow(buttons, 1);
            left.Children.Add(buttons);

            System.Windows.Controls.Grid.SetColumn(left, 0);
            root.Children.Add(left);

            // ── 右：預覽＋訊息 ──
            var right = new System.Windows.Controls.Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var previewTitle = new TextBlock
            {
                Text = "立面預覽（自室外看；虛線＝向內開；橘框＝目前選取的格）",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            System.Windows.Controls.Grid.SetRow(previewTitle, 0);
            right.Children.Add(previewTitle);

            _canvas = new Canvas { ClipToBounds = true, Background = Brushes.White };
            _canvas.SizeChanged += (s, e) => DrawPreview();
            var canvasBorder = new Border
            {
                BorderBrush = Hex("#9AA5B1"),
                BorderThickness = new Thickness(1),
                Child = _canvas
            };
            System.Windows.Controls.Grid.SetRow(canvasBorder, 1);
            right.Children.Add(canvasBorder);

            _txtSummary = new TextBlock
            {
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Hex("#2C3E50")
            };
            System.Windows.Controls.Grid.SetRow(_txtSummary, 2);
            right.Children.Add(_txtSummary);

            _txtError = new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Hex("#C0392B"),
                FontWeight = FontWeights.SemiBold
            };
            System.Windows.Controls.Grid.SetRow(_txtError, 3);
            right.Children.Add(_txtError);

            _txtResult = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 150,
                Margin = new Thickness(0, 6, 0, 0),
                Background = Hex("#F7F9FA"),
                FontFamily = new FontFamily("Microsoft JhengHei UI, Consolas")
            };
            System.Windows.Controls.Grid.SetRow(_txtResult, 4);
            right.Children.Add(_txtResult);

            System.Windows.Controls.Grid.SetColumn(right, 1);
            root.Children.Add(right);

            return root;
        }

        private UIElement BuildPresetSection()
        {
            var box = Section("範例窗型");
            var panel = (StackPanel)box.Child;
            _cboPreset = new ComboBox
            {
                ItemsSource = WindowSpecBuilder.Presets,
                DisplayMemberPath = "Name"
            };
            _cboPreset.SelectionChanged += (s, e) =>
            {
                if (_updating) return;
                var p = _cboPreset.SelectedItem as WindowPreset;
                if (p == null) return;
                _b.ApplyPreset(p);
                _selRow = 0;
                _selCol = 0;
                SyncFromBuilder();
            };
            panel.Children.Add(_cboPreset);
            panel.Children.Add(Hint("套用範例會覆蓋格子、尺寸與比例；框料與輸出設定維持不變。"));
            return box;
        }

        private UIElement BuildSizeSection()
        {
            var box = Section("尺寸與格子");
            var panel = (StackPanel)box.Child;

            var g = new System.Windows.Controls.Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _cboRows = NumberCombo(1, WindowFamilySpec.MaxRows);
            _cboCols = NumberCombo(1, WindowFamilySpec.MaxCols);
            _cboRows.SelectionChanged += OnGridSizeChanged;
            _cboCols.SelectionChanged += OnGridSizeChanged;
            _txtW = NumberBox();
            _txtH = NumberBox();
            _txtSill = NumberBox();
            _txtRowRatio = NumberBox();
            _txtColRatio = NumberBox();

            int r = 0;
            AddRow(g, r++, "排數（上→下）", _cboRows);
            AddRow(g, r++, "樘數（左→右）", _cboCols);
            AddRow(g, r++, "總寬 (cm)", _txtW);
            AddRow(g, r++, "總高 (cm)", _txtH);
            AddRow(g, r++, "窗台高 (cm)", _txtSill);
            AddRow(g, r++, "排高比（上→下）", _txtRowRatio);
            AddRow(g, r++, "樘寬比（左→右）", _txtColRatio);
            panel.Children.Add(g);
            panel.Children.Add(Hint("比例可只填到倒數第二個值（自動補滿 100），或填任意比例（自動縮放到 100），分隔符號：冒號、逗號或空白。"));
            return box;
        }

        private UIElement BuildCellSection()
        {
            var box = Section("格子（點選一格編輯）");
            var panel = (StackPanel)box.Child;

            _minimapHost = new Border { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(_minimapHost);

            _selCellTitle = new TextBlock { FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 4) };
            panel.Children.Add(_selCellTitle);

            var g = new System.Windows.Controls.Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _cboSashCount = NumberCombo(1, WindowFamilySpec.MaxSashesPerCell);
            _cboSashCount.SelectionChanged += (s, e) =>
            {
                if (_updating) return;
                int n = _cboSashCount.SelectedIndex + 1;
                if (n < 1) return;
                _b.SetSashCount(_selRow, _selCol, n);
                RebuildMinimap();
                RebuildSashEditor();
                Refresh();
            };
            AddRow(g, 0, "扇數", _cboSashCount);
            panel.Children.Add(g);

            _sashEditor = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            panel.Children.Add(_sashEditor);
            panel.Children.Add(Hint("滑動類（橫拉／垂直上下拉／紗窗）必須整格都是滑動類；軌序 0＝最外軌，「自動」由引擎依扇序分配。"));
            return box;
        }

        private UIElement BuildFrameSection()
        {
            var box = Section("框料與玻璃");
            var panel = (StackPanel)box.Child;

            var g = new System.Windows.Controls.Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _txtFrameOuter = NumberBox();
            _txtMullion = NumberBox();
            _txtSash = NumberBox();
            _txtDepth = NumberBox();
            _txtGlass = NumberBox();
            _txtOverlap = NumberBox();
            _chkOverlapAuto = new CheckBox { Content = "交疊同扇框寬（預設）", Margin = new Thickness(0, 2, 0, 2) };
            _chkOverlapAuto.Checked += OnFieldChanged;
            _chkOverlapAuto.Unchecked += OnFieldChanged;

            int r = 0;
            AddRow(g, r++, "外框寬 (cm)", _txtFrameOuter);
            AddRow(g, r++, "中框寬 (cm)", _txtMullion);
            AddRow(g, r++, "扇框寬 (cm)", _txtSash);
            AddRow(g, r++, "框深 (cm)", _txtDepth);
            AddRow(g, r++, "玻璃厚 (mm)", _txtGlass);
            AddRow(g, r++, "交疊", _chkOverlapAuto);
            AddRow(g, r++, "手動交疊 (mm)", _txtOverlap);
            panel.Children.Add(g);
            panel.Children.Add(Hint("框深預設 7 cm（YKK AP YRB-A70；A100 為 10）。"));
            return box;
        }

        private UIElement BuildOutputSection()
        {
            var box = Section("族名與輸出");
            var panel = (StackPanel)box.Child;

            _chkAutoNames = new CheckBox { Content = "族名／型別名自動命名", Margin = new Thickness(0, 0, 0, 4) };
            _chkAutoNames.Checked += OnFieldChanged;
            _chkAutoNames.Unchecked += OnFieldChanged;
            panel.Children.Add(_chkAutoNames);

            var g = new System.Windows.Controls.Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _txtFamily = new TextBox { Margin = new Thickness(0, 2, 0, 2) };
            _txtType = new TextBox { Margin = new Thickness(0, 2, 0, 2) };
            _txtOutDir = new TextBox { Margin = new Thickness(0, 2, 0, 2) };
            _txtFamily.TextChanged += OnFieldChanged;
            _txtType.TextChanged += OnFieldChanged;
            _txtOutDir.TextChanged += OnFieldChanged;
            AddRow(g, 0, "族名", _txtFamily);
            AddRow(g, 1, "型別名", _txtType);
            AddRow(g, 2, "輸出資料夾", _txtOutDir);
            panel.Children.Add(g);
            panel.Children.Add(Hint("輸出資料夾留白＝%APPDATA%\\RevitMCP\\families。"));

            _chkLoad = new CheckBox { Content = "生成後載入目前專案", Margin = new Thickness(0, 4, 0, 2) };
            _chkOverwrite = new CheckBox { Content = "同名族已在專案內時覆蓋", Margin = new Thickness(0, 2, 0, 2) };
            _chkLoad.Checked += OnFieldChanged;
            _chkLoad.Unchecked += OnFieldChanged;
            _chkOverwrite.Checked += OnFieldChanged;
            _chkOverwrite.Unchecked += OnFieldChanged;
            panel.Children.Add(_chkLoad);
            panel.Children.Add(_chkOverwrite);
            return box;
        }

        // ────────────────────────────────────────────────
        // 狀態同步
        // ────────────────────────────────────────────────

        /// <summary>把 builder 的狀態整份寫回控制項（套用範例、開啟視窗時）。</summary>
        private void SyncFromBuilder()
        {
            _updating = true;
            try
            {
                _cboRows.SelectedIndex = _b.Rows - 1;
                _cboCols.SelectedIndex = _b.Cols - 1;
                _txtW.Text = Fmt(_b.Width);
                _txtH.Text = Fmt(_b.Height);
                _txtSill.Text = Fmt(_b.SillHeight);
                _txtRowRatio.Text = WindowSpecBuilder.RatioText(_b.RowRatios);
                _txtColRatio.Text = WindowSpecBuilder.RatioText(_b.ColRatios);
                _txtFrameOuter.Text = Fmt(_b.FrameOuter);
                _txtMullion.Text = Fmt(_b.Mullion);
                _txtSash.Text = Fmt(_b.Sash);
                _txtDepth.Text = Fmt(_b.FrameDepth);
                _txtGlass.Text = Fmt(_b.GlassMm);
                _txtOverlap.Text = Fmt(_b.OverlapMm);
                _chkOverlapAuto.IsChecked = _b.OverlapEqualsSash;
                _chkAutoNames.IsChecked = _b.AutoNames;
                _txtOutDir.Text = _b.OutputDir ?? "";
                _chkOverwrite.IsChecked = _b.Overwrite;

                // 沒有可載入的專案（未開檔或在族編輯器）→ 取消勾選並停用
                bool canLoad = CanLoadIntoProject();
                if (!canLoad) _b.LoadIntoProject = false;
                _chkLoad.IsChecked = _b.LoadIntoProject;
                _chkLoad.IsEnabled = canLoad;
                _chkLoad.ToolTip = canLoad ? null : "目前沒有開啟中的專案文件，只能生成 .rfa 檔。";

                ClampSelection();
                RebuildMinimap();
                RebuildSashEditor();
            }
            finally
            {
                _updating = false;
            }
            Refresh();
        }

        private bool CanLoadIntoProject()
        {
            try
            {
                var uiDoc = _uiApp == null ? null : _uiApp.ActiveUIDocument;
                return uiDoc != null && uiDoc.Document != null && !uiDoc.Document.IsFamilyDocument;
            }
            catch
            {
                return false;
            }
        }

        private void ClampSelection()
        {
            if (_selRow >= _b.Rows) _selRow = _b.Rows - 1;
            if (_selCol >= _b.Cols) _selCol = _b.Cols - 1;
            if (_selRow < 0) _selRow = 0;
            if (_selCol < 0) _selCol = 0;
        }

        private void OnGridSizeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating) return;
            int rows = _cboRows.SelectedIndex + 1;
            int cols = _cboCols.SelectedIndex + 1;
            if (rows < 1 || cols < 1) return;

            _b.SetGrid(rows, cols);
            _updating = true;
            try
            {
                _txtRowRatio.Text = WindowSpecBuilder.RatioText(_b.RowRatios);
                _txtColRatio.Text = WindowSpecBuilder.RatioText(_b.ColRatios);
            }
            finally { _updating = false; }
            ClampSelection();
            RebuildMinimap();
            RebuildSashEditor();
            Refresh();
        }

        private void OnFieldChanged(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            Refresh();
        }

        /// <summary>讀表單 → 更新預覽與錯誤訊息。</summary>
        private void Refresh()
        {
            if (_btnGenerate == null) return;
            _formError = ReadForm();

            _updating = true;
            try
            {
                _txtFamily.IsReadOnly = _b.AutoNames;
                _txtType.IsReadOnly = _b.AutoNames;
                if (_b.AutoNames)
                {
                    _txtFamily.Text = _b.AutoFamilyName();
                    _txtType.Text = _b.AutoTypeName();
                }
                _txtOverlap.IsEnabled = !_b.OverlapEqualsSash;
            }
            finally { _updating = false; }

            _layout = null;
            _previewSpec = null;
            string engineError = null;
            try
            {
                var spec = _b.BuildSpec();
                _layout = WindowLayoutMath.Compute(spec);
                _previewSpec = spec;
            }
            catch (WindowSpecException ex)
            {
                engineError = ex.Message;
            }
            catch (Exception ex)
            {
                engineError = "規格組裝失敗：" + ex.Message;
            }

            string err = _formError;
            if (!string.IsNullOrEmpty(engineError))
                err = string.IsNullOrEmpty(err) ? engineError : err + "\n" + engineError;
            _txtError.Text = err ?? "";

            UpdateSummary();
            DrawPreview();
        }

        /// <summary>控制項 → builder；回傳輸入格式錯誤訊息（沒有錯回 null）。</summary>
        private string ReadForm()
        {
            var errs = new List<string>();
            _b.Width = ReadNum(_txtW, "總寬", errs, _b.Width);
            _b.Height = ReadNum(_txtH, "總高", errs, _b.Height);
            _b.SillHeight = ReadNum(_txtSill, "窗台高", errs, _b.SillHeight);
            _b.FrameOuter = ReadNum(_txtFrameOuter, "外框寬", errs, _b.FrameOuter);
            _b.Mullion = ReadNum(_txtMullion, "中框寬", errs, _b.Mullion);
            _b.Sash = ReadNum(_txtSash, "扇框寬", errs, _b.Sash);
            _b.FrameDepth = ReadNum(_txtDepth, "框深", errs, _b.FrameDepth);
            _b.GlassMm = ReadNum(_txtGlass, "玻璃厚", errs, _b.GlassMm);

            _b.OverlapEqualsSash = _chkOverlapAuto.IsChecked == true;
            if (!_b.OverlapEqualsSash)
                _b.OverlapMm = ReadNum(_txtOverlap, "手動交疊", errs, _b.OverlapMm);

            try { _b.SetRowRatios(_txtRowRatio.Text); }
            catch (WindowSpecException ex) { errs.Add("排高比：" + ex.Message); }
            try { _b.SetColRatios(_txtColRatio.Text); }
            catch (WindowSpecException ex) { errs.Add("樘寬比：" + ex.Message); }

            _b.AutoNames = _chkAutoNames.IsChecked == true;
            if (!_b.AutoNames)
            {
                _b.FamilyName = _txtFamily.Text;
                _b.TypeName = _txtType.Text;
            }
            _b.OutputDir = _txtOutDir.Text;
            _b.LoadIntoProject = _chkLoad.IsChecked == true;
            _b.Overwrite = _chkOverwrite.IsChecked == true;

            return errs.Count == 0 ? null : string.Join("\n", errs.ToArray());
        }

        private static double ReadNum(TextBox tb, string label, List<string> errs, double fallback)
        {
            double v;
            if (double.TryParse(tb.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                && !double.IsNaN(v) && !double.IsInfinity(v))
                return v;
            errs.Add(label + " 必須是數字（目前輸入：「" + tb.Text + "」）。");
            return fallback;
        }

        // ────────────────────────────────────────────────
        // 小地圖與扇編輯
        // ────────────────────────────────────────────────

        private void RebuildMinimap()
        {
            var ug = new UniformGrid { Rows = _b.Rows, Columns = _b.Cols };
            for (int r = 0; r < _b.Rows; r++)
            {
                for (int c = 0; c < _b.Cols; c++)
                {
                    int rr = r, cc = c;
                    var content = new StackPanel();
                    content.Children.Add(new TextBlock
                    {
                        Text = string.Format(CultureInfo.InvariantCulture, "R{0}C{1}", r + 1, c + 1),
                        FontSize = 10,
                        Foreground = Hex("#7F8C8D"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                    content.Children.Add(new TextBlock
                    {
                        Text = _b.CellSummary(r, c),
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                    bool sel = (r == _selRow && c == _selCol);
                    var btn = new Button
                    {
                        Content = content,
                        Width = 86,
                        Height = 46,
                        Margin = new Thickness(1),
                        Background = sel ? Hex("#FFD9A0") : Hex("#EAF1F7"),
                        BorderBrush = sel ? Hex("#E67E22") : Hex("#9AA5B1"),
                        BorderThickness = new Thickness(sel ? 2 : 1)
                    };
                    btn.Click += (s, e) =>
                    {
                        _selRow = rr;
                        _selCol = cc;
                        RebuildMinimap();
                        RebuildSashEditor();
                        DrawPreview();
                    };
                    ug.Children.Add(btn);
                }
            }
            _minimapHost.Child = ug;
        }

        private void RebuildSashEditor()
        {
            bool wasUpdating = _updating;
            _updating = true;
            try
            {
                ClampSelection();
                var sashes = _b.Cells[_selRow][_selCol];
                _selCellTitle.Text = string.Format(CultureInfo.InvariantCulture,
                    "目前選取：R{0}C{1}（{2}）", _selRow + 1, _selCol + 1, _b.CellSummary(_selRow, _selCol));
                _cboSashCount.SelectedIndex = sashes.Count - 1;

                _sashEditor.Children.Clear();
                for (int k = 0; k < sashes.Count; k++)
                {
                    _sashEditor.Children.Add(BuildSashRow(k + 1, sashes[k]));
                }
            }
            finally { _updating = wasUpdating; }
        }

        private UIElement BuildSashRow(int number, SashSpec sash)
        {
            var box = new Border
            {
                BorderBrush = Hex("#D5DBE1"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(6, 4, 6, 4)
            };
            var g = new System.Windows.Controls.Grid();
            // 第 0 排：標題＋型式；第 1 排：鉸鏈／開向／軌序三欄等寬（跨滿整列）
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "扇 " + number.ToString(CultureInfo.InvariantCulture),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold
            };
            System.Windows.Controls.Grid.SetRow(title, 0);
            System.Windows.Controls.Grid.SetColumn(title, 0);
            g.Children.Add(title);

            // 型式
            var cboType = new ComboBox { ItemsSource = SashTypeNames, Margin = new Thickness(2) };
            cboType.SelectedIndex = Array.IndexOf(SashTypes, sash.Type);
            System.Windows.Controls.Grid.SetRow(cboType, 0);
            System.Windows.Controls.Grid.SetColumn(cboType, 1);
            System.Windows.Controls.Grid.SetColumnSpan(cboType, 2);
            g.Children.Add(cboType);

            // 鉸鏈
            var cboHinge = new ComboBox { ItemsSource = new[] { "鉸鏈左", "鉸鏈右" }, Margin = new Thickness(2) };
            cboHinge.SelectedIndex = sash.Hinge == HingeSide.Left ? 0 : 1;
            cboHinge.IsEnabled = WindowSpecBuilder.UsesHinge(sash.Type);
            System.Windows.Controls.Grid.SetRow(cboHinge, 1);
            System.Windows.Controls.Grid.SetColumn(cboHinge, 0);
            g.Children.Add(cboHinge);

            // 開向
            var cboSwing = new ComboBox
            {
                ItemsSource = new[] { "開向預設", "向外開", "向內開" },
                Margin = new Thickness(2)
            };
            cboSwing.SelectedIndex = !sash.SwingRaw.HasValue ? 0 : (sash.SwingRaw.Value == SwingDirection.Out ? 1 : 2);
            cboSwing.IsEnabled = WindowSpecBuilder.UsesSwing(sash.Type);
            System.Windows.Controls.Grid.SetRow(cboSwing, 1);
            System.Windows.Controls.Grid.SetColumn(cboSwing, 1);
            g.Children.Add(cboSwing);

            // 軌序
            var cboTrack = new ComboBox
            {
                ItemsSource = new[] { "軌序自動", "第 0 軌（最外）", "第 1 軌", "第 2 軌", "第 3 軌" },
                Margin = new Thickness(2)
            };
            cboTrack.SelectedIndex = sash.Track.HasValue ? sash.Track.Value + 1 : 0;
            cboTrack.IsEnabled = WindowSpecBuilder.UsesTrack(sash.Type);
            System.Windows.Controls.Grid.SetRow(cboTrack, 1);
            System.Windows.Controls.Grid.SetColumn(cboTrack, 2);
            g.Children.Add(cboTrack);

            cboType.SelectionChanged += (s, e) =>
            {
                if (_updating || cboType.SelectedIndex < 0) return;
                _b.SetSashType(sash, SashTypes[cboType.SelectedIndex]);
                RebuildMinimap();
                RebuildSashEditor();
                Refresh();
            };
            cboHinge.SelectionChanged += (s, e) =>
            {
                if (_updating || cboHinge.SelectedIndex < 0) return;
                sash.Hinge = cboHinge.SelectedIndex == 0 ? HingeSide.Left : HingeSide.Right;
                RebuildMinimap();
                Refresh();
            };
            cboSwing.SelectionChanged += (s, e) =>
            {
                if (_updating || cboSwing.SelectedIndex < 0) return;
                int i = cboSwing.SelectedIndex;
                sash.SwingRaw = i == 0 ? (SwingDirection?)null : (i == 1 ? SwingDirection.Out : SwingDirection.In);
                RebuildMinimap();
                Refresh();
            };
            cboTrack.SelectionChanged += (s, e) =>
            {
                if (_updating || cboTrack.SelectedIndex < 0) return;
                int i = cboTrack.SelectedIndex;
                sash.Track = i == 0 ? (int?)null : i - 1;
                RebuildMinimap();
                Refresh();
            };

            box.Child = g;
            return box;
        }

        // ────────────────────────────────────────────────
        // 預覽
        // ────────────────────────────────────────────────

        private void UpdateSummary()
        {
            if (_layout == null)
            {
                _txtSummary.Text = "（規格目前無效，修正下方錯誤後預覽會恢復）";
                return;
            }
            var d = _layout.Derived;
            var sb = new StringBuilder();
            sb.Append("扇數 ").Append(d.SashCount);
            if (d.ScreenCount > 0) sb.Append("（另有紗窗 ").Append(d.ScreenCount).Append("）");
            sb.Append("｜可開 ").Append(d.OpenableCount);
            sb.Append("｜玻璃 ").Append(d.GlassAreaM2.ToString("0.00", CultureInfo.InvariantCulture)).Append(" m²");
            sb.Append("｜有效採光 ").Append(d.DaylightAreaM2.ToString("0.00", CultureInfo.InvariantCulture)).Append(" m²");
            sb.Append("｜可開口 ").Append(d.OpenableAreaM2.ToString("0.00", CultureInfo.InvariantCulture)).Append(" m²");
            sb.Append("｜MO ").Append(Fmt(d.MoWidth)).Append(" × ").Append(Fmt(d.MoHeight)).Append(" cm");
            _txtSummary.Text = sb.ToString();
        }

        private void DrawPreview()
        {
            if (_canvas == null) return;
            _canvas.Children.Clear();
            if (_layout == null || _previewSpec == null) return;

            double cw = _canvas.ActualWidth, ch = _canvas.ActualHeight;
            if (cw <= 10 || ch <= 10) return;

            const double margin = 48;
            double scale = Math.Min((cw - 2 * margin) / _layout.Width, (ch - 2 * margin) / _layout.Height);
            if (scale <= 0) return;
            double ox = (cw - _layout.Width * scale) / 2;
            double oy = (ch - _layout.Height * scale) / 2;

            Func<double, double> X = v => ox + v * scale;
            Func<double, double> Y = v => oy + v * scale;

            var frameFill = Hex("#CDD3D9");
            var frameStroke = Hex("#4A5560");
            var glassFill = new SolidColorBrush(Color.FromArgb(150, 160, 205, 235));
            glassFill.Freeze();

            // 外框
            AddRect(X(0), Y(0), _layout.Width * scale, _layout.Height * scale, frameFill, frameStroke, 1.6, false);

            // 各格底（開口淨空，中框留白成框色）
            foreach (var cell in _layout.AllCells)
                AddRect(X(cell.X), Y(cell.Y), cell.Width * scale, cell.Height * scale, Hex("#EEF3F7"), frameStroke, 0.8, false);

            // 扇：先畫玻璃扇，紗窗最後（蓋在上面）
            var sashes = _layout.AllSashes.ToList();
            foreach (var s in sashes.Where(x => !x.IsScreen).OrderBy(x => x.Track))
            {
                AddRect(X(s.X), Y(s.Y), s.Width * scale, s.Height * scale, frameFill, frameStroke, 1.0, false);
                AddRect(X(s.GlassX), Y(s.GlassY), s.GlassWidth * scale, s.GlassHeight * scale, glassFill, Hex("#6B8499"), 0.8, false);
                DrawOpeningSymbol(s, X, Y, scale);
            }
            foreach (var s in sashes.Where(x => x.IsScreen))
            {
                double inset = 1.0;
                double gx = s.GlassX + inset, gy = s.GlassY + inset;
                double gw = s.GlassWidth - 2 * inset, gh = s.GlassHeight - 2 * inset;
                AddRect(X(gx), Y(gy), gw * scale, gh * scale, Brushes.Transparent, Hex("#7F8C8D"), 1.0, true);
                AddLine(X(gx), Y(gy), X(gx + gw), Y(gy + gh), Hex("#7F8C8D"), 0.8, true);
            }

            // 選取的格
            if (_selRow < _b.Rows && _selCol < _b.Cols)
            {
                var sc = _layout.AllCells.FirstOrDefault(c => c.Row == _selRow && c.Col == _selCol);
                if (sc != null)
                    AddRect(X(sc.X) - 1, Y(sc.Y) - 1, sc.Width * scale + 2, sc.Height * scale + 2,
                        Brushes.Transparent, Hex("#E67E22"), 3, false);
            }

            // 尺寸註記
            AddText(Fmt(_layout.Width) + " cm", X(_layout.Width / 2) - 24, Y(_layout.Height) + 6);
            AddText(Fmt(_layout.Height) + " cm", X(_layout.Width) + 6, Y(_layout.Height / 2) - 8);
        }

        private void DrawOpeningSymbol(SashLayout s, Func<double, double> X, Func<double, double> Y, double scale)
        {
            var pen = Hex("#34495E");
            double gx = X(s.GlassX), gy = Y(s.GlassY);
            double gw = s.GlassWidth * scale, gh = s.GlassHeight * scale;
            if (gw < 6 || gh < 6) return;
            bool dashed = s.Swing == SwingDirection.In;
            bool apexAtHinge = _previewSpec.Symbols.ApexAtHinge;

            switch (s.Type)
            {
                case SashType.Casement:
                    {
                        bool apexLeft = (s.Hinge == HingeSide.Left) == apexAtHinge;
                        double ax = apexLeft ? gx : gx + gw;
                        double bx = apexLeft ? gx + gw : gx;
                        AddLine(bx, gy, ax, gy + gh / 2, pen, 1.1, dashed);
                        AddLine(bx, gy + gh, ax, gy + gh / 2, pen, 1.1, dashed);
                        break;
                    }
                case SashType.Awning:
                    {
                        // 鉸鏈在上：頂點在上緣中點（apexAtHinge），否則頂點在下緣
                        double ay = apexAtHinge ? gy : gy + gh;
                        double by = apexAtHinge ? gy + gh : gy;
                        AddLine(gx, by, gx + gw / 2, ay, pen, 1.1, dashed);
                        AddLine(gx + gw, by, gx + gw / 2, ay, pen, 1.1, dashed);
                        break;
                    }
                case SashType.Hopper:
                    {
                        // 鉸鏈在下
                        double ay = apexAtHinge ? gy + gh : gy;
                        double by = apexAtHinge ? gy : gy + gh;
                        AddLine(gx, by, gx + gw / 2, ay, pen, 1.1, dashed);
                        AddLine(gx + gw, by, gx + gw / 2, ay, pen, 1.1, dashed);
                        break;
                    }
                case SashType.Sliding:
                    {
                        bool right = (s.GlassIndex % 2) == 0;
                        double cy = gy + gh / 2, len = Math.Min(gw * 0.6, 60);
                        double x1 = gx + gw / 2 - len / 2, x2 = gx + gw / 2 + len / 2;
                        AddArrow(right ? x1 : x2, cy, right ? x2 : x1, cy, pen);
                        break;
                    }
                case SashType.SlidingV:
                    {
                        bool down = s.GlassIndex == 0;
                        double cx = gx + gw / 2, len = Math.Min(gh * 0.6, 60);
                        double y1 = gy + gh / 2 - len / 2, y2 = gy + gh / 2 + len / 2;
                        AddArrow(cx, down ? y1 : y2, cx, down ? y2 : y1, pen);
                        break;
                    }
            }
        }

        // ── 繪圖小工具 ──

        private void AddRect(double x, double y, double w, double h, Brush fill, Brush stroke, double thickness, bool dashed)
        {
            if (w <= 0 || h <= 0) return;
            var r = new Rectangle { Width = w, Height = h, Fill = fill, Stroke = stroke, StrokeThickness = thickness };
            if (dashed) r.StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 });
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            _canvas.Children.Add(r);
        }

        private void AddLine(double x1, double y1, double x2, double y2, Brush stroke, double thickness, bool dashed)
        {
            var l = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = thickness };
            if (dashed) l.StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 });
            _canvas.Children.Add(l);
        }

        private void AddArrow(double x1, double y1, double x2, double y2, Brush stroke)
        {
            AddLine(x1, y1, x2, y2, stroke, 1.3, false);
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            double ux = dx / len, uy = dy / len;
            const double head = 7;
            AddLine(x2, y2, x2 - head * ux + head * 0.5 * uy, y2 - head * uy - head * 0.5 * ux, stroke, 1.3, false);
            AddLine(x2, y2, x2 - head * ux - head * 0.5 * uy, y2 - head * uy + head * 0.5 * ux, stroke, 1.3, false);
        }

        private void AddText(string text, double x, double y)
        {
            var t = new TextBlock { Text = text, FontSize = 11, Foreground = Hex("#566573") };
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, y);
            _canvas.Children.Add(t);
        }

        // ────────────────────────────────────────────────
        // 產生
        // ────────────────────────────────────────────────

        private void OnGenerate(object sender, RoutedEventArgs e)
        {
            _formError = ReadForm();
            if (_formError != null)
            {
                _txtError.Text = _formError;
                return;
            }

            WindowFamilySpec spec;
            try
            {
                spec = _b.BuildSpec();
                WindowLayoutMath.Compute(spec);   // 提早擋掉「尺寸扣完框料變負值」之類的錯誤
            }
            catch (WindowSpecException ex)
            {
                _txtError.Text = ex.Message;
                return;
            }

            _btnGenerate.IsEnabled = false;
            _txtError.Text = "";
            _txtResult.Text = "生成中…（建立族文件、存檔" + (spec.LoadIntoProject ? "、載入專案" : "") + "，請稍候）";
            // 先讓畫面重繪，再進入會佔住 UI 執行緒的 Revit API 呼叫
            Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Render);

            try
            {
                // 此處就在 IExternalCommand.Execute 的 Revit API 內容裡（對話框為 modal），不需 ExternalEvent
                WindowFamilyBuildResult result = WindowFamilyService.Generate(_uiApp, spec);
                _txtResult.Text = FormatResult(result);
                Logger.Info("萬用窗 add-in 已生成：" + result.FamilyPath);
            }
            catch (WindowSpecException ex)
            {
                _txtResult.Text = "";
                _txtError.Text = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.Error("萬用窗 add-in 生成失敗", ex);
                _txtResult.Text = "";
                _txtError.Text = "生成失敗：" + ex.Message;
            }
            finally
            {
                _btnGenerate.IsEnabled = true;
            }
        }

        private static string FormatResult(WindowFamilyBuildResult r)
        {
            var d = r.Derived;
            var sb = new StringBuilder();
            sb.AppendLine("已生成族「" + r.FamilyName + "」，型別「" + r.TypeName + "」");
            sb.AppendLine("族檔：" + r.FamilyPath);
            sb.AppendLine(r.Loaded ? "已載入目前專案。" : "未載入專案（只生成 .rfa 檔）。");
            sb.Append("扇數 ").Append(d.SashCount);
            if (d.ScreenCount > 0) sb.Append("（另有紗窗 ").Append(d.ScreenCount).Append("）");
            sb.Append("，可開 ").AppendLine(d.OpenableCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("玻璃面積 " + d.GlassAreaM2.ToString("0.000", CultureInfo.InvariantCulture)
                          + " m²｜有效採光 " + d.DaylightAreaM2.ToString("0.000", CultureInfo.InvariantCulture)
                          + " m²｜可開口 " + d.OpenableAreaM2.ToString("0.000", CultureInfo.InvariantCulture) + " m²");
            sb.AppendLine("MO：" + Fmt(d.MoWidth) + " × " + Fmt(d.MoHeight) + " cm｜IFC：IfcWindow／" + r.IfcPartitioningType);
            if (r.Warnings != null && r.Warnings.Count > 0)
            {
                sb.AppendLine("警告：");
                foreach (var w in r.Warnings) sb.AppendLine("  - " + w);
            }
            return sb.ToString();
        }

        // ────────────────────────────────────────────────
        // 版面小工具
        // ────────────────────────────────────────────────

        private static Border Section(string title)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = Hex("#1F3A52")
            });
            return new Border
            {
                BorderBrush = Hex("#C8D0D8"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = panel
            };
        }

        private static TextBlock Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Hex("#7F8C8D"),
                Margin = new Thickness(0, 4, 0, 0)
            };
        }

        private static void AddRow(System.Windows.Controls.Grid g, int row, string label, UIElement control)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 2) };
            System.Windows.Controls.Grid.SetRow(tb, row);
            System.Windows.Controls.Grid.SetColumn(tb, 0);
            g.Children.Add(tb);
            var fe = control as FrameworkElement;
            if (fe != null && fe.Margin == new Thickness(0)) fe.Margin = new Thickness(0, 2, 0, 2);
            System.Windows.Controls.Grid.SetRow(control, row);
            System.Windows.Controls.Grid.SetColumn(control, 1);
            g.Children.Add(control);
        }

        private ComboBox NumberCombo(int from, int to)
        {
            var cbo = new ComboBox();
            for (int i = from; i <= to; i++) cbo.Items.Add(i.ToString(CultureInfo.InvariantCulture));
            return cbo;
        }

        private TextBox NumberBox()
        {
            var tb = new TextBox();
            tb.TextChanged += OnFieldChanged;
            return tb;
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static SolidColorBrush Hex(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
