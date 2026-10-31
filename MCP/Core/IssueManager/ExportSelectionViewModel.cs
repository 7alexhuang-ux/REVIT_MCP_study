#nullable disable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace RevitMCP.Core.IssueManager
{
    public abstract class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>被勾選的視圖。同一個視圖在整個視窗只有一個節點（圖紙底下與「其他視圖」共用）。</summary>
    public sealed class ViewNode : NotifyBase
    {
        private bool _isChecked;
        public ViewNode(ViewItem item) { Item = item; }
        public ViewItem Item { get; private set; }
        public string Label { get { return Item.Name; } }
        public bool IsChecked { get { return _isChecked; } set { Set(ref _isChecked, value); } }
    }

    public sealed class SheetNode : NotifyBase
    {
        private bool _isChecked;
        public SheetNode(SheetItem item, IList<ViewNode> viewports) { Item = item; Viewports = viewports; }
        public SheetItem Item { get; private set; }
        public IList<ViewNode> Viewports { get; private set; }
        public string Label { get { return Item.Number + "　" + Item.Title; } }
        public bool IsChecked { get { return _isChecked; } set { Set(ref _isChecked, value); } }
    }

    public sealed class ViewGroupNode
    {
        public string Kind { get; set; }
        public string Label { get { return ViewKinds.Label(Kind); } }
        public IList<ViewNode> Views { get; set; }
    }

    public sealed class StageOption
    {
        /// <summary>PD／SD／DD；「請選擇階段」為 null（不可送出，且沒有任何預設值，D-587）。</summary>
        public string Code { get; set; }
        public string Label { get; set; }
    }

    public sealed class SheetSetOption
    {
        /// <summary>null＝（自訂）。</summary>
        public SheetSetItem Set { get; set; }
        public string Label { get; set; }
    }

    /// <summary>
    /// 勾選視窗的檢視模型（revit-addin-export.md §2、驗收 X06）。完全不依賴 Revit／WPF，所以能離線單元測試。
    /// 規則：
    /// ‧階段一開始就是「請選擇階段」，沒有預設值；
    /// ‧選 ViewSheetSet ＝ 取代目前勾選（圖紙＋其視口視圖＋集內的視圖）；使用者再手動增減，下拉自動回到「（自訂）」；
    /// ‧「上傳」需要：已登入、專案、階段、說明、至少勾一張圖紙或一個模型（只勾視圖會被伺服器拒絕：400 invalid_manifest）。
    /// </summary>
    public sealed class ExportSelectionViewModel : NotifyBase
    {
        private static readonly StageOption[] StageList =
        {
            new StageOption { Code = null, Label = Strings.StagePlaceholder },
            new StageOption { Code = "PD", Label = Strings.StagePd },
            new StageOption { Code = "SD", Label = Strings.StageSd },
            new StageOption { Code = "DD", Label = Strings.StageDd }
        };

        private readonly Dictionary<string, ViewNode> _viewByUniqueId = new Dictionary<string, ViewNode>(StringComparer.Ordinal);
        private readonly List<SheetNode> _sheets = new List<SheetNode>();
        private readonly List<ViewGroupNode> _viewGroups = new List<ViewGroupNode>();
        private readonly List<SheetSetOption> _sheetSetOptions = new List<SheetSetOption>();

        private bool _suspendCustomSwitch;
        private bool _isLoggedIn;
        private bool _isBusy;
        private bool _includeModel = true;
        private bool _includeParams = true;
        private bool _includeDesignOptions = true;
        private string _description = string.Empty;
        private AddinProject _selectedProject;
        private StageOption _selectedStage = StageList[0];
        private SheetSetOption _selectedSheetSet;

        public ExportSelectionViewModel(ProjectSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            Snapshot = snapshot;
            Projects = new List<AddinProject>();

            foreach (var view in snapshot.Views)
            {
                if (!ViewKinds.IsKnown(view.Kind) || _viewByUniqueId.ContainsKey(view.UniqueId)) continue;
                var node = new ViewNode(view);
                node.PropertyChanged += OnSelectionNodeChanged;
                _viewByUniqueId[view.UniqueId] = node;
            }

            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sheet in snapshot.Sheets.OrderBy(s => s.Number, NaturalComparer.Instance))
            {
                var viewports = new List<ViewNode>();
                foreach (var viewport in sheet.Viewports)
                {
                    ViewNode node;
                    if (_viewByUniqueId.TryGetValue(viewport.ViewUniqueId, out node))
                    {
                        viewports.Add(node);
                        placed.Add(viewport.ViewUniqueId);
                    }
                }
                var sheetNode = new SheetNode(sheet, viewports);
                sheetNode.PropertyChanged += OnSheetNodeChanged;
                _sheets.Add(sheetNode);
            }

            foreach (var kind in ViewKinds.Ordered)
            {
                var views = _viewByUniqueId.Values
                    .Where(v => v.Item.Kind == kind && !placed.Contains(v.Item.UniqueId))
                    .OrderBy(v => v.Item.Name, NaturalComparer.Instance).ToList();
                if (views.Count > 0) _viewGroups.Add(new ViewGroupNode { Kind = kind, Views = views });
            }

            _sheetSetOptions.Add(new SheetSetOption { Set = null, Label = Strings.CustomSheetSet });
            foreach (var set in snapshot.SheetSets.OrderBy(s => s.Name, NaturalComparer.Instance))
            {
                _sheetSetOptions.Add(new SheetSetOption { Set = set, Label = set.Name });
            }
            _selectedSheetSet = _sheetSetOptions[0];
        }

        public ProjectSnapshot Snapshot { get; private set; }
        public IList<AddinProject> Projects { get; private set; }
        public IList<StageOption> Stages { get { return StageList; } }
        public IList<SheetNode> Sheets { get { return _sheets; } }
        public IList<ViewGroupNode> ViewGroups { get { return _viewGroups; } }
        public IList<SheetSetOption> SheetSets { get { return _sheetSetOptions; } }

        public void SetProjects(IEnumerable<AddinProject> projects)
        {
            Projects = projects.ToList();
            if (_selectedProject != null && !Projects.Any(p => p.Id == _selectedProject.Id)) SelectedProject = null;
            Raise("Projects");
            RaiseDerived();
        }

        public bool IsLoggedIn { get { return _isLoggedIn; } set { if (Set(ref _isLoggedIn, value)) RaiseDerived(); } }
        public bool IsBusy { get { return _isBusy; } set { if (Set(ref _isBusy, value)) RaiseDerived(); } }

        public AddinProject SelectedProject
        {
            get { return _selectedProject; }
            set { if (Set(ref _selectedProject, value)) RaiseDerived(); }
        }

        public StageOption SelectedStage
        {
            get { return _selectedStage; }
            set { if (Set(ref _selectedStage, value ?? StageList[0])) RaiseDerived(); }
        }

        public string Description
        {
            get { return _description; }
            set { if (Set(ref _description, value ?? string.Empty)) RaiseDerived(); }
        }

        public bool IncludeModel
        {
            get { return _includeModel; }
            set { if (Set(ref _includeModel, value)) { Raise("DesignOptionsEnabled"); RaiseDerived(); } }
        }

        public bool IncludeDesignOptions
        {
            get { return _includeDesignOptions; }
            set { if (Set(ref _includeDesignOptions, value)) RaiseDerived(); }
        }

        /// <summary>匯出參數與面積表（D-803）；預設勾選，不依賴 GLB（參數是 Revit 讀的）。</summary>
        public bool IncludeParams
        {
            get { return _includeParams; }
            set { if (Set(ref _includeParams, value)) RaiseDerived(); }
        }

        public bool DesignOptionsEnabled { get { return _includeModel; } }

        public SheetSetOption SelectedSheetSet
        {
            get { return _selectedSheetSet; }
            set
            {
                var option = value ?? _sheetSetOptions[0];
                if (!Set(ref _selectedSheetSet, option)) return;
                if (option.Set != null) ApplySheetSet(option.Set);
                RaiseDerived();
            }
        }

        // ── 勾選動作 ──────────────────────────────────

        public void SelectAllSheets(bool select)
        {
            _suspendCustomSwitch = true;
            try
            {
                foreach (var sheet in _sheets) sheet.IsChecked = select;
            }
            finally { _suspendCustomSwitch = false; }
            SwitchToCustom();
            RaiseDerived();
        }

        /// <summary>取代目前勾選：集內圖紙＋其視口視圖，加上集內單獨的視圖。</summary>
        private void ApplySheetSet(SheetSetItem set)
        {
            _suspendCustomSwitch = true;
            try
            {
                foreach (var sheet in _sheets) sheet.IsChecked = false;
                foreach (var view in _viewByUniqueId.Values) view.IsChecked = false;

                var sheetIds = new HashSet<string>(set.SheetUniqueIds, StringComparer.Ordinal);
                foreach (var sheet in _sheets.Where(s => sheetIds.Contains(s.Item.UniqueId)))
                {
                    sheet.IsChecked = true; // 同時勾選其視口視圖（見 OnSheetNodeChanged）
                }
                foreach (var id in set.ViewUniqueIds)
                {
                    ViewNode node;
                    if (_viewByUniqueId.TryGetValue(id, out node)) node.IsChecked = true;
                }
            }
            finally { _suspendCustomSwitch = false; }
        }

        private void OnSheetNodeChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "IsChecked") return;
            var sheet = (SheetNode)sender;
            // 勾圖紙＝連同它的視口視圖一起勾；取消圖紙＝一起取消。之後可以單獨改視口。
            var wasSuspended = _suspendCustomSwitch;
            _suspendCustomSwitch = true;
            try
            {
                foreach (var view in sheet.Viewports) view.IsChecked = sheet.IsChecked;
            }
            finally { _suspendCustomSwitch = wasSuspended; }
            OnSelectionChanged();
        }

        private void OnSelectionNodeChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsChecked") OnSelectionChanged();
        }

        private void OnSelectionChanged()
        {
            if (!_suspendCustomSwitch) SwitchToCustom();
            RaiseDerived();
        }

        private void SwitchToCustom()
        {
            if (_selectedSheetSet.Set == null) return;
            _selectedSheetSet = _sheetSetOptions[0];
            Raise("SelectedSheetSet");
        }

        // ── 摘要與是否可上傳 ──────────────────────────────

        public int CheckedSheetCount { get { return _sheets.Count(s => s.IsChecked); } }
        public int CheckedViewCount { get { return _viewByUniqueId.Values.Count(v => v.IsChecked); } }
        public int ModelCount { get { return _includeModel ? 1 : 0; } }
        /// <summary>已勾選、且網站能轉成 3D 視角的平面／立面／剖面視圖數（D-803：位置資料跟著勾選自動匯出）。</summary>
        public int CheckedPositionedViewCount { get { return _viewByUniqueId.Values.Count(v => v.IsChecked && ViewKinds.HasPosition(v.Item.Kind)); } }

        public string SummaryText { get { return Strings.Summary(CheckedSheetCount, CheckedViewCount, CheckedPositionedViewCount, ModelCount, _includeParams); } }

        /// <summary>沒勾圖紙或模型時的提示（允許送出，缺項留空，D-594）。</summary>
        public IList<string> MissingWarnings
        {
            get
            {
                var list = new List<string>();
                if (CheckedSheetCount == 0) list.Add(Strings.MissingSheets);
                if (!_includeModel) list.Add(Strings.MissingModel);
                return list;
            }
        }

        /// <summary>
        /// 至少要有一張圖紙或一個模型：只勾視圖的版本包，伺服器 finalize 會回 400 invalid_manifest（PDF 與 GLB 至少要有一個，
        /// revit-addin-export.md §5.2）。視圖本身只是圖紙／模型的附屬資料，不能單獨成為一個版本。
        /// </summary>
        public bool HasAnySelection { get { return CheckedSheetCount > 0 || _includeModel; } }

        /// <summary>「上傳」被停用的原因；可以上傳時為 null。</summary>
        public string BlockReason
        {
            get
            {
                if (_isBusy) return "上傳進行中";
                if (!_isLoggedIn) return Strings.NotLoggedInReason;
                if (_selectedProject == null) return Strings.NoProjectSelected;
                if (_selectedStage == null || _selectedStage.Code == null) return Strings.NoStageSelected;
                if (string.IsNullOrWhiteSpace(_description)) return Strings.NoDescription;
                if (!HasAnySelection) return Strings.NothingSelected;
                if (CheckedSheetCount > ManifestBuilder.MaxSheets) return Strings.TooManySheets(ManifestBuilder.MaxSheets);
                if (CheckedViewCount > ManifestBuilder.MaxViews) return Strings.TooManyViews(ManifestBuilder.MaxViews);
                return null;
            }
        }

        public bool CanUpload { get { return BlockReason == null; } }

        private void RaiseDerived()
        {
            Raise("CheckedSheetCount");
            Raise("CheckedViewCount");
            Raise("CheckedPositionedViewCount");
            Raise("ModelCount");
            Raise("SummaryText");
            Raise("MissingWarnings");
            Raise("HasAnySelection");
            Raise("BlockReason");
            Raise("CanUpload");
        }

        /// <summary>組出匯出請求。圖紙依圖號自然排序；每張圖紙只留下被勾選的視口視圖。</summary>
        public ExportRequest BuildRequest()
        {
            if (!CanUpload) throw new InvalidOperationException(BlockReason ?? "目前不能上傳。");
            var request = new ExportRequest
            {
                ProjectId = _selectedProject.Id,
                Stage = _selectedStage.Code,
                Description = _description.Trim(),
                SheetSetName = _selectedSheetSet.Set != null ? _selectedSheetSet.Set.Name : null,
                IncludeModel = _includeModel,
                IncludeDesignOptions = _includeModel && _includeDesignOptions,
                IncludeParams = _includeParams
            };

            foreach (var node in _sheets.Where(s => s.IsChecked))
            {
                var keep = new HashSet<string>(node.Viewports.Where(v => v.IsChecked).Select(v => v.Item.UniqueId), StringComparer.Ordinal);
                request.Sheets.Add(new SheetItem
                {
                    UniqueId = node.Item.UniqueId,
                    ElementId = node.Item.ElementId,
                    Number = node.Item.Number,
                    Title = node.Item.Title,
                    Viewports = node.Item.Viewports.Where(v => keep.Contains(v.ViewUniqueId)).ToList()
                });
            }

            // 視口大小只量一次會有快取問題：視窗開著時使用者在 Revit 改了圖紙版面，下次上傳不會更新。
            // 每次按上傳（建立匯出請求）就清掉先前量到的結果，匯出時只重量需要的視口（見 BundleExporter.ViewportsNeedingMeasure）。
            // 同一次上傳的「重試」不會再走這裡，所以不會重量。
            foreach (var viewport in request.Sheets.SelectMany(s => s.Viewports).Where(v => v.ViewportElementId != 0))
            {
                viewport.SizeMeasured = false;
                viewport.Width = 0;
                viewport.Height = 0;
            }

            request.Views.AddRange(_viewByUniqueId.Values.Where(v => v.IsChecked)
                .Select(v => v.Item)
                .OrderBy(v => Array.IndexOf(ViewKinds.Ordered, v.Kind))
                .ThenBy(v => v.Name, NaturalComparer.Instance)
                .ThenBy(v => v.UniqueId, StringComparer.Ordinal));
            return request;
        }
    }
}
