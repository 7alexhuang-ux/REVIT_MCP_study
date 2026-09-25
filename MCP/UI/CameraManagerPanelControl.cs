using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitMCP.Configuration;
using RevitMCP.Core;

using WpfGrid = System.Windows.Controls.Grid;
// Autodesk.Revit.UI 也有 TextBox／ComboBox（Ribbon 控制項），這裡明確 alias WPF 版本避免 CS0104。
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
// 2026-09-24 新增右鍵選單時發現：Revit 2025+ SDK 的 Autodesk.Revit.UI 也有 ContextMenu／
// MenuItem（R24 SDK 沒有這兩個型別，只在 R25/R26 build 才會 CS0104）——同一套理由，明確
// alias WPF 版本，三個 Revit 版本都能過。
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace RevitMCP.UI
{
    /// <summary>
    /// 相機管理停靠面板的根元件。版面沿革：
    ///   v1：修「整個面板背景黑掉、文字看不到」的黑底問題（顯式設定 Background/Surface）。
    ///   v2：分頁化（相機／位置圖／細節／工具四分頁），「不要出現滾輪，功能太多就用分頁」。
    ///   v3：拿掉分頁——面板改成單頁、上下捲動也不要。
    ///   v4（2026-09-24 第三輪實測回報）：「選取中相機」卡片整個拿掉（DataGrid 單擊即編輯＋
    ///   新增「↗」欄取代雙擊跳轉，卡片欄位移進 DataGrid 選用欄位）。
    ///   v5（同日，第四輪）：拿掉「↗」欄；名稱欄改成唯讀，單擊直接觸發「開啟動作」（預設
    ///   一鍵 4 視圖，跟動作列按鈕同一條路徑）；地圖標記左鍵單擊同樣行為；DataGrid 列與地圖
    ///   標記都新增右鍵選單（一鍵 4 視圖／僅打開透視圖／重新命名…／複製到其他樓層…／重設
    ///   剖面到視線），重新命名從「就地編輯」改成右鍵選單裡的小型主題化輸入 Popup（見
    ///   ShowRenamePopup）。版面固定：
    ///     標題列（搜尋、重新整理、從透視圖同步、匯出 Excel、開啟預覽視窗）
    ///     → 相機 DataGrid（點名稱開 4 視圖、點數字直接修改、右鍵更多；右鍵標題可勾選選用欄位）
    ///     → GridSplitter（可拖曳，位置存在 <see cref="CameraManagerSettings.CameraTabListFraction"/>）
    ///     → 迷你 key plan（底圖 combo 疊在右上角，支援滾輪縮放／拖曳平移／符合畫面）
    ///     → 動作列（一鍵 4 視圖＋等角範圍 combo＋重設剖面位置、並排、還原、複製到其他樓層）
    ///     → 人視高 preset 列
    ///     → 狀態列。
    ///   只有 DataGrid 本身可以在自己範圍內捲動；面板太矮時先縮地圖（最小 120px），再縮
    ///   清單區（最小約 3 列，見 <see cref="_topRow"/> 的 MinHeight）。<see cref="PillTabControl"/>
    ///   這次沒有用到，保留檔案但不再參與面板組裝。
    ///
    /// 這個類別本身不快取 Document —— 每次動作都在 ExternalEvent 內重新拿
    /// uiApp.ActiveUIDocument.Document，文件切換後行為才會正確。
    /// </summary>
    internal class CameraManagerPanelControl : UserControl
    {
        private readonly CameraListSection _listSection = new CameraListSection();
        private readonly PresetsSection _presetsSection = new PresetsSection();
        private readonly KeyPlanSection _keyPlanSection = new KeyPlanSection();
        private readonly MultiViewSection _multiView = new MultiViewSection();

        private readonly WpfTextBox _searchBox;
        private readonly TextBlock _statusText;
        private Button _oneClickFourViewsButton;

        private readonly WpfGrid _rootGrid = new WpfGrid();
        private RowDefinition _topRow, _mapRow;

        private List<CameraRow> _rows = new List<CameraRow>();
        private List<LevelOption> _levels = new List<LevelOption>();
        private bool _hasCroppedPlan;

        /// <summary>避免「面板改值 → Revit 觸發 DocumentChanged → 面板又刷新一次」的迴圈；
        /// 我們自己發起的交易不是問題本身（不會無限迴圈），但這裡仍主動抑制，減少一次浪費的重讀。</summary>
        private bool _suppressLiveSync;

        // 4.9 A 並排檢視狀態
        private List<ElementId> _sideBySideOpenedViewIds = new List<ElementId>();
        private ElementId _sideBySidePreviousActiveViewId;
        private bool _sideBySideActive;

        /// <summary>2026-09-24 崩潰修復：底圖匯出（ExportImage）重入防護——同一時間只允許一個
        /// 匯出在跑，避免選取變化很快（例如程式化的「預設選取」＋「自動切換底圖」連續觸發）
        /// 疊出多個並行的 doc.ExportImage() 呼叫。見 RefreshBaseMap／OnBaseMapReady。</summary>
        private bool _baseMapRefreshInFlight;

        /// <summary>2026-09-24 崩潰修復：追蹤目前開著的「相機預覽視窗」，讓 Application.cs 能在
        /// 文件關閉（DocumentClosing）／Revit 關閉（OnShutdown）時強制關閉並 Dispose 底下的
        /// PreviewControl——PreviewControl 包著 AdWindows/SxSActivationContext 這類原生資源，
        /// 沒有明確 Dispose、留給 GC 在 finalizer 執行緒收尾就是本次崩潰調查發現的
        /// SEHException 樣態（Application.cs 頂端 OnStartup 訂閱處有完整說明）。</summary>
        private readonly List<CameraPreviewWindow> _openPreviewWindows = new List<CameraPreviewWindow>();

        // ── 2026-09-24 第二輪崩潰調查：面板改成「懶載入／預設安全」 ──
        /// <summary>中性狀態提示文字：開檔、切換視圖、面板 Loaded 時面板不讀、不匯出、不畫任何東西，
        /// 一律停在這個狀態，直到使用者按「重新整理」（或點清單／地圖區）。</summary>
        internal const string NeutralMessage = "按『重新整理』載入相機";

        /// <summary>使用者是否已經在「目前這份文件」上明確按過重新整理。false 時 live sync 完全
        /// 不動作（Application.cs 的 Idling 會先問這個旗標）；文件切換／關閉會重設回 false。</summary>
        private bool _armed;
        /// <summary>目前面板資料來自哪一份文件（<see cref="GetDocumentKey"/>）；null＝中性狀態。</summary>
        private string _loadedDocKey;
        /// <summary>每次 ResetToNeutral 遞增；非同步回呼帶著發出時的代號，回來時不一致就丟棄，
        /// 避免舊文件的底圖／清單在文件切換後才畫到面板上。</summary>
        private int _generation;
        /// <summary>底圖匯出進行中又有新的底圖要求（例如使用者點了另一層的相機）時，記下最後一個，
        /// 目前這次結束後接著匯出——不再像過去那樣直接丟掉。</summary>
        private ElementId _pendingBaseMapPlanId;
        /// <summary>目前底圖實際顯示的平面（live sync 只更新清單時，底圖 combo 維持這一張）。</summary>
        private ElementId _currentBaseMapPlanId;

        /// <summary>給 Application.cs 的 Idling 判斷 live sync 是否已啟用。</summary>
        public bool IsLiveSyncArmed => _armed;
        /// <summary>給 Application.cs 判斷作用中文件是否仍是面板資料的來源文件。</summary>
        public string LoadedDocumentKey => _loadedDocKey;

        /// <summary>文件識別字串（標題＋路徑）。只在 API context 內呼叫（Idling／ExternalEvent／DocumentClosing）。</summary>
        public static string GetDocumentKey(Document doc)
        {
            if (doc == null) return null;
            try { return (doc.Title ?? "") + "|" + (doc.PathName ?? ""); }
            catch { return null; }
        }

        public CameraManagerPanelControl()
        {
            // 2026-09-24 第四輪版面（精簡、位置圖佔下半）：
            //   0 ① 工具列：搜尋｜重新整理｜從透視圖同步｜⋯（匯出 Excel、開啟預覽視窗）——單列
            //   1 ② 相機清單：預設約 1/3 高（CameraListFractionV3，可拖曳分割線、存檔）
            //   2 GridSplitter
            //   3 ③ 位置圖：吃掉剩下所有高度；控制項浮在地圖右上角（KeyPlanSection）
            //   4 ④ 視圖操作：一鍵 4 視圖 ▾（分割按鈕）｜並排｜還原｜⋯ 更多——單列
            //   5 ⑥ 狀態列
            //   ⑤ 人視高預設值收進「⋯ 更多 → ⑤ 人視高預設值…」小 Popup。
            // 所有選單／Popup 只建立一次、重複使用（不每次點擊 new，避免 HWND 累積），關閉即釋放 HWND。
            _rootGrid.Background = CameraManagerTheme.Background;
            double listFraction = ClampFraction(ConfigManager.Instance.Settings.CameraManager.CameraListFractionV3);
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });            // 0 ① 工具列
            _topRow = new RowDefinition { Height = new GridLength(listFraction, GridUnitType.Star), MinHeight = 110 };
            _rootGrid.RowDefinitions.Add(_topRow);                                                     // 1 ② 清單
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 2 分割線
            _mapRow = new RowDefinition { Height = new GridLength(1 - listFraction, GridUnitType.Star), MinHeight = 120 };
            _rootGrid.RowDefinitions.Add(_mapRow);                                                     // 3 ③ 位置圖
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 4 ④ 視圖操作
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 5 ⑥ 狀態列

            // ── 0：① 工具列（單列） ──
            Border header = new Border
            {
                Background = CameraManagerTheme.PaneTitleBackground,
                BorderBrush = CameraManagerTheme.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(CameraManagerTheme.Spacing, 4, CameraManagerTheme.Spacing, 6)
            };
            DockPanel headerRow = new DockPanel { LastChildFill = true };

            _toolbarMoreButton = CameraManagerTheme.IconButton("⋯", "① 更多：匯出 Excel、開啟預覽視窗");
            _toolbarMoreButton.Margin = new Thickness(4, 0, 0, 0);
            _toolbarMoreButton.Click += (s, e) => SafeUiAction(() => OpenMenu(EnsureToolbarMenu(), _toolbarMoreButton), "ToolbarMore");
            DockPanel.SetDock(_toolbarMoreButton, Dock.Right);
            headerRow.Children.Add(_toolbarMoreButton);

            Button syncButton = CameraManagerTheme.IconButton("從透視圖同步",
                "① 從透視圖同步：如果剛才在透視圖內滑鼠導覽後清單沒有自動更新，按這個強制重新整理");
            syncButton.Margin = new Thickness(4, 0, 0, 0);
            syncButton.Click += (s, e) => SafeUiAction(RefreshAll, "Sync");
            DockPanel.SetDock(syncButton, Dock.Right);
            headerRow.Children.Add(syncButton);

            Button refreshButton = CameraManagerTheme.IconButton("重新整理", "① 重新整理：重新讀取目前文件的相機清單");
            refreshButton.Margin = new Thickness(4, 0, 0, 0);
            refreshButton.Click += (s, e) => SafeUiAction(RefreshAll, "Refresh");
            DockPanel.SetDock(refreshButton, Dock.Right);
            headerRow.Children.Add(refreshButton);

            _searchBox = new WpfTextBox { MinWidth = 70, ToolTip = "① 搜尋相機：依名稱篩選相機清單" };
            CameraManagerTheme.StyleTextBox(_searchBox);
            ApplyPlaceholder(_searchBox, "搜尋相機");
            _searchBox.TextChanged += (s, e) => SafeUiAction(() =>
            {
                string text = _searchBox.Text == "搜尋相機" ? "" : _searchBox.Text;
                _listSection.SetSearchFilter(text);
            }, "Search");
            headerRow.Children.Add(_searchBox);

            StackPanel headerStack = new StackPanel();
            headerStack.Children.Add(CameraManagerTheme.AreaTitle("① 工具列"));
            headerStack.Children.Add(headerRow);
            header.Child = headerStack;
            WpfGrid.SetRow(header, 0);
            _rootGrid.Children.Add(header);

            // ── 1：② 相機清單 ──
            _listSection.Height = double.NaN;
            Border gridCard = CameraManagerTheme.CardWrap(_listSection, new Thickness(0, 0, 0, 2));
            gridCard.Padding = new Thickness(0); // DataGrid 自己有 CellStyle 內距，卡片不用再加一層
            WpfGrid listArea = WrapWithAreaTitle("② 相機清單", gridCard, new Thickness(0, 4, 0, 0));
            WpfGrid.SetRow(listArea, 1);
            _rootGrid.Children.Add(listArea);

            // ── 2：GridSplitter（拖曳結束後把新比例存進 camera 設定） ──
            GridSplitter splitter = new GridSplitter
            {
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Background = CameraManagerTheme.BorderBrush,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = GridResizeDirection.Rows,
                Cursor = Cursors.SizeNS,
                ToolTip = "拖曳調整 ② 相機清單與 ③ 位置圖的高度比例（會記住）"
            };
            splitter.DragCompleted += (s, e) => OnSplitterDragCompleted();
            WpfGrid.SetRow(splitter, 2);
            _rootGrid.Children.Add(splitter);

            // ── 3：③ 位置圖（吃掉剩下所有高度） ──
            Border mapCard = CameraManagerTheme.CardWrap(_keyPlanSection, new Thickness(0, 2, 0, 4));
            mapCard.Padding = new Thickness(2);
            WpfGrid mapArea = WrapWithAreaTitle("③ 位置圖", mapCard, new Thickness(0, 2, 0, 0));
            WpfGrid.SetRow(mapArea, 3);
            _rootGrid.Children.Add(mapArea);

            // ── 4：④ 視圖操作（單列） ──
            DockPanel actionRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };

            StackPanel splitButton = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
            _oneClickFourViewsButton = CameraManagerTheme.PrimaryButton("一鍵 4 視圖");
            _oneClickFourViewsButton.ToolTip = "④ 一鍵 4 視圖：建立或更新 Key Plan 平面／剖面／等角並與透視圖並排（會先關閉其他已開啟的視圖）";
            _oneClickFourViewsButton.Click += (s, e) => SafeUiAction(DoOneClickFourViews, "OneClickFourViews");
            splitButton.Children.Add(_oneClickFourViewsButton);
            _oneClickDropButton = CameraManagerTheme.PrimaryButton("▾");
            _oneClickDropButton.Padding = new Thickness(6, 5, 6, 5);
            _oneClickDropButton.Margin = new Thickness(1, 0, 0, 0);
            _oneClickDropButton.ToolTip = "④ 一鍵 4 視圖選項：等角範圍、剖面方向、重設剖面位置、全部符合畫面";
            _oneClickDropButton.Click += (s, e) => SafeUiAction(() => OpenMenu(EnsureFourViewsMenu(), _oneClickFourViewsButton), "FourViewsDrop");
            splitButton.Children.Add(_oneClickDropButton);
            DockPanel.SetDock(splitButton, Dock.Left);
            actionRow.Children.Add(splitButton);

            FrameworkElement sideBySide = _multiView.SideBySideBar;
            DockPanel.SetDock(sideBySide, Dock.Left);
            actionRow.Children.Add(sideBySide);

            _moreButton = CameraManagerTheme.SecondaryButton("⋯ 更多");
            _moreButton.ToolTip = "④ 更多：複製到其他樓層、標記顏色、⑤ 人視高預設值、匯出 Excel、開啟預覽視窗";
            _moreButton.Margin = new Thickness(6, 0, 0, 0);
            _moreButton.Click += (s, e) => SafeUiAction(() => OpenMenu(EnsureMoreMenu(), _moreButton), "More");
            DockPanel.SetDock(_moreButton, Dock.Left);
            actionRow.Children.Add(_moreButton);

            StackPanel actionStack = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            actionStack.Children.Add(CameraManagerTheme.AreaTitle("④ 視圖操作"));
            actionStack.Children.Add(actionRow);
            WpfGrid.SetRow(actionStack, 4);
            _rootGrid.Children.Add(actionStack);

            // Popup（不佔版面、IsOpen=false 不渲染）一律掛在 _rootGrid，確保有視覺樹可依附。
            _rootGrid.Children.Add(_presetsSection.HeightPopup);
            _rootGrid.Children.Add(_presetsSection.CopyPopup);

            // ── 5：⑥ 狀態列 ──
            Border statusBar = new Border
            {
                Background = CameraManagerTheme.StatusBarBackground,
                BorderBrush = CameraManagerTheme.BorderBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(CameraManagerTheme.Spacing, 6, CameraManagerTheme.Spacing, 6)
            };
            _statusText = new TextBlock
            {
                Foreground = CameraManagerTheme.SecondaryText, FontFamily = CameraManagerTheme.UiFont,
                FontSize = CameraManagerTheme.SecondaryFontSize, TextWrapping = TextWrapping.Wrap
            };
            DockPanel statusDock = new DockPanel();
            TextBlock statusLabel = new TextBlock
            {
                Text = "⑥", FontWeight = FontWeights.SemiBold, FontSize = CameraManagerTheme.AreaTitleFontSize,
                Foreground = CameraManagerTheme.SecondaryText, FontFamily = CameraManagerTheme.UiFont,
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "⑥ 狀態列：最近一次動作的結果訊息"
            };
            DockPanel.SetDock(statusLabel, Dock.Left);
            statusDock.Children.Add(statusLabel);
            statusDock.Children.Add(_statusText);
            statusBar.Child = statusDock;
            WpfGrid.SetRow(statusBar, 5);
            _rootGrid.Children.Add(statusBar);

            Content = _rootGrid;
            FontFamily = CameraManagerTheme.UiFont;
            FontSize = CameraManagerTheme.BodyFontSize;
            Background = CameraManagerTheme.Background;

            WireEvents();

            // 2026-09-24 崩潰修復：Dispatcher.UnhandledException 是同一條 UI 執行緒上「所有」
            // WPF 內容共用的事件（不是這個 control 專屬）——Revit 自己的 UI、其他外掛的面板
            // 拋出的例外也會經過這裡。所以只在例外堆疊追蹤明確含有「RevitMCP.」（本外掛的
            // 命名空間）時才 Logger.Error 記錄＋e.Handled=true 吞掉，把原本會讓 Revit 帶著
            // 0xe0434352 直接死掉的例外，變成一筆可追查的 log；不是我們寫的例外一律不處理，
            // 維持原本行為（該怎麼樣就怎麼樣），不要冒然吞掉可能代表 Revit 核心或別的外掛已經
            // 壞掉的例外。這是「每個 WPF 事件處理器都個別包 try/catch」在實務上做不到齊全時
            // （面板底下有 5 個子元件檔、上百個 lambda）的最後一道防線，跟 RunOnUiThread／
            // ExternalEventManager 呼叫端的 try/catch 是互補、不是取代關係。
            Dispatcher.UnhandledException += OnDispatcherUnhandledException;

            // 2026-09-24 第二輪崩潰調查：Loaded 不再自動刷新。面板顯示中開檔時 Loaded 會在文件開啟
            // 流程中觸發（log 20:57:16），過去這裡直接 RefreshAll()→排 ExternalEvent→讀快照→
            // 匯出底圖→預設選取→自動切換底圖→讀剖面線，一連串全自動。現在 Loaded 只記錄＋延長
            // 崩潰診斷觀察窗，面板停在中性狀態等使用者按「重新整理」。
            Loaded += (s, e) =>
            {
                Logger.Info("相機管理面板：Loaded（不自動載入，等待使用者按『重新整理』）。");
                CameraManagerDiagnostics.ExtendWatch(60, "相機管理面板 Loaded");
            };

            // 中性初始狀態（純 WPF，不碰 Revit）。
            _keyPlanSection.ResetToNeutral(NeutralMessage);
            _statusText.Text = NeutralMessage;

            // 尚未載入時，點清單區或地圖區＝等同按「重新整理」（使用者明確操作）。
            _listSection.PreviewMouseLeftButtonDown += (s, e) => { if (!_armed) RefreshAll(); };
            _keyPlanSection.PreviewMouseLeftButtonDown += (s, e) => { if (!_armed) RefreshAll(); };

            CameraManagerDiagnostics.AttachLayoutStormDetector(this, () =>
                $"pane={ActualWidth:F0}x{ActualHeight:F0} rows={_rows?.Count ?? 0} armed={_armed}");
        }

        /// <summary>把一個區塊（卡片）包成「區名小標＋內容」兩列 Grid（內容列 Star，吸收高度），回傳外層 Grid。</summary>
        private static WpfGrid WrapWithAreaTitle(string title, FrameworkElement content, Thickness titleMargin)
        {
            WpfGrid g = new WpfGrid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            TextBlock t = CameraManagerTheme.AreaTitle(title);
            t.Margin = new Thickness(2 + titleMargin.Left, titleMargin.Top, 0, 3);
            WpfGrid.SetRow(t, 0);
            g.Children.Add(t);
            WpfGrid.SetRow(content, 1);
            g.Children.Add(content);
            return g;
        }

        // ══════════════════════════════════════════════════════════
        // 2026-09-24 第四輪：主題化選單（① ⋯、④ 一鍵 4 視圖 ▾、④ ⋯ 更多、相機右鍵）
        // 每個選單只建立一次並重複使用；ContextMenu 關閉時 WPF 會銷毀其 Popup HWND，
        // 不會因反覆點擊累積。不套任何 Style（避免 TargetType 與元素型別不符的崩潰）。
        // ══════════════════════════════════════════════════════════

        private Button _toolbarMoreButton, _oneClickDropButton, _moreButton;
        private WpfContextMenu _toolbarMenu, _fourViewsMenu, _moreMenu, _cameraMenu;
        private WpfMenuItem _isoFullItem, _isoLevelItem;
        private CameraRow _cameraMenuRow;
        private UIElement _cameraMenuTarget;

        private static WpfContextMenu NewThemedMenu() => new WpfContextMenu
        {
            FontFamily = CameraManagerTheme.UiFont,
            FontSize = CameraManagerTheme.BodyFontSize,
            Background = CameraManagerTheme.Surface,
            Foreground = CameraManagerTheme.Text,
            BorderBrush = CameraManagerTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            Placement = PlacementMode.Bottom
        };

        private WpfMenuItem NewMenuItem(string header, string tooltip, Action onClick, string logName)
        {
            WpfMenuItem item = new WpfMenuItem
            {
                Header = header,
                Foreground = CameraManagerTheme.Text,
                FontFamily = CameraManagerTheme.UiFont,
                ToolTip = tooltip
            };
            if (onClick != null) item.Click += (s, e) => SafeUiAction(onClick, logName);
            return item;
        }

        private static void OpenMenu(WpfContextMenu menu, UIElement target)
        {
            if (menu == null || target == null) return;
            if (menu.IsOpen) { menu.IsOpen = false; return; }
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>從選單項目開啟另一個 StaysOpen=false 的 Popup：延到選單關閉、輸入處理完之後，
        /// 否則同一次滑鼠點擊會立刻把剛開的 Popup 關掉。</summary>
        private void OpenPopupAfterMenu(Action open, string logName)
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => SafeUiAction(open, logName)));
            }
            catch (Exception ex)
            {
                Logger.Error("相機管理面板：延後開啟 Popup 失敗（" + logName + "）", ex);
            }
        }

        private static void SafeUiAction(Action action, string what)
        {
            try { action?.Invoke(); }
            catch (Exception ex)
            {
                Logger.Error("相機管理面板：UI 操作失敗（" + what + "）", ex);
                CameraManagerDiagnostics.RecordCaught("ui:" + what, ex);
            }
        }

        private WpfContextMenu EnsureToolbarMenu()
        {
            if (_toolbarMenu != null) return _toolbarMenu;
            _toolbarMenu = NewThemedMenu();
            _toolbarMenu.Items.Add(NewMenuItem("匯出 Excel", "① 匯出 Excel：把目前相機清單匯出成 Excel 檔", OnExportExcelRequested, "ExportExcel"));
            _toolbarMenu.Items.Add(NewMenuItem("開啟預覽視窗", "① 開啟預覽視窗：用獨立小視窗顯示選取相機的透視圖＋所在樓層平面兩格預覽", OpenPreviewWindow, "OpenPreview"));
            return _toolbarMenu;
        }

        private WpfContextMenu EnsureFourViewsMenu()
        {
            if (_fourViewsMenu != null) return _fourViewsMenu;
            _fourViewsMenu = NewThemedMenu();
            const string isoTip = "④ 等角範圍：整層（見上層樑）＝切在上層樓板底 −10mm；只到當層（不含上層）＝切在上層樓板底與上層樑底較低者 −10mm";
            _isoFullItem = NewMenuItem("等角：整層（見上層樑）", isoTip, () => SetIsoExtentMode(CameraIsoExtentMode.FullWithUpperBeams), "IsoFull");
            _isoLevelItem = NewMenuItem("等角：只到當層（不含上層）", isoTip, () => SetIsoExtentMode(CameraIsoExtentMode.CurrentLevelTopOnly), "IsoLevel");
            _isoFullItem.IsCheckable = false; _isoLevelItem.IsCheckable = false; // 自行維護單選勾號
            _fourViewsMenu.Items.Add(_isoFullItem);
            _fourViewsMenu.Items.Add(_isoLevelItem);
            _fourViewsMenu.Items.Add(new Separator());
            // 2026-09-25（使用者拍板）：剖面方向單選（存在相機設定，下次一鍵 4 視圖／重設剖面位置套用）
            const string secTip = "④ 剖面方向：對齊房間長邊＝剖面沿眼睛所在房間的長軸、通過眼睛（預設）；沿視線＝剖面包含水平視線（側視）";
            _secRoomAxisItem = NewMenuItem("剖面方向：對齊房間長邊（預設）", secTip, () => SetSectionOrientationMode(CameraSectionOrientationMode.AlongRoomLongAxis), "SectionRoomAxis");
            _secSightItem = NewMenuItem("剖面方向：沿視線", secTip, () => SetSectionOrientationMode(CameraSectionOrientationMode.AlongSightLine), "SectionSightLine");
            _secRoomAxisItem.IsCheckable = false; _secSightItem.IsCheckable = false;
            _fourViewsMenu.Items.Add(_secRoomAxisItem);
            _fourViewsMenu.Items.Add(_secSightItem);
            _fourViewsMenu.Items.Add(new Separator());
            // 2026-09-25：室外相機（眼睛不在任何房間）的 Key Plan 範圍半徑，實際＝min(遠裁剪, 本值)
            _fourViewsMenu.Items.Add(NewMenuItem("室外範圍半徑… (m)",
                "④ 室外範圍半徑：眼睛不在任何房間的相機，Key Plan 平面／剖面／等角範圍＝視野扇形，半徑＝min(遠裁剪, 本值)；預設 60 m",
                () => OpenPopupAfterMenu(() => ShowOutdoorRadiusPopup(_oneClickDropButton), "OutdoorRadiusPopup"), "OutdoorRadius"));
            _fourViewsMenu.Items.Add(new Separator());
            _fourViewsMenu.Items.Add(NewMenuItem("重設剖面位置",
                "④ 重設剖面位置：依目前的剖面方向設定，把剖面 CropBox 強制覆蓋回自動算出的位置（會蓋掉你在 Revit 內手動調整過的剖面）",
                DoResetSectionToSightLine, "ResetSection"));
            _fourViewsMenu.Items.Add(NewMenuItem("全部符合畫面",
                "④ 全部符合畫面：把選取相機已開啟的 4 張視圖（透視、Key Plan 平面／剖面／等角）各自縮放到符合視窗",
                DoZoomAllFourViews, "ZoomAll"));
            _fourViewsMenu.Opened += (s, e) => SafeUiAction(UpdateIsoMenuChecks, "IsoMenuOpened");
            UpdateIsoMenuChecks();
            return _fourViewsMenu;
        }

        private void UpdateIsoMenuChecks()
        {
            bool level = ConfigManager.Instance.Settings.CameraManager.IsoExtentMode == CameraIsoExtentMode.CurrentLevelTopOnly;
            if (_isoFullItem != null) _isoFullItem.IsChecked = !level;
            if (_isoLevelItem != null) _isoLevelItem.IsChecked = level;
            bool sight = ConfigManager.Instance.Settings.CameraManager.SectionOrientationMode == CameraSectionOrientationMode.AlongSightLine;
            if (_secRoomAxisItem != null) _secRoomAxisItem.IsChecked = !sight;
            if (_secSightItem != null) _secSightItem.IsChecked = sight;
        }

        private WpfMenuItem _secRoomAxisItem, _secSightItem;

        private void SetSectionOrientationMode(CameraSectionOrientationMode mode)
        {
            ConfigManager.Instance.Settings.CameraManager.SectionOrientationMode = mode;
            ConfigManager.Instance.SaveSettings();
            UpdateIsoMenuChecks();
            Logger.Info("相機管理面板：剖面方向 → " + mode);
            SetStatus(mode == CameraSectionOrientationMode.AlongSightLine
                ? "剖面方向：沿視線——下次一鍵 4 視圖／重設剖面位置套用。"
                : "剖面方向：對齊房間長邊——下次一鍵 4 視圖／重設剖面位置套用。");
        }

        private void SetIsoExtentMode(CameraIsoExtentMode mode)
        {
            ConfigManager.Instance.Settings.CameraManager.IsoExtentMode = mode;
            ConfigManager.Instance.SaveSettings();
            UpdateIsoMenuChecks();
            SetStatus(mode == CameraIsoExtentMode.CurrentLevelTopOnly
                ? "等角範圍：只到當層（不含上層）——下次一鍵 4 視圖套用。"
                : "等角範圍：整層（見上層樑）——下次一鍵 4 視圖套用。");
        }

        // ── ④ 標記顏色（⋯ 更多 → 標記顏色 ▸） ──
        private static readonly (string Name, string Hex)[] MarkerColorPresets =
        {
            ("藍", "#3B82C4"), ("紅", "#D9534F"), ("橘", "#E8A33D"), ("綠", "#4CAF7A"), ("紫", "#8E6BBF"), ("黑", "#1C2B3A")
        };
        private readonly List<WpfMenuItem> _markerColorItems = new List<WpfMenuItem>();

        private WpfContextMenu EnsureMoreMenu()
        {
            if (_moreMenu != null) return _moreMenu;
            _moreMenu = NewThemedMenu();

            _moreMenu.Items.Add(NewMenuItem("複製到其他樓層…", "④ 複製到其他樓層：把選取相機複製到勾選的樓層",
                () => OpenPopupAfterMenu(() => _presetsSection.OpenCopyToLevelsPopup(_moreButton), "CopyToLevelsPopup"), "CopyToLevels"));

            const string colorTip = "④ 標記顏色：改變 Key Plan 平面／剖面上相機標記線的顏色（也可在 物件型式 → 線 → 相機標記線 修改）";
            WpfMenuItem colorRoot = NewMenuItem("標記顏色", colorTip, null, "MarkerColor");
            foreach (var preset in MarkerColorPresets)
            {
                string hex = preset.Hex;
                System.Windows.Media.Color c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                WpfMenuItem item = NewMenuItem(preset.Name + "  " + hex, "④ 標記顏色：" + preset.Name + " " + hex, () => ApplyMarkerColorHex(hex), "MarkerColorPreset");
                item.Tag = hex;
                item.Icon = new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(2),
                    Background = new System.Windows.Media.SolidColorBrush(c),
                    BorderBrush = CameraManagerTheme.BorderBrush, BorderThickness = new Thickness(1)
                };
                _markerColorItems.Add(item);
                colorRoot.Items.Add(item);
            }
            colorRoot.Items.Add(new Separator());
            colorRoot.Items.Add(NewMenuItem("自訂…", "④ 標記顏色：自訂…（開啟顏色選擇對話框）", () =>
            {
                // System.Windows.Forms.ColorDialog：專案沒有參考 WinForms（改 csproj 會牽動全專案），
                // 用反射呼叫 Revit 行程內已載入的 System.Windows.Forms 組件，net48／net8 皆可。
                OpenPopupAfterMenu(() =>
                {
                    try
                    {
                        string picked = ShowWinFormsColorDialog();
                        if (picked != null) ApplyMarkerColorHex(picked);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("相機管理面板：自訂標記顏色失敗", ex);
                        SetStatus("自訂標記顏色失敗：" + ex.Message);
                    }
                }, "CustomColor");
            }, "MarkerColorCustom"));
            _moreMenu.Items.Add(colorRoot);

            // 2026-09-25：Key Plan 樣式 ▸ 淡化背景（預設）／原始（存在相機設定，下次一鍵 4 視圖套用）
            const string styleTip = "④ Key Plan 樣式：淡化背景＝模型半色調＋隱藏軸線/樓層/尺寸/標籤等雜項註解＋自動比例；原始＝還原（下次一鍵 4 視圖套用）";
            WpfMenuItem styleRoot = NewMenuItem("Key Plan 樣式", styleTip, null, "KeyPlanStyle");
            _keyPlanFadedItem = NewMenuItem("淡化背景（預設）", styleTip, () => SetKeyPlanFadedStyle(true), "KeyPlanStyleFaded");
            _keyPlanOriginalItem = NewMenuItem("原始", styleTip, () => SetKeyPlanFadedStyle(false), "KeyPlanStyleOriginal");
            styleRoot.Items.Add(_keyPlanFadedItem);
            styleRoot.Items.Add(_keyPlanOriginalItem);
            _moreMenu.Items.Add(styleRoot);

            _moreMenu.Items.Add(NewMenuItem("⑤ 人視高預設值…", "⑤ 人視高預設值：設定離樓板高度 (cm) 並套用到選取／全部相機",
                () => OpenPopupAfterMenu(() => _presetsSection.OpenHeightPopup(_moreButton), "HeightPopup"), "HeightPreset"));
            _moreMenu.Items.Add(new Separator());
            _moreMenu.Items.Add(NewMenuItem("匯出 Excel", "① 匯出 Excel：把目前相機清單匯出成 Excel 檔", OnExportExcelRequested, "ExportExcel"));
            _moreMenu.Items.Add(NewMenuItem("開啟預覽視窗", "① 開啟預覽視窗：用獨立小視窗顯示選取相機的透視圖＋所在樓層平面兩格預覽", OpenPreviewWindow, "OpenPreview"));

            _moreMenu.Opened += (s, e) => SafeUiAction(UpdateMarkerColorChecks, "MoreMenuOpened");
            UpdateMarkerColorChecks();
            return _moreMenu;
        }

        private void UpdateMarkerColorChecks()
        {
            string current = ConfigManager.Instance.Settings.CameraManager.MarkerColorHex ?? "#3B82C4";
            foreach (WpfMenuItem item in _markerColorItems)
                item.IsChecked = string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase);
            bool faded = ConfigManager.Instance.Settings.CameraManager.KeyPlanFadedStyle;
            if (_keyPlanFadedItem != null) _keyPlanFadedItem.IsChecked = faded;
            if (_keyPlanOriginalItem != null) _keyPlanOriginalItem.IsChecked = !faded;
        }

        private WpfMenuItem _keyPlanFadedItem, _keyPlanOriginalItem;

        private void SetKeyPlanFadedStyle(bool faded)
        {
            ConfigManager.Instance.Settings.CameraManager.KeyPlanFadedStyle = faded;
            ConfigManager.Instance.SaveSettings();
            UpdateMarkerColorChecks();
            Logger.Info("相機管理面板：Key Plan 樣式 → " + (faded ? "淡化背景" : "原始"));
            SetStatus(faded
                ? "Key Plan 樣式：淡化背景——下次一鍵 4 視圖套用。"
                : "Key Plan 樣式：原始——下次一鍵 4 視圖還原。");
        }

        /// <summary>以反射開啟 System.Windows.Forms.ColorDialog；使用者按確定回傳 #RRGGBB，取消或失敗回傳 null。</summary>
        private static string ShowWinFormsColorDialog()
        {
            System.Reflection.Assembly wf = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "System.Windows.Forms");
            if (wf == null)
            {
                try { wf = System.Reflection.Assembly.Load("System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"); }
                catch { wf = System.Reflection.Assembly.Load("System.Windows.Forms"); }
            }
            Type dlgType = wf.GetType("System.Windows.Forms.ColorDialog", throwOnError: true);
            object dlg = Activator.CreateInstance(dlgType);
            try
            {
                dlgType.GetProperty("FullOpen")?.SetValue(dlg, true);
                dlgType.GetProperty("AnyColor")?.SetValue(dlg, true);
                object result = dlgType.GetMethod("ShowDialog", Type.EmptyTypes).Invoke(dlg, null);
                if (result == null || result.ToString() != "OK") return null;
                object color = dlgType.GetProperty("Color").GetValue(dlg);
                Type ct = color.GetType();
                byte r = (byte)ct.GetProperty("R").GetValue(color);
                byte g = (byte)ct.GetProperty("G").GetValue(color);
                byte b = (byte)ct.GetProperty("B").GetValue(color);
                return $"#{r:X2}{g:X2}{b:X2}";
            }
            finally
            {
                (dlg as IDisposable)?.Dispose();
            }
        }

        private void ApplyMarkerColorHex(string hex)
        {
            if (!CameraKeyPlanService.TryParseHexColor(hex, out byte r, out byte g, out byte b)) return;
            ConfigManager.Instance.Settings.CameraManager.MarkerColorHex = hex;
            ConfigManager.Instance.SaveSettings();
            UpdateMarkerColorChecks();
            if (!_armed) { SetStatus("已記住標記顏色 " + hex + "（下次建立 Key Plan 時套用）。"); return; }
            SetStatus("變更相機標記顏色中...");
            EnqueueRevit(uiApp =>
            {
                string err;
                try { err = CameraKeyPlanService.ApplyMarkerColor(uiApp?.ActiveUIDocument?.Document, r, g, b); }
                catch (Exception ex) { err = "變更標記顏色失敗：" + ex.Message; }
                RunOnUiThread(() => SetStatus(err ?? ("已變更相機標記顏色為 " + hex + "。")));
            });
        }

        // ── ④ 全部符合畫面 ──
        private void DoZoomAllFourViews()
        {
            CameraRow row = GetPrimarySelectedRow();
            if (row == null) { SetStatus("請先在清單選取一個相機。"); return; }
            string uid = row.UniqueId;
            ElementId perspId = row.ViewId;
            EnqueueRevit(uiApp =>
            {
                string msg;
                try
                {
                    Document doc = uiApp?.ActiveUIDocument?.Document;
                    List<ElementId> ids = new List<ElementId> { perspId };
                    if (doc != null) ids.AddRange(CameraKeyPlanService.GetLinkedKeyPlanViewIds(doc, uid));
                    int n = CameraViewZoomService.ZoomAllNow(uiApp, ids);
                    msg = $"已將 {n} 張已開啟的視圖縮放到符合畫面。";
                }
                catch (Exception ex)
                {
                    Logger.Error("相機管理面板：全部符合畫面失敗", ex);
                    msg = "全部符合畫面失敗：" + ex.Message;
                }
                RunOnUiThread(() => SetStatus(msg));
            });
        }

        /// <summary>一鍵 4 視圖／並排用：關閉作用中文件裡「不是 keep 名單」的所有視圖視窗。
        /// 必須先把 keep 裡的某張設為作用視圖（Revit 不允許關閉作用中／最後一張視圖）。每張獨立 try/catch。</summary>
        private static int CloseOtherViews(UIApplication uiApp, ICollection<ElementId> keep)
        {
            int closed = 0;
            UIDocument uidoc = uiApp?.ActiveUIDocument;
            if (uidoc == null) return 0;
            IList<UIView> open;
            try { open = uidoc.GetOpenUIViews(); }
            catch (Exception ex) { Logger.Error("相機管理面板：GetOpenUIViews 失敗，略過關閉其他視圖", ex); return 0; }
            foreach (UIView uv in open.ToList())
            {
                try
                {
                    if (keep.Contains(uv.ViewId)) continue;
                    uv.Close();
                    closed++;
                }
                catch (Exception ex)
                {
                    Logger.Error($"相機管理面板：關閉視圖 {uv.ViewId?.GetIdValue()} 失敗（略過）", ex);
                }
            }
            Logger.Info($"相機管理面板：已關閉 {closed} 張其他視圖。");
            return closed;
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                Exception ex = e.Exception;
                string stack = ex?.StackTrace ?? "";
                bool fromOurCode = stack.IndexOf("RevitMCP.", StringComparison.Ordinal) >= 0;
                // 2026-09-24 第二輪：不論是不是本外掛的例外都同步寫進崩潰紀錄（含完整 ToString），
                // 下一次崩潰才看得到真正的例外型別與堆疊；是否 Handled 的規則不變。
                CameraManagerDiagnostics.RecordDispatcherException(ex, fromOurCode);
                if (!fromOurCode) return; // 不是本外掛拋的例外，不攔，維持原本（可能讓 Revit 崩潰的）行為。

                Logger.Error("相機管理面板：攔截到未處理的 WPF Dispatcher 例外（已阻止可能的 Revit 崩潰）", ex);
                e.Handled = true;
            }
            catch
            {
                // 這個 handler 本身絕對不能再拋例外，否則等於完全沒攔到。
            }
        }

        // ══════════════════════════════════════════════════════════
        // 事件接線
        // ══════════════════════════════════════════════════════════

        private void WireEvents()
        {
            _listSection.RefreshRequested += RefreshAll;
            _listSection.SelectionChanged += OnSelectionChanged;
            // 2026-09-24 二次改版：拿掉「↗」欄與名稱就地編輯——名稱欄改成單擊直接觸發開啟動作
            // （預設一鍵 4 視圖），重新命名移到右鍵選單（見 RowOpenActionRequested／
            // RowContextMenuRequested／ShowCameraContextMenu／ShowRenamePopup）。
            _listSection.RowOpenActionRequested += RunConfiguredOpenAction;
            _listSection.RowContextMenuRequested += ShowCameraContextMenu;
            _listSection.LevelEditCommitted += (row, levelId) => SafeUiAction(() => OnLevelEditCommitted(row, levelId), "LevelEditCommitted");
            _listSection.NumericFieldEditCommitted += OnNumericFieldEditCommitted;
            _listSection.PlaneFieldEditCommitted += OnPlaneFieldEditCommitted;
            _listSection.FarClipEditCommitted += (row, valueCm) => OnGridFieldEditCommitted(row, req => req.FarClipCm = valueCm);

            _presetsSection.ApplyHeightRequested += OnApplyHeightRequested;
            _presetsSection.CopyToLevelsRequested += OnCopyToLevelsRequested;

            _keyPlanSection.PlanSelected += plan => RefreshBaseMap(plan.ViewId);
            // 左鍵點地圖上的相機標記＝選取＋直接跑開啟動作（預設一鍵 4 視圖，規格 4.9 C）；
            // 右鍵點標記＝同一顆相機的右鍵選單（跟 DataGrid 列右鍵選單共用 ShowCameraContextMenu）。
            _keyPlanSection.MarkerActivated += RunConfiguredOpenAction;
            _keyPlanSection.MarkerContextMenuRequested += ShowCameraContextMenu;

            _multiView.SideBySideRequested += DoSideBySide;
            _multiView.RestoreRequested += DoRestore;
        }

        // ══════════════════════════════════════════════════════════
        // 4.9 C：點名稱／點地圖標記＝直接開啟動作；右鍵＝更多動作選單
        // ══════════════════════════════════════════════════════════

        /// <summary>2026-09-24 崩潰修復：跟底圖匯出（_baseMapRefreshInFlight）同一套道理——
        /// 「一鍵 4 視圖」會開交易＋建立/更新 3 張視圖＋切換作用中視圖，同一時間只允許一個在跑，
        /// 避免使用者連續快速點名稱／標記疊出多個並行的 CreateOrUpdateFourViews 呼叫。</summary>
        private bool _fourViewsInFlight;
        private bool _schemaSelfChecked;

        /// <summary>名稱欄單擊、地圖標記左鍵單擊、右鍵選單「一鍵 4 視圖」共用的進入點：先確保
        /// 這台相機在清單裡被選取（DataGrid 選取狀態＋地圖高亮都要同步），再跑一鍵 4 視圖
        /// （沿用目前的等角範圍設定，跟動作列按鈕完全相同的程式路徑，見 RunOneClickFourViews）。</summary>
        private void RunConfiguredOpenAction(CameraRow row)
        {
            if (row == null) return;
            Logger.Info($"相機管理面板：開啟動作觸發（viewId={row.ViewId?.GetIdValue()}），選取後執行一鍵 4 視圖。");
            _listSection.SelectByViewId(row.ViewId);
            RunOneClickFourViews(forceResetSection: false);
        }

        /// <summary>DataGrid 列右鍵、地圖標記右鍵共用：顯示「一鍵 4 視圖／僅打開透視圖／重新命名…／
        /// 複製到其他樓層…／重設剖面位置」的主題化選單。所有動作一律先選取這台相機，確保
        /// GetPrimarySelectedRow() 在選單項目稍後（非同步）被點擊時仍能拿到正確的目標。</summary>
        private void ShowCameraContextMenu(CameraRow row, UIElement placementTarget)
        {
            if (row == null || placementTarget == null) return;
            _listSection.SelectByViewId(row.ViewId);
            _cameraMenuRow = row;
            _cameraMenuTarget = placementTarget;

            if (_cameraMenu == null)
            {
                // 2026-09-24 第四輪：只建立一次、重複使用（過去每次右鍵都 new 一個 ContextMenu）。
                _cameraMenu = NewThemedMenu();
                _cameraMenu.Items.Add(NewMenuItem("一鍵 4 視圖", null, () => RunConfiguredOpenAction(_cameraMenuRow), "CtxFourViews"));
                _cameraMenu.Items.Add(NewMenuItem("僅打開透視圖", null, () =>
                {
                    CameraRow r = _cameraMenuRow; if (r == null) return;
                    _listSection.SelectByViewId(r.ViewId); JumpToView(r.ViewId);
                }, "CtxOpenOnly"));
                _cameraMenu.Items.Add(new Separator());
                _cameraMenu.Items.Add(NewMenuItem("重新命名…", null,
                    () => OpenPopupAfterMenu(() => ShowRenamePopup(_cameraMenuRow, _cameraMenuTarget), "RenamePopup"), "CtxRename"));
                _cameraMenu.Items.Add(NewMenuItem("複製到其他樓層…", null, () =>
                {
                    CameraRow r = _cameraMenuRow; if (r == null) return;
                    _listSection.SelectByViewId(r.ViewId);
                    UIElement t = _cameraMenuTarget;
                    OpenPopupAfterMenu(() => _presetsSection.OpenCopyToLevelsPopup(t), "CopyToLevelsPopup");
                }, "CtxCopy"));
                _cameraMenu.Items.Add(NewMenuItem("重設剖面位置", null, () =>
                {
                    CameraRow r = _cameraMenuRow; if (r == null) return;
                    _listSection.SelectByViewId(r.ViewId); DoResetSectionToSightLine();
                }, "CtxResetSection"));
                // 關閉後放掉對列／視覺元素的參照（清單重建後舊 DataGridRow 不再被選單抓住）。
                _cameraMenu.Closed += (s, e) => SafeUiAction(() => { _cameraMenu.PlacementTarget = null; }, "CtxClosed");
            }

            if (_cameraMenu.IsOpen) _cameraMenu.IsOpen = false;
            _cameraMenu.PlacementTarget = placementTarget;
            _cameraMenu.Placement = PlacementMode.MousePoint;
            _cameraMenu.IsOpen = true;
        }

        // ── 重新命名：小型主題化輸入 Popup（取代原本 DataGrid 名稱欄就地編輯） ──
        private Popup _renamePopup;
        private WpfTextBox _renameTextBox;
        private CameraRow _renameTargetRow;

        private void ShowRenamePopup(CameraRow row, UIElement placementTarget)
        {
            if (row == null) return;
            _renameTargetRow = row;
            EnsureRenamePopupBuilt();
            _renameTextBox.Text = row.Name;
            _renamePopup.PlacementTarget = placementTarget;
            _renamePopup.IsOpen = true;
            RunOnUiThread(() =>
            {
                _renameTextBox.Focus();
                _renameTextBox.SelectAll();
            });
        }

        private void EnsureRenamePopupBuilt()
        {
            if (_renamePopup != null) return;

            _renameTextBox = new WpfTextBox { Width = 180, Margin = new Thickness(0, 0, 0, CameraManagerTheme.Spacing) };
            CameraManagerTheme.StyleTextBox(_renameTextBox);

            Border card = new Border
            {
                Background = CameraManagerTheme.Surface,
                BorderBrush = CameraManagerTheme.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(CameraManagerTheme.CornerRadiusPx),
                Padding = new Thickness(CameraManagerTheme.Spacing),
                Effect = CameraManagerTheme.CardShadow()
            };
            StackPanel panel = new StackPanel();
            panel.Children.Add(CameraManagerTheme.SectionTitle("重新命名相機"));
            panel.Children.Add(_renameTextBox);

            StackPanel buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = CameraManagerTheme.SecondaryButton("取消");
            cancel.Margin = new Thickness(0, 0, 6, 0);
            Button confirm = CameraManagerTheme.PrimaryButton("確定");
            buttonRow.Children.Add(cancel);
            buttonRow.Children.Add(confirm);
            panel.Children.Add(buttonRow);
            card.Child = panel;

            _renamePopup = new Popup { StaysOpen = false, Placement = PlacementMode.Bottom, Child = card };

            Action confirmAction = () =>
            {
                string newName = _renameTextBox.Text?.Trim();
                _renamePopup.IsOpen = false;
                if (!string.IsNullOrEmpty(newName) && _renameTargetRow != null && newName != _renameTargetRow.Name)
                    OnNameEditCommitted(_renameTargetRow, newName);
            };
            confirm.Click += (s, e) => confirmAction();
            cancel.Click += (s, e) => _renamePopup.IsOpen = false;
            _renameTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) confirmAction();
                else if (e.Key == Key.Escape) _renamePopup.IsOpen = false;
            };

            // Popup 必須先掛進某個已經在視覺樹裡的容器才能正常顯示（同 PresetsSection「複製到
            // 其他樓層」Popup 的作法）；IsOpen==false 時不佔版面、不渲染，掛進 _rootGrid 沒有
            // 額外開銷。
            WpfGrid.SetRow(_renamePopup, 0);
            _rootGrid.Children.Add(_renamePopup);
        }

        // ── 2026-09-25：室外範圍半徑（m）小型主題化輸入 Popup（只建立一次、重複使用，不累積 HWND） ──
        private Popup _outdoorRadiusPopup;
        private WpfTextBox _outdoorRadiusTextBox;
        private TextBlock _outdoorRadiusHint;

        private void ShowOutdoorRadiusPopup(UIElement placementTarget)
        {
            EnsureOutdoorRadiusPopupBuilt();
            double cur = ConfigManager.Instance.Settings.CameraManager.GetEffectiveOutdoorRadiusM();
            _outdoorRadiusTextBox.Text = cur.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            _outdoorRadiusHint.Text = $"範圍 {CameraManagerSettings.MinOutdoorRadiusM:0}～{CameraManagerSettings.MaxOutdoorRadiusM:0} m；實際半徑＝min(遠裁剪, 本值)";
            _outdoorRadiusHint.Foreground = CameraManagerTheme.SecondaryText;
            _outdoorRadiusPopup.PlacementTarget = placementTarget ?? (UIElement)_oneClickFourViewsButton;
            _outdoorRadiusPopup.IsOpen = true;
            RunOnUiThread(() =>
            {
                _outdoorRadiusTextBox.Focus();
                _outdoorRadiusTextBox.SelectAll();
            });
        }

        private void EnsureOutdoorRadiusPopupBuilt()
        {
            if (_outdoorRadiusPopup != null) return;

            _outdoorRadiusTextBox = new WpfTextBox { Width = 180, Margin = new Thickness(0, 0, 0, 4) };
            CameraManagerTheme.StyleTextBox(_outdoorRadiusTextBox);
            _outdoorRadiusHint = new TextBlock
            {
                FontFamily = CameraManagerTheme.UiFont,
                FontSize = CameraManagerTheme.BodyFontSize - 1,
                Foreground = CameraManagerTheme.SecondaryText,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 200,
                Margin = new Thickness(0, 0, 0, CameraManagerTheme.Spacing)
            };

            Border card = new Border
            {
                Background = CameraManagerTheme.Surface,
                BorderBrush = CameraManagerTheme.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(CameraManagerTheme.CornerRadiusPx),
                Padding = new Thickness(CameraManagerTheme.Spacing),
                Effect = CameraManagerTheme.CardShadow()
            };
            StackPanel panel = new StackPanel();
            panel.Children.Add(CameraManagerTheme.SectionTitle("室外範圍半徑 (m)"));
            panel.Children.Add(_outdoorRadiusTextBox);
            panel.Children.Add(_outdoorRadiusHint);

            StackPanel buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = CameraManagerTheme.SecondaryButton("取消");
            cancel.Margin = new Thickness(0, 0, 6, 0);
            Button confirm = CameraManagerTheme.PrimaryButton("確定");
            buttonRow.Children.Add(cancel);
            buttonRow.Children.Add(confirm);
            panel.Children.Add(buttonRow);
            card.Child = panel;

            _outdoorRadiusPopup = new Popup { StaysOpen = false, Placement = PlacementMode.Bottom, Child = card };

            Action confirmAction = () => SafeUiAction(() =>
            {
                string text = _outdoorRadiusTextBox.Text?.Trim() ?? "";
                bool ok = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
                          || double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out v);
                if (!ok || double.IsNaN(v) || double.IsInfinity(v) || v < CameraManagerSettings.MinOutdoorRadiusM || v > CameraManagerSettings.MaxOutdoorRadiusM)
                {
                    _outdoorRadiusHint.Text = $"請輸入 {CameraManagerSettings.MinOutdoorRadiusM:0}～{CameraManagerSettings.MaxOutdoorRadiusM:0} 之間的數字（m）";
                    _outdoorRadiusHint.Foreground = CameraManagerTheme.Error;
                    return;
                }
                _outdoorRadiusPopup.IsOpen = false;
                double before = ConfigManager.Instance.Settings.CameraManager.OutdoorRadiusM;
                ConfigManager.Instance.Settings.CameraManager.OutdoorRadiusM = v;
                ConfigManager.Instance.SaveSettings();
                Logger.Info($"相機管理面板：室外範圍半徑 {before:0.##}m → {v:0.##}m");
                SetStatus($"室外範圍半徑：{v:0.##} m——下次一鍵 4 視圖／重新整理套用（實際半徑＝min(遠裁剪, 本值)）。");
            }, "OutdoorRadiusConfirm");
            confirm.Click += (s, e) => confirmAction();
            cancel.Click += (s, e) => _outdoorRadiusPopup.IsOpen = false;
            _outdoorRadiusTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) confirmAction();
                else if (e.Key == Key.Escape) _outdoorRadiusPopup.IsOpen = false;
            };

            WpfGrid.SetRow(_outdoorRadiusPopup, 0);
            _rootGrid.Children.Add(_outdoorRadiusPopup);
        }

        private CameraRow GetPrimarySelectedRow() => _listSection.GetSelectedRows().FirstOrDefault();

        /// <summary>2026-09-24 第二輪：只有「使用者點選」造成的選取變化才會自動切換底圖（→ExportImage）
        /// 與讀剖面線（→ExternalEvent）；程式化選取（重新整理後的預設選取、SetRows 換資料）只做
        /// 純 WPF 的地圖高亮，不碰 Revit。</summary>
        private void OnSelectionChanged(List<CameraRow> selected, bool userInitiated)
        {
            CameraRow primary = selected.FirstOrDefault();
            _keyPlanSection.HighlightSelected(primary?.ViewId);
            if (!userInitiated) return;
            MaybeAutoSwitchBaseMapForSelection(primary);
            RefreshSelectedSectionLine(primary);
        }

        /// <summary>選到的相機若不在目前底圖所在樓層、且該樓層有現成的裁剪平面可用，自動切換底圖
        /// （2026-09-24 實測回報：不換底圖時容易誤以為「標記位置壞了」，其實是看錯樓層的圖）。
        /// 透過既有的 <see cref="KeyPlanSection.SelectPlanByLevel"/>→PlanSelected→RefreshBaseMap
        /// 既有管線完成，這裡不重複底圖刷新邏輯。</summary>
        private void MaybeAutoSwitchBaseMapForSelection(CameraRow primary)
        {
            if (primary?.LevelId == null) return;
            // 2026-09-25（§4.8）：同樓層、且目前底圖已包含這台相機 → 不換；否則挑「包含相機、面積最大」的同層平面。
            ElementId mapLevel = _keyPlanSection.SelectedPlanLevelId;
            if (mapLevel != null && mapLevel.GetIdValue() == primary.LevelId.GetIdValue() &&
                _keyPlanSection.SelectedPlanContains(primary.EyeXFeet, primary.EyeYFeet)) return;
            _keyPlanSection.SelectPlanByLevel(primary.LevelId, primary.EyeXFeet, primary.EyeYFeet);
        }

        /// <summary>選到的相機若已經有連結的 Key Plan 剖面，讀取其目前實際剖面線（反映使用者
        /// 手動調整後的位置）疊畫在迷你 key plan 上；沒有就清空。</summary>
        // 2026-09-24 第三輪：讀剖面線去重／合併。同一時間最多一個在佇列裡；跑的期間再有要求只記
        // 「最後一個」，跑完再補一次（跟 _pendingBaseMapPlanId 同一套作法）。
        private bool _sectionLineInFlight;
        private CameraRow _sectionLinePending;
        private bool _sectionLineHasPending;

        /// <param name="force">true＝即使快照說這台相機還沒有 Key Plan 剖面也要讀（一鍵 4 視圖剛建完剖面，
        /// 快照還沒重讀，HasKeyPlanSection 仍是舊值）。</param>
        private void RefreshSelectedSectionLine(CameraRow primary, bool force = false)
        {
            if (primary == null || string.IsNullOrEmpty(primary.UniqueId) || (!force && !primary.HasKeyPlanSection))
            {
                // 沒有剖面的相機：完全不碰 Revit，直接清掉地圖上的剖面線。
                if (!_sectionLineInFlight) _keyPlanSection.SetSectionLine(null, null);
                else { _sectionLinePending = null; _sectionLineHasPending = true; }
                return;
            }

            if (_sectionLineInFlight)
            {
                _sectionLinePending = primary;
                _sectionLineHasPending = true;
                return;
            }
            _sectionLineInFlight = true;

            int gen = _generation;
            string uid = primary.UniqueId;
            EnqueueRevit(uiApp =>
            {
                XYZ p1 = null, p2 = null;
                bool ok = false;
                try
                {
                    Document doc = uiApp?.ActiveUIDocument?.Document;
                    ok = doc != null && CameraKeyPlanService.TryGetSectionLineForCamera(doc, uid, out p1, out p2);
                }
                finally
                {
                    RunOnUiThread(() =>
                    {
                        _sectionLineInFlight = false;
                        if (gen == _generation) _keyPlanSection.SetSectionLine(ok ? p1 : null, ok ? p2 : null);
                        if (_sectionLineHasPending)
                        {
                            CameraRow next = _sectionLinePending;
                            _sectionLineHasPending = false;
                            _sectionLinePending = null;
                            if (next == null || next.UniqueId != uid) RefreshSelectedSectionLine(next);
                        }
                    });
                }
            }, "RefreshSelectedSectionLine");
        }

        /// <summary>選用欄「平面 X/Y cm」就地編輯：換算成 feet 後沿用既有的「用目前 row 值補齊剩餘
        /// 欄位」交易組裝邏輯（<see cref="OnGridFieldEditCommitted"/>），不重複實作。</summary>
        private void OnPlaneFieldEditCommitted(CameraRow row, string axis, double valueCm)
        {
            double feet = CameraMath.MmToFeet(valueCm * 10.0);
            OnGridFieldEditCommitted(row, req =>
            {
                if (axis == "X") req.EyeXFeet = feet;
                else req.EyeYFeet = feet;
            });
        }

        // ══════════════════════════════════════════════════════════
        // 讀取相機清單 / 4.4 雙向連動進入點
        // ══════════════════════════════════════════════════════════

        /// <summary>使用者明確要求的完整重新整理（「重新整理」／「從透視圖同步」按鈕、尚未載入時點
        /// 清單或地圖區）：讀快照＋匯出預設底圖。這是唯一會「自動」匯出預設底圖的入口。</summary>
        public void RefreshAll()
        {
            if (_suppressLiveSync) return;
            _armed = true;
            if (!_schemaSelfChecked)
            {
                // 面板第一次被使用者明確載入時做一次 ES Schema 自我檢查（需要 API context，所以不在
                // Loaded 做——Loaded 依設計不碰 Revit）。結果只寫 log，不影響任何流程。
                _schemaSelfChecked = true;
                EnqueueRevit(uiApp =>
                {
                    string r = CameraKeyPlanService.SelfCheckSchemas();
                    if (r == null) Logger.Info("相機管理面板：ES Schema 自我檢查 OK（KeyPlanLink＋SectionStateV2，double 欄位皆有 Spec）。");
                    else Logger.Error("相機管理面板：ES Schema 自我檢查 FAIL：" + r);
                }, "SchemaSelfCheck");
            }
            RequestSnapshot(exportBaseMap: true, reason: "RefreshAll（使用者明確操作）");
        }

        /// <summary>編輯／一鍵 4 視圖等動作完成後的輕量刷新：只重讀清單，沿用目前底圖重畫標記，
        /// 不重新匯出底圖。</summary>
        private void RefreshListOnly()
        {
            if (_suppressLiveSync) return;
            if (!_armed) return;
            RequestSnapshot(exportBaseMap: false, reason: "RefreshListOnly");
        }

        private void RequestSnapshot(bool exportBaseMap, string reason)
        {
            Logger.Info($"相機管理面板：{reason} 開始。");
            SetStatus("讀取相機清單...");
            int gen = _generation;
            EnqueueRevit(uiApp =>
            {
                Document doc = uiApp?.ActiveUIDocument?.Document;
                CameraManagerSnapshot snapshot = CameraManagerService.ReadSnapshot(doc);
                try { snapshot.ActiveViewId = uiApp?.ActiveUIDocument?.ActiveView?.Id; } catch { snapshot.ActiveViewId = null; }
                string key = GetDocumentKey(doc);
                RunOnUiThread(() => OnSnapshotReady(snapshot, exportBaseMap, key, gen));
            });
        }

        /// <summary>
        /// 由 Application.cs 的 <c>OnCameraManagerIdling</c> 呼叫（Idling＝API context，可直接讀
        /// Document）。2026-09-24 第二輪：只有在使用者已經對「同一份文件」按過重新整理之後
        /// （<see cref="IsLiveSyncArmed"/>、<see cref="LoadedDocumentKey"/> 由呼叫端先檢查），且只做
        /// 輕量的清單更新——絕不匯出底圖、不做預設選取的副作用。畫面更新一律非同步排到 Dispatcher，
        /// 不在 Idling 回呼裡同步做 WPF 版面工作。
        /// </summary>
        public void OnRevitDocumentEvent(Document doc)
        {
            if (_suppressLiveSync || !_armed) return;
            Logger.Info("相機管理面板：live sync 讀取快照（僅更新清單）。");
            CameraManagerSnapshot snapshot = CameraManagerService.ReadSnapshot(doc);
            string key = GetDocumentKey(doc);
            int gen = _generation;
            RunOnUiThread(() => OnSnapshotReady(snapshot, exportBaseMap: false, docKey: key, generation: gen));
        }

        // ── 2026-09-25：③ 位置圖跟隨作用中透視圖 ──
        /// <summary>換底圖後要對準的相機（底圖匯出是非同步的，畫好後在 OnBaseMapReady 補置中）。</summary>
        private ElementId _followCenterPendingViewId;
        private bool _followInProgress;

        /// <summary>
        /// 由 Application.cs 的 Idling 呼叫（ViewActivated 只設旗標、延後到 Idling；呼叫端已確認 live sync 已啟用、
        /// 同一份文件、面板顯示中、作用中視圖是透視相機）。這裡不讀 Revit——只把 Id 交給 UI 執行緒做輕量更新：
        ///   ① 程式化選取清單那一列（不觸發任何 Revit 副作用）；
        ///   ② 目前底圖不在該相機的下方樓層時，切到該樓層的底圖（只有樓層真的換了才匯出）；
        ///   ③ 地圖平移到該相機（已在可視範圍內就維持目前縮放／位置）。
        /// </summary>
        public void OnActivePerspectiveChanged(ElementId viewId)
        {
            if (viewId == null || !_armed) return;
            int gen = _generation;
            RunOnUiThread(() =>
            {
                if (gen != _generation || !_armed || _followInProgress) return;
                _followInProgress = true;
                try
                {
                    CameraRow row = _rows?.FirstOrDefault(r => r.ViewId == viewId);
                    if (row == null)
                    {
                        // 2026-09-25：新相機（同一次 Idling 的 live sync 快照可能還沒套用）→ 記下待跟隨，
                        // 下一份快照套用後再跟隨；600ms 內沒有快照進來就自己排一次輕量刷新（不匯出底圖）。
                        _followAfterSnapshotViewId = viewId;
                        Logger.Info($"相機管理面板：作用中透視圖 id={viewId.GetIdValue()} 不在目前清單（可能是新相機）→ 輕量刷新清單後再跟隨。");
                        StartFollowRefreshTimer();
                        return;
                    }
                    FollowRow(row);
                }
                catch (Exception ex)
                {
                    Logger.Error("相機管理面板：跟隨作用中透視圖失敗", ex);
                }
                finally
                {
                    _followInProgress = false;
                }
            });
        }

        /// <summary>待跟隨的新相機（不在清單時記下，快照套用後處理）。</summary>
        private ElementId _followAfterSnapshotViewId;
        private DispatcherTimer _followRefreshTimer;

        private void StartFollowRefreshTimer()
        {
            if (_followRefreshTimer == null)
            {
                _followRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
                _followRefreshTimer.Tick += (s, e) =>
                {
                    try
                    {
                        _followRefreshTimer.Stop();
                        if (_followAfterSnapshotViewId == null || !_armed) return;
                        RefreshListOnly(); // 快照 only，exportBaseMap=false；跟隨換樓層時才匯出
                    }
                    catch (Exception ex) { Logger.Error("相機管理面板：跟隨新相機的輕量刷新失敗", ex); }
                };
            }
            _followRefreshTimer.Stop();
            _followRefreshTimer.Start();
        }

        /// <summary>快照套用後：若有待跟隨的新相機且已在清單，跟隨它；仍不在就放棄（記 log）。</summary>
        private void TryFollowPendingAfterSnapshot()
        {
            ElementId pending = _followAfterSnapshotViewId;
            if (pending == null) return;
            _followAfterSnapshotViewId = null;
            _followRefreshTimer?.Stop();
            CameraRow row = _rows?.FirstOrDefault(r => r.ViewId == pending);
            if (row == null)
            {
                Logger.Info($"相機管理面板：刷新後作用中透視圖 id={pending.GetIdValue()} 仍不在清單，放棄跟隨。");
                return;
            }
            Logger.Info($"相機管理面板：新相機「{row.Name}」已出現在清單，開始跟隨。");
            FollowRow(row);
        }

        /// <summary>跟隨一台相機：選取清單列、地圖切到它的樓層（沒有已裁剪平面就用最近下方、再上方樓層）並置中。</summary>
        private void FollowRow(CameraRow row)
        {
            _listSection.SelectByViewId(row.ViewId); // 程式化 → 只做地圖高亮
            _keyPlanSection.HighlightSelected(row.ViewId);

            string mapAction = "底圖不變";
            if (_hasCroppedPlan && row.LevelId != null)
            {
                ElementId mapLevel = _keyPlanSection.SelectedPlanLevelId;
                // 2026-09-25（§4.8）：同樓層但目前底圖裁剪框不含相機（例如放大平面）也要重挑。
                bool sameLevel = mapLevel != null && mapLevel.GetIdValue() == row.LevelId.GetIdValue();
                if (!sameLevel || !_keyPlanSection.SelectedPlanContains(row.EyeXFeet, row.EyeYFeet))
                {
                    _followCenterPendingViewId = row.ViewId;
                    // → PlanSelected → RefreshBaseMap（ExternalEvent 匯出，已有 in-flight 合併）。
                    ElementId chosen = _keyPlanSection.SelectPlanByLevelWithFallback(row.LevelId, _levels, out bool switched, row.EyeXFeet, row.EyeYFeet);
                    if (chosen == null)
                    {
                        _followCenterPendingViewId = null;
                        mapAction = $"樓層 {row.LevelName} 與上下樓層都沒有可用的已裁剪平面，底圖不變";
                    }
                    else
                    {
                        string chosenName = _levels.FirstOrDefault(l => l.LevelId != null && l.LevelId.GetIdValue() == chosen.GetIdValue())?.Name ?? chosen.GetIdValue().ToString();
                        bool fallback = chosen.GetIdValue() != row.LevelId.GetIdValue();
                        if (fallback)
                            Logger.Info($"相機管理面板：樓層 {row.LevelName} 沒有已裁剪平面，改用最近樓層 {chosenName} 的底圖。");
                        if (switched) mapAction = fallback ? $"切換底圖到最近樓層 {chosenName}（{row.LevelName} 沒有已裁剪平面）" : $"切換底圖到樓層 {row.LevelName}";
                        else { _followCenterPendingViewId = null; mapAction = $"底圖已是樓層 {chosenName}"; }
                    }
                }
            }
            bool panned = _followCenterPendingViewId == null && _keyPlanSection.CenterOnCamera(row.ViewId);
            Logger.Info($"相機管理面板：跟隨作用中透視圖「{row.Name}」：選取清單列、{mapAction}、" +
                        (_followCenterPendingViewId != null ? "底圖畫好後置中" : (panned ? "地圖平移置中" : "已在可視範圍內（不平移）")));
        }

        /// <summary>文件關閉／切換到別的文件時由 Application.cs 呼叫：清回中性狀態、停用 live sync。
        /// 純 WPF，非同步執行。</summary>
        public void ResetToNeutral(string message)
        {
            RunOnUiThread(() =>
            {
                _generation++;
                _armed = false;
                _loadedDocKey = null;
                _rows = new List<CameraRow>();
                _levels = new List<LevelOption>();
                _hasCroppedPlan = false;
                _pendingBaseMapPlanId = null;
                _currentBaseMapPlanId = null;
                _followCenterPendingViewId = null;
                _followAfterSnapshotViewId = null;
                _followRefreshTimer?.Stop();
                _baseMapRefreshInFlight = false;
                _sideBySideActive = false;
                _sideBySideOpenedViewIds = new List<ElementId>();
                _sideBySidePreviousActiveViewId = null;
                _multiView.SetRestoreEnabled(false);
                _markerRefreshPending.Clear();
                _markerRefreshTimer?.Stop();
                _presetsSection.CloseAllPopups();
                foreach (WpfContextMenu m in new[] { _toolbarMenu, _fourViewsMenu, _moreMenu, _cameraMenu })
                    if (m != null && m.IsOpen) m.IsOpen = false;
                _listSection.SetRows(_rows);
                _listSection.SetLevels(_levels);
                _presetsSection.SetLevels(_levels);
                _keyPlanSection.ResetToNeutral(message ?? NeutralMessage);
                _statusText.Text = message ?? NeutralMessage;
                Logger.Info("相機管理面板：回到中性狀態（" + (message ?? NeutralMessage) + "）。");
            });
        }

        private void OnSnapshotReady(CameraManagerSnapshot snapshot, bool exportBaseMap, string docKey, int generation)
        {
            if (generation != _generation) { Logger.Info("相機管理面板：丟棄過期的快照（文件已切換）。"); return; }
            Logger.Info($"相機管理面板：OnSnapshotReady，共 {snapshot?.Cameras?.Count ?? 0} 台相機（exportBaseMap={exportBaseMap}）。");
            if (snapshot == null) return;

            ElementId previouslySelected = GetPrimarySelectedRow()?.ViewId;
            bool sameDocument = docKey != null && docKey == _loadedDocKey;
            _loadedDocKey = docKey;

            _rows = snapshot.Cameras ?? new List<CameraRow>();
            _levels = snapshot.Levels ?? new List<LevelOption>();
            _hasCroppedPlan = snapshot.HasCroppedPlan;

            _listSection.SetLevels(_levels);
            _listSection.SetRows(_rows);
            _presetsSection.SetLevels(_levels);
            // 程式化設定底圖 combo 不會觸發匯出（KeyPlanSection._suppressPlanEvent）。清單刷新時
            // combo 維持目前實際顯示的那張底圖。
            ElementId comboPlanId = (!exportBaseMap && _currentBaseMapPlanId != null) ? _currentBaseMapPlanId : snapshot.DefaultPlanViewId;
            // 2026-09-25：底圖清單預設只列有相機的樓層（每層一張），需要每樓層相機數。
            var cameraCountByLevel = new Dictionary<ElementId, int>();
            foreach (CameraRow r in _rows)
            {
                if (r?.LevelId == null) continue;
                cameraCountByLevel[r.LevelId] = cameraCountByLevel.TryGetValue(r.LevelId, out int c) ? c + 1 : 1;
            }
            _keyPlanSection.SetPlanOptions(snapshot.PlanViews, comboPlanId, cameraCountByLevel);

            if (snapshot.ErrorMessage != null)
            {
                SetStatus(snapshot.ErrorMessage);
                _keyPlanSection.ShowMessage(snapshot.ErrorMessage);
                return;
            }

            string summary = _rows.Count == 0
                ? "目前文件沒有任何透視相機視圖（View3D.IsPerspective）。"
                : $"共 {_rows.Count} 個透視相機。";
            SetStatus(summary);
            if (_statusAfterSnapshot != null) { SetStatus(_statusAfterSnapshot); _statusAfterSnapshot = null; }

            if (exportBaseMap)
            {
                if (!_hasCroppedPlan)
                {
                    _currentBaseMapPlanId = null;
                    _keyPlanSection.DrawWithNeutralGrid(_rows);
                    _listSection.RefreshStatusColumn();
                }
                else if (snapshot.DefaultPlanViewId != null && snapshot.DefaultPlanViewId != ElementId.InvalidElementId)
                {
                    RefreshBaseMap(snapshot.DefaultPlanViewId);
                }
            }
            else
            {
                // 輕量刷新：沿用目前底圖重畫標記（純 WPF），不匯出。
                _keyPlanSection.RedrawMarkers(_rows, _hasCroppedPlan);
                _listSection.RefreshStatusColumn();
            }

            // 預設選取（程式化 → 只做地圖高亮，不觸發自動切換底圖／讀剖面線）：同一份文件優先
            // 保留原本的選取，其次作用中透視相機，最後第一列。
            if (_rows.Count > 0)
            {
                CameraRow defaultRow =
                    (sameDocument && previouslySelected != null ? _rows.FirstOrDefault(r => r.ViewId == previouslySelected) : null)
                    ?? (snapshot.ActiveViewId != null ? _rows.FirstOrDefault(r => r.ViewId == snapshot.ActiveViewId) : null)
                    ?? _rows[0];
                _listSection.SelectByViewId(defaultRow.ViewId);
                _keyPlanSection.HighlightSelected(defaultRow.ViewId);
            }

            // 2026-09-25：作用中透視圖是新相機時，快照套用後補跟隨（見 OnActivePerspectiveChanged）。
            try { TryFollowPendingAfterSnapshot(); }
            catch (Exception ex) { Logger.Error("相機管理面板：快照後跟隨新相機失敗", ex); }
        }

        // ══════════════════════════════════════════════════════════
        // 4.8 底圖
        // ══════════════════════════════════════════════════════════

        private void RefreshBaseMap(ElementId planViewId)
        {
            // 2026-09-24 崩潰修復：重入防護——選取變化很快（例如「預設選取」＋「自動切換底圖」
            // 連續觸發，見 OnSelectionChanged／MaybeAutoSwitchBaseMapForSelection）可能在上一次
            // ExportImage 還沒跑完時又觸發下一次；同時開兩個 doc.ExportImage() 沒有已知安全性
            // 保證，一律以「目前這次為準，重複的先丟掉」處理，等目前這次跑完（見
            // OnBaseMapReady 的 finally）之後，使用者仍可用「重新整理」/「從透視圖同步」再要一次。
            if (planViewId == null || planViewId == ElementId.InvalidElementId) return;
            if (_baseMapRefreshInFlight)
            {
                // 2026-09-24 第二輪：不再丟掉——記下最後一個要求，目前這次結束後接著匯出。
                _pendingBaseMapPlanId = planViewId;
                Logger.Info($"相機管理面板：底圖匯出進行中，排定結束後改匯出 planViewId={planViewId.GetIdValue()}。");
                return;
            }
            _baseMapRefreshInFlight = true;

            Logger.Info($"相機管理面板：底圖匯出開始（planViewId={planViewId.GetIdValue()}）。");
            SetStatus("匯出底圖...");
            int gen = _generation;
            EnqueueRevit(uiApp =>
            {
                BaseMapSnapshot map = null;
                try
                {
                    Document doc = uiApp?.ActiveUIDocument?.Document;
                    map = doc == null
                        ? new BaseMapSnapshot { ErrorMessage = "目前沒有開啟中的文件。" }
                        : CameraManagerService.ExportBaseMap(doc, planViewId);
                }
                catch (Exception ex)
                {
                    map = new BaseMapSnapshot { ErrorMessage = "底圖匯出失敗：" + ex.Message };
                }
                finally
                {
                    // 不論成功失敗，一定要回到 UI 執行緒清掉 _baseMapRefreshInFlight。
                    BaseMapSnapshot result = map ?? new BaseMapSnapshot { ErrorMessage = "底圖匯出失敗。" };
                    RunOnUiThread(() => OnBaseMapReady(result, planViewId, gen));
                }
            });
        }

        private void OnBaseMapReady(BaseMapSnapshot map, ElementId planViewId, int generation)
        {
            if (generation != _generation)
            {
                CameraManagerService.CleanupTempDir(map?.TempDirectory);
                _baseMapRefreshInFlight = false;
                _pendingBaseMapPlanId = null;
                return;
            }
            try
            {
                if (map.ErrorMessage != null)
                {
                    SetStatus(map.ErrorMessage);
                    _keyPlanSection.ShowMessage(map.ErrorMessage);
                    CameraManagerService.CleanupTempDir(map.TempDirectory);
                    return;
                }

                BitmapImage bitmap;
                try
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(map.ImageFilePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
                catch (Exception ex)
                {
                    SetStatus("底圖讀取失敗：" + ex.Message);
                    _keyPlanSection.ShowMessage("底圖讀取失敗：" + ex.Message);
                    CameraManagerService.CleanupTempDir(map.TempDirectory);
                    return;
                }
                CameraManagerService.CleanupTempDir(map.TempDirectory);

                _currentBaseMapPlanId = planViewId;
                _keyPlanSection.DrawWithBaseMap(bitmap, map, _rows);
                _listSection.RefreshStatusColumn();

                // 2026-09-25：作用中透視圖跟隨——換底圖是因為跟隨某台相機時，底圖畫好後再把地圖對準那台相機。
                if (_followCenterPendingViewId != null)
                {
                    ElementId follow = _followCenterPendingViewId;
                    _followCenterPendingViewId = null;
                    try { _keyPlanSection.CenterOnCamera(follow); }
                    catch (Exception exFollow) { Logger.Error("相機管理面板：跟隨作用中透視圖置中失敗", exFollow); }
                }

                CameraRow primary = GetPrimarySelectedRow();
                _keyPlanSection.HighlightSelected(primary?.ViewId);

                // 2026-09-24 實測回報「標記位置壞了」：健檢用——理論上換算範圍（CropMinX/Y～
                // CropMaxX/Y，已含註記裁剪外擴，見 ExportBaseMap）的長寬比應該等於匯出圖片本身的
                // 像素長寬比；差超過 1% 代表對位假設在這張視圖上失準（例如註記裁剪關閉但仍有格線
                // 泡外露、或視圖本身不是單純水平投影），用狀態列明講出來，不要讓標記悄悄畫錯位置
                // 卻看起來一切正常。
                double contentW = map.CropMaxXFeet - map.CropMinXFeet;
                double contentH = map.CropMaxYFeet - map.CropMinYFeet;
                bool aspectMismatch = false;
                if (contentW > 1e-6 && contentH > 1e-6)
                {
                    double contentAspect = contentW / contentH;
                    double imageAspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
                    double bigger = Math.Max(contentAspect, imageAspect);
                    aspectMismatch = bigger > 1e-9 && Math.Abs(contentAspect - imageAspect) / bigger > 0.01;
                }

                string suffix = map.Warning != null ? "（" + map.Warning + "）" : "";
                if (aspectMismatch) suffix += "（底圖對位可能有誤差：換算範圍長寬比與圖片不一致）";
                SetStatus($"底圖已更新：{bitmap.PixelWidth}x{bitmap.PixelHeight}px" + suffix);
            }
            finally
            {
                _baseMapRefreshInFlight = false;
                Logger.Info("相機管理面板：底圖匯出結束。");
                ElementId next = _pendingBaseMapPlanId;
                _pendingBaseMapPlanId = null;
                if (next != null && next != planViewId) RefreshBaseMap(next);
            }
        }

        // ══════════════════════════════════════════════════════════
        // 「選取中相機」卡片就地編輯 ／ Tab1 DataGrid 就地編輯
        // ══════════════════════════════════════════════════════════

        private void OnNameEditCommitted(CameraRow row, string newName)
        {
            SetStatus("重新命名中...");
            RunSuppressed(() => EnqueueRevit(uiApp =>
            {
                Document doc = uiApp?.ActiveUIDocument?.Document;
                string err = CameraManagerService.RenameCameraView(doc, row.ViewId, newName);
                RunOnUiThread(() =>
                {
                    SetStatus(err ?? $"已重新命名為「{newName}」。");
                    EndSuppressed();
                    RefreshListOnly();
                });
            }));
        }

        /// <summary>離樓板cm／水平角／俯仰角三個數字欄共用：從目前 row 的既有值組一份完整
        /// <see cref="CameraEditRequest"/>，只覆寫使用者剛剛改的那一格，其餘欄位維持原值不動
        /// （沿用既有 ApplyDetailEdit 的「一次寫齊」交易，不必為單一欄位另外開一套寫入邏輯）。</summary>
        private void OnNumericFieldEditCommitted(CameraRow row, string fieldKey, double value)
        {
            OnGridFieldEditCommitted(row, req =>
            {
                if (fieldKey == "Height") req.HeightAboveLevelCm = value;
                else if (fieldKey == "HAngle") req.HorizontalAngleDeg = value;
                else if (fieldKey == "Pitch") req.PitchDeg = value;
            });
        }

        /// <summary>DataGrid 就地編輯與「選取中相機」卡片共用的組裝邏輯：從目前 row 的既有值
        /// 組一份完整 <see cref="CameraEditRequest"/>，讓呼叫端只覆寫剛剛改的欄位。</summary>
        /// <param name="afterApply">選用：編輯成功後在同一個 ExternalEvent 內接著執行（交易外），回傳要附加在狀態列的文字。</param>
        /// <param name="onDone">選用：不論成敗，最後在 UI 執行緒呼叫一次。</param>
        private void OnGridFieldEditCommitted(CameraRow row, Action<CameraEditRequest> overrideField,
            Func<UIApplication, Document, string> afterApply = null, Action onDone = null)
        {
            try
            {
                OnGridFieldEditCommittedCore(row, overrideField, afterApply, onDone);
            }
            catch (Exception ex)
            {
                Logger.Error("相機管理面板：套用清單編輯失敗", ex);
                try { onDone?.Invoke(); } catch { }
            }
        }

        private void OnGridFieldEditCommittedCore(CameraRow row, Action<CameraEditRequest> overrideField,
            Func<UIApplication, Document, string> afterApply, Action onDone)
        {
            var req = new CameraEditRequest
            {
                ViewId = row.ViewId,
                EyeXFeet = row.EyeXFeet,
                EyeYFeet = row.EyeYFeet,
                LevelId = row.LevelId,
                HeightAboveLevelCm = row.HeightAboveLevelCm ?? 0,
                HorizontalAngleDeg = row.HorizontalAngleDeg,
                PitchDeg = row.PitchDeg,
                FarClipCm = null
            };
            overrideField(req);

            if (req.LevelId == null)
            {
                SetStatus("這台相機沒有可用的樓層基準，無法編輯。");
                onDone?.Invoke();
                return;
            }

            SetStatus("套用編輯中...");
            RunSuppressed(() => EnqueueRevit(uiApp =>
            {
                string err = null, extra = null;
                try
                {
                    Document doc = uiApp?.ActiveUIDocument?.Document;
                    err = CameraManagerService.ApplyDetailEdit(doc, req);
                    if (err == null && afterApply != null && doc != null)
                    {
                        try { extra = afterApply(uiApp, doc); }
                        catch (Exception ex)
                        {
                            Logger.Error("相機管理面板：編輯後續動作失敗", ex);
                            extra = "；後續動作失敗：" + ex.Message;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("相機管理面板：套用編輯失敗", ex);
                    err = err ?? ("套用編輯失敗：" + ex.Message);
                }
                RunOnUiThread(() =>
                {
                    try
                    {
                        SetStatus((err ?? "已套用編輯。") + (extra ?? ""));
                        EndSuppressed();
                        RefreshListOnly();
                    }
                    finally
                    {
                        try { onDone?.Invoke(); } catch (Exception ex) { Logger.Error("相機管理面板：編輯完成回呼失敗", ex); }
                    }
                });
            }));
        }

        // ══════════════════════════════════════════════════════════
        // 4.5 人視高預設值
        // ══════════════════════════════════════════════════════════

        private void OnApplyHeightRequested(double heightCm, bool toAll)
        {
            List<ElementId> targetIds = toAll
                ? _rows.Select(r => r.ViewId).ToList()
                : _listSection.GetSelectedRows().Select(r => r.ViewId).ToList();

            if (targetIds.Count == 0)
            {
                SetStatus("請先在清單選取至少一個相機。");
                return;
            }

            ConfigManager.Instance.Settings.CameraManager.DefaultHeightAboveLevelCm = heightCm;
            ConfigManager.Instance.SaveSettings();

            SetStatus("套用高度中...");
            RunSuppressed(() => EnqueueRevit(uiApp =>
            {
                Document doc = uiApp?.ActiveUIDocument?.Document;
                var (okCount, skippedCount, err) = CameraManagerService.ApplyHeightToCameras(doc, targetIds, heightCm);
                RunOnUiThread(() =>
                {
                    if (err != null) SetStatus(err);
                    else SetStatus($"已將 {okCount} 個相機的高度設為離樓板 {heightCm:F1} cm" + (skippedCount > 0 ? $"（{skippedCount} 個被跳過）" : ""));
                    EndSuppressed();
                    RefreshListOnly();
                });
            }));
        }

        // ══════════════════════════════════════════════════════════
        // 4.6 複製相機到其他樓層
        // ══════════════════════════════════════════════════════════

        private void OnCopyToLevelsRequested(List<ElementId> targetLevelIds)
        {
            CameraRow source = GetPrimarySelectedRow();
            if (source == null)
            {
                SetStatus("請先在清單選取一個相機（複製只認第一個選取）。");
                return;
            }

            SetStatus("複製相機中...");
            RunSuppressed(() => EnqueueRevit(uiApp =>
            {
                Document doc = uiApp?.ActiveUIDocument?.Document;
                var (created, err) = CameraManagerService.CopyCameraToLevels(doc, source.ViewId, targetLevelIds);
                RunOnUiThread(() =>
                {
                    SetStatus(err ?? $"已複製 {created} 台相機。");
                    EndSuppressed();
                    RefreshListOnly();
                });
            }));
        }

        // ══════════════════════════════════════════════════════════
        // 4.7 匯出 Excel（標題列圖示按鈕）
        // ══════════════════════════════════════════════════════════

        private void OnExportExcelRequested()
        {
            if (_rows.Count == 0)
            {
                SetStatus("目前沒有相機可以匯出。");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                FileName = "相機清單_" + DateTime.Now.ToString("yyyyMMdd") + ".xlsx"
            };
            if (dialog.ShowDialog() != true) return;

            string err = CameraManagerService.ExportExcel(_rows, dialog.FileName);
            SetStatus(err ?? "已匯出：" + dialog.FileName);
        }

        // ══════════════════════════════════════════════════════════
        // 跳轉
        // ══════════════════════════════════════════════════════════

        private void JumpToView(ElementId viewId)
        {
            SetStatus("跳轉視圖...");
            EnqueueRevit(uiApp =>
            {
                string err = CameraManagerService.JumpToView(uiApp, viewId);
                RunOnUiThread(() => SetStatus(err ?? "已跳轉"));
            });
        }

        // ══════════════════════════════════════════════════════════
        // 「一鍵 4 視圖」（規格 §5.1／§5.3，Task B）
        // ══════════════════════════════════════════════════════════

        private void DoOneClickFourViews() => RunOneClickFourViews(forceResetSection: false);

        /// <summary>一鍵 4 視圖（含重設剖面位置）共用：依序啟動透視／平面／剖面／等角、關閉其他視圖、
        /// PostCommand 並排、排定並排後 ZoomToFit。須在 ExternalEvent（交易外）呼叫。回傳錯誤訊息或 null。</summary>
        private static string OpenAndTileFourViews(UIApplication uiApp, Document doc, ElementId cameraViewId, CameraKeyPlanService.KeyPlanResult result)
        {
            try
            {
                View perspective = doc.GetElement(cameraViewId) as View;
                View plan = doc.GetElement(result.PlanViewId) as View;
                View section = doc.GetElement(result.SectionViewId) as View;
                View iso = doc.GetElement(result.IsoViewId) as View;

                // 規格：依序啟動 4 張視圖，最後一步才 PostCommand TileViews
                // （PostCommand 排入佇列，等這次 Execute() 整段結束、控制權還給 Revit 訊息迴圈後才真的執行）。
                if (perspective != null) uiApp.ActiveUIDocument.ActiveView = perspective;
                if (plan != null) uiApp.ActiveUIDocument.ActiveView = plan;
                if (section != null) uiApp.ActiveUIDocument.ActiveView = section;
                if (iso != null) uiApp.ActiveUIDocument.ActiveView = iso;
                // 2026-09-24：先關閉其他所有視圖視窗（作用中已是 4 張之一），只留這 4 張再並排。
                var keep = new List<ElementId>();
                foreach (View v in new[] { perspective, plan, section, iso }) if (v != null) keep.Add(v.Id);
                CloseOtherViews(uiApp, keep);
                if (iso != null)
                {
                    // 2026-09-24：等角視圖啟用 Section Box 後補一次 ZoomToFit（縮放基準＝目前裁切範圍）。
                    try
                    {
                        UIView isoUiView = uiApp.ActiveUIDocument.GetOpenUIViews()?.FirstOrDefault(v => v.ViewId == iso.Id);
                        isoUiView?.ZoomToFit();
                    }
                    catch { /* 縮放失敗不應該讓整個流程中斷，忽略即可 */ }
                }

                uiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.TileViews));
                // 並排會在本次 ExternalEvent 結束後才改變視窗大小 → 在之後的 Idling 才 ZoomToFit。
                CameraViewZoomService.ScheduleZoomToFit(uiApp, keep, expectTile: true);
                return null;
            }
            catch (Exception ex)
            {
                return "已建立/更新 Key Plan 視圖，但開啟/並排時發生錯誤：" + ex.Message;
            }
        }

        /// <summary>
        /// 2026-09-25（使用者決策）：相機的樓層「不就地修改」。清單「樓層」欄選另一個樓層＝沿用「複製到其他樓層」
        /// 邏輯（單一目標樓層）在同一平面 XY、同離樓板高度、同方向／裁剪／FOV／遠裁剪複製一台新相機，
        /// 名稱「原名 - 樓層名」（既有去重）。原相機與它的 Key Plan 視圖完全不動；不建立／刪除任何 Key Plan 視圖。
        /// 完成後刷新清單並選取新相機（沿用 _followAfterSnapshotViewId 跟隨機制），原列樓層格由清單端還原顯示。
        /// </summary>
        private void OnLevelEditCommitted(CameraRow row, ElementId levelId)
        {
            if (row == null || levelId == null) return;
            ElementId sourceId = row.ViewId;
            string srcName = row.Name ?? "?";
            long targetVal = levelId.GetIdValue();
            string levelName = _levels?.FirstOrDefault(l => l?.LevelId != null && l.LevelId.GetIdValue() == targetVal)?.Name
                               ?? targetVal.ToString();

            SetStatus($"複製「{srcName}」到 {levelName}...");
            bool queued = false;
            try
            {
                RunSuppressed(() => EnqueueRevit(uiApp =>
                {
                    string err = null;
                    var createdList = new List<(ElementId id, string name)>();
                    try
                    {
                        Document doc = uiApp?.ActiveUIDocument?.Document;
                        err = CameraManagerService.CopyCameraToLevels(doc, sourceId, new List<ElementId> { levelId }, createdList).error;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("相機管理面板：改樓層＝複製相機失敗（未預期例外）", ex);
                        err = "複製相機失敗：" + ex.Message;
                    }
                    RunOnUiThread(() =>
                    {
                        try
                        {
                            if (err == null && createdList.Count > 0)
                            {
                                var (newId, newName) = createdList[0];
                                Logger.Info($"清單編輯：{srcName} 樓層 → 複製到 {levelName} 新相機 {newName}");
                                string msg = $"已複製「{srcName}」到 {levelName} → 「{newName}」（原相機不變）";
                                _followAfterSnapshotViewId = newId;   // 快照套用後選取新相機（TryFollowPendingAfterSnapshot）
                                _statusAfterSnapshot = msg;           // 快照摘要不要蓋掉這句
                                SetStatus(msg);
                            }
                            else
                            {
                                string msg = err ?? $"沒有複製任何相機（找不到樓層 {levelName}）。";
                                Logger.Info($"清單編輯：{srcName} 樓層 → 複製到 {levelName} 未完成：{msg}");
                                SetStatus(msg);
                            }
                        }
                        catch (Exception ex) { Logger.Error("相機管理面板：改樓層＝複製後處理失敗", ex); }
                        finally
                        {
                            EndSuppressed();
                            RefreshListOnly();
                        }
                    });
                }));
                queued = true;
            }
            catch (Exception ex)
            {
                Logger.Error("相機管理面板：改樓層＝複製排入佇列失敗", ex);
                SetStatus("複製相機失敗：" + ex.Message);
            }
            finally
            {
                if (!queued) EndSuppressed();
            }
        }

        /// <summary>2026-09-25：下一份快照套用後要顯示的狀態列文字（取代「共 N 個透視相機」摘要，用一次即清除）。</summary>
        private string _statusAfterSnapshot;

        /// <summary>動作列「重設剖面位置」：無條件把剖面 CropBox 強制覆蓋回目前相機的視線位置
        /// （見 <see cref="CameraKeyPlanService.ResetSectionToSightLine"/>），供使用者手動調整
        /// 剖面之後想要「還原成自動算出來的視線」時使用。</summary>
        private void DoResetSectionToSightLine() => RunOneClickFourViews(forceResetSection: true);

        private void RunOneClickFourViews(bool forceResetSection)
        {
            CameraRow row = GetPrimarySelectedRow();
            if (row == null) { SetStatus("請先在清單選取一個相機。"); return; }

            // 2026-09-24 崩潰修復＋新功能重入防護：見 _fourViewsInFlight 欄位註解。名稱單擊／
            // 地圖標記／右鍵選單都會經過這裡，快速連點只會跑第一次，其餘忽略並提示狀態列。
            if (_fourViewsInFlight)
            {
                Logger.Info("相機管理面板：一鍵 4 視圖（或重設剖面）已在進行中，忽略這次重複觸發。");
                SetStatus("正在建立 4 視圖，請稍候...");
                return;
            }
            _fourViewsInFlight = true;

            Logger.Info($"相機管理面板：一鍵 4 視圖開始（viewId={row.ViewId?.GetIdValue()}, forceResetSection={forceResetSection}）。");
            SetStatus(forceResetSection ? "重設剖面位置中..." : "建立/更新 Key Plan 中...");
            EnqueueRevit(uiApp =>
            {
                Document doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    RunOnUiThread(() => { _fourViewsInFlight = false; SetStatus("目前沒有開啟中的文件。"); });
                    return;
                }

                CameraKeyPlanService.KeyPlanResult result;
                try
                {
                    result = CameraKeyPlanService.CreateOrUpdateFourViews(uiApp, doc, row.ViewId, forceResetSection);
                }
                catch (Exception ex)
                {
                    Logger.Error("相機管理面板：一鍵 4 視圖失敗（未預期例外）", ex);
                    CameraManagerDiagnostics.RecordCaught("CreateOrUpdateFourViews", ex);
                    RunOnUiThread(() => { _fourViewsInFlight = false; SetStatus("一鍵 4 視圖失敗：" + ex.Message); });
                    return;
                }
                if (result.Error != null)
                {
                    Logger.Error("相機管理面板：一鍵 4 視圖失敗：" + result.Error);
                    RunOnUiThread(() => { _fourViewsInFlight = false; SetStatus(result.Error); });
                    return;
                }

                string err = OpenAndTileFourViews(uiApp, doc, row.ViewId, result);

                if (err == null)
                {
                    err = result.SectionPreserved
                        ? "剖面位置已保留（你曾手動調整）；已建立/更新 Key Plan 平面／等角並補畫相機標記，並排開啟 4 張視圖。"
                        : "已建立/更新 Key Plan 平面／剖面／等角三視圖並並排開啟（若未真的並排顯示，請確認 Revit 選項未勾選「使用索引標籤視圖」）。";
                }

                RunOnUiThread(() =>
                {
                    try
                    {
                        Logger.Info("相機管理面板：一鍵 4 視圖結束。");
                        SetStatus(err);
                        RefreshListOnly();
                        RefreshSelectedSectionLine(row, force: true);
                    }
                    finally
                    {
                        _fourViewsInFlight = false;
                    }
                });
            });
        }

        // ══════════════════════════════════════════════════════════
        // 4.9 A：並排檢視 / 還原
        // ══════════════════════════════════════════════════════════

        private void DoSideBySide()
        {
            CameraRow row = GetPrimarySelectedRow();
            if (row == null) { SetStatus("請先在清單選取一個相機。"); return; }
            if (row.LevelId == null) { SetStatus("找不到這台相機的所在樓層，無法並排檢視。"); return; }

            SetStatus("並排檢視中...");
            EnqueueRevit(uiApp =>
            {
                Document doc = uiApp?.ActiveUIDocument?.Document;
                string err = null;
                List<ElementId> opened = new List<ElementId>();
                ElementId previousActive = null;

                if (doc == null)
                {
                    err = "目前沒有開啟中的文件。";
                }
                else
                {
                    try
                    {
                        var openBefore = CameraManagerService.SnapshotOpenViews(uiApp);
                        HashSet<ElementId> openBeforeIds = new HashSet<ElementId>(openBefore.Select(t => t.viewId));
                        previousActive = uiApp.ActiveUIDocument.ActiveView?.Id;

                        View3D perspective = doc.GetElement(row.ViewId) as View3D;
                        if (perspective == null)
                        {
                            err = "找不到這個相機視圖（可能已被刪除），請按重新整理。";
                        }
                        else
                        {
                            ElementId planId = CameraManagerService.FindPlanViewForLevel(uiApp, doc, row.LevelId);
                            if (planId == null)
                            {
                                err = "找不到「" + row.LevelName + "」樓層任何既有平面視圖，無法並排檢視（4.9 A 只用既有視圖，不會新建）。";
                            }
                            else
                            {
                                uiApp.ActiveUIDocument.ActiveView = perspective;
                                uiApp.ActiveUIDocument.ActiveView = doc.GetElement(planId) as View;

                                if (!openBeforeIds.Contains(perspective.Id)) opened.Add(perspective.Id);
                                if (!openBeforeIds.Contains(planId)) opened.Add(planId);

                                // 2026-09-24：關閉其他視圖（包含這台相機若已有的 Key Plan 視圖以外的全部），只留並排的視圖。
                                var keep = new List<ElementId> { perspective.Id, planId };
                                CloseOtherViews(uiApp, keep);
                                uiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.TileViews));
                                CameraViewZoomService.ScheduleZoomToFit(uiApp, keep, expectTile: true);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        err = "並排檢視失敗：" + ex.Message;
                    }
                }

                RunOnUiThread(() =>
                {
                    if (err != null)
                    {
                        SetStatus(err);
                    }
                    else
                    {
                        _sideBySideOpenedViewIds = opened;
                        _sideBySidePreviousActiveViewId = previousActive;
                        _sideBySideActive = true;
                        _multiView.SetRestoreEnabled(true);
                        SetStatus("已並排檢視（若視窗沒有真的並排顯示，請確認 Revit 選項未勾選「使用索引標籤視圖」）。");
                    }
                });
            });
        }

        private void DoRestore()
        {
            if (!_sideBySideActive) return;
            List<ElementId> toClose = _sideBySideOpenedViewIds;
            ElementId reactivate = _sideBySidePreviousActiveViewId;

            SetStatus("還原中...");
            EnqueueRevit(uiApp =>
            {
                Document doc = uiApp?.ActiveUIDocument?.Document;
                string err = null;
                try
                {
                    if (doc != null && uiApp.ActiveUIDocument != null)
                    {
                        foreach (ElementId id in toClose)
                        {
                            IList<UIView> openViews = uiApp.ActiveUIDocument.GetOpenUIViews();
                            if (openViews.Count <= 1) break;
                            UIView uv = openViews.FirstOrDefault(v => v.ViewId == id);
                            uv?.Close();
                        }
                        if (reactivate != null)
                        {
                            View v = doc.GetElement(reactivate) as View;
                            if (v != null) uiApp.ActiveUIDocument.ActiveView = v;
                        }
                    }
                }
                catch (Exception ex)
                {
                    err = "還原失敗：" + ex.Message;
                }

                RunOnUiThread(() =>
                {
                    _sideBySideActive = false;
                    _multiView.SetRestoreEnabled(false);
                    SetStatus(err ?? "已還原。");
                });
            });
        }

        // ══════════════════════════════════════════════════════════
        // 開啟預覽視窗（4.9 B 收斂後的唯一入口，標題列圖示按鈕呼叫）
        // ══════════════════════════════════════════════════════════

        private void OpenPreviewWindow()
        {
            CameraRow row = GetPrimarySelectedRow();
            if (row == null) { SetStatus("請先在清單選取一個相機。"); return; }
            if (row.LevelId == null) { SetStatus("找不到這台相機的所在樓層。"); return; }

            EnqueueRevit(uiApp =>
            {
                Document doc = uiApp?.ActiveUIDocument?.Document;
                PreviewControl a = null, b = null;
                string err = null;
                try
                {
                    if (doc == null) throw new Exception("目前沒有開啟中的文件。");
                    ElementId planId = CameraManagerService.FindPlanViewForLevel(uiApp, doc, row.LevelId);
                    if (planId == null) throw new Exception("找不到既有平面視圖。");
                    a = new PreviewControl(doc, row.ViewId);
                    b = new PreviewControl(doc, planId);
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    SafeDispose(a); SafeDispose(b);
                    a = null; b = null;
                }

                RunOnUiThread(() =>
                {
                    if (err != null) { SetStatus("開啟預覽視窗失敗：" + err); return; }
                    var win = new CameraPreviewWindow();
                    win.SetPreviews(a, b);
                    // 2026-09-24 崩潰修復：追蹤這個視窗，讓文件關閉／Revit 關閉時能強制關掉
                    // （見 CloseAllPreviewWindows／Application.cs 的 DocumentClosing／OnShutdown）；
                    // 使用者自己關視窗時，Closed 事件自然會把它從清單移除，不會累積殘留參照。
                    _openPreviewWindows.Add(win);
                    win.Closed += (s2, e2) => _openPreviewWindows.Remove(win);
                    win.Show();
                    Logger.Info($"相機管理面板：已開啟預覽視窗（perspectiveViewId={row.ViewId?.GetIdValue()}）。");
                    SetStatus("已開啟預覽視窗。");
                });
            });
        }

        private static void SafeDispose(PreviewControl c)
        {
            try { c?.Dispose(); } catch { /* 視窗建立失敗時盡量不要再拋例外 */ }
        }

        /// <summary>由 Application.cs 在 DocumentClosing／OnShutdown 呼叫：強制關閉所有還開著的
        /// 相機預覽視窗。CameraPreviewWindow.Closed 已經接了 DisposeControls()，所以 Close()
        /// 就會連帶把底下的 PreviewControl（含其原生 AdWindows 資源）Dispose 掉，不留給 GC
        /// 在 finalizer 執行緒收尾——本次崩潰調查發現的 SxSActivationContext SEHException
        /// 就是這類物件沒被明確 Dispose 的後果（Application.cs 訂閱處有完整說明）。
        /// 一律經 RunOnUiThread：Window.Close() 必須在建立該視窗的執行緒（UI 執行緒）上呼叫，
        /// 而 DocumentClosing／OnShutdown 都可能不是從 UI 執行緒的正常訊息處理路徑呼叫進來。</summary>
        public void CloseAllPreviewWindows()
        {
            Action closeAll = () =>
            {
                if (_openPreviewWindows.Count == 0) return;
                Logger.Info($"相機管理面板：強制關閉 {_openPreviewWindows.Count} 個相機預覽視窗。");
                foreach (CameraPreviewWindow w in _openPreviewWindows.ToList())
                {
                    try { w.Close(); }
                    catch (Exception ex) { Logger.Error("關閉相機預覽視窗失敗: " + ex.Message); }
                }
                _openPreviewWindows.Clear();
            };
            // 文件關閉前必須「同步」關掉（之後 Document 就失效了），所以在 UI 執行緒上直接執行。
            if (Dispatcher.CheckAccess()) SafeInvoke(closeAll, nameof(CloseAllPreviewWindows));
            else RunOnUiThread(closeAll);
        }

        // ══════════════════════════════════════════════════════════
        // §4.4（2026-09-24 第四輪）：透視圖 FOV／相機變動 → 去抖 500ms 後只重畫標記
        // ══════════════════════════════════════════════════════════

        private readonly HashSet<ElementId> _markerRefreshPending = new HashSet<ElementId>();
        private DispatcherTimer _markerRefreshTimer;
        private bool _markerRefreshInFlight;

        /// <summary>由 Application.cs 的 DocumentChanged 呼叫（只傳 Id，不在 DocumentChanged 內改模型）。
        /// 同一相機多次變更合併；最後一次變更後 500ms 才排入「更新相機標記」。</summary>
        public void QueueCameraMarkerRefresh(IEnumerable<ElementId> perspectiveViewIds)
        {
            try
            {
                List<ElementId> ids = perspectiveViewIds?.Where(i => i != null).ToList();
                if (ids == null || ids.Count == 0 || !_armed) return;
                RunOnUiThread(() =>
                {
                    foreach (ElementId id in ids) _markerRefreshPending.Add(id);
                    if (_markerRefreshTimer == null)
                    {
                        _markerRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                        {
                            Interval = TimeSpan.FromMilliseconds(500)
                        };
                        _markerRefreshTimer.Tick += OnMarkerRefreshTimerTick;
                    }
                    _markerRefreshTimer.Stop();
                    _markerRefreshTimer.Start(); // 重新計時＝去抖
                });
            }
            catch (Exception ex)
            {
                Logger.Error("相機管理面板：排入相機標記更新失敗", ex);
            }
        }

        private void OnMarkerRefreshTimerTick(object sender, EventArgs e)
        {
            try
            {
                _markerRefreshTimer?.Stop();
                if (_markerRefreshPending.Count == 0 || !_armed) { _markerRefreshPending.Clear(); return; }
                if (_markerRefreshInFlight || _fourViewsInFlight)
                {
                    // 上一批或一鍵 4 視圖還在跑：稍後再試（一鍵 4 視圖本身就會重畫標記）。
                    _markerRefreshTimer?.Start();
                    return;
                }
                List<ElementId> batch = _markerRefreshPending.ToList();
                _markerRefreshPending.Clear();
                _markerRefreshInFlight = true;
                Logger.Info($"相機管理面板：排入「更新相機標記」（{batch.Count} 台相機，去抖 500ms）。");
                int gen = _generation;
                EnqueueRevit(uiApp =>
                {
                    int n = 0;
                    try
                    {
                        Document doc = uiApp?.ActiveUIDocument?.Document;
                        if (doc != null && GetDocumentKey(doc) == _loadedDocKey)
                            n = CameraKeyPlanService.UpdateMarkersForCameras(doc, batch);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("相機管理面板：更新相機標記失敗", ex);
                        CameraManagerDiagnostics.RecordCaught("UpdateMarkersForCameras", ex);
                    }
                    finally
                    {
                        RunOnUiThread(() =>
                        {
                            _markerRefreshInFlight = false;
                            if (gen != _generation) return;
                            if (n > 0)
                            {
                                SetStatus($"已更新 {n} 台相機的 Key Plan 視野標記。");
                                // 選取中的相機若在這批裡，順手更新 ③ 位置圖上的剖面線（剖面本身不動，通常不變）。
                                CameraRow primary = GetPrimarySelectedRow();
                                if (primary != null && batch.Contains(primary.ViewId)) RefreshSelectedSectionLine(primary);
                            }
                            if (_markerRefreshPending.Count > 0) _markerRefreshTimer?.Start();
                        });
                    }
                }, "UpdateCameraMarkers");
            }
            catch (Exception ex)
            {
                _markerRefreshInFlight = false;
                Logger.Error("相機管理面板：相機標記去抖計時器失敗", ex);
            }
        }

        // ══════════════════════════════════════════════════════════
        // GridSplitter 拖曳位置持久化
        // ══════════════════════════════════════════════════════════

        private void OnSplitterDragCompleted()
        {
            try
            {
                double topHeight = _topRow.ActualHeight;
                double mapHeight = _mapRow.ActualHeight;
                double total = topHeight + mapHeight;
                if (total <= 1e-3) return;

                double fraction = ClampFraction(topHeight / total);
                _topRow.Height = new GridLength(fraction, GridUnitType.Star);
                _mapRow.Height = new GridLength(1 - fraction, GridUnitType.Star);

                ConfigManager.Instance.Settings.CameraManager.CameraListFractionV3 = fraction;
                ConfigManager.Instance.SaveSettings();
            }
            catch
            {
                // 拖曳結束後存檔失敗（例如設定檔一時寫入失敗）不應該讓面板壞掉，安靜略過即可。
            }
        }

        private static double ClampFraction(double fraction)
        {
            if (double.IsNaN(fraction) || double.IsInfinity(fraction)) return 0.35;
            return Math.Max(0.15, Math.Min(0.85, fraction));
        }

        // ══════════════════════════════════════════════════════════
        // 小工具
        // ══════════════════════════════════════════════════════════

        private void RunSuppressed(Action action)
        {
            _suppressLiveSync = true;
            action();
        }

        private void EndSuppressed() => _suppressLiveSync = false;

        private void SetStatus(string text) => RunOnUiThread(() => _statusText.Text = text);

        /// <summary>
        /// 2026-09-24 崩潰修復：所有從 ExternalEventManager／Application.cs Idling 回呼marshal
        /// 回 UI 執行緒的動作都經過這裡，也是整個面板自動刷新路徑（OnSnapshotReady、
        /// OnBaseMapReady…）唯一共用的入口——直接在這裡包 try/catch＋Logger.Error，一次涵蓋
        /// 所有既有呼叫點，不需要逐一改每個呼叫端。CheckAccess()==true 時是同步呼叫（呼叫端
        /// 本身可能還有自己的 try/catch，例如 ExternalEventManager.CommandEventHandler.Execute），
        /// 但 BeginInvoke 的非同步分支完全沒有任何外層 try/catch 可以接——那正是「WPF Dispatcher
        /// 未處理例外＝Revit 直接以 0xe0434352 崩潰」的路徑，兩個分支都要包。
        /// </summary>
        private void RunOnUiThread(Action action, [CallerMemberName] string caller = "")
        {
            if (action == null) return;
            // 2026-09-24 第二輪：一律非同步（Background 優先權）。過去在 ExternalEvent／Idling
            // 回呼裡 CheckAccess()==true 就「同步」執行，等於在 Revit 的 API 回呼堆疊裡直接做
            // DataGrid 換資料、Canvas 重畫、選取連鎖等 WPF 版面工作；現在一律等 Revit 回呼結束、
            // 版面／輸入處理完之後才做。
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => SafeInvoke(action, caller)));
            }
            catch (Exception ex)
            {
                Logger.Error("相機管理面板：排入 UI 回呼失敗（" + caller + "）", ex);
            }
        }

        private static void SafeInvoke(Action action, string caller)
        {
            using (CameraManagerDiagnostics.BeginOperation("ui:" + caller))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Logger.Error("相機管理面板：UI 執行緒回呼發生未處理例外（" + caller + "）", ex);
                    CameraManagerDiagnostics.RecordCaught("ui:" + caller, ex);
                }
            }
        }

        /// <summary>排入需要 Revit API context 的動作（相機管理專用佇列，見 CameraManagerEventQueue）。</summary>
        private static void EnqueueRevit(Action<UIApplication> action, [CallerMemberName] string caller = "")
        {
            CameraManagerEventQueue.Enqueue(caller, action);
        }

        /// <summary>簡易 placeholder（標題列搜尋框用）：空白時顯示灰字提示，聚焦時清空。</summary>
        private static void ApplyPlaceholder(WpfTextBox box, string placeholder)
        {
            box.Text = placeholder;
            box.Foreground = CameraManagerTheme.SecondaryText;
            box.GotFocus += (s, e) =>
            {
                if (box.Text == placeholder)
                {
                    box.Text = "";
                    box.Foreground = CameraManagerTheme.Text;
                }
            };
            box.LostFocus += (s, e) =>
            {
                if (string.IsNullOrEmpty(box.Text))
                {
                    box.Text = placeholder;
                    box.Foreground = CameraManagerTheme.SecondaryText;
                }
            };
        }
    }
}
