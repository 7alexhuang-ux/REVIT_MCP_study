using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System.Reflection;
using RevitMCP.Core;
using RevitMCP.Configuration;
using RevitMCP.UI;

namespace RevitMCP
{
    public class Application : IExternalApplication
    {
        private static SocketService _socketService;
        private static UIApplication _uiApp;
        private static bool _startingSocket;
        private UIControlledApplication _startupApplication;
        private static CameraManagerPanelControl _cameraManagerPanel;

        // ── 相機管理面板：2026-09-24 崩潰修復（見 docs/camera-manager/adr.zh-TW.md D-008、spec.zh-TW.md §3.1）。
        // DocumentChanged／ViewActivated 本身只負責「判斷這次變更跟相機管理有沒有關係」＋
        // 設一個 dirty flag，真正的刷新一律延後到下一次 Idling 才做（那時 Revit 保證已經完全
        // 閒置：文件開啟流程、Project Browser、UI chrome 都已經穩定下來），避免在文件開啟／
        // 視圖啟用這種還不穩定的時間點上，同步做 DataGrid 重繪、Canvas 重畫、ExportImage
        // 這些重量級 WPF／Revit API 操作。
        private static bool _cameraManagerRefreshPending;
        private static UIApplication _cameraManagerIdlingUiApp;

        // ── 2026-09-24 第二輪崩潰調查：面板改「懶載入」。開檔、ViewActivated、面板 Loaded 一律不讀、
        // 不匯出、不畫；只有使用者在「目前這份文件」上按過重新整理（panel.IsLiveSyncArmed）之後，
        // DocumentChanged 才會觸發輕量的清單更新。ViewActivated 只用來偵測「作用中文件換了」，
        // 換了就把面板清回中性狀態（見 OnCameraManagerIdling）。
        private static bool _cameraManagerDocCheckPending;
        /// <summary>2026-09-25：ViewActivated 發生過 → 下次 Idling 檢查作用中視圖是否為透視相機，是就讓 ③ 位置圖跟隨。</summary>
        private static bool _cameraManagerActiveViewPending;

        public static SocketService SocketService => _socketService;
        public static UIApplication UIApp => _uiApp;

        public Result OnStartup(UIControlledApplication application)
        {
            string autostartMarker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".revit-mcp", "autostart");
            bool automatic = File.Exists(autostartMarker);
            try
            {
                // 2026-09-24 第二輪崩潰調查：相機管理崩潰診斷（AppDomain.UnhandledException／
                // FirstChanceException／UnobservedTaskException，同步直寫
                // %APPDATA%\RevitMCP\Logs\CameraManagerCrash_yyyyMMdd.log）。放在最前面，確保之後
                // 任何初始化失敗也記得到。
                CameraManagerDiagnostics.Install();

                // 初始化配置管理器
                _ = ConfigManager.Instance;
                
                // 記錄啟動
                Logger.Info("RevitMCP Plugin 正在啟動...");

                // 建立功能區面板
                RibbonPanel panel = application.CreateRibbonPanel("MCP Tools");
                
                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                // 1. MCP 服務切換按鈕 (Toggle)
                PushButtonData toggleButtonData = new PushButtonData(
                    "MCPToggle",
                    "MCP 服務\n(開/關)",
                    assemblyPath,
                    "RevitMCP.Commands.ToggleServiceCommand");
                toggleButtonData.ToolTip = "啟動或停止 MCP WebSocket 服務";
                panel.AddItem(toggleButtonData);

                // 2. 開啟日誌按鈕
                PushButtonData logButtonData = new PushButtonData(
                    "OpenLog",
                    "開啟\n日誌",
                    assemblyPath,
                    "RevitMCP.Commands.OpenLogCommand");
                logButtonData.ToolTip = "開啟 MCP 執行日誌";
                panel.AddItem(logButtonData);

                // 3. 設定按鈕
                PushButtonData settingsButtonData = new PushButtonData(
                    "MCPSettings",
                    "MCP\n設定",
                    assemblyPath,
                    "RevitMCP.Commands.SettingsCommand");
                settingsButtonData.ToolTip = "開啟 MCP 設定視窗";
                panel.AddItem(settingsButtonData);

                // 4–5. 網頁配對（issue-manager 網頁 3D 視角／剖面聯動，D-710）
                PushButtonData pairButtonData = new PushButtonData(
                    "MCPWebPair",
                    "網頁\n配對",
                    assemblyPath,
                    "RevitMCP.Commands.WebPairCommand");
                pairButtonData.ToolTip = "顯示 6 位數配對碼（5 分鐘有效），在議題管理網頁輸入後，網頁才能把 3D 視角／剖面送進這台電腦的 Revit";
                panel.AddItem(pairButtonData);

                PushButtonData revokeButtonData = new PushButtonData(
                    "MCPWebRevoke",
                    "撤銷\n網頁配對",
                    assemblyPath,
                    "RevitMCP.Commands.WebRevokeCommand");
                revokeButtonData.ToolTip = "撤銷所有已配對的網頁裝置（網頁需重新配對才能再連線）";
                panel.AddItem(revokeButtonData);

#if REVIT2024_OR_GREATER
                // 整地圖說面板（Revit 2024+），掛在自訂頁籤「黃道資訊」：
                // 核心入口（新整地方案／方案總覽）優先，樓板準備備板工具其後。
                // 2026-07-10：整地檢視／六級坡度／比較出圖／各別出圖 全部併入「方案總覽」清單動作，
                // 不再是主面板獨立按鈕（見 docs/superpowers/specs/2026-07-10-grading-ribbon-consolidation-design.md）。
                application.CreateRibbonTab("黃道資訊");
                RibbonPanel gradingPanel = application.CreateRibbonPanel("黃道資訊", "整地圖說");
                AddGradingButton(gradingPanel, assemblyPath, "NewGradingScheme", "新整地\n方案",
                    "RevitMCP.Commands.NewGradingSchemeCommand",
                    "選取一片地形＋控制樓板後，選模式（投影直貼/外延漸變/放坡銜接）與參數，執行並記錄一筆方案",
                    "new");
                AddGradingButton(gradingPanel, assemblyPath, "SchemeOverview", "方案\n總覽",
                    "RevitMCP.Commands.SchemeOverviewCommand",
                    "方案儀表板：看地形/開啟方案視圖/產生分析圖/出圖/還原/刪除/匯出，一個視窗總管全部方案",
                    "overview");
                gradingPanel.AddSeparator();
                AddGradingButton(gradingPanel, assemblyPath, "DuplicateFloorDraft", "複製\n樓板",
                    "RevitMCP.Commands.DuplicateFloorDraftCommand",
                    "複製選取的樓板，並在目前視圖隱藏原本的樓板，方便在複製出來的樓板上調整下一個方案的範圍",
                    "duplicate");
                AddGradingButton(gradingPanel, assemblyPath, "FloorElevation", "板高程\n斜板",
                    "RevitMCP.Commands.FloorElevationCommand",
                    "選取樓板後設定平板偏移、改斜板（點低側邊＋坡率 1:n）或還原平板",
                    "elev");
                AddGradingButton(gradingPanel, assemblyPath, "EdgeSlope", "邊坡\n坡率",
                    "RevitMCP.Commands.EdgeSlopeCommand",
                    "點選樓板邊指定例外放坡坡率（如臨路無障礙 1:12），其餘邊用整地時的預設坡率",
                    "edge");
                AddGradingButton(gradingPanel, assemblyPath, "BalancedElevation", "平衡高程\n試算",
                    "RevitMCP.Commands.BalancedElevationCommand",
                    "新整地方案的進階替代做法：以選取地形＋樓板，二分法試算統一升降量使淨土方逼近目標",
                    "balance");
#endif

                // 5. 相機管理按鈕（開啟／顯示停靠面板，取代舊版無模式浮動視窗）
                PushButtonData cameraManagerButtonData = new PushButtonData(
                    "CameraManager",
                    "相機\n管理",
                    assemblyPath,
                    "RevitMCP.Commands.CameraManagerCommand");
                cameraManagerButtonData.ToolTip = "顯示相機管理停靠面板：檢視所有透視相機的離樓板高度、在平面上的位置，並可編輯、複製、匯出";
                panel.AddItem(cameraManagerButtonData);

                // 初始化 ExternalEventManager (必須在 UI 執行緒建立)
                _ = ExternalEventManager.Instance;
                // 相機管理面板專用的 ExternalEvent FIFO 佇列（不再與 MCP 命令共用
                // ExternalEventManager 的單一 _action 欄位，見 CameraManagerEventQueue 註解）。
                CameraManagerEventQueue.Initialize();

                // 註冊相機管理停靠面板（固定 GUID，見 CameraManagerPaneProvider）。
                // FrameworkElement 實例必須在這裡（OnStartup，Revit 主執行緒）建立好，
                // RegisterDockablePane 之後不會再重新建立這個實例。
                _cameraManagerPanel = new CameraManagerPanelControl();
                application.RegisterDockablePane(
                    CameraManagerPaneProvider.PaneId,
                    "相機管理",
                    new CameraManagerPaneProvider(_cameraManagerPanel));

                // 4.4 雙向連動：DocumentChanged／ViewActivated 都是 Revit 自己在 API context
                // 內觸發的事件，面板可以直接讀 Document，不需要再經 ExternalEventManager。
                // 2026-09-24 起：這兩個 handler 本身只設 dirty flag，實際刷新延後到 Idling
                // （見 OnCameraManagerIdling），詳見上方欄位註解與本次交付報告。
                application.ControlledApplication.DocumentChanged += OnCameraManagerDocumentChanged;
                application.ViewActivated += OnCameraManagerViewActivated;
                application.Idling += OnCameraManagerIdling;

                // 文件關閉前強制關掉所有還開著的「相機預覽視窗」（PreviewControl 內部持有
                // AdWindows/SxSActivationContext 這類原生資源，2026-09-24 崩潰調查發現的
                // finalizer-thread SEHException 就是這類物件沒被明確 Dispose、留給 GC 收尾
                // 導致——文件關閉是最後一個保證還能拿到有效 Document 的時間點，這裡主動收乾淨，
                // 不要等使用者自己記得關預覽視窗）。
                application.ControlledApplication.DocumentClosing += OnCameraManagerDocumentClosing;
                // 崩潰診斷：文件開啟後 60 秒內記錄所有第一機會例外（2026-09-24 的崩潰都發生在開檔
                // 後 5–10 秒、無使用者操作）。只是更新一個時間戳，不碰面板。
                application.ControlledApplication.DocumentOpened += OnCameraManagerDocumentOpened;
                // 2026-09-26 稽核：面板以「標題｜路徑」識別文件，另存新檔（含新專案第一次存檔）後鍵會變 →
                // 導覽監看、標記計時器、DocumentClosing 全部靜默失效。另存前記舊鍵、另存後改成新鍵。
                application.ControlledApplication.DocumentSavingAs += OnCameraManagerDocumentSavingAs;
                application.ControlledApplication.DocumentSavedAs += OnCameraManagerDocumentSavedAs;

                // 註冊 Process 退出事件 — Revit crash 時 OnShutdown 不會被呼叫，
                // 但 ProcessExit 仍有機會觸發，確保 HttpListener 被釋放，
                // 避免 HTTP.sys 孤兒 Request Queue 佔住 port。
                AppDomain.CurrentDomain.ProcessExit += (s, e) => EmergencyStopSocket();

                Logger.Info("RevitMCP Plugin 已成功載入");

                // 自動啟動只由使用者預先建立的 opt-in 標記啟用；外掛不自行建立標記或改 Add-ins 部署。
                if (automatic)
                {
                    _startupApplication = application;
                    application.Idling += StartMcpOnFirstIdle;
                    Logger.Info($"偵測到自動啟動標記，等待首次 Idling: {autostartMarker}");
                }
                else
                {
                    TaskDialog.Show("RevitMCP",
                        "RevitMCP Plugin 已載入\n\n" +
                        "請點擊「MCP 服務 (開/關)」按鈕來啟用 AI 控制功能");
                }
                
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("載入 MCP Tools 失敗", ex);
                if (!automatic) TaskDialog.Show("錯誤", "載入 MCP Tools 失敗: " + ex.Message);
                return Result.Failed;
            }
        }

        private async void StartMcpOnFirstIdle(object sender, IdlingEventArgs args)
        {
            // 先解除訂閱，失敗也不在每次 Idling 無限重試或重複建立 WebSocket listener。
            if (_startupApplication != null)
            {
                _startupApplication.Idling -= StartMcpOnFirstIdle;
                _startupApplication = null;
            }
            if (!(sender is UIApplication uiApp))
            {
                Logger.Error("MCP 自動啟動取消：Idling sender 不是 UIApplication。");
                return;
            }
            await StartMcpServiceAsync(uiApp, true);
        }

#if REVIT2024_OR_GREATER
        private static void AddGradingButton(
            RibbonPanel panel, string assemblyPath, string name, string text, string className,
            string tooltip, string iconKey)
        {
            var buttonData = new PushButtonData(name, text, assemblyPath, className) { ToolTip = tooltip };
            var large = LoadGradingIcon(iconKey + "_32.png");
            var small = LoadGradingIcon(iconKey + "_16.png");
            if (large != null) { buttonData.LargeImage = large; }
            if (small != null) { buttonData.Image = small; }
            panel.AddItem(buttonData);
        }

        /// <summary>從嵌入資源載入 ribbon 色磚圖示（Resources/icons/*.png）；找不到回 null（退回純文字，不影響按鈕）。</summary>
        private static System.Windows.Media.ImageSource LoadGradingIcon(string fileName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith(".icons." + fileName, StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
                if (resourceName == null) { return null; }

                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) { return null; }
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch
            {
                return null;
            }
        }
#endif

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.Idling -= StartMcpOnFirstIdle;
                _startupApplication = null;
                // 4.4 雙向連動：關掉外掛前一定要解除訂閱，否則 Revit 內部仍持有這個委派的參照，
                // 下次 reload（core-reload-boundary 情境）或關閉文件時可能對已釋放的物件觸發事件。
                try
                {
                    application.ControlledApplication.DocumentChanged -= OnCameraManagerDocumentChanged;
                    application.ViewActivated -= OnCameraManagerViewActivated;
                    application.Idling -= OnCameraManagerIdling;
                    application.ControlledApplication.DocumentClosing -= OnCameraManagerDocumentClosing;
                    application.ControlledApplication.DocumentOpened -= OnCameraManagerDocumentOpened;
                    application.ControlledApplication.DocumentSavingAs -= OnCameraManagerDocumentSavingAs;
                    application.ControlledApplication.DocumentSavedAs -= OnCameraManagerDocumentSavedAs;
                }
                catch (Exception ex)
                {
                    Logger.Error($"解除相機管理面板事件訂閱失敗: {ex.Message}");
                }

                // Revit 關閉前，跟文件關閉時一樣，把還開著的相機預覽視窗收乾淨。
                try
                {
                    _cameraManagerPanel?.CloseAllPreviewWindows();
                }
                catch (Exception ex)
                {
                    Logger.Error($"OnShutdown 關閉相機預覽視窗失敗: {ex.Message}");
                }

                EmergencyStopSocket();
                return Result.Succeeded;
            }
            catch
            {
                return Result.Failed;
            }
        }

        /// <summary>
        /// 4.4：Revit 文件有任何變更（包含使用者自己改相機、改樓層…）都會觸發。⚠️ 是否包含
        /// 「純滑鼠環繞透視圖」尚未經 Revit 2024 實機驗證——面板上另外保留「從透視圖同步」手動
        /// 按鈕當保險（見 docs/camera-manager/spec.zh-TW.md §9）。
        ///
        /// 2026-09-24 崩潰修復：這裡不再直接呼叫面板讀快照／刷新畫面——DocumentChanged 有可能
        /// 在文件開啟過程中被觸發（journal 證據：三次崩潰都發生在剛開檔、使用者完全沒有操作的
        /// 情況下），這時候同步做 ReadSnapshot／DataGrid 重繪／ExportImage 這些重量級操作，
        /// 疑似就是讓 Revit 帶著還沒穩定的內部狀態被打斷、最終以 0xe0434352（未處理 CLR 例外）
        /// 崩潰的根因。改成只判斷「這次變更跟相機管理有沒有關係」，有關係就設 dirty flag，
        /// 真正的刷新交給 <see cref="OnCameraManagerIdling"/> 在下一次 Idling（Revit 保證完全
        /// 閒置時）才執行。
        /// </summary>
        private static void OnCameraManagerDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            // 2026-09-25（§4.8 步驟 0）：記錄「相機是從哪張平面放置的」——不管面板有沒有啟用都要記。
            CaptureCameraSourcePlan(e);
            // 2026-09-26 第四輪：相機以外的元素變了 → 房間／樓層快取與底圖點陣快取失效（面板有沒有啟用都要記）。
            NoteCameraModelEpoch(e);
            // 2026-09-26 稽核：本工具自己改了相機（面板／MCP 編輯、還原版本、拖 A 標記）→ 導覽監看重抓基準，
            // 不把自己的修改當成使用者導覽（舊版 1 秒後再排一次後續更新，甚至誤判大幅移動而重算整個 Key Plan）。
            ResyncNavigationBaselineIfOwnCameraEdit(e);
            bool undoRedo = false;
            try
            {
                // 2026-09-26 稽核：Undo／Redo——「上次已畫」紀錄與快取不再可信 → 清掉（下次變動全部重畫）；
                // 並且這次不排任何會開交易的後續更新（標記重畫、反向同步、孤兒清理），否則新交易會清掉 Redo 堆疊，
                // 還會在使用者復原的同時又把標記／相機改回去。
                // 只認真正的復原／重做：TransactionGroup 回滾（底圖匯出每次都會回滾暫存視圖群組）不算。
                undoRedo = (e.Operation == UndoOperation.TransactionUndone || e.Operation == UndoOperation.TransactionRedone) &&
                           !RevitMCP.Core.CameraManagerService.IsExportingBaseMap && !RevitMCP.Core.CameraSourcePlanStore.IsWriting;
                if (undoRedo)
                {
                    RevitMCP.Core.CameraKeyPlanService.ClearPoseRecords();
                    RevitMCP.Core.CameraKeyPlanService.BumpModelEpoch();
                    // 復原若改到作用中相機，導覽監看會把它當成導覽而排更新（開交易＝清掉 Redo）→ 以復原後的姿態重建基準。
                    ResetNavigationWatch();
                }
            }
            catch { undoRedo = false; }
            try
            {
                // 使用者還沒按過重新整理＝live sync 未啟用，連過濾都不做（最便宜的早退）。
                if (_cameraManagerPanel == null || !_cameraManagerPanel.IsLiveSyncArmed) return;
                // 自己寫入放置平面記錄的交易不算使用者變更。
                if (RevitMCP.Core.CameraSourcePlanStore.IsWriting) return;
                // 底圖匯出自己建立／回滾的暫存視圖不算使用者變更，避免「匯出→DocumentChanged→刷新」迴圈。
                if (RevitMCP.Core.CameraManagerService.IsExportingBaseMap) return;
                // 2026-09-24 第四輪：我們自己的「更新相機標記」交易（DocumentChanged 在 Commit 時觸發）
                // 一律忽略，否則會「重畫標記 → DocumentChanged → 再排一次重畫」無限回饋。
                if (RevitMCP.Core.CameraKeyPlanService.IsUpdatingMarkers || (!undoRedo && IsOwnMarkerTransaction(e))) return;
                Document doc = e.GetDocument();
                if (!IsCameraManagerRelevantDocument(doc)) return;
                // 2026-09-26 稽核：只處理面板資料來源文件的變更——另一份文件的 ElementId 若排進面板佇列，
                // 會套到面板文件的同號元素上（兩份文件同時開著、MCP 改另一份時）。文件切換由 Idling 的中性化處理。
                if (CameraManagerPanelControl.GetDocumentKey(doc) != _cameraManagerPanel.LoadedDocumentKey) return;

                // 規格 §4.4：透視相機本身被修改（拖曳裁剪框改 FOV、移動／旋轉相機）→ 只排入
                // 「更新相機標記」（面板端 500ms 去抖、同一相機合併）。這裡只收集 Id，不修改模型。
                bool inPlaceOnly = false;
                try
                {
                    System.Collections.Generic.List<ElementId> perspIds = null;
                    System.Collections.Generic.List<ElementId> aMarkerIds = null;
                    bool levelChanged = false;
                    foreach (ElementId id in e.GetModifiedElementIds())
                    {
                        Element el = doc.GetElement(id);
                        if (el is View3D v3 && v3.IsPerspective && !v3.IsTemplate)
                            (perspIds ?? (perspIds = new System.Collections.Generic.List<ElementId>())).Add(id);
                        else if (el is Level) levelChanged = true; // 主體 Level（高程／名稱）→ 重畫 Key Plan 剖面的簡化樓層記號
                        // 2026-09-25 Wave 2（§10.12）：使用者移動／旋轉 A 型相機標記 → 反向更新相機（去抖）。
                        else if (el is FamilyInstance && RevitMCP.Core.CameraKeyPlanService.IsAMarkerInstance(el, out _))
                            (aMarkerIds ?? (aMarkerIds = new System.Collections.Generic.List<ElementId>())).Add(id);
                    }
                    if (undoRedo)
                    {
                        if (perspIds != null || aMarkerIds != null || levelChanged)
                            Logger.Info("相機管理面板：DocumentChanged＝Undo／Redo → 不排會開交易的標記／相機後續更新（保留 Redo），已清除「已畫」紀錄。");
                        perspIds = null; aMarkerIds = null; levelChanged = false;
                    }
                    // 2026-09-26 稽核：本工具的 Key Plan／標記顏色交易會動到樓層的視圖專屬設定（隱藏樓層標頭、基準範圍），
                    // Level 被列為已修改 → 不要因此再排一次「更新 Key Plan 樓層線」。
                    if (levelChanged && IsOwnKeyPlanStyleTransaction(e)) levelChanged = false;
                    if (perspIds != null) _cameraManagerPanel.QueueCameraMarkerRefresh(perspIds);
                    // 2026-09-26 第六輪（編輯快速路徑）：面板自己的「編輯相機」交易、只改了剛就地更新過清單列的相機
                    // → 清單已是最新，不再排 live sync 整份快照重讀（標記更新已在上一行排入去抖）。
                    try
                    {
                        System.Collections.Generic.ICollection<string> names = e.GetTransactionNames();
                        inPlaceOnly = perspIds != null && aMarkerIds == null && !levelChanged &&
                                      e.Operation == UndoOperation.TransactionCommitted &&
                                      e.GetAddedElementIds().Count == 0 && e.GetDeletedElementIds().Count == 0 &&
                                      perspIds.Count == e.GetModifiedElementIds().Count &&
                                      names != null && names.Count > 0 &&
                                      names.All(n => n == RevitMCP.Core.CameraManagerService.FastEditTransactionName) &&
                                      RevitMCP.Core.CameraManagerService.ConsumeInPlaceEdits(perspIds);
                    }
                    catch { inPlaceOnly = false; }
                    if (levelChanged) _cameraManagerPanel.QueueLevelMarkRefresh();
                    if (aMarkerIds != null) _cameraManagerPanel.QueueMarkerToCameraSync(aMarkerIds);
                    // 2026-09-25 Wave 2：有元素被刪除（可能是相機）→ 去抖後清掉「相機已不存在」的標記（A／總覽）。
                    if (!undoRedo && e.GetDeletedElementIds().Count > 0) _cameraManagerPanel.QueueOrphanMarkerCleanup();
                }
                catch (Exception exMarker)
                {
                    Logger.Error($"相機管理面板：收集透視相機變更失敗: {exMarker.Message}");
                }

                if (inPlaceOnly)
                {
                    Logger.Info("相機管理面板：DocumentChanged＝面板編輯快速路徑（清單列已就地更新），不排整份快照重讀。");
                    return;
                }
                if (!IsCameraManagerRelevantChange(doc, e)) return;

                Logger.Info("相機管理面板：DocumentChanged 命中相關元素變更，排入下次 Idling 刷新。");
                _cameraManagerRefreshPending = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"相機管理面板處理 DocumentChanged 失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 2026-09-25（§4.8 步驟 0）：新增的透視 View3D → 讀「當下」的作用中視圖；是 ViewPlan（樓層／天花／面積平面）
        /// 就排入 <see cref="RevitMCP.Core.CameraSourcePlanStore"/>，下一次 Idling 以 ES 寫到相機上（DocumentChanged
        /// 不可修改文件）。時序假設：放置相機的交易 Commit（觸發本事件）時 Revit 尚未切到新的 3D 視圖——
        /// log 會記下當時作用中視圖的 id／型別以便實機驗證。只讀 Id／型別，任何失敗只記 log。
        /// </summary>
        private static void CaptureCameraSourcePlan(DocumentChangedEventArgs e)
        {
            try
            {
                if (RevitMCP.Core.CameraSourcePlanStore.IsWriting || RevitMCP.Core.CameraManagerService.IsExportingBaseMap) return;
                Document doc = e.GetDocument();
                if (!IsCameraManagerRelevantDocument(doc)) return;
                System.Collections.Generic.List<ElementId> newCams = null;
                foreach (ElementId id in e.GetAddedElementIds())
                    if (doc.GetElement(id) is View3D v3 && v3.IsPerspective && !v3.IsTemplate &&
                        !RevitMCP.Core.CameraKeyPlanService.IsSheetPerspectiveCopy(v3)) // 2026-09-25：出圖用透視複本不是新相機
                        (newCams ?? (newCams = new System.Collections.Generic.List<ElementId>())).Add(id);
                if (newCams == null) return;

                UIApplication uiApp = _cameraManagerIdlingUiApp ?? _uiApp;
                UIDocument uiDoc = uiApp?.ActiveUIDocument;
                View av = null;
                try { if (uiDoc != null && uiDoc.Document.Equals(doc)) av = uiDoc.ActiveView; } catch { av = null; }
                string txn = "";
                try { txn = string.Join("／", e.GetTransactionNames()); } catch { }
                foreach (ElementId camId in newCams)
                {
                    string avText = av == null ? "(無／非此文件)" : $"「{av.Name}」id={av.Id.GetIdValue()} type={av.ViewType}";
                    if (av is ViewPlan vp && !vp.IsTemplate &&
                        (vp.ViewType == ViewType.FloorPlan || vp.ViewType == ViewType.CeilingPlan || vp.ViewType == ViewType.AreaPlan))
                    {
                        RevitMCP.Core.CameraSourcePlanStore.Enqueue(doc, camId, vp.Id);
                        Logger.Info($"相機管理：新增透視相機 id={camId.GetIdValue()}（交易「{txn}」），當下作用中視圖 {avText} → 排入記錄放置平面。");
                    }
                    else
                    {
                        Logger.Info($"相機管理：新增透視相機 id={camId.GetIdValue()}（交易「{txn}」），當下作用中視圖 {avText} 不是平面 → 不記錄放置平面。");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"相機管理：擷取相機放置平面失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 2026-09-26 第四輪（D-041）：模型 epoch。本工具自己的交易（標記、總覽、Key Plan、圖紙、版本…）與「只改了透視相機」的變更
        /// 不算；其他任何新增／修改／刪除 → epoch +1（房間／樓層判定快取與 ③ 底圖點陣快取以此判斷過期）。只讀型別，不改模型。
        /// </summary>
        private static void NoteCameraModelEpoch(DocumentChangedEventArgs e)
        {
            try
            {
                if (RevitMCP.Core.CameraManagerService.IsExportingBaseMap || RevitMCP.Core.CameraSourcePlanStore.IsWriting) return;
                if (RevitMCP.Core.CameraKeyPlanService.IsUpdatingMarkers || IsOwnMarkerTransaction(e)) return;
                try
                {
                    System.Collections.Generic.ICollection<string> names = e.GetTransactionNames();
                    if (names != null && names.Count > 0 && names.All(n => n == "建立相機 Key Plan" || n == "變更相機標記顏色")) return;
                }
                catch { }
                Document doc = e.GetDocument();
                if (!IsCameraManagerRelevantDocument(doc)) return;
                if (e.GetDeletedElementIds().Count > 0) { RevitMCP.Core.CameraKeyPlanService.BumpModelEpoch(); return; }
                var ids = e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).ToList();
                if (ids.Count > 200) { RevitMCP.Core.CameraKeyPlanService.BumpModelEpoch(); return; }
                foreach (ElementId id in ids)
                {
                    Element el = doc.GetElement(id);
                    if (el is View3D v3 && v3.IsPerspective) continue; // 相機姿態本身已在快取鍵裡
                    RevitMCP.Core.CameraKeyPlanService.BumpModelEpoch();
                    return;
                }
            }
            catch
            {
                try { RevitMCP.Core.CameraKeyPlanService.BumpModelEpoch(); } catch { }
            }
        }

        /// <summary>這次 DocumentChanged 是否只來自相機管理的「更新相機標記」交易（以交易名稱判斷）。</summary>
        private static bool IsOwnMarkerTransaction(DocumentChangedEventArgs e)
        {
            try
            {
                System.Collections.Generic.ICollection<string> names = e.GetTransactionNames();
                return names != null && names.Count > 0 &&
                       names.All(n => n == RevitMCP.Core.CameraKeyPlanService.MarkerUpdateTransactionName ||
                                      n == RevitMCP.Core.CameraKeyPlanService.LevelMarkUpdateTransactionName ||
                                      n == RevitMCP.Core.CameraKeyPlanService.AMarkerTransactionName ||
                                      n == RevitMCP.Core.CameraKeyPlanService.AFamilyLoadTransactionName ||
                                      n == RevitMCP.Core.CameraKeyPlanService.OverviewTransactionName ||
                                      n == RevitMCP.Core.CameraKeyPlanService.SheetTransactionName ||
                                      n == RevitMCP.Core.CameraKeyPlanService.VersionSaveTransactionName ||
                                      n == RevitMCP.Core.CameraKeyPlanService.VersionDeleteTransactionName ||
                                      n == RevitMCP.Core.CameraSourcePlanStore.TransactionName);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>2026-09-26 稽核：一鍵 4 視圖／標記顏色交易（會改到樓層的視圖專屬顯示，但不是使用者改樓層）。</summary>
        private static bool IsOwnKeyPlanStyleTransaction(DocumentChangedEventArgs e)
        {
            try
            {
                System.Collections.Generic.ICollection<string> names = e.GetTransactionNames();
                return names != null && names.Count > 0 &&
                       names.All(n => n == RevitMCP.Core.CameraKeyPlanService.KeyPlanTransactionName ||
                                      n == RevitMCP.Core.CameraKeyPlanService.MarkerColorTransactionName);
            }
            catch { return false; }
        }

        /// <summary>2026-09-26 稽核：本工具改相機姿態的交易（編輯相機／還原版本／由標記更新相機）→ 導覽監看重抓基準。</summary>
        private static void ResyncNavigationBaselineIfOwnCameraEdit(DocumentChangedEventArgs e)
        {
            try
            {
                if (_navCamId == null) return;
                System.Collections.Generic.ICollection<string> names = e.GetTransactionNames();
                if (names == null || names.Count == 0) return;
                if (names.Any(n => n == RevitMCP.Core.CameraManagerService.FastEditTransactionName ||
                                   n == RevitMCP.Core.CameraKeyPlanService.MarkerToCameraTransactionName ||
                                   (n != null && n.StartsWith(RevitMCP.Core.CameraKeyPlanService.VersionRestoreTransactionPrefix, StringComparison.Ordinal))))
                {
                    // 下一次 Idling 以「修改後」的姿態重新建立基準（不在這裡讀，DocumentChanged 內只做最少的事）。
                    ResetNavigationWatch();
                }
            }
            catch { }
        }

        private static string _savingAsOldDocKey;

        private static void OnCameraManagerDocumentSavingAs(object sender, Autodesk.Revit.DB.Events.DocumentSavingAsEventArgs e)
        {
            try { _savingAsOldDocKey = CameraManagerPanelControl.GetDocumentKey(e.Document); }
            catch { _savingAsOldDocKey = null; }
        }

        private static void OnCameraManagerDocumentSavedAs(object sender, Autodesk.Revit.DB.Events.DocumentSavedAsEventArgs e)
        {
            try
            {
                string oldKey = _savingAsOldDocKey;
                _savingAsOldDocKey = null;
                string newKey = CameraManagerPanelControl.GetDocumentKey(e.Document);
                if (oldKey == null || newKey == null || oldKey == newKey) return;
                CameraManagerPanelControl panel = _cameraManagerPanel;
                if (panel != null && panel.RekeyLoadedDocument(oldKey, newKey))
                    Logger.Info("相機管理面板：文件另存新檔 → 面板文件識別已更新（" + oldKey + " → " + newKey + "）。");
                ResetNavigationWatch();
                // 房間／姿態快取以路徑＋標題為鍵 → 另存後全部失效（重算一次即可）。
                RevitMCP.Core.CameraKeyPlanService.ResetDocumentCaches("另存新檔");
            }
            catch (Exception ex)
            {
                Logger.Error($"相機管理面板處理 DocumentSavedAs 失敗: {ex.Message}");
            }
        }

        private static void OnCameraManagerViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                // 2026-09-24 第二輪：ViewActivated 不再觸發刷新（過去開檔時就是它把整條自動刷新鏈
                // 拉起來）。只在 live sync 已啟用時，排一次「作用中文件是否換了」的檢查。
                if (_cameraManagerPanel == null || !_cameraManagerPanel.IsLiveSyncArmed) return;
                _cameraManagerDocCheckPending = true;
                _cameraManagerActiveViewPending = true; // 跟隨作用中透視圖（實際判斷延後到 Idling）
            }
            catch (Exception ex)
            {
                Logger.Error($"相機管理面板處理 ViewActivated 失敗: {ex.Message}");
            }
        }

        /// <summary>排除：沒有文件、族群文件、連結文件（連結文件不是使用者主動開的「目前文件」，
        /// 相機管理面板本來就只處理主文件）。任何屬性存取本身失敗（例如文件正在關閉的瞬間）都
        /// 視為「不相關」，保守跳過，不讓判斷本身變成新的例外來源。</summary>
        private static bool IsCameraManagerRelevantDocument(Document doc)
        {
            if (doc == null) return false;
            try
            {
                if (doc.IsFamilyDocument) return false;
                if (doc.IsLinked) return false;
            }
            catch
            {
                return false;
            }
            return true;
        }

        /// <summary>DocumentChanged 的變更集合裡有沒有相機管理面板真正在意的元素：透視相機／
        /// 等角 Key Plan 視圖（View3D）、樓層（Level）、平面／剖面 Key Plan 視圖（ViewPlan／
        /// ViewSection）。刪除的元素已經拿不到型別，保守一律視為相關（寧可多刷新一次，也不要漏掉
        /// 「使用者刪掉一台相機」這種必須反映到清單上的變更）。任何一步失敗都保守回傳 true，
        /// 交給下一次 Idling 刷新去處理，不要因為過濾邏輯本身出錯而整個漏掉真正的變更。</summary>
        private static bool IsCameraManagerRelevantChange(Document doc, DocumentChangedEventArgs e)
        {
            try
            {
                if (e.GetDeletedElementIds().Count > 0) return true;

                foreach (ElementId id in e.GetAddedElementIds().Concat(e.GetModifiedElementIds()))
                {
                    Element el = doc.GetElement(id);
                    if (el is View3D || el is ViewPlan || el is ViewSection || el is Level) return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 2026-09-24 崩潰修復：真正的相機管理面板刷新只在這裡發生——Idling 保證此刻 Revit
        /// 完全閒置（訊息佇列已清空、文件開啟／視圖切換的內部流程都已經跑完），不會再有「文件
        /// 還在開、UI chrome 還在初始化」這種不穩定狀態。同時利用 sender（Idling 事件的 sender
        /// 就是 UIApplication）補上一個不依賴使用者按過任何按鈕就能拿到的 UIApplication 參照，
        /// 用來查 DockablePane.IsShown()。
        /// 這個 handler 從 OnStartup 就永久訂閱，每次 Idling 都會被呼叫；沒有 pending 刷新時
        /// 只是檢查一個 bool，成本可忽略，是 Revit add-in 常見的 debounce 寫法。
        /// </summary>
        private static void OnCameraManagerIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            try
            {
                if (_cameraManagerIdlingUiApp == null) _cameraManagerIdlingUiApp = sender as UIApplication;
                // 2026-09-26 第四輪（使用者：Navigation Wheel 移動相機不會觸發 DocumentChanged）：作用中透視相機的導覽監看。
                try { CameraNavigationWatch(_cameraManagerIdlingUiApp ?? sender as UIApplication, e); }
                catch (Exception exNav) { Logger.Error($"相機管理面板：導覽監看失敗: {exNav.Message}"); }
                // 2026-09-25（§4.8 步驟 0）：把 DocumentChanged 排隊的「相機放置平面」寫進 ES（不受面板狀態影響）。
                if (RevitMCP.Core.CameraSourcePlanStore.HasPending)
                {
                    RevitMCP.Core.CameraSourcePlanStore.FlushPending();
                    // 寫入後讓面板（若已啟用）重讀一次，步驟 0 才拿得到記錄。
                    if (_cameraManagerPanel != null && _cameraManagerPanel.IsLiveSyncArmed) _cameraManagerRefreshPending = true;
                }
                if (!_cameraManagerRefreshPending && !_cameraManagerDocCheckPending && !_cameraManagerActiveViewPending) return;
                bool refresh = _cameraManagerRefreshPending;
                bool followActiveView = _cameraManagerActiveViewPending;
                _cameraManagerRefreshPending = false;
                _cameraManagerDocCheckPending = false;
                _cameraManagerActiveViewPending = false;

                CameraManagerPanelControl panel = _cameraManagerPanel;
                if (panel == null || !panel.IsLiveSyncArmed) return; // 使用者尚未按重新整理：什麼都不做

                UIApplication uiApp = _cameraManagerIdlingUiApp ?? sender as UIApplication;
                if (uiApp == null) return;

                using (CameraManagerDiagnostics.BeginOperation("idling-live-sync"))
                {
                    UIDocument uiDoc = uiApp.ActiveUIDocument;
                    Document doc = uiDoc?.Document;
                    if (!IsCameraManagerRelevantDocument(doc)) return;

                    // 作用中文件已不是面板資料的來源文件 → 清回中性狀態，等使用者自己再按重新整理
                    // （開檔／切換文件一律不自動讀取）。
                    string key = CameraManagerPanelControl.GetDocumentKey(doc);
                    if (key == null || key != panel.LoadedDocumentKey)
                    {
                        Logger.Info("相機管理面板：作用中文件已切換，面板回到中性狀態（不自動載入）。");
                        panel.ResetToNeutral("目前文件已切換，" + CameraManagerPanelControl.NeutralMessage + "。");
                        return;
                    }

                    // 2026-09-25：③ 位置圖跟隨作用中透視圖（專案瀏覽器開啟、一鍵 4 視圖、僅打開透視圖、點並排視窗）。
                    // 只讀 ActiveView 的 Id 與型別，其餘交給面板在 UI 執行緒做輕量更新；面板隱藏時不做。
                    if (followActiveView)
                    {
                        try
                        {
                            var swFollow = System.Diagnostics.Stopwatch.StartNew();
                            View av = uiDoc.ActiveView;
                            if (av is View3D av3 && av3.IsPerspective && !av3.IsTemplate && IsCameraManagerPaneShown(uiApp))
                                panel.OnActivePerspectiveChanged(av3.Id);
                            else if (!(av is View3D pv && pv.IsPerspective && !RevitMCP.Core.CameraKeyPlanService.IsSheetPerspectiveCopy(pv)))
                                panel.OnActiveViewIsNotCamera(); // 2026-09-26：清單「目前檢視中」標示清掉
                            Logger.Info($"⏱ 視圖啟動跟隨（Idling 端：讀作用中視圖＋排入 UI）{swFollow.ElapsedMilliseconds} ms（{av?.Name}）");
                        }
                        catch (Exception exFollow)
                        {
                            Logger.Error($"相機管理面板：跟隨作用中透視圖失敗: {exFollow.Message}");
                        }
                    }

                    if (!refresh) return; // 只是文件檢查，沒有相關變更

                    if (uiDoc.ActiveView == null)
                    {
                        Logger.Info("相機管理面板：live sync 略過（作用中視圖尚未就緒）。");
                        return;
                    }

                    DockablePane pane;
                    try
                    {
                        pane = uiApp.GetDockablePane(CameraManagerPaneProvider.PaneId);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"相機管理面板：查詢停靠面板顯示狀態失敗: {ex.Message}");
                        return;
                    }
                    if (pane == null || !pane.IsShown())
                    {
                        // 面板隱藏時不讀；等使用者再打開面板自己按重新整理（清單可能已過時，
                        // 狀態列不會自動更新——這是刻意的安全取捨）。
                        return;
                    }

                    panel.OnRevitDocumentEvent(doc);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"相機管理面板 Idling 刷新失敗: {ex.Message}");
                CameraManagerDiagnostics.RecordCaught("OnCameraManagerIdling", ex);
            }
        }

        // ══════════════════════════════════════════════════════════
        // 2026-09-26 第四輪（D-042）：導覽監看——Navigation Wheel／繞行／ViewCube 移動作用中透視相機時 Revit 不發 DocumentChanged，
        // 所以在 Idling 比對姿態雜湊：每 ≥ 300 ms 讀一次（只讀方位＋裁剪框，便宜）；變了就記下時間並要求 Idling 立即再來
        // （SetRaiseWithoutDelay，只在「變了、還沒停住」的這段期間）；停住 1 秒不再變 → 排入面板的相機更新
        // （標記／扇形；移動 > 300 mm 或轉 > 5° 另重算 Key Plan）。使用者還在動的期間不開任何交易。
        // ══════════════════════════════════════════════════════════
        private static ElementId _navCamId;
        private static string _navDocKey;
        private static string _navHash, _navBaseHash;
        private static XYZ _navBaseEye;
        private static double _navBaseYaw;
        private static RevitMCP.Core.CameraKeyPlanService.CameraViewVersion _navBaseVersion;
        private static DateTime _navLastCheck = DateTime.MinValue, _navLastChange = DateTime.MinValue;
        private static bool _navDirty;
        private const int NavCheckIntervalMs = 300;
        private const int NavSettleMs = 1000;

        private static void ResetNavigationWatch()
        {
            _navCamId = null; _navDocKey = null; _navHash = null; _navBaseHash = null; _navBaseEye = null;
            _navBaseVersion = null; _navDirty = false;
        }

        private static void CameraNavigationWatch(UIApplication uiApp, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            CameraManagerPanelControl panel = _cameraManagerPanel;
            if (panel == null || !panel.IsLiveSyncArmed || uiApp == null) { if (_navCamId != null) ResetNavigationWatch(); return; }
            DateTime now = DateTime.UtcNow;
            if ((now - _navLastCheck).TotalMilliseconds < NavCheckIntervalMs)
            {
                if (_navDirty) e.SetRaiseWithoutDelay();
                return;
            }
            _navLastCheck = now;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            if (!IsCameraManagerRelevantDocument(doc)) { ResetNavigationWatch(); return; }
            if (!(uiDoc.ActiveView is View3D cam) || !cam.IsPerspective || cam.IsTemplate || RevitMCP.Core.CameraKeyPlanService.IsSheetPerspectiveCopy(cam))
            {
                if (_navCamId != null) ResetNavigationWatch();
                return;
            }
            string docKey = CameraManagerPanelControl.GetDocumentKey(doc);
            if (docKey == null || docKey != panel.LoadedDocumentKey) { ResetNavigationWatch(); return; }
            // 2026-09-26 稽核：面板隱藏時不監看（不在背景持續開交易改標記；再打開面板後重新建立基準）。
            if (!IsCameraManagerPaneShown(uiApp)) { if (_navCamId != null) ResetNavigationWatch(); return; }
            // 本工具正在寫入（標記／還原／編輯）時不比對，避免把自己的修改當成使用者導覽。
            if (RevitMCP.Core.CameraKeyPlanService.IsUpdatingMarkers) return;

            string hash = RevitMCP.Core.CameraKeyPlanService.ComputePoseHash(cam);
            if (_navCamId == null || _navCamId != cam.Id || _navDocKey != docKey)
            {
                _navCamId = cam.Id; _navDocKey = docKey;
                _navHash = _navBaseHash = hash;
                SetNavBaseline(cam);
                _navDirty = false;
                return;
            }
            if (hash != _navHash)
            {
                _navHash = hash;
                _navLastChange = now;
                _navDirty = true;
                e.SetRaiseWithoutDelay();
                return;
            }
            if (!_navDirty) return;
            if ((now - _navLastChange).TotalMilliseconds < NavSettleMs) { e.SetRaiseWithoutDelay(); return; }
            _navDirty = false;
            if (hash == _navBaseHash) return; // 動了又回到原位
            var hint = new RevitMCP.Core.CameraKeyPlanService.CameraPoseHint
            {
                Eye = _navBaseEye, YawDeg = _navBaseYaw, Before = _navBaseVersion, Source = "導覽（Navigation Wheel 等）"
            };
            Logger.Info($"相機管理面板：導覽監看——「{cam.Name}」姿態已改變且停住 {NavSettleMs} ms → 排入相機更新（DocumentChanged 不會通知這類變動）。");
            panel.QueueCameraUpdate(cam.Id, hint);
            _navBaseHash = hash;
            SetNavBaseline(cam);
        }

        private static void SetNavBaseline(View3D cam)
        {
            try
            {
                ViewOrientation3D o = cam.GetOrientation();
                _navBaseEye = o.EyePosition;
                _navBaseYaw = RevitMCP.Core.CameraMath.ComputeHorizontalAngleDeg(o.ForwardDirection);
                _navBaseVersion = RevitMCP.Core.CameraKeyPlanService.CaptureVersion(cam, RevitMCP.Core.CameraKeyPlanService.OriginalVersionName);
            }
            catch (Exception ex)
            {
                _navBaseEye = null; _navBaseVersion = null;
                Logger.Error($"相機管理面板：導覽監看記錄基準失敗: {ex.Message}");
            }
        }

        private static bool IsCameraManagerPaneShown(UIApplication uiApp)
        {
            try
            {
                DockablePane pane = uiApp?.GetDockablePane(CameraManagerPaneProvider.PaneId);
                return pane != null && pane.IsShown();
            }
            catch (Exception ex)
            {
                Logger.Error($"相機管理面板：查詢停靠面板顯示狀態失敗: {ex.Message}");
                return false;
            }
        }

        private static void OnCameraManagerDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
            try
            {
                string title = null;
                try { title = e.Document?.Title; } catch { }
                CameraManagerDiagnostics.ExtendWatch(60, "DocumentOpened: " + (title ?? "?"));
                // 2026-09-26 稽核：同名／同路徑的文件重新開啟 → 以路徑＋標題為鍵的快取一律作廢（舊 Room 物件屬於已關閉文件）。
                // 族群文件（例如本工具建立 A 標記族群時開的暫存族群檔）與連結文件不算。
                if (IsCameraManagerRelevantDocument(e.Document))
                    RevitMCP.Core.CameraKeyPlanService.ResetDocumentCaches("開啟");
            }
            catch { }
        }

        /// <summary>文件關閉前收乾淨相機預覽視窗（見 OnStartup 訂閱處註解）。</summary>
        private static void OnCameraManagerDocumentClosing(object sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
        {
            try
            {
                // 2026-09-26 稽核：族群文件（本工具建 A 標記族群時 fam.Close）與連結文件的關閉不影響面板與快取。
                if (!IsCameraManagerRelevantDocument(e.Document)) return;
                _cameraManagerPanel?.CloseAllPreviewWindows();
                // 2026-09-26 稽核：快取的 Room／姿態紀錄屬於這份文件 → 關閉時清掉；導覽監看也放掉這份文件的相機。
                RevitMCP.Core.CameraKeyPlanService.ResetDocumentCaches("關閉");
                ResetNavigationWatch();

                // 關掉的是面板資料的來源文件 → 清回中性狀態（非同步、純 WPF）。
                CameraManagerPanelControl panel = _cameraManagerPanel;
                if (panel != null && panel.IsLiveSyncArmed)
                {
                    string key = CameraManagerPanelControl.GetDocumentKey(e.Document);
                    if (key != null && key == panel.LoadedDocumentKey)
                        panel.ResetToNeutral(CameraManagerPanelControl.NeutralMessage);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"相機管理面板處理 DocumentClosing 失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 緊急釋放 Socket 服務。同時被 OnShutdown 和 ProcessExit 呼叫，
        /// 內部有 null check 防止重複執行。
        /// </summary>
        private static void EmergencyStopSocket()
        {
            try
            {
                if (_socketService != null)
                {
                    _socketService.Stop();
                    _socketService = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"EmergencyStopSocket failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 啟動 MCP 服務
        /// </summary>
        public static void StartMCPService(UIApplication uiApp)
        {
            _ = StartMcpServiceAsync(uiApp, false);
        }

        private static async Task StartMcpServiceAsync(UIApplication uiApp, bool automatic)
        {
            if (_startingSocket) return;
            _startingSocket = true;
            try
            {
                _uiApp = uiApp;
                var settings = ConfigManager.Instance.Settings;

                if (_socketService != null && _socketService.IsRunning)
                {
                    if (!automatic) TaskDialog.Show("MCP 服務", "服務已在執行中。");
                    return;
                }

                // 環境變數僅覆寫這次 listener，不把 port/automatic 狀態寫入使用者設定檔。
                var runtimeSettings = new ServiceSettings
                {
                    ConfigVersion = settings.ConfigVersion, Host = settings.Host, Port = settings.Port,
                    IsEnabled = settings.IsEnabled, ReconnectInterval = settings.ReconnectInterval,
                    CommandTimeout = settings.CommandTimeout
                };
                var portOverride = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
                if (!string.IsNullOrWhiteSpace(portOverride))
                {
                    if (!int.TryParse(portOverride, out var port) || port < 1024 || port > 65535)
                        throw new ArgumentException("REVIT_MCP_PORT 必須是 1024–65535 的整數；服務未啟動。");
                    runtimeSettings.Port = port;
                }
                _socketService = new SocketService(runtimeSettings);

                // 訂閱命令接收事件
                _socketService.CommandReceived += OnCommandReceived;

                // 啟動服務
                // 自動啟動不顯示對話框、不終止佔用 port 的程序，也不執行 netsh 修復。
                await _socketService.StartAsync(showDialogs: !automatic, repairPortConflicts: !automatic);
                if (!_socketService.IsRunning)
                {
                    Logger.Error($"MCP {(automatic ? "自動" : "手動")}啟動失敗，listener 未就緒；port={runtimeSettings.Port}");
                    return;
                }
                Logger.Info($"MCP {(automatic ? "自動" : "手動")}啟動就緒，port={runtimeSettings.Port}");

                // 更新設定
                if (!automatic)
                {
                    settings.IsEnabled = true;
                    ConfigManager.Instance.SaveSettings();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("啟動 MCP 服務失敗", ex);
                if (!automatic) TaskDialog.Show("錯誤", $"啟動 MCP 服務失敗: {ex.Message}");
            }
            finally { _startingSocket = false; }
        }

        /// <summary>
        /// 停止 MCP 服務
        /// </summary>
        public static void StopMCPService()
        {
            try
            {
                if (_socketService != null)
                {
                    _socketService.Stop();
                    _socketService = null;
                }

                var settings = ConfigManager.Instance.Settings;
                settings.IsEnabled = false;
                ConfigManager.Instance.SaveSettings();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("錯誤", $"停止 MCP 服務失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 處理接收到的命令
        /// </summary>
        private static async void OnCommandReceived(object sender, Models.RevitCommandRequest request)
        {
            // 使用外部事件在 Revit UI 執行緒執行命令
            ExternalEventManager.Instance.ExecuteCommand((uiApp) =>
            {
                try
                {
                    var executor = new CommandExecutor(uiApp  );
                    var response = executor.ExecuteCommand(request);

                    // 發送回應
                    _socketService?.SendResponseAsync(response, request.ConnectionId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var errorResponse = new Models.RevitCommandResponse
                    {
                        Success = false,
                        Error = ex.Message,
                        RequestId = request.RequestId
                    };

                    _socketService?.SendResponseAsync(errorResponse, request.ConnectionId).ConfigureAwait(false);
                }
            });
        }
    }
}
