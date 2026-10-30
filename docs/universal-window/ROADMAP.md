# 萬用窗族生成器 路線圖（ROADMAP）

> 更新：2026-09-14｜對應 SPEC.md v0.1｜決策見 docs/ADR.md

## Phase 0 互動示意 ✅
- configurator-demo.html：排 × 樘格子、三級圖面、MO 切透、檢核參數表、規格 JSON。

## Phase 1 固定尺寸生成器（進行中）
- [x] 共用參數檔 `MCP/Resources/shared-parameters/universal-window.txt`（18 個 UW_ 參數，GUID 凍結，測試防漂移；installer 一併部署到 %APPDATA%\RevitMCP\shared-parameters）
- [x] C#：規格模型、佈局數學（純 C#，24 測試對驗 demo）、族生成器（擠出／空心／符號線／參數）、`generate_window_family` 命令 — **已編譯（R23/R24 0 error）未在 Revit 內實測**
- [ ] MCP-Server：`window-family-tools.ts`、註冊、bridge、測試
- [x] MCP-Server：`window-family-tools.ts`、註冊、bridge、測試（15 pass）
- [x] 計數同步 181 → 182（CLAUDE.md、README、README.zh-TW、DOCUMENT_AUDIENCE_INVENTORY、docs/BIM_MCP 公開頁）、QA/QC PASS 60 / FAIL 0
- [ ] Revit 2024 實測：六範例生成、三級截圖、門窗表、三層牆 MO 切透
- [ ] 回填 SPEC §10「樣板室外方向與參考平面命名」

## Phase 2 Type 層活拉
- 外框四面對齊鎖到 Left／Right／Sill／Head 參考平面；Width／Height 綁標註
- 排高、樘寬以參考平面＋標註標籤＋公式驅動
- Type Catalog 匯出：同拓樸多尺寸一次載入
- 極端值 flex 測試清單（最小寬、最小高、比例極端、牆厚 8–30 cm）

## Phase 3 配件與檢核
- 紗窗（佔一軌，影響框深）
- 防墜檢核：建技規 §45 窗台高門檻、開啟限制器、隱形鐵窗顯示
- 窗台板、外窗台滴水、雨遮（細緻級）
- 玻璃分格條（型別參數）
- 開啟角度（Instance，3D 表現）
- 隔音等級、廠牌系列型號（文字參數）
- 放窗後自動填方位（供等效開窗率）
- 內倒內開、直軸窗（IFC 有對應枚舉）
- MCP：放窗後自動切割相交飾板（D-006 專案端）

## Phase 4 SOP 與整合
- `domain/universal-window.md`（雙語 SOP）＋ `.claude/skills/universal-window/`
- 門窗表、圖例視圖自動產生；窗編號自動化
- 檢核串接既有 domain：採光（daylight-area-check）、通風、排煙（smoke-exhaust-review）、節能欄位

## 暫不做
- 落地窗（門類別）、轉角窗、弧形／非矩形窗：另開族。
- 真實鋁材斷面：等事務所指定廠牌斷面後評估。
