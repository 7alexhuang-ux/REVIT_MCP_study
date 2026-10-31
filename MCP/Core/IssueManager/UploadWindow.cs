#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;
// Nice3point SDK 的全域 using 帶入 Autodesk.Revit.DB，下列名稱會與 WPF 同名，明確指向 WPF。
using Grid = System.Windows.Controls.Grid;
using Binding = System.Windows.Data.Binding;
using WpfVisibility = System.Windows.Visibility;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>勾選視窗需要的外部服務（Revit 與真實網路都從這裡注入，測試時換成假的）。</summary>
    public sealed class UploadWindowServices
    {
        /// <summary>網址未設定或不合法時的說明；不為 null 時視窗只顯示這段說明、不能登入。</summary>
        public string ConfigurationError { get; set; }
        public AddinAuthService Auth { get; set; }
        public AddinApiClient Api { get; set; }
        public IBrowserLauncher Browser { get; set; }
        public Func<UploadFlow> CreateFlow { get; set; }
        public Action<string> OpenUrl { get; set; }
        public Action<string> Log { get; set; }
        /// <summary>
        /// 關閉視窗時還有可重試的上傳（伺服器端 bundle 與本機暫存檔都還在）：詢問是否放棄並清除。
        /// 回傳 false＝不關視窗。null＝不詢問，直接放棄並清除。
        /// </summary>
        public Func<bool> ConfirmDiscardOnClose { get; set; }
    }

    /// <summary>
    /// 「上傳到 Issue Manager」勾選視窗（非模態；Revit 單執行緒，匯出期間靠 Pump 更新畫面與處理取消）。
    /// 欄位與規則見 docs/spec/revit-addin-export.md §2（Issue Manager repo）與本 repo 的 docs/ISSUE_MANAGER_UPLOAD.md。
    /// 這個類別不引用 Revit API，所以能在單元測試中建構並檢查控制項狀態。
    /// </summary>
    public sealed class IssueManagerUploadWindow : Window
    {
        private readonly ExportSelectionViewModel _vm;
        private readonly UploadWindowServices _services;
        private UploadFlow _flow;
        private CancellationTokenSource _cts;
        private bool _running;
        private bool _loggingIn;
        private int _runGeneration;

        private readonly TextBlock _accountText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _loginButton = MakeButton(Strings.Login);
        private readonly Button _logoutButton = MakeButton(Strings.Logout);
        private readonly Button _switchButton = MakeButton(Strings.SwitchAccount);
        private readonly ComboBox _projectBox = new ComboBox { MinWidth = 280 };
        private readonly ComboBox _stageBox = new ComboBox { MinWidth = 280 };
        private readonly TextBox _descriptionBox = new TextBox { MinHeight = 48, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly ComboBox _sheetSetBox = new ComboBox { MinWidth = 280 };
        private readonly TreeView _tree = new TreeView { MinHeight = 160 };
        private readonly CheckBox _modelBox = new CheckBox { Content = "匯出 3D 模型（GLB）", Margin = new Thickness(0, 4, 0, 0) };
        private readonly CheckBox _paramsBox = new CheckBox { Content = Strings.IncludeParamsText, Margin = new Thickness(0, 4, 0, 0), IsChecked = true };
        private readonly CheckBox _optionsBox = new CheckBox { Content = "含設計選項（未勾＝只含主模型與各選項集合的主要選項）", Margin = new Thickness(18, 2, 0, 0) };
        private readonly TextBlock _summaryText = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _warningText = new TextBlock { Foreground = System.Windows.Media.Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly ProgressBar _progress = new ProgressBar { Height = 14, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 4, 0, 0), Visibility = WpfVisibility.Collapsed };
        private readonly TextBlock _resultText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        private readonly Button _uploadButton = MakeButton("上傳");
        private readonly Button _retryButton = MakeButton("重試上傳");
        private readonly Button _cancelButton = MakeButton("取消");

        public IssueManagerUploadWindow(ExportSelectionViewModel vm, UploadWindowServices services)
        {
            _vm = vm ?? throw new ArgumentNullException("vm");
            _services = services ?? throw new ArgumentNullException("services");
            Title = Strings.WindowTitle;
            Width = 620;
            Height = 780;
            MinWidth = 520;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            DataContext = _vm;

            BuildLayout();
            WireEvents();
            RefreshAccount();
            RefreshSummary();
        }

        /// <summary>登入、匯出或上傳進行中。</summary>
        public bool IsBusy { get { return _running; } }

        /// <summary>測試與外部程式可讀的狀態。</summary>
        public Button UploadButton { get { return _uploadButton; } }
        public Button LoginButton { get { return _loginButton; } }
        public Button RetryButton { get { return _retryButton; } }
        public Button CancelButton { get { return _cancelButton; } }
        public Button LogoutButton { get { return _logoutButton; } }
        public Button SwitchAccountButton { get { return _switchButton; } }
        public TextBlock ResultText { get { return _resultText; } }
        public TextBlock SummaryText { get { return _summaryText; } }
        public TextBlock WarningText { get { return _warningText; } }
        public TextBlock StatusText { get { return _statusText; } }
        public TextBlock AccountText { get { return _accountText; } }
        public ComboBox StageBox { get { return _stageBox; } }
        public ComboBox ProjectBox { get { return _projectBox; } }
        public ComboBox SheetSetBox { get { return _sheetSetBox; } }
        public TreeView Tree { get { return _tree; } }
        public CheckBox ModelBox { get { return _modelBox; } }
        public CheckBox OptionsBox { get { return _optionsBox; } }
        public CheckBox ParamsBox { get { return _paramsBox; } }

        // ── 版面 ──────────────────────────────────────

        private static Button MakeButton(string text)
        {
            return new Button { Content = text, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        }

        private void BuildLayout()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 登入列
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 表單
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 樹
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 模型、摘要、狀態
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 按鈕

            var loginRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
            DockPanel.SetDock(_accountText, Dock.Left);
            loginRow.Children.Add(_accountText);
            var loginButtons = new StackPanel { Orientation = Orientation.Horizontal };
            loginButtons.Children.Add(_loginButton);
            loginButtons.Children.Add(_logoutButton);
            loginButtons.Children.Add(_switchButton);
            DockPanel.SetDock(loginButtons, Dock.Right);
            loginRow.Children.Add(loginButtons);
            Grid.SetRow(loginRow, 0);
            root.Children.Add(loginRow);

            var form = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddFormRow(form, 0, "專案", _projectBox);
            AddFormRow(form, 1, "設計階段", _stageBox);
            AddFormRow(form, 2, "版本說明", _descriptionBox);
            AddFormRow(form, 3, "圖紙集", _sheetSetBox);
            Grid.SetRow(form, 1);
            root.Children.Add(form);

            var treePanel = new DockPanel();
            var treeButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var selectAll = MakeButton("全選圖紙");
            selectAll.Click += (s, e) => _vm.SelectAllSheets(true);
            var selectNone = MakeButton("全不選");
            selectNone.Click += (s, e) => _vm.SelectAllSheets(false);
            treeButtons.Children.Add(selectAll);
            treeButtons.Children.Add(selectNone);
            DockPanel.SetDock(treeButtons, Dock.Top);
            treePanel.Children.Add(treeButtons);
            treePanel.Children.Add(_tree);
            Grid.SetRow(treePanel, 2);
            root.Children.Add(treePanel);

            var lower = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            lower.Children.Add(_modelBox);
            lower.Children.Add(_optionsBox);
            lower.Children.Add(_paramsBox);
            lower.Children.Add(_summaryText);
            lower.Children.Add(_warningText);
            lower.Children.Add(_statusText);
            lower.Children.Add(_progress);
            lower.Children.Add(_resultText);
            Grid.SetRow(lower, 3);
            root.Children.Add(lower);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(_retryButton);
            buttons.Children.Add(_uploadButton);
            buttons.Children.Add(_cancelButton);
            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);

            Content = root;

            _projectBox.DisplayMemberPath = "Label";
            _stageBox.DisplayMemberPath = "Label";
            _sheetSetBox.DisplayMemberPath = "Label";
            _retryButton.Visibility = WpfVisibility.Collapsed;
            Bind(_projectBox, ItemsControl.ItemsSourceProperty, "Projects", BindingMode.OneWay);
            Bind(_projectBox, System.Windows.Controls.Primitives.Selector.SelectedItemProperty, "SelectedProject", BindingMode.TwoWay);
            Bind(_stageBox, ItemsControl.ItemsSourceProperty, "Stages", BindingMode.OneWay);
            Bind(_stageBox, System.Windows.Controls.Primitives.Selector.SelectedItemProperty, "SelectedStage", BindingMode.TwoWay);
            Bind(_descriptionBox, TextBox.TextProperty, "Description", BindingMode.TwoWay, UpdateSourceTrigger.PropertyChanged);
            Bind(_sheetSetBox, ItemsControl.ItemsSourceProperty, "SheetSets", BindingMode.OneWay);
            Bind(_sheetSetBox, System.Windows.Controls.Primitives.Selector.SelectedItemProperty, "SelectedSheetSet", BindingMode.TwoWay);
            Bind(_modelBox, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "IncludeModel", BindingMode.TwoWay);
            Bind(_optionsBox, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "IncludeDesignOptions", BindingMode.TwoWay);
            Bind(_optionsBox, IsEnabledProperty, "DesignOptionsEnabled", BindingMode.OneWay);
            Bind(_paramsBox, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "IncludeParams", BindingMode.TwoWay);
            Bind(_uploadButton, ToolTipProperty, "BlockReason", BindingMode.OneWay);

            BuildTree();
        }

        private static void AddFormRow(Grid form, int row, string label, UIElement control)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 6) };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            form.Children.Add(text);
            var host = new Border { Margin = new Thickness(0, 0, 0, 6), Child = control };
            Grid.SetRow(host, row);
            Grid.SetColumn(host, 1);
            form.Children.Add(host);
        }

        private void Bind(DependencyObject target, DependencyProperty property, string path, BindingMode mode,
            UpdateSourceTrigger trigger = UpdateSourceTrigger.Default)
        {
            BindingOperations.SetBinding(target, property, new Binding(path) { Source = _vm, Mode = mode, UpdateSourceTrigger = trigger });
        }

        private void BuildTree()
        {
            _tree.Items.Clear();
            var sheetsRoot = new TreeViewItem { Header = Strings.SheetsHeader + "（" + _vm.Sheets.Count + "）", IsExpanded = true };
            foreach (var sheet in _vm.Sheets)
            {
                var item = new TreeViewItem { Header = CheckHeader(sheet.Label, sheet, "IsChecked") };
                foreach (var view in sheet.Viewports)
                {
                    item.Items.Add(new TreeViewItem { Header = CheckHeader(ViewKinds.Label(view.Item.Kind).Split('（')[0] + "：" + view.Label, view, "IsChecked") });
                }
                sheetsRoot.Items.Add(item);
            }
            _tree.Items.Add(sheetsRoot);

            if (_vm.ViewGroups.Count > 0)
            {
                var othersRoot = new TreeViewItem { Header = Strings.OtherViewsHeader, IsExpanded = true };
                foreach (var group in _vm.ViewGroups)
                {
                    var groupItem = new TreeViewItem { Header = group.Label + "（" + group.Views.Count + "）" };
                    foreach (var view in group.Views)
                    {
                        groupItem.Items.Add(new TreeViewItem { Header = CheckHeader(view.Label, view, "IsChecked") });
                    }
                    othersRoot.Items.Add(groupItem);
                }
                _tree.Items.Add(othersRoot);
            }
        }

        private static CheckBox CheckHeader(string label, object source, string path)
        {
            var box = new CheckBox { Content = label };
            BindingOperations.SetBinding(box, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding(path) { Source = source, Mode = BindingMode.TwoWay });
            return box;
        }

        // ── 事件 ──────────────────────────────────────

        private void WireEvents()
        {
            _vm.PropertyChanged += (s, e) => RefreshSummary();
            _loginButton.Click += async (s, e) => await LoginAsync(false);
            _switchButton.Click += async (s, e) => await LoginAsync(true);
            _logoutButton.Click += async (s, e) => await LogoutAsync();
            _uploadButton.Click += async (s, e) => await UploadAsync();
            _retryButton.Click += async (s, e) => await RetryAsync();
            _cancelButton.Click += (s, e) => CancelOrClose();
            Closing += OnClosing;
            Loaded += async (s, e) => await InitialLoadAsync();
        }

        private void RefreshAccount()
        {
            var auth = _services.Auth;
            if (_services.ConfigurationError != null)
            {
                _accountText.Text = _services.ConfigurationError;
                _accountText.Foreground = System.Windows.Media.Brushes.Firebrick;
                _loginButton.IsEnabled = false;
                _logoutButton.Visibility = WpfVisibility.Collapsed;
                _switchButton.Visibility = WpfVisibility.Collapsed;
                return;
            }

            var user = auth == null ? null : auth.CurrentUser;
            var loggedIn = auth != null && auth.IsLoggedIn;
            _vm.IsLoggedIn = loggedIn;
            _accountText.Text = loggedIn
                ? (string.IsNullOrEmpty(user.Email) ? "已登入" : user.Email) + (string.IsNullOrEmpty(user.Role) ? string.Empty : "（" + user.Role + "）")
                : Strings.NotLoggedIn;
            _loginButton.Visibility = loggedIn ? WpfVisibility.Collapsed : WpfVisibility.Visible;
            _logoutButton.Visibility = loggedIn ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            _switchButton.Visibility = loggedIn ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            _loginButton.IsEnabled = !_running;
            _logoutButton.IsEnabled = !_running;
            _switchButton.IsEnabled = !_running;
        }

        private void RefreshSummary()
        {
            _summaryText.Text = _vm.SummaryText;
            _warningText.Text = string.Join("\n", _vm.MissingWarnings);
            _uploadButton.IsEnabled = _vm.CanUpload;
        }

        private void SetStatus(string text)
        {
            _statusText.Text = text ?? string.Empty;
        }

        /// <summary>
        /// 這一輪流程已有結果：之後才到達的舊進度回報（BeginInvoke 排在結果之前、執行在結果之後）不能蓋掉最後的狀態文字。
        /// </summary>
        private void InvalidateProgress()
        {
            _runGeneration++;
        }

        /// <summary>
        /// 在 UI 執行緒執行。async 事件處理常式的 await 之後不一定回到 UI 執行緒（取決於呼叫當下有沒有 Dispatcher 的
        /// SynchronizationContext，Revit 內的行為只能實機確認），所以每個 await 之後碰控制項都明確切回 UI。
        /// </summary>
        private void Ui(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.Invoke(action);
        }

        private async Task InitialLoadAsync()
        {
            if (_services.Auth == null || _services.ConfigurationError != null) return;
            if (_services.Auth.IsLoggedIn) await LoadProjectsAsync();
        }

        private async Task LoadProjectsAsync()
        {
            try
            {
                Ui(() => SetStatus("讀取可上傳的專案…"));
                var projects = await _services.Api.GetProjectsAsync(CancellationToken.None);
                Ui(() =>
                {
                    _vm.SetProjects(projects);
                    SetStatus(projects.Count == 0 ? "這個帳號目前沒有可以上傳工作版的專案。" : string.Empty);
                });
            }
            catch (Exception ex)
            {
                Ui(() =>
                {
                    ShowError(ex);
                    RefreshAccount();
                });
            }
        }

        private async Task LoginAsync(bool switchAccount)
        {
            if (_services.Auth == null) return;
            CancellationToken token = CancellationToken.None;
            try
            {
                Ui(() =>
                {
                    _loggingIn = true;
                    SetBusy(true);
                    _vm.SetProjects(new AddinProject[0]);
                    SetStatus("已開啟瀏覽器，請在瀏覽器中登入並按「授權」（最多等待 2 分鐘）…");
                    _cts = new CancellationTokenSource();
                    token = _cts.Token;
                });
                var revokeFailed = false;
                if (switchAccount)
                    revokeFailed = await _services.Auth.LogoutAsync(CancellationToken.None) == LogoutOutcome.ServerRevokeFailed;
                await _services.Auth.LoginAsync(_services.Browser, TimeSpan.FromSeconds(120), token);
                Ui(() =>
                {
                    SetBusy(false);
                    RefreshAccount();
                    // 換帳號時舊帳號的伺服器端撤銷沒成功：登入完成後也要讓使用者看到。
                    _resultText.Text = revokeFailed ? Strings.SwitchRevokeFailed : string.Empty;
                });
                await LoadProjectsAsync();
            }
            catch (OperationCanceledException)
            {
                Ui(() =>
                {
                    SetBusy(false);
                    SetStatus(Strings.LoginCancelled);
                    RefreshAccount();
                });
            }
            catch (Exception ex)
            {
                Ui(() =>
                {
                    SetBusy(false);
                    ShowError(ex);
                    RefreshAccount();
                });
            }
        }

        private async Task LogoutAsync()
        {
            try
            {
                Ui(() => SetBusy(true));
                var outcome = await _services.Auth.LogoutAsync(CancellationToken.None);
                Ui(() =>
                {
                    _vm.SetProjects(new AddinProject[0]);
                    _resultText.Text = string.Empty;
                    // 伺服器端撤銷沒成功時如實告知（本機資料不論如何都已刪除），並指引到網站手動撤銷。
                    SetStatus(outcome == LogoutOutcome.ServerRevokeFailed ? Strings.LoggedOutRevokeFailed : Strings.LoggedOut);
                });
            }
            catch (Exception ex)
            {
                Ui(() => ShowError(ex));
            }
            finally
            {
                Ui(() =>
                {
                    SetBusy(false);
                    RefreshAccount();
                });
            }
        }

        private async Task UploadAsync()
        {
            if (!_vm.CanUpload) return;
            ExportRequest request;
            UploadFlow flow;
            try
            {
                request = _vm.BuildRequest();
                flow = _services.CreateFlow();
                _flow = flow;
            }
            catch (Exception ex)
            {
                ShowError(ex);
                return;
            }

            _resultText.Text = string.Empty;
            _retryButton.Visibility = WpfVisibility.Collapsed;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var generation = ++_runGeneration;
            SetBusy(true);
            try
            {
                var result = await flow.RunAsync(request, new WindowExportProgress(this, generation), new WindowFlowProgress(this, generation), token);
                Ui(() => { InvalidateProgress(); ShowSuccess(result); });
            }
            catch (OperationCanceledException)
            {
                Ui(() => { InvalidateProgress(); SetStatus("已取消，沒有建立任何版本（伺服器端暫存已清除）。"); });
            }
            catch (Exception ex)
            {
                Ui(() =>
                {
                    InvalidateProgress();
                    ShowError(ex);
                    _retryButton.Visibility = flow.CanResume ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                });
            }
            finally
            {
                Ui(() =>
                {
                    SetBusy(false);
                    RefreshAccount();
                });
            }
        }

        private async Task RetryAsync()
        {
            var flow = _flow;
            if (flow == null || !flow.CanResume) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var generation = ++_runGeneration;
            _retryButton.Visibility = WpfVisibility.Collapsed;
            SetBusy(true);
            try
            {
                var result = await flow.ResumeAsync(new WindowFlowProgress(this, generation), token);
                Ui(() => { InvalidateProgress(); ShowSuccess(result); });
            }
            catch (OperationCanceledException)
            {
                Ui(() => { InvalidateProgress(); SetStatus("已取消，沒有建立任何版本（伺服器端暫存已清除）。"); });
            }
            catch (Exception ex)
            {
                Ui(() =>
                {
                    InvalidateProgress();
                    ShowError(ex);
                    _retryButton.Visibility = flow.CanResume ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                });
            }
            finally
            {
                Ui(() =>
                {
                    SetBusy(false);
                    RefreshAccount();
                });
            }
        }

        private void CancelOrClose()
        {
            if (_running && _cts != null)
            {
                // 先顯示「正在取消」再取消：Cancel() 會同步執行取消的延續，結果文字（「已取消登入。」）可能在這一行之前就已寫入，
                // 先取消再設文字會把它蓋掉（偶發失敗 CancellingWhileWaitingForBrowserLoginShowsCancelledNotAnError 的原因）。
                SetStatus("正在取消…（目前的步驟結束後就會停止；3D 模型匯出無法中途取消）");
                _cts.Cancel();
                return;
            }
            Close(); // 還有待重試的上傳時，由 OnClosing 詢問並清除（按 X 與按「關閉」走同一條路）
        }

        /// <summary>
        /// 關閉視窗（含右上角 X）：進行中就送出取消，由流程自己清伺服器與本機暫存；
        /// 閒置但還有待重試的上傳 → 詢問「放棄這次上傳並清除？」，同意就通知伺服器刪除並刪本機檔，不同意就留在視窗。
        /// </summary>
        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_running)
            {
                if (_cts != null) _cts.Cancel();
                return;
            }
            var flow = _flow;
            if (flow == null || !flow.CanResume) return;
            var confirm = _services.ConfirmDiscardOnClose;
            if (confirm != null && !confirm())
            {
                e.Cancel = true;
                return;
            }
            _retryButton.Visibility = WpfVisibility.Collapsed;
            var ignored = DiscardQuietlyAsync(flow); // 本機檔同步刪除；伺服器 DELETE 在背景盡力而為
        }

        private async Task DiscardQuietlyAsync(UploadFlow flow)
        {
            try
            {
                await flow.DiscardAsync();
            }
            catch (Exception ex)
            {
                if (_services.Log != null) _services.Log("放棄上傳時清除暫存失敗：" + ex.GetType().Name);
            }
        }

        private void SetBusy(bool busy)
        {
            _running = busy;
            if (!busy) _loggingIn = false;
            _vm.IsBusy = busy;
            _progress.Visibility = busy ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            _progress.IsIndeterminate = busy;
            _tree.IsEnabled = !busy;
            _projectBox.IsEnabled = !busy;
            _stageBox.IsEnabled = !busy;
            _descriptionBox.IsEnabled = !busy;
            _sheetSetBox.IsEnabled = !busy;
            _modelBox.IsEnabled = !busy;
            _paramsBox.IsEnabled = !busy;
            _loginButton.IsEnabled = !busy;
            _logoutButton.IsEnabled = !busy;
            _switchButton.IsEnabled = !busy;
            _cancelButton.Content = busy ? (_loggingIn ? "取消登入" : "取消上傳") : "關閉";
            RefreshSummary();
        }

        private void ShowError(Exception ex)
        {
            var api = ex as AddinApiException;
            if (api != null)
            {
                SetStatus(api.Message);
                if (_services.Log != null) _services.Log("上傳視窗：" + api.Kind + (api.Code != null ? "／" + api.Code : string.Empty));
                return;
            }
            var invalid = ex as InvalidOperationException;
            if (invalid != null)
            {
                SetStatus(invalid.Message); // 匯出步驟與流程丟的 InvalidOperationException 都是白話說明
                if (_services.Log != null) _services.Log("上傳視窗：" + ex.GetType().Name);
                return;
            }
            SetStatus("發生未預期的錯誤（" + ex.GetType().Name + "），詳情請看 MCP 日誌。");
            if (_services.Log != null) _services.Log("上傳視窗未預期的錯誤：" + ex.GetType().Name);
        }

        private void ShowSuccess(UploadFlowResult result)
        {
            SetStatus(Strings.Uploaded(result.Version));
            _resultText.Inlines.Clear();
            Uri uri;
            if (!string.IsNullOrEmpty(result.Url) && Uri.TryCreate(result.Url, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback))
            {
                var link = new Hyperlink(new Run("開啟版本頁面")) { NavigateUri = uri };
                link.RequestNavigate += (s, e) =>
                {
                    if (_services.OpenUrl != null) _services.OpenUrl(e.Uri.AbsoluteUri);
                    e.Handled = true;
                };
                _resultText.Inlines.Add(link);
            }
            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                _resultText.Inlines.Add(new Run("\n伺服器提醒：\n" + string.Join("\n", result.Warnings)));
            }
        }

        // ── 進度（Revit 單執行緒：匯出期間 UI 與 API 同一條，靠 Pump 重繪）──────────────

        private sealed class WindowExportProgress : IExportProgress
        {
            private readonly IssueManagerUploadWindow _window;
            private readonly int _generation;
            public WindowExportProgress(IssueManagerUploadWindow window, int generation) { _window = window; _generation = generation; }

            // 匯出的每個 Revit 步驟各自是一次 ExternalEvent，進度從背景執行緒回報：一律排進視窗的 Dispatcher，
            // 不在 API 呼叫中途跑巢狀訊息迴圈。
            public void Report(string text, int step, int totalSteps)
            {
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_window._running || _window._runGeneration != _generation) return;
                    _window._statusText.Text = "[" + step + "/" + totalSteps + "] " + text
                        + "\n匯出進行中：請勿切換文件、編輯模型或關閉專案，直到匯出完成。";
                    _window._progress.IsIndeterminate = false;
                    _window._progress.Value = totalSteps <= 0 ? 0 : 100.0 * (step - 1) / totalSteps;
                }));
            }
        }

        private sealed class WindowFlowProgress : IUploadFlowProgress
        {
            private readonly IssueManagerUploadWindow _window;
            private readonly int _generation;
            public WindowFlowProgress(IssueManagerUploadWindow window, int generation) { _window = window; _generation = generation; }

            // 進度是用 BeginInvoke 排進視窗的；流程結束後才到達的舊進度不能蓋掉最後的狀態文字。
            private bool IsCurrent { get { return _window._running && _window._runGeneration == _generation; } }

            public void Stage(string text)
            {
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsCurrent) return;
                    _window._statusText.Text = text;
                    _window._progress.IsIndeterminate = true;
                }));
            }

            public void Upload(UploadProgress progress)
            {
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsCurrent) return;
                    _window._progress.IsIndeterminate = false;
                    _window._progress.Value = progress.BytesTotal <= 0 ? 100 : 100.0 * progress.BytesDone / progress.BytesTotal;
                    _window._statusText.Text = "上傳 " + progress.Kind + "（" + progress.PartsDone + "/" + progress.PartsTotal + " 段"
                        + (progress.Skipped ? "，已上傳過，略過" : string.Empty) + "）";
                }));
            }
        }
    }
}
