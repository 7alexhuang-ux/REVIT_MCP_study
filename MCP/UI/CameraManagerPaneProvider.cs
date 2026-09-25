using System;
using Autodesk.Revit.UI;

namespace RevitMCP.UI
{
    /// <summary>
    /// 相機管理停靠面板的註冊資訊。固定 GUID——面板一旦註冊過，之後每次 Revit 啟動都要用
    /// 同一個 Id 才能找到／顯示同一個面板，不可隨意更換。
    /// </summary>
    internal class CameraManagerPaneProvider : IDockablePaneProvider
    {
        /// <summary>固定 GUID，不要改。</summary>
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("A3E1F0B2-7C4D-4E9A-9B0F-6D2C8F4E1A77"));

        private readonly CameraManagerPanelControl _panel;

        public CameraManagerPaneProvider(CameraManagerPanelControl panel)
        {
            _panel = panel;
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _panel;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
        }
    }
}
