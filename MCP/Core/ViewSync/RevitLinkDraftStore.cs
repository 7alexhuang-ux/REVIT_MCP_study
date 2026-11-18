using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.ViewSync
{
    // Revit 跳轉（docs/REVIT_LINK.md §1.2、ADR D-015）的草稿暫存。純邏輯，不依賴 Revit API，可單元測試。
    // 草稿只放記憶體：取一次就刪、最久 N 分鐘過期、不寫硬碟；一次性代碼 ≥128 位元隨機。
    // 只有外掛內部（OpenIssueDraft）能 Put；瀏覽器只能 Take（viewsync_draft_take）。

    /// <summary>取草稿的結果。</summary>
    public enum DraftTakeStatus { Ok, NotFound, Expired }

    public sealed class RevitLinkDraftStore
    {
        private sealed class Entry
        {
            public JObject Draft;
            public DateTime ExpiresAtUtc;
        }

        /// <summary>外掛共用的那一份（功能區按鈕、Claude 指令、瀏覽器領取都看同一份）。</summary>
        public static readonly RevitLinkDraftStore Shared = new RevitLinkDraftStore(null);

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _items = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Func<DateTime> _utcNow;

        /// <param name="utcNow">測試用時鐘；正式使用傳 null（用系統時間）。</param>
        public RevitLinkDraftStore(Func<DateTime> utcNow)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>目前還留在記憶體裡的草稿數（含已過期但還沒被清掉的）；測試與日誌用。</summary>
        public int Count
        {
            get { lock (_gate) return _items.Count; }
        }

        /// <summary>草稿內容 { intent:"create"|"lookup", model, view, elements, image? }（由 OpenIssueDraft 組好）。回傳一次性代碼（base64url，144 位元亂數＝24 字元）。</summary>
        public string Put(JObject draft, TimeSpan lifetime)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            var bytes = new byte[18]; // 144 位元
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var code = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            lock (_gate)
            {
                Sweep();
                _items[code] = new Entry { Draft = (JObject)draft.DeepClone(), ExpiresAtUtc = _utcNow().Add(lifetime) };
            }
            return code;
        }

        /// <summary>取一次就刪：不論成功、過期都從記憶體移除。</summary>
        public DraftTakeStatus Take(string code, out JObject draft)
        {
            draft = null;
            if (string.IsNullOrEmpty(code)) return DraftTakeStatus.NotFound;
            lock (_gate)
            {
                Entry entry;
                if (!_items.TryGetValue(code, out entry)) return DraftTakeStatus.NotFound;
                _items.Remove(code);
                if (_utcNow() > entry.ExpiresAtUtc) return DraftTakeStatus.Expired;
                draft = entry.Draft;
                return DraftTakeStatus.Ok;
            }
        }

        /// <summary>清掉過期超過 5 分鐘的草稿（留 5 分鐘讓使用者看得到「已過期」而不是「不存在」）。</summary>
        private void Sweep()
        {
            var now = _utcNow();
            var dead = new List<string>();
            foreach (var pair in _items)
                if (now > pair.Value.ExpiresAtUtc.AddMinutes(5)) dead.Add(pair.Key);
            foreach (var key in dead) _items.Remove(key);
        }
    }
}
