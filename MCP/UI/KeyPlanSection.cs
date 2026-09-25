using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using RevitMCP.Configuration;
using RevitMCP.Core;

using WpfGrid = System.Windows.Controls.Grid;
using WpfColor = System.Windows.Media.Color;
using WpfLine = System.Windows.Shapes.Line;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfPath = System.Windows.Shapes.Path;
using WpfRectangle = System.Windows.Shapes.Rectangle;
// Autodesk.Revit.DB 與 System.Windows 都有 Point／Transform，兩邊都 using 會是 CS0104
// 模稜兩可參考，這裡明確 alias 螢幕座標用的 WPF 版本；模型座標一律用 XYZ，不需要 alias。
using WpfPoint = System.Windows.Point;
using WpfButton = System.Windows.Controls.Button;

namespace RevitMCP.UI
{
    /// <summary>
    /// 迷你 key plan：底圖（已裁剪的平面匯出圖，沒有的話中性格線）＋每台相機的
    /// 點／方向箭頭／視野扇形＋裁剪框外框（目視對齊檢查）＋選取相機的剖面線（若已產生剖面）。
    ///
    /// 2026-09-24 三次改版：
    ///   (a) 改嵌進「相機」單頁，底圖平面 ComboBox 疊在地圖右上角。
    ///   (b) 拿掉 ScrollViewer，改成「符合畫面」自動縮放置中＋滑鼠滾輪縮放（游標為中心）＋
    ///       左鍵／中鍵拖曳平移；地圖區大小改變時（分割線拖曳、面板縮放）若使用者沒有手動
    ///       調整過視角，自動重新符合畫面。單一 MatrixTransform 套在整個地圖內容層
    ///       （<see cref="_mapLayer"/>）；每個相機標記另外包一層反向縮放（1/scale，以標記
    ///       錨點為中心），讓標記／箭頭／文字大小在任何縮放層級下永遠是固定螢幕像素，只有
    ///       錨點位置隨底圖縮放/平移移動——底圖本身（點陣圖／中性格線）不做反向縮放，縮放時
    ///       自然跟著變大變小是預期行為。
    ///   (c) 只畫「底圖所在樓層」的相機；其他樓層的相機預設隱藏，可用「顯示其他樓層」勾選框
    ///       切換成灰色淡化＋樓層字尾顯示（2026-09-24 實測回報：不同樓層相機混在一張底圖上
    ///       位置容易誤判）。
    /// </summary>
    internal class KeyPlanSection : UserControl
    {
        private const double ArrowLen = 22;
        private const double FanRadius = 46;
        private const double DefaultFanHalfAngleDeg = 35; // 沒有 FOV 估算值時的預設扇形張角
        private const double HeadLen = 9;   // 箭頭三角形長度（螢幕像素，固定，不隨底圖縮放）
        private const double HeadHalfWidth = 5; // 箭頭三角形半寬（螢幕像素）

        private const double WheelZoomStep = 1.2;
        private const double MinScaleFactor = 0.5;
        private const double MaxScaleFactor = 20.0;
        private const double PanThresholdPx = 4.0;

        private readonly ComboBox _planCombo;
        private readonly System.Windows.Controls.Primitives.ToggleButton _showOtherLevelsCheck;
        private readonly System.Windows.Controls.Primitives.ToggleButton _alignmentToggle;
        private readonly System.Windows.Controls.Primitives.ToggleButton _showAllPlansToggle;
        /// <summary>2026-09-25：底圖清單原始資料（全部已裁剪平面）與每樓層相機數；精簡清單由這兩者算出。</summary>
        private List<PlanViewOption> _allPlans = new List<PlanViewOption>();
        private Dictionary<ElementId, int> _cameraCountByLevel = new Dictionary<ElementId, int>();
        private readonly Border _viewport;
        private readonly Canvas _mapLayer;
        private readonly MatrixTransform _mapTransform = new MatrixTransform(Matrix.Identity);
        private readonly TextBlock _hintText;
        private readonly WpfButton _fitButton;
        /// <summary>2026-09-24 第二輪崩潰調查：程式化設定底圖 combo（SetPlanOptions／ResetToNeutral）
        /// 不可觸發 PlanSelected→底圖匯出；只有使用者自己選 combo、或使用者點選相機造成的
        /// SelectPlanByLevel 才會觸發。</summary>
        private bool _suppressPlanEvent;

        private List<CameraRow> _rows = new List<CameraRow>();
        private BaseMapSnapshot _currentMap;
        private ElementId _selectedViewId;

        // ── 縮放／平移狀態 ──
        private double _contentWidth = 500, _contentHeight = 350;
        private double _scale = 1.0;
        private double _fitScale = 1.0;
        private WpfPoint _offset = new WpfPoint(0, 0);
        private bool _userAdjustedView;
        private bool _potentialPan;
        private bool _panning;
        private WpfPoint _panStartScreen;
        private WpfPoint _panStartOffset;

        // ── 選取相機的剖面線（模型座標；沒有就是 null，不畫） ──
        private XYZ _sectionP1, _sectionP2;
        private WpfLine _sectionLineVisual;
        private Func<XYZ, WpfPoint> _currentProject;

        /// <summary>一台相機在畫布上畫出的標記群組：獨立小 Canvas（錨點在標記位置、套用 1/scale
        /// 反向縮放，讓標記在任何縮放層級下維持固定螢幕大小），群組內含四個圖元（點／方向桿／
        /// 箭頭三角形／視野扇形），選取狀態變化時不必整個重畫，只要重上色（<see cref="HighlightSelected"/>）。</summary>
        private class MarkerVisual
        {
            public Canvas Group;
            public ScaleTransform InverseScale;
            public WpfEllipse Dot;
            public WpfLine Shaft;
            /// <summary>2026-09-25：眼睛處的實心小楔形（取代舊的朝前三角形箭頭）。</summary>
            public WpfPath Head;
            public WpfPath Fan;
        }
        private readonly Dictionary<ElementId, MarkerVisual> _markersByViewId = new Dictionary<ElementId, MarkerVisual>();

        public event Action<PlanViewOption> PlanSelected;
        /// <summary>左鍵單擊相機標記（2026-09-24 v5：語意從「只選取」改成「選取＋觸發開啟動作」，
        /// 由 CameraManagerPanelControl.RunConfiguredOpenAction 決定實際動作，本類別不知道
        /// 「一鍵 4 視圖」這件事）。</summary>
        public event Action<CameraRow> MarkerActivated;
        /// <summary>右鍵單擊相機標記：(row, 這個標記的視覺元素，供選單 PlacementTarget 用)。</summary>
        public event Action<CameraRow, UIElement> MarkerContextMenuRequested;

        private static CameraManagerSettings Settings => ConfigManager.Instance.Settings.CameraManager;

        /// <summary>除錯疊圖「顯示對位框」（右鍵地圖切換，預設關閉，不存設定）。</summary>
        private bool _showAlignmentOverlay;

        public KeyPlanSection()
        {
            WpfGrid root = new WpfGrid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _mapLayer = new Canvas
            {
                Background = CameraManagerTheme.Background,
                Width = _contentWidth,
                Height = _contentHeight,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = _mapTransform
            };
            _hintText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Width = 460, Margin = new Thickness(20),
                Foreground = CameraManagerTheme.SecondaryText, FontFamily = CameraManagerTheme.UiFont
            };
            Canvas.SetLeft(_hintText, 10);
            Canvas.SetTop(_hintText, 10);
            _mapLayer.Children.Add(_hintText);

            _viewport = new Border
            {
                ClipToBounds = true,
                Background = CameraManagerTheme.Background,
                Child = _mapLayer
            };
            _viewport.SizeChanged += (s, e) => { if (!_userAdjustedView) FitToView(); };
            _viewport.PreviewMouseWheel += Viewport_PreviewMouseWheel;
            _viewport.PreviewMouseLeftButtonDown += (s, e) => TryStartPan(e);
            _viewport.PreviewMouseDown += (s, e) => { if (e.ChangedButton == MouseButton.Middle) TryStartPan(e); };
            _viewport.PreviewMouseMove += Viewport_PreviewMouseMove;
            _viewport.PreviewMouseUp += Viewport_PreviewMouseUp;
            // 2026-09-24 第四輪：拿掉「右鍵地圖＝除錯選單」（每次右鍵都 new 一個 ContextMenu），
            // 「顯示對位框」改成右上角浮動工具列上的切換鈕。
            WpfGrid.SetRow(_viewport, 0);
            root.Children.Add(_viewport);

            // ── 右上角：半透明浮動工具列（底圖 combo ＋ 顯示其他樓層／符合畫面／顯示對位框 圖示鈕） ──
            Border overlay = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                BorderBrush = CameraManagerTheme.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(3),
                Margin = new Thickness(0, 4, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            StackPanel overlayRow = new StackPanel { Orientation = Orientation.Horizontal };

            _planCombo = new ComboBox
            {
                Width = 132, Height = 22, FontSize = CameraManagerTheme.SecondaryFontSize,
                FontFamily = CameraManagerTheme.UiFont, Foreground = CameraManagerTheme.Text, Background = CameraManagerTheme.Surface,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 1, 4, 1),
                ToolTip = "③ 底圖：選擇位置圖使用的已裁剪平面視圖"
            };
            _planCombo.SelectionChanged += (s, e) =>
            {
                try
                {
                    if (_suppressPlanEvent) return;
                    if (_planCombo.SelectedItem is PlanViewOption plan) PlanSelected?.Invoke(plan);
                }
                catch (Exception ex) { Logger.Error("相機管理面板：底圖 combo 選擇失敗", ex); }
            };
            overlayRow.Children.Add(_planCombo);

            // 2026-09-25：「顯示全部平面」——預設只列有相機的樓層（每層一張偏好平面），打開才列全部已裁剪平面。
            _showAllPlansToggle = OverlayToggle("全", "③ 顯示全部平面：底圖清單列出所有已裁剪平面（關閉＝只列有相機的樓層，每層一張）", Settings.ShowAllPlansInBaseMap);
            _showAllPlansToggle.Checked += (s, e) => SafeUi(() => OnShowAllPlansChanged(true), "ShowAllPlans");
            _showAllPlansToggle.Unchecked += (s, e) => SafeUi(() => OnShowAllPlansChanged(false), "ShowAllPlans");
            overlayRow.Children.Add(_showAllPlansToggle);

            _showOtherLevelsCheck = OverlayToggle("層", "③ 顯示其他樓層：以灰色淡化顯示不在底圖樓層的相機", Settings.ShowOtherLevelCamerasOnMap);
            _showOtherLevelsCheck.Checked += (s, e) => SafeUi(() => OnShowOtherLevelsChanged(true), "ShowOtherLevels");
            _showOtherLevelsCheck.Unchecked += (s, e) => SafeUi(() => OnShowOtherLevelsChanged(false), "ShowOtherLevels");
            overlayRow.Children.Add(_showOtherLevelsCheck);

            _fitButton = new WpfButton
            {
                Content = OverlayGlyph("⤢"), Width = 24, Height = 22, Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(0),
                Background = CameraManagerTheme.Surface, BorderBrush = CameraManagerTheme.BorderBrush, Foreground = CameraManagerTheme.Text,
                Cursor = Cursors.Hand,
                ToolTip = "③ 符合畫面：縮放並置中至符合目前地圖區大小（雙擊地圖空白處也會這麼做）"
            };
            _fitButton.Click += (s, e) => SafeUi(ResetToFit, "FitButton");
            overlayRow.Children.Add(_fitButton);

            _alignmentToggle = OverlayToggle("框", "③ 顯示對位框：紅虛線＝模型裁剪框、藍虛線＝底圖換算範圍（除錯用，檢查標記對位）", false);
            _alignmentToggle.Checked += (s, e) => SafeUi(() => SetAlignmentOverlay(true), "AlignmentToggle");
            _alignmentToggle.Unchecked += (s, e) => SafeUi(() => SetAlignmentOverlay(false), "AlignmentToggle");
            overlayRow.Children.Add(_alignmentToggle);

            overlay.Child = overlayRow;
            WpfGrid.SetRow(overlay, 0);
            root.Children.Add(overlay);

            Content = root;
            MinHeight = 120; // 規格：面板太矮時地圖區先縮，但不小於 120px
        }

        /// <param name="cameraCountByLevel">每個樓層（相機的下方樓層 LevelId）有幾台相機；精簡清單用。</param>
        public void SetPlanOptions(List<PlanViewOption> plans, ElementId defaultId, Dictionary<ElementId, int> cameraCountByLevel = null)
        {
            _allPlans = plans ?? new List<PlanViewOption>();
            _cameraCountByLevel = cameraCountByLevel ?? new Dictionary<ElementId, int>();
            RebuildPlanItems(defaultId);
        }

        /// <summary>依「顯示全部平面」設定重建底圖 combo 的項目並選回 <paramref name="selectId"/>（程式化，不觸發匯出）。
        /// 精簡模式：只列 GenLevel 上至少有一台相機的樓層，每層一張（沿用既有預設底圖的挑法：依名稱排序的第一張），
        /// 標籤「樓層 — 平面（N 台相機）」；目前選取的底圖若不在精簡清單內，保留並附加在最後。</summary>
        private void RebuildPlanItems(ElementId selectId)
        {
            List<PlanViewOption> items;
            bool showAll = Settings.ShowAllPlansInBaseMap;
            if (showAll)
            {
                items = _allPlans;
            }
            else
            {
                items = new List<PlanViewOption>();
                var seenLevels = new HashSet<ElementId>();
                foreach (PlanViewOption first in _allPlans) // _allPlans 已依名稱排序（ReadSnapshot）
                {
                    if (first?.GenLevelId == null) continue;
                    if (!_cameraCountByLevel.TryGetValue(first.GenLevelId, out int n) || n <= 0) continue;
                    if (!seenLevels.Add(first.GenLevelId)) continue;
                    // 2026-09-25（§4.8）：每層一張＝該層面積最大的已裁剪平面（樓層平面、非從屬優先），不再是名稱第一張。
                    PlanViewOption p = BaseMapPlanChooser.ChooseForLevel(_allPlans, first.GenLevelId, null, out string why) ?? first;
                    Logger.Info($"相機管理面板：底圖精簡清單 樓層 {p.LevelName ?? "?"} → {p.CropLogText}（{why}）");
                    items.Add(new PlanViewOption
                    {
                        ViewId = p.ViewId,
                        GenLevelId = p.GenLevelId,
                        PlanName = p.PlanName,
                        LevelName = p.LevelName,
                        DisplayName = $"{p.LevelName ?? "?"} — {p.PlanName ?? p.DisplayName}（{n} 台相機）",
                        IsFloorPlan = p.IsFloorPlan,
                        IsDependent = p.IsDependent,
                        HasCrop = p.HasCrop,
                        CropOriginX = p.CropOriginX, CropOriginY = p.CropOriginY,
                        CropBasisXX = p.CropBasisXX, CropBasisXY = p.CropBasisXY,
                        CropBasisYX = p.CropBasisYX, CropBasisYY = p.CropBasisYY,
                        CropMinX = p.CropMinX, CropMinY = p.CropMinY, CropMaxX = p.CropMaxX, CropMaxY = p.CropMaxY
                    });
                }
                if (selectId != null && !items.Any(i => i.ViewId == selectId))
                {
                    PlanViewOption cur = _allPlans.FirstOrDefault(p => p.ViewId == selectId);
                    if (cur != null) items.Add(cur);
                }
            }

            _suppressPlanEvent = true;
            try
            {
                _planCombo.ItemsSource = items;
                if (items.Count > 0)
                    _planCombo.SelectedItem = (selectId != null ? items.FirstOrDefault(p => p.ViewId == selectId) : null) ?? items[0];
            }
            finally
            {
                _suppressPlanEvent = false;
            }
            Logger.Info($"相機管理面板：底圖清單（{(showAll ? "全部平面" : "只列有相機的樓層")}）{items.Count}/{_allPlans.Count} 項。");
        }

        private void OnShowAllPlansChanged(bool value)
        {
            Settings.ShowAllPlansInBaseMap = value;
            ConfigManager.Instance.SaveSettings();
            ElementId current = (_planCombo.SelectedItem as PlanViewOption)?.ViewId;
            RebuildPlanItems(current);
        }

        /// <summary>目前底圖 combo 選的那張平面所在樓層（沒有則 null）。</summary>
        public ElementId SelectedPlanLevelId => (_planCombo.SelectedItem as PlanViewOption)?.GenLevelId;

        /// <summary>2026-09-25：把地圖平移到讓指定相機位於視窗中央；相機已在可視範圍內（留 16px 邊）時不動，
        /// 維持目前縮放。純 WPF。回傳是否有平移。</summary>
        public bool CenterOnCamera(ElementId viewId)
        {
            if (viewId == null || _currentProject == null) return false;
            CameraRow row = _rows.FirstOrDefault(r => r.ViewId == viewId);
            if (row == null || !IsFinite(row.EyeXFeet) || !IsFinite(row.EyeYFeet) || !IsFinite(row.EyeZFeet)) return false;
            WpfPoint c = _currentProject(new XYZ(row.EyeXFeet, row.EyeYFeet, row.EyeZFeet));
            if (!IsFinite(c.X) || !IsFinite(c.Y)) return false;
            double vw = _viewport.ActualWidth, vh = _viewport.ActualHeight;
            if (vw <= 0 || vh <= 0) return false;
            double sx = c.X * _scale + _offset.X, sy = c.Y * _scale + _offset.Y;
            const double margin = 16;
            if (sx >= margin && sx <= vw - margin && sy >= margin && sy <= vh - margin) return false;
            _offset = new WpfPoint(vw / 2.0 - c.X * _scale, vh / 2.0 - c.Y * _scale);
            _userAdjustedView = true;
            ApplyTransform();
            UpdateAllMarkerInverseScale();
            return true;
        }

        /// <summary>清空成中性狀態（尚未按「重新整理」、文件切換／關閉）：清掉底圖、相機、剖面線與
        /// 底圖清單，只顯示提示文字。不觸發任何事件、不碰 Revit。</summary>
        public void ResetToNeutral(string message)
        {
            _suppressPlanEvent = true;
            try { _planCombo.ItemsSource = null; }
            finally { _suppressPlanEvent = false; }
            _rows = new List<CameraRow>();
            _sectionP1 = _sectionP2 = null;
            _sectionLineVisual = null;
            _selectedViewId = null;
            ShowMessage(message);
        }

        /// <summary>live sync／編輯後用：沿用目前已顯示的底圖（不重新匯出），只用新的相機資料
        /// 重畫標記並保留使用者目前的縮放平移。沒有底圖時什麼都不畫（不會自己去匯出）。</summary>
        public void RedrawMarkers(List<CameraRow> rows, bool hasCroppedPlan)
        {
            if (_currentMap != null && _mapLayer.Background is ImageBrush ib && ib.ImageSource is BitmapSource bmp)
                DrawWithBaseMap(bmp, _currentMap, rows, preserveView: true);
            else if (!hasCroppedPlan && _currentMap == null && _currentProject != null)
                DrawWithNeutralGrid(rows);
            else
                _rows = rows ?? new List<CameraRow>();
        }

        /// <summary>選到某個樓層對應的底圖平面（若清單裡有的話），觸發跟手動選 combo 一樣的
        /// PlanSelected 事件；找不到就不動作。供「選取相機時自動切換底圖到該相機樓層」使用。</summary>
        public bool SelectPlanByLevel(ElementId levelId, double? eyeX = null, double? eyeY = null)
        {
            if (levelId == null || !(_planCombo.ItemsSource is IEnumerable<PlanViewOption>)) return false;
            // 2026-09-25（§4.8）：同層已裁剪平面中「包含相機眼睛、面積最大」者；都不包含 → 面積最大。
            IList<double[]> eyes = eyeX.HasValue && eyeY.HasValue ? new List<double[]> { new[] { eyeX.Value, eyeY.Value } } : null;
            PlanViewOption target = BaseMapPlanChooser.ChooseForLevel(_allPlans, levelId, eyes, out string why);
            if (target == null) return false;
            Logger.Info($"相機管理面板：選取相機 → 底圖候選 {target.CropLogText}（{why}）");
            return SelectPlanOption(target);
        }

        /// <summary>目前底圖 combo 選的平面裁剪框是否包含模型座標 (x,y)（沒有選取／沒有裁剪資訊＝false）。</summary>
        public bool SelectedPlanContains(double x, double y)
            => (_planCombo.SelectedItem as PlanViewOption)?.CropContains(x, y) == true;

        /// <summary>把 target 選進 combo（精簡清單沒有就附加）；已經是它就不動。回傳是否真的換了（→ PlanSelected → 匯出）。</summary>
        private bool SelectPlanOption(PlanViewOption target)
        {
            if (target?.ViewId == null) return false;
            List<PlanViewOption> items = (_planCombo.ItemsSource as IEnumerable<PlanViewOption>)?.ToList() ?? new List<PlanViewOption>();
            PlanViewOption match = items.FirstOrDefault(p => p.ViewId != null && p.ViewId.GetIdValue() == target.ViewId.GetIdValue());
            if (match == null)
            {
                object cur = _planCombo.SelectedItem;
                items.Add(target);
                _suppressPlanEvent = true;
                try { _planCombo.ItemsSource = items; _planCombo.SelectedItem = cur; }
                finally { _suppressPlanEvent = false; }
                match = target;
            }
            if (ReferenceEquals(_planCombo.SelectedItem, match)) return false; // 已經是這張，不必重觸發
            _planCombo.SelectedItem = match;
            return true;
        }

        /// <summary>2026-09-25：跟隨作用中透視圖用——找「該樓層」的已裁剪平面；沒有就找最近的下方樓層，
        /// 再沒有就最近的上方樓層（<paramref name="levelsByElevation"/> 由低到高）。在全部平面（_allPlans）裡找，
        /// 精簡清單沒有的就附加進 combo。回傳選到的樓層 Id（null＝完全找不到）；<paramref name="switched"/>＝
        /// 是否真的換了 combo（已經是那張就不換、不觸發匯出）。</summary>
        internal ElementId SelectPlanByLevelWithFallback(ElementId levelId, IList<LevelOption> levelsByElevation, out bool switched,
            double? eyeX = null, double? eyeY = null)
        {
            switched = false;
            if (levelId == null) return null;
            List<PlanViewOption> all = _allPlans ?? new List<PlanViewOption>();
            // 2026-09-25（§4.8）：每層用 BaseMapPlanChooser（包含相機眼睛且面積最大 → 否則面積最大），不再取名稱第一張。
            IList<double[]> eyes = eyeX.HasValue && eyeY.HasValue ? new List<double[]> { new[] { eyeX.Value, eyeY.Value } } : null;
            string lastWhy = "";
            PlanViewOption FirstPlanOf(ElementId lv)
            {
                if (lv == null) return null;
                PlanViewOption p = BaseMapPlanChooser.ChooseForLevel(all, lv, eyes, out string why);
                if (p != null) lastWhy = why;
                return p;
            }

            PlanViewOption target = FirstPlanOf(levelId);
            if (target == null && levelsByElevation != null)
            {
                int idx = -1;
                for (int i = 0; i < levelsByElevation.Count; i++)
                    if (levelsByElevation[i]?.LevelId != null && levelsByElevation[i].LevelId.GetIdValue() == levelId.GetIdValue()) { idx = i; break; }
                if (idx >= 0)
                {
                    for (int i = idx - 1; i >= 0 && target == null; i--) target = FirstPlanOf(levelsByElevation[i]?.LevelId);
                    for (int i = idx + 1; i < levelsByElevation.Count && target == null; i++) target = FirstPlanOf(levelsByElevation[i]?.LevelId);
                }
            }
            if (target == null) return null;
            Logger.Info($"相機管理面板：跟隨 → 底圖候選 {target.CropLogText}（{lastWhy}）");
            switched = SelectPlanOption(target); // → PlanSelected → 匯出
            return target.GenLevelId;
        }

        public void ShowMessage(string text)
        {
            _currentMap = null;
            _mapLayer.Children.Clear();
            _markersByViewId.Clear();
            _contentWidth = 500; _contentHeight = 350;
            _mapLayer.Width = _contentWidth; _mapLayer.Height = _contentHeight;
            _mapLayer.Background = CameraManagerTheme.Background;
            _hintText.Text = text;
            Canvas.SetLeft(_hintText, 10);
            Canvas.SetTop(_hintText, 10);
            _mapLayer.Children.Add(_hintText);
            _currentProject = null;
            _userAdjustedView = false;
            FitToView();
        }

        /// <summary>用真實裁剪底圖畫面板。</summary>
        public void DrawWithBaseMap(BitmapSource bitmap, BaseMapSnapshot map, List<CameraRow> rows, bool preserveView = false)
        {
            _currentMap = map;
            _rows = rows ?? new List<CameraRow>();
            _mapLayer.Children.Clear();
            _markersByViewId.Clear();

            _contentWidth = bitmap.PixelWidth;
            _contentHeight = bitmap.PixelHeight;
            _mapLayer.Width = _contentWidth;
            _mapLayer.Height = _contentHeight;
            // 2026-09-24 第三輪對位根因：Revit ExportImage 的 PNG 帶 pHYs=5905 px/m（150 DPI）。
            // BitmapImage 讀進來 DpiX=150，ImageBrush Stretch.None 會以 DIP 尺寸
            // PixelWidth×96/150（1600px → 1024 DIP）畫在左上角，但畫布與標記座標都用「像素」
            // （1600 寬）——底圖被縮成 64%、標記卻沒縮，越往右下偏越多（Loft Unit 被畫到建築下方）。
            // 改用 Stretch.Fill：底圖一律填滿 _mapLayer（寬高＝像素數），與 DPI 中繼資料脫鉤。
            _mapLayer.Background = new ImageBrush(bitmap) { Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };

            double widthFeet = map.CropMaxXFeet - map.CropMinXFeet;
            double heightFeet = map.CropMaxYFeet - map.CropMinYFeet;
            if (!(widthFeet > 1e-6) || !(heightFeet > 1e-6) || !IsFinite(widthFeet) || !IsFinite(heightFeet)) { _currentProject = null; FitToView(); return; }

            double scaleX = bitmap.PixelWidth / widthFeet;
            double scaleY = bitmap.PixelHeight / heightFeet;
            Func<XYZ, WpfPoint> project = p => ToLocalPixel(p, map, scaleX, scaleY);
            _currentProject = project;

            // 2026-09-24 對位改版：除錯疊圖「顯示對位框」（右鍵地圖開關，預設關閉）——畫出換算用
            // 的模型裁剪框（紅虛線）與整張換算範圍（藍虛線），圖面內容若沒有剛好落在紅框內，
            // 就是對位有誤差，一眼可見。
            if (_showAlignmentOverlay) DrawAlignmentOverlay(map, project, bitmap.PixelWidth, bitmap.PixelHeight);

            Logger.Info($"相機管理面板：底圖疊圖 bitmap={bitmap.PixelWidth}x{bitmap.PixelHeight}px (png={map.ImagePixelWidth}x{map.ImagePixelHeight}) " +
                $"extentsMm={widthFeet * 304.8:F0}x{heightFeet * 304.8:F0} source={map.ExtentsSource ?? "legacy"} " +
                $"scalePxPerFt X={scaleX:F4} Y={scaleY:F4} dpi={bitmap.DpiX:F1}x{bitmap.DpiY:F1} (DIP {bitmap.Width:F0}x{bitmap.Height:F0}, brush=Fill) " +
                $"cropLocalFt Min=({map.CropMinXFeet:F3},{map.CropMinYFeet:F3}) Max=({map.CropMaxXFeet:F3},{map.CropMaxYFeet:F3}) " +
                $"tfOrigin=({map.OriginX:F3},{map.OriginY:F3},{map.OriginZ:F3}) basisX=({map.BasisXX:F4},{map.BasisXY:F4}) basisY=({map.BasisYX:F4},{map.BasisYY:F4})");

            bool showOthers = Settings.ShowOtherLevelCamerasOnMap;
            foreach (CameraRow row in _rows)
            {
                bool dim = map.GenLevelId != null && row.LevelId != null && row.LevelId != map.GenLevelId;
                if (dim && !showOthers)
                {
                    row.InMapRange = false;
                    continue;
                }
                DrawOneCamera(row, project, dim, checkRange: true, map: map);
            }

            if (preserveView && _userAdjustedView)
            {
                ApplyTransform();
                UpdateAllMarkerInverseScale();
            }
            else
            {
                _userAdjustedView = false;
                FitToView();
            }
            RedrawSectionLineOverlay();
        }

        /// <summary>沒有任何已啟用 CropBox 的平面時，用中性格線＋依相機分布自動縮放（4.8）。
        /// 這個模式沒有「底圖所在樓層」的概念，不做跨樓層過濾，全部相機一起畫。</summary>
        public void DrawWithNeutralGrid(List<CameraRow> rows)
        {
            _currentMap = null;
            _rows = rows ?? new List<CameraRow>();
            _mapLayer.Children.Clear();
            _markersByViewId.Clear();

            const double canvasSize = 480;
            const double margin = 40;
            _contentWidth = canvasSize;
            _contentHeight = canvasSize;
            _mapLayer.Width = canvasSize;
            _mapLayer.Height = canvasSize;
            _mapLayer.Background = CameraManagerTheme.Background;

            for (int i = 0; i <= 10; i++)
            {
                double t = i / 10.0 * canvasSize;
                _mapLayer.Children.Add(new WpfLine { X1 = t, Y1 = 0, X2 = t, Y2 = canvasSize, Stroke = CameraManagerTheme.GridDivider, StrokeThickness = 0.5, IsHitTestVisible = false });
                _mapLayer.Children.Add(new WpfLine { X1 = 0, Y1 = t, X2 = canvasSize, Y2 = t, Stroke = CameraManagerTheme.GridDivider, StrokeThickness = 0.5, IsHitTestVisible = false });
            }

            if (_rows.Count == 0)
            {
                TextBlock msg = new TextBlock { Text = "沒有相機可顯示。", Foreground = CameraManagerTheme.SecondaryText, Margin = new Thickness(10), FontFamily = CameraManagerTheme.UiFont };
                _mapLayer.Children.Add(msg);
                _currentProject = null;
                _userAdjustedView = false;
                FitToView();
                return;
            }

            double minX = _rows.Min(r => r.EyeXFeet), maxX = _rows.Max(r => r.EyeXFeet);
            double minY = _rows.Min(r => r.EyeYFeet), maxY = _rows.Max(r => r.EyeYFeet);
            double spanX = Math.Max(maxX - minX, 1.0);
            double spanY = Math.Max(maxY - minY, 1.0);
            double scale = (canvasSize - margin * 2) / Math.Max(spanX, spanY);

            Func<XYZ, WpfPoint> project = p =>
            {
                double px = margin + (p.X - minX) * scale;
                double py = canvasSize - margin - (p.Y - minY) * scale; // Y 往上為正，畫布往下為正
                return new WpfPoint(px, py);
            };
            _currentProject = project;

            foreach (CameraRow row in _rows)
                DrawOneCamera(row, project, dim: false, checkRange: false, map: null);

            TextBlock hint = new TextBlock
            {
                Text = "（此樓層沒有已啟用裁剪框的平面視圖，改用依相機分布自動縮放的中性格線，非實際比例底圖）",
                FontSize = 10, Foreground = CameraManagerTheme.SecondaryText, TextWrapping = TextWrapping.Wrap, Width = canvasSize - 10
            };
            Canvas.SetLeft(hint, 5);
            Canvas.SetTop(hint, canvasSize - 24);
            _mapLayer.Children.Add(hint);

            _userAdjustedView = false;
            FitToView();
            RedrawSectionLineOverlay();
        }

        private void OnShowOtherLevelsChanged(bool value)
        {
            Settings.ShowOtherLevelCamerasOnMap = value;
            ConfigManager.Instance.SaveSettings();
            // 重畫目前這張底圖（沿用既有資料，不必重新匯出圖片）。
            if (_currentMap != null && _mapLayer.Background is ImageBrush ib && ib.ImageSource is BitmapSource bmp)
                DrawWithBaseMap(bmp, _currentMap, _rows);
            else if (_currentMap == null && _rows.Count > 0)
                DrawWithNeutralGrid(_rows);
        }

        /// <summary>模型座標 → 底圖像素。只用 BaseMapSnapshot 攤平的純 double（見其註解），不碰
        /// 任何 Revit 原生物件。</summary>
        private static WpfPoint ToLocalPixel(XYZ eyeModel, BaseMapSnapshot map, double scaleX, double scaleY)
        {
            map.ModelToLocal(eyeModel.X, eyeModel.Y, eyeModel.Z, out double lx, out double ly);
            double px = (lx - map.CropMinXFeet) * scaleX;
            double py = (map.CropMaxYFeet - ly) * scaleY;
            return new WpfPoint(px, py);
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static bool IsFinite(WpfPoint p) => IsFinite(p.X) && IsFinite(p.Y);

        private void DrawOneCamera(CameraRow row, Func<XYZ, WpfPoint> project, bool dim, bool checkRange, BaseMapSnapshot map)
        {
            if (!IsFinite(row.EyeXFeet) || !IsFinite(row.EyeYFeet) || !IsFinite(row.EyeZFeet)) { row.InMapRange = false; return; }
            XYZ eyeModel = new XYZ(row.EyeXFeet, row.EyeYFeet, row.EyeZFeet);

            if (checkRange && map != null)
            {
                map.ModelToLocal(eyeModel.X, eyeModel.Y, eyeModel.Z, out double localX, out double localY);
                bool inRange = localX >= map.CropMinXFeet && localX <= map.CropMaxXFeet
                    && localY >= map.CropMinYFeet && localY <= map.CropMaxYFeet;
                row.InMapRange = inRange;
                if (!inRange) return;
            }
            else
            {
                row.InMapRange = true;
            }

            WpfPoint anchor = project(eyeModel); // 內容（世界像素）座標，隨底圖 pan/zoom 移動
            if (checkRange && map != null)
            {
                map.ModelToLocal(eyeModel.X, eyeModel.Y, eyeModel.Z, out double lgx, out double lgy);
                Logger.Info($"相機管理面板：底圖標記「{row.Name}」eyeModelFt=({row.EyeXFeet:F3},{row.EyeYFeet:F3}) localFt=({lgx:F3},{lgy:F3}) → px=({anchor.X:F1},{anchor.Y:F1})");
            }
            // 2026-09-24 第二輪崩潰調查：Canvas.SetLeft/SetTop 給 Infinity 會讓 WPF 版面在稍後
            // （不在我們的呼叫堆疊內）丟例外，這類例外的堆疊不含 RevitMCP，面板的 Dispatcher 過濾器
            // 攔不到——非有限值一律不畫。
            if (!IsFinite(anchor)) { row.InMapRange = false; return; }

            double planarAngleRad;
            if (checkRange && map != null)
            {
                map.ModelVectorToLocal(row.ForwardXFeet, row.ForwardYFeet, row.ForwardZFeet, out double fx, out double fy);
                planarAngleRad = Math.Atan2(-fy, fx); // 畫布 Y 反向
            }
            else
            {
                planarAngleRad = Math.Atan2(-row.ForwardYFeet, row.ForwardXFeet);
            }

            if (!IsFinite(planarAngleRad)) planarAngleRad = 0;
            double halfFanRad = (row.FovEstimateDeg.HasValue && IsFinite(row.FovEstimateDeg.Value) && row.FovEstimateDeg.Value > 0 && row.FovEstimateDeg.Value < 179
                ? row.FovEstimateDeg.Value / 2.0 : DefaultFanHalfAngleDeg) * Math.PI / 180.0;
            // 2026-09-25（非對稱視錐）：扇形從相機左緣到右緣（相對正前方帶號角，相機右為正）。畫布 Y 向下，
            // 角度增加＝畫面上順時針＝相機右側（例：朝北 forward→畫布角 −90°，東＝相機右→0°），所以邊界角＝planar + edge。
            double leftEdgeRad = -halfFanRad, rightEdgeRad = halfFanRad;
            if (row.FovLeftDeg.HasValue && row.FovRightDeg.HasValue && IsFinite(row.FovLeftDeg.Value) && IsFinite(row.FovRightDeg.Value)
                && row.FovRightDeg.Value > row.FovLeftDeg.Value && row.FovRightDeg.Value - row.FovLeftDeg.Value < 179)
            {
                leftEdgeRad = row.FovLeftDeg.Value * Math.PI / 180.0;
                rightEdgeRad = row.FovRightDeg.Value * Math.PI / 180.0;
            }
            bool isSelected = !dim && _selectedViewId != null && row.ViewId == _selectedViewId;

            // 2026-09-25 室外相機：另畫一個「模型比例」的扇形（半徑＝min(遠裁剪, 室外範圍半徑)，即一鍵 4 視圖
            // Key Plan 會涵蓋的範圍），放在內容座標（隨底圖 pan/zoom 一起縮放，不套標記的反向縮放）。
            // 室內相機維持原本固定像素大小的扇形符號。
            if (!dim && row.IsOutdoor == true && row.OutdoorFanRadiusFeet > 0)
            {
                try
                {
                    double fhx = row.ForwardXFeet, fhy = row.ForwardYFeet;
                    double fl = Math.Sqrt(fhx * fhx + fhy * fhy);
                    if (fl > 1e-9)
                    {
                        XYZ tip = new XYZ(row.EyeXFeet + fhx / fl * row.OutdoorFanRadiusFeet, row.EyeYFeet + fhy / fl * row.OutdoorFanRadiusFeet, row.EyeZFeet);
                        WpfPoint tipPx = project(tip);
                        double rPx = Math.Sqrt((tipPx.X - anchor.X) * (tipPx.X - anchor.X) + (tipPx.Y - anchor.Y) * (tipPx.Y - anchor.Y));
                        if (IsFinite(rPx) && rPx > 1)
                        {
                            WpfPath outdoorFan = BuildFanPath(anchor, planarAngleRad + leftEdgeRad, planarAngleRad + rightEdgeRad, rPx);
                            outdoorFan.IsHitTestVisible = false;
                            WpfColor oc = isSelected ? CameraManagerTheme.AccentColor : CameraManagerTheme.SecondaryTextColor;
                            outdoorFan.Fill = new SolidColorBrush(WpfColor.FromArgb(18, oc.R, oc.G, oc.B));
                            outdoorFan.Stroke = new SolidColorBrush(WpfColor.FromArgb(160, oc.R, oc.G, oc.B));
                            outdoorFan.StrokeThickness = 1;
                            outdoorFan.StrokeDashArray = new DoubleCollection { 4, 3 };
                            _mapLayer.Children.Add(outdoorFan);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("相機管理面板：室外扇形（模型比例）繪製失敗，略過", ex);
                }
            }

            Canvas group = new Canvas { IsHitTestVisible = true };
            double inv = SafeInverseScale();
            ScaleTransform inverseScale = new ScaleTransform(inv, inv, 0, 0);
            group.RenderTransform = inverseScale;
            Canvas.SetLeft(group, anchor.X);
            Canvas.SetTop(group, anchor.Y);

            WpfPoint origin = new WpfPoint(0, 0); // 群組本地座標系原點＝anchor

            // 視野扇形（局部座標，中心在原點）
            WpfPath fan = BuildFanPath(origin, planarAngleRad + leftEdgeRad, planarAngleRad + rightEdgeRad, FanRadius);
            fan.IsHitTestVisible = false;
            group.Children.Add(fan);

            // 中央視線：沿相機正前方到扇形半徑。
            WpfPoint shaftEnd = new WpfPoint(Math.Cos(planarAngleRad) * FanRadius, Math.Sin(planarAngleRad) * FanRadius);
            WpfLine shaft = new WpfLine { X1 = 0, Y1 = 0, X2 = shaftEnd.X, Y2 = shaftEnd.Y, StrokeThickness = 2, IsHitTestVisible = false };
            group.Children.Add(shaft);

            // 眼睛楔形（取代舊的朝前三角形箭頭）：頂點在眼睛、沿同樣兩條邊界張開，長度＝ArrowLen。
            WpfPath head = BuildFanPath(origin, planarAngleRad + leftEdgeRad, planarAngleRad + rightEdgeRad, ArrowLen);
            head.IsHitTestVisible = false;
            group.Children.Add(head);

            WpfEllipse dot = new WpfEllipse
            {
                Width = 11, Height = 11,
                Stroke = CameraManagerTheme.WhiteText, StrokeThickness = 1.5,
                Cursor = Cursors.Hand, Tag = row
            };
            // 左鍵＝開啟動作、右鍵＝選單，分開掛在對應的路由事件上，避免 MouseDown（不分左右鍵）
            // 同時觸發兩邊（2026-09-24 v5 新增右鍵選單時一併修正）。
            dot.MouseLeftButtonDown += (s, e) => SafeUi(() => MarkerActivated?.Invoke(row), "MarkerActivated");
            dot.MouseRightButtonDown += (s, e) => { e.Handled = true; SafeUi(() => MarkerContextMenuRequested?.Invoke(row, dot), "MarkerContextMenu"); };
            Canvas.SetLeft(dot, -5.5);
            Canvas.SetTop(dot, -5.5);
            group.Children.Add(dot);

            ApplyMarkerColors(fan, shaft, head, isSelected, dim);
            dot.Fill = dim ? CameraManagerTheme.SecondaryText : (isSelected ? CameraManagerTheme.Accent : CameraManagerTheme.SecondaryText);
            if (dim) dot.Opacity = 0.45;
            group.Opacity = dim ? 0.55 : 1.0;

            TextBlock label = new TextBlock
            {
                Text = dim ? row.Name + "（" + row.LevelName + "）" : row.Name,
                FontSize = 11, Foreground = CameraManagerTheme.Text, FontFamily = CameraManagerTheme.UiFont,
                Background = new SolidColorBrush(WpfColor.FromArgb(190, 255, 255, 255)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, ArrowLen * 0.4 + 6);
            Canvas.SetTop(label, -18);
            group.Children.Add(label);

            _mapLayer.Children.Add(group);
            _markersByViewId[row.ViewId] = new MarkerVisual { Group = group, InverseScale = inverseScale, Dot = dot, Shaft = shaft, Head = head, Fan = fan };
        }

        /// <summary>依選取狀態把 accent（選取中）／次要色（一般）／淡化（其他樓層）套到扇形填色＋
        /// 邊線／方向桿／箭頭三角形。</summary>
        private static void ApplyMarkerColors(WpfPath fan, WpfLine shaft, WpfPath head, bool isSelected, bool dim)
        {
            WpfColor rgb = isSelected ? CameraManagerTheme.AccentColor : CameraManagerTheme.SecondaryTextColor;
            Brush solid = isSelected ? CameraManagerTheme.Accent : CameraManagerTheme.SecondaryText;

            fan.Fill = new SolidColorBrush(WpfColor.FromArgb(38, rgb.R, rgb.G, rgb.B)); // ~15% 透明度
            fan.Stroke = solid;
            fan.StrokeThickness = 1;

            if (shaft != null) shaft.Stroke = solid;
            // 眼睛楔形：同色、透明度 30%（alpha≈179），比扇形填滿深。
            if (head != null) { head.Fill = new SolidColorBrush(WpfColor.FromArgb(179, rgb.R, rgb.G, rgb.B)); head.Stroke = null; }
        }

        /// <summary>扇形（畫布角度，Y 向下）：從 startAngleRad 順時針（畫面上）掃到 endAngleRad（end &gt; start）。</summary>
        private static WpfPath BuildFanPath(WpfPoint center, double startAngleRad, double endAngleRad, double radius)
        {
            WpfPoint p1 = new WpfPoint(center.X + Math.Cos(startAngleRad) * radius, center.Y + Math.Sin(startAngleRad) * radius);
            WpfPoint p2 = new WpfPoint(center.X + Math.Cos(endAngleRad) * radius, center.Y + Math.Sin(endAngleRad) * radius);

            var figure = new System.Windows.Media.PathFigure { StartPoint = center, IsClosed = true };
            figure.Segments.Add(new System.Windows.Media.LineSegment(p1, true));
            figure.Segments.Add(new System.Windows.Media.ArcSegment
            {
                Point = p2,
                Size = new Size(radius, radius),
                SweepDirection = System.Windows.Media.SweepDirection.Clockwise,
                IsLargeArc = endAngleRad - startAngleRad > Math.PI
            });
            figure.Segments.Add(new System.Windows.Media.LineSegment(center, true));

            var geometry = new System.Windows.Media.PathGeometry();
            geometry.Figures.Add(figure);
            return new WpfPath { Data = geometry };
        }

        private void SetAlignmentOverlay(bool on)
        {
            _showAlignmentOverlay = on;
            if (_currentMap != null && _mapLayer.Background is ImageBrush ib && ib.ImageSource is BitmapSource bmp)
                DrawWithBaseMap(bmp, _currentMap, _rows, preserveView: true);
        }

        /// <summary>浮動工具列的小型圖示切換鈕（ToggleButton，不套任何 Style，避免 TargetType 不符）。</summary>
        private static System.Windows.Controls.Primitives.ToggleButton OverlayToggle(string glyph, string tooltip, bool isChecked)
        {
            return new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = OverlayGlyph(glyph), Width = 24, Height = 22, Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(0),
                Background = CameraManagerTheme.Surface, BorderBrush = CameraManagerTheme.BorderBrush, Foreground = CameraManagerTheme.Text,
                Cursor = Cursors.Hand, IsChecked = isChecked, ToolTip = tooltip
            };
        }

        private static TextBlock OverlayGlyph(string glyph) => new TextBlock
        {
            Text = glyph, FontSize = 12, FontFamily = new FontFamily("Segoe UI Symbol, Microsoft JhengHei UI"),
            Foreground = CameraManagerTheme.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };

        private static void SafeUi(Action action, string what)
        {
            try { action(); }
            catch (Exception ex) { Logger.Error("相機管理面板：位置圖操作失敗（" + what + "）", ex); }
        }

        /// <summary>除錯疊圖：紅虛線＝模型裁剪框（view.CropBox），藍虛線＝換算用的完整範圍（＝圖片外框）。
        /// 兩者都經由與相機標記相同的 project 函式換算，確保檢查的是同一套對位。</summary>
        private void DrawAlignmentOverlay(BaseMapSnapshot map, Func<XYZ, WpfPoint> project, double imgW, double imgH)
        {
            try
            {
                WpfRectangle extents = new WpfRectangle
                {
                    Width = imgW, Height = imgH,
                    Stroke = CameraManagerTheme.Accent, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(extents, 0);
                Canvas.SetTop(extents, 0);
                _mapLayer.Children.Add(extents);

                bool hasModelCrop = map.ModelCropMaxXFeet - map.ModelCropMinXFeet > 1e-9 && map.ModelCropMaxYFeet - map.ModelCropMinYFeet > 1e-9;
                if (!hasModelCrop) return;
                double sx = imgW / (map.CropMaxXFeet - map.CropMinXFeet);
                double sy = imgH / (map.CropMaxYFeet - map.CropMinYFeet);
                double left = (map.ModelCropMinXFeet - map.CropMinXFeet) * sx;
                double top = (map.CropMaxYFeet - map.ModelCropMaxYFeet) * sy;
                double w = (map.ModelCropMaxXFeet - map.ModelCropMinXFeet) * sx;
                double h = (map.ModelCropMaxYFeet - map.ModelCropMinYFeet) * sy;
                if (!IsFinite(left) || !IsFinite(top) || !(w > 0) || !(h > 0)) return;
                WpfRectangle crop = new WpfRectangle
                {
                    Width = w, Height = h,
                    Stroke = CameraManagerTheme.Error, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 2 },
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(crop, left);
                Canvas.SetTop(crop, top);
                _mapLayer.Children.Add(crop);
                Logger.Info($"相機管理面板：對位框 modelCropPx=({left:F1},{top:F1},{w:F1}x{h:F1}) image={imgW}x{imgH}");
            }
            catch (Exception ex)
            {
                Logger.Error("相機管理面板：畫對位框失敗", ex);
            }
        }

        public void HighlightSelected(ElementId viewId)
        {
            _selectedViewId = viewId;
            foreach (var kv in _markersByViewId)
            {
                bool selected = viewId != null && kv.Key == viewId;
                MarkerVisual v = kv.Value;
                bool dim = v.Group.Opacity < 0.99; // 淡化標記不參與選取變色（見 DrawOneCamera 的 dim 邏輯）
                v.Dot.Fill = dim ? CameraManagerTheme.SecondaryText : (selected ? CameraManagerTheme.Accent : CameraManagerTheme.SecondaryText);
                ApplyMarkerColors(v.Fan, v.Shaft, v.Head, selected && !dim, dim);
            }
        }

        // ══════════════════════════════════════════════════════════
        // 選取相機的剖面線（簡化版：只畫虛線，不含端點刻度／箭頭，見報告已知限制）
        // ══════════════════════════════════════════════════════════

        public void SetSectionLine(XYZ p1, XYZ p2)
        {
            _sectionP1 = p1;
            _sectionP2 = p2;
            RedrawSectionLineOverlay();
        }

        private void RedrawSectionLineOverlay()
        {
            if (_sectionLineVisual != null)
            {
                _mapLayer.Children.Remove(_sectionLineVisual);
                _sectionLineVisual = null;
            }
            if (_sectionP1 == null || _sectionP2 == null || _currentProject == null) return;

            WpfPoint a = _currentProject(_sectionP1);
            WpfPoint b = _currentProject(_sectionP2);
            if (!IsFinite(a) || !IsFinite(b)) return;
            _sectionLineVisual = new WpfLine
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = CameraManagerTheme.Accent, StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false
            };
            _mapLayer.Children.Add(_sectionLineVisual);
        }

        // ══════════════════════════════════════════════════════════
        // 縮放／平移
        // ══════════════════════════════════════════════════════════

        private void FitToView()
        {
            double vw = _viewport.ActualWidth;
            double vh = _viewport.ActualHeight;
            if (vw <= 0 || vh <= 0 || _contentWidth <= 0 || _contentHeight <= 0)
            {
                _fitScale = _scale = 1;
                _offset = new WpfPoint(0, 0);
                return;
            }

            _fitScale = Math.Min(vw / _contentWidth, vh / _contentHeight);
            if (_fitScale <= 1e-6 || double.IsNaN(_fitScale) || double.IsInfinity(_fitScale)) _fitScale = 1;
            _scale = _fitScale;
            double offsetX = (vw - _contentWidth * _scale) / 2.0;
            double offsetY = (vh - _contentHeight * _scale) / 2.0;
            _offset = new WpfPoint(IsFinite(offsetX) ? offsetX : 0, IsFinite(offsetY) ? offsetY : 0);
            ApplyTransform();
            UpdateAllMarkerInverseScale();
        }

        private void ResetToFit()
        {
            _userAdjustedView = false;
            FitToView();
        }

        private void ApplyTransform()
        {
            // 2026-09-24 第二輪崩潰調查：NaN／Infinity 的 RenderTransform 會在渲染階段才失敗（堆疊
            // 不含 RevitMCP），這裡一律擋掉。
            if (!IsFinite(_scale) || _scale <= 1e-6) _scale = 1;
            if (!IsFinite(_offset)) _offset = new WpfPoint(0, 0);
            _mapTransform.Matrix = new Matrix(_scale, 0, 0, _scale, _offset.X, _offset.Y);
        }

        private double SafeInverseScale()
        {
            double inv = 1.0 / _scale;
            return IsFinite(inv) && inv > 0 ? inv : 1.0;
        }

        private void UpdateAllMarkerInverseScale()
        {
            double inv = SafeInverseScale();
            foreach (MarkerVisual v in _markersByViewId.Values)
                v.InverseScale.ScaleX = v.InverseScale.ScaleY = inv;
        }

        private void Viewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (_contentWidth <= 0 || _contentHeight <= 0) return;

            WpfPoint screenPos = e.GetPosition(_viewport);
            double factor = e.Delta > 0 ? WheelZoomStep : 1.0 / WheelZoomStep;
            double minScale = _fitScale * MinScaleFactor;
            double maxScale = _fitScale * MaxScaleFactor;
            double newScale = Math.Max(minScale, Math.Min(maxScale, _scale * factor));
            if (Math.Abs(newScale - _scale) < 1e-9) return;

            double contentX = (screenPos.X - _offset.X) / _scale;
            double contentY = (screenPos.Y - _offset.Y) / _scale;

            _scale = newScale;
            _offset = new WpfPoint(screenPos.X - contentX * _scale, screenPos.Y - contentY * _scale);
            _userAdjustedView = true;
            ApplyTransform();
            UpdateAllMarkerInverseScale();
        }

        private void TryStartPan(MouseButtonEventArgs e)
        {
            if (e.OriginalSource is WpfEllipse ellipse && ellipse.Tag is CameraRow) return; // 交給標記自己的選取邏輯

            if (e.ClickCount >= 2)
            {
                ResetToFit();
                e.Handled = true;
                return;
            }

            _panStartScreen = e.GetPosition(_viewport);
            _panStartOffset = _offset;
            _potentialPan = true;
            _panning = false;
            _viewport.CaptureMouse();
        }

        private void Viewport_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_potentialPan) return;
            WpfPoint cur = e.GetPosition(_viewport);
            double dx = cur.X - _panStartScreen.X;
            double dy = cur.Y - _panStartScreen.Y;

            if (!_panning)
            {
                if (Math.Abs(dx) < PanThresholdPx && Math.Abs(dy) < PanThresholdPx) return;
                _panning = true;
                _userAdjustedView = true;
            }

            _offset = new WpfPoint(_panStartOffset.X + dx, _panStartOffset.Y + dy);
            ApplyTransform();
        }

        private void Viewport_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_potentialPan) return;
            _potentialPan = false;
            _panning = false;
            _viewport.ReleaseMouseCapture();
        }
    }
}
