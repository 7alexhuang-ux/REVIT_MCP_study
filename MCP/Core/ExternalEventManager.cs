using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace RevitMCP.Core
{
    /// <summary>
    /// 外部事件管理器
    /// 確保命令在 Revit UI 執行緒中執行
    /// </summary>
    public class ExternalEventManager
    {
        private static ExternalEventManager _instance;
        private static readonly object _lock = new object();
        
        private ExternalEvent _externalEvent;
        private CommandEventHandler _eventHandler;

        private ExternalEventManager()
        {
            _eventHandler = new CommandEventHandler();
            _externalEvent = ExternalEvent.Create(_eventHandler);
        }

        public static ExternalEventManager Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new ExternalEventManager();
                    }
                    return _instance;
                }
            }
        }

        /// <summary>
        /// 執行命令
        /// </summary>
        public void ExecuteCommand(Action<UIApplication> action)
        {
            TryExecuteCommand(action);
        }

        /// <summary>
        /// 同 <see cref="ExecuteCommand"/>，但回傳 <c>ExternalEvent.Raise()</c> 的結果，讓呼叫端能發現要求被拒絕
        /// （Accepted／Pending 以外）。工作仍已進入佇列；呼叫端若放棄，要自行讓工作變成不執行。
        /// </summary>
        public ExternalEventRequest TryExecuteCommand(Action<UIApplication> action)
        {
            _eventHandler.Enqueue(action);
            return _externalEvent.Raise();
        }

        /// <summary>
        /// 命令事件處理器
        /// </summary>
        private class CommandEventHandler : IExternalEventHandler
        {
            private readonly object _queueLock = new object();
            private readonly Queue<Action<UIApplication>> _actions = new Queue<Action<UIApplication>>();

            public void Enqueue(Action<UIApplication> action)
            {
                if (action == null) { return; }
                lock (_queueLock)
                {
                    _actions.Enqueue(action);
                }
            }

            public void Execute(UIApplication app)
            {
                while (true)
                {
                    Action<UIApplication> action = null;
                    lock (_queueLock)
                    {
                        if (_actions.Count > 0)
                        {
                            action = _actions.Dequeue();
                        }
                    }

                    if (action == null)
                    {
                        return;
                    }

                    try
                    {
                        action.Invoke(app);
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("命令執行錯誤", ex.Message);
                    }
                }
            }

            public string GetName()
            {
                return "RevitMCP Command Handler";
            }
        }
    }
}
