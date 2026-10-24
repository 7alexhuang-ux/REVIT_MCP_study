---
name: hillside-site-review
description: "統整山坡地基地檢討六階段流程的順序與交接：坵塊法（原地形限建判定）→建整地方案→限建範圍核對→坡度熱區（設計後視覺化）→土方平衡→方格法計算書，開場先確認使用者要做哪一階段，不一口氣全跑。當使用者提到山坡地、山坡地開發、坵塊、坵塊圖、坵塊法、平均坡度、整地、整地方案、整地總流程、土方平衡、挖填平衡、方格法、土方計算書、坡度熱區、六級坡、限建、限建範圍、hillside site review、parcel slope、grading、earthwork balance、earthwork gridsheet 時觸發。"
user-invocable: true
---

# Hillside Site Review (山坡地基地檢討總流程)

This skill is orchestration only. All method, formulas, legal thresholds, and tool parameter detail
live in `domain/hillside-site-review.md` (the master SOP) and the four sub-domains it indexes. Read
`domain/hillside-site-review.md` in full before acting — this skill only encodes the sequencing,
handoffs, and stop points on top of it.

## Guard rails (non-negotiable, read before any stage)

1. **兩種坡度不可混用、不可互相替代。** 坵塊法算**原地形**（法定認定，有法律拘束力）；坡度熱區算**設計後地形**（視覺化，無法律拘束力）。任何時候回報坡度數字，必須明確標示是哪一種，並指回 `domain/hillside-site-review.md` §2 的分工表。**不得**拿坡度熱區圖充當坵塊圖送審。
2. **法規條文、門檻、公式只引用 domain 檔內容，並標示出處**（格式：`Per domain/xxx.md §N`）。不得憑模型一般知識補充或改寫數值（例：π 一律 3.14，不是 `Math.PI`；30%／55%／六級坡級距一律照 domain 原文）。
3. **不替使用者下「可否開發建築」的終局結論。** 均質區劃分、但書適用、土地可利用限度判定都是建築師的專業判斷，skill 與工具只產出逐格數據。
4. **每個具體數字（坡度、交點數、CUT/FILL、面積）必須來自本輪的工具回應**，不得沿用上一輪或記憶中的數字（Per `CLAUDE.md` Tool Call Data Honesty）。
5. **動到模型或視圖前先 `get_active_view` 重新定錨**（找不到再用 `get_all_views` 備援），不要沿用對話中更早的視圖/樓層/選取狀態（Per `CLAUDE.md` Active State Re-Anchoring）。
6. **坵塊法與限建核對尚未在 Revit 實機完整驗收。** 任一階段輸出坵塊圖或限建核對結果時，都要提醒使用者人工覆核，不要包裝成「已驗證」的結論。
7. **法規查證日為 2026-09-21**（四份子 domain metadata）。送審前提醒使用者複核現行條文版本，不要假設條文未變。
8. Skill 與 domain 內容衝突時，**domain 為準**；發現衝突回報使用者，不要自行調和。

## Scope

Use this skill when the user is working a hillside/mountain-slope site (山坡地) through any part of:
原地形限建判定 (坵塊法) → 整地方案 → 限建範圍核對 → 設計後坡度熱區 → 土方平衡 → 方格法計算書與比較出圖。

Do not use this skill for flat-site floor drainage slope checks (`floor-slope-analysis.md`, unrelated
workflow) or for generic floor/toposolid grading that is not part of a hillside development review.

## Opening move: ask which stage, don't run all six at once

Mirrors the collaborative, step-by-step posture of `dwg-column-import` — a hillside case is rarely a
single request that runs end to end. Before doing anything, ask the user (or infer from explicit
context) **which of the six stages below they want**, and whether they already have outputs from an
earlier stage (an existing 坵塊圖, an existing 整地方案, etc.). Do not silently assume stage ① just
because the word "山坡地" appeared.

If the user's intent spans multiple stages (e.g. "幫我從頭跑一次"), confirm the stage boundaries and
stop points below with them before proceeding, and still honor every ⛔ stop point inside each stage.

## The six stages

### ① 原地形坵塊法 (parcel slope / 限建坵塊判定)

- **Read first**: `domain/parcel-slope-analysis.md` (full; especially §4 均質區不可自動化, §6 等高線來源限制).
- **Tools, in order**: `get_parcel_contour_layers` (identify which CAD layer is the 等高線 layer) →
  `preview_parcel_slope` (compute only, no drawing — sanity-check parameters) →
  `create_parcel_slope_map` (produce the 坵塊圖).
- **Confirm with user before `create_parcel_slope_map`**: which contour layer, 坵塊邊長 L (≤25m),
  首曲線間距 h (must be user-supplied, never guessed), rotation if any.
- **Output**: 坵塊圖 (每格編號、交點數 n、平均坡度 S%、§262 分級標示).
- **⛔ Stop**: do not state a final "可／不可開發建築" conclusion. Hand the per-cell slopes to the
  user for 均質區 judgment; only their confirmed 均質區 result feeds stage ②/③.
- **Common error to guard against**: L is a single edge length, not the sum of four edges — see
  `domain/parcel-slope-analysis.md` §2.1.

### ② 建立整地方案 (grading scheme)

- **Read first**: this stage is the grading module itself, not covered by the four sub-domains;
  `docs/superpowers/specs/2026-07-02-toposolid-footprint-cut-fill-design.md` is the reference both
  `earthwork-balance.md` §10 and `grading-slope-classification.md` §6 point to.
- **Tools**: `generate_grading_analysis` → `set_floor_elevations` / `set_floor_edge_slope` (as needed
  per the chosen grading mode: 直貼／外延漸變／指定坡度放坡) → `duplicate_floor_draft` (when
  preparing an alternative scheme) → `grade_toposolid_to_floors` (commits scheme + ledger record) →
  `list_grading_schemes` (check what already exists before creating a duplicate).
- **Confirm with user**: floor plate layout relative to stage ①'s 限建 ranges, which grading mode.
- **Output**: a registered grading scheme (control floors + design terrain + ledger record).
- **⛔ Stop**: do not proceed to ④⑤⑥ until stage ③ clears this scheme.

### ③ 限建範圍核對 (does the scheme land on a 限建坵塊?)

- **Read first**: `domain/parcel-slope-analysis.md` §5.1 (分級標示對照).
- **Tool**: `check_grading_parcel_limits` — ⚠️ **implemented (confirmed registered in
  `MCP-Server/src/tools/grading-tools.ts`) but not yet field-verified in Revit; results must still
  be cross-checked manually before relying on them for submission.** Re-anchor with `get_active_view`
  before calling it. Its actual behavior: it recomputes with the **same 坵塊參數** used in
  `create_parcel_slope_map` (pass `parcelViewId`, not `viewId`, for the plan view that holds the
  contour DWG — `viewId` is reserved for the scheme's 3D view used only when `drawOverlay=true`);
  `drawOverlay` defaults to `false` (compute only) and only draws overlay lines on the scheme view
  when set `true`; it returns, per floor plate, which 限建坵塊 it overlaps and the overlap area —
  it does **not** return a legal verdict. **It also returns a `warnings` array** (e.g. a floor
  partly outside the 坵塊 grid's coverage, a floor with an inner loop/hole that isn't subtracted,
  a floor whose footprint could not be extracted, or a stale overlay left over from a prior run) —
  **relay every warning to the user verbatim; do not summarize the run as "pass" or report only the
  area numbers while dropping the warnings.**
  If the tool is unavailable or its output is suspect, fall back to manual overlay: place stage ①'s
  坵塊圖 and stage ②'s floor footprint on the same plan view (optionally via `show_grading_terrain`
  for the scheme terrain) and visually check for overlap.
- **Output**: pass/needs-adjustment per floor plate, plus any `warnings` relayed to the user.
- **⛔ Stop**: do not advance to ④ until the scheme passes (or the user explicitly accepts a flagged
  overlap). On fail, loop back to ② with adjusted floor layout.

### ④ 設計後坡度熱區 (post-grading slope heatmap)

- **Read first**: `domain/grading-slope-classification.md` (full; especially §2.1 — only 六級坡 may
  be labeled 宜林地; "加強保育地" must never appear).
- **Tool**: `create_slope_heatmap`.
- **Output**: 坡度熱區圖 (六級坡 AVF coloring, legend with per-level range and attribute).
- **⛔ Stop / check before handing to user**: scan the legend/report for "加強保育地" or 宜林地 on
  anything other than 六級坡 — either is a guard-rail violation per `domain/grading-slope-classification.md` §2.1.

### ⑤ 土方平衡反求 (balance the net earthwork)

- **Read first**: `domain/earthwork-balance.md` (full; especially §8 鬆實方三本帳).
- **Tool**: `solve_balanced_elevation`.
- **Confirm with user before calling with `apply=true`**: target net (default 0), tolerance, whether
  this run feeds 料源調度/外運計價/送審土方數量— if so, `looseFactor` + `compactionFactor` are
  **required** (must be provided as a pair) so `netBasis="effective"` rather than `"bank"`.
- **Output**: converged offset (or best-effort result with a warning if not converged).
- **⛔ Stop**: only apply (`apply=true`) when `Converged=true`. Never apply an unconverged result —
  surface it to the user as a warning with the best-effort offset instead.

### ⑥ 方格法計算書與比較出圖 (gridsheet + comparison drawings)

- **Read first**: `domain/earthwork-gridsheet.md` (full; especially §1 — gridsheet totals are a
  presentation value, not the authoritative one).
- **Tools**: `export_earthwork_gridsheet` (the Excel gridsheet) and, for comparison drawings as
  needed: `export_grading_comparison`, `create_grading_comparison_sheet`, `create_grading_presentation`,
  `create_cutfill_heatmap`, `create_grading_extent_lines`, `annotate_grading_scheme`,
  `create_grading_scheme_view`, `place_views_on_sheet`, `export_sheets_pdf`.
- **Output**: gridsheet Excel, scheme comparison Excel/PDF, per-scheme drawing sets.
- **⛔ Stop / check before handing to user**: if the user intends this for 送審或計價, remind them to
  cite Revit's formal CUT/FILL ledger value, not the gridsheet's grid-sampled total — see
  `domain/earthwork-gridsheet.md` §7.

## 工具 (Tools)

| 工具名稱 | 階段 | 用途 | 驗證來源 |
|---|---|---|---|
| `get_parcel_contour_layers` | ① | 列出 CAD 圖層，辨認等高線層 | `grading-tools.ts:364` |
| `preview_parcel_slope` | ① | 只算不畫，驗坵塊參數 | `grading-tools.ts:379` |
| `create_parcel_slope_map` | ① | 產出坵塊圖 | `grading-tools.ts:418` |
| `generate_grading_analysis` | ② | 備妥整地方案視覺內容 | `grading-tools.ts:657` |
| `set_floor_elevations` | ② | 批次設樓板高程/斜板 | `grading-tools.ts:607` |
| `set_floor_edge_slope` | ② | 指定邊坡率政策 | `grading-tools.ts:641` |
| `duplicate_floor_draft` | ② | 複製樓板草稿準備下一版方案 | `grading-tools.ts:672` |
| `grade_toposolid_to_floors` | ②⑤ | 依樓板投影整平地形，落成方案登記簿 | `grading-tools.ts:6` |
| `list_grading_schemes` | ② | 查目前已有哪些方案 | `grading-tools.ts:168` |
| `check_grading_parcel_limits` | ③ | 核對樓板是否落在限建坵塊（已實作，尚未實機驗收，見 stage③說明） | `grading-tools.ts`（已 grep 到；詳見 stage③） |
| `show_grading_terrain` | ③ | 顯示方案地形供人工疊圖核對 | `grading-tools.ts:689` |
| `create_slope_heatmap` | ④ | 設計後坡度熱區 | `grading-tools.ts:246` |
| `solve_balanced_elevation` | ⑤ | 二分法反求統一升降量 | `grading-tools.ts:84` |
| `export_earthwork_gridsheet` | ⑥ | 方格法計算書 Excel | `grading-tools.ts:339` |
| `export_grading_comparison` | ⑥ | 方案登記簿比較 Excel | `grading-tools.ts:177` |
| `create_grading_comparison_sheet` | ⑥ | C-00c 總比較圖紙 | `grading-tools.ts:549` |
| `create_grading_presentation` | ⑥ | 各方案別出圖 | `grading-tools.ts:517` |
| `create_cutfill_heatmap` | ⑥ | 挖填熱區圖 | `grading-tools.ts:221` |
| `create_grading_extent_lines` | ⑥ | 範圍線 | `grading-tools.ts:271` |
| `annotate_grading_scheme` | ⑥ | 方案標註 | `grading-tools.ts:290` |
| `create_grading_scheme_view` | ⑥ | 方案專用視圖 | `grading-tools.ts:197` |
| `place_views_on_sheet` | ⑥ | 視圖排圖 | `grading-tools.ts:460` |
| `export_sheets_pdf` | ⑥ | 出圖 PDF | `grading-tools.ts:491` |
| `get_active_view` | 全程 | 動作前重新定錨 | `base-tools.ts:92` |
| `get_all_views` | 全程（備援） | `get_active_view` 失敗時備援 | `base-tools.ts:81` |

## Reference

- Master SOP (sequencing, stage handoffs, flowchart): `domain/hillside-site-review.md`
- 原地形限建判定 (坵塊法): `domain/parcel-slope-analysis.md`
- 設計後坡度視覺化 (六級坡): `domain/grading-slope-classification.md`
- 土方平衡反求: `domain/earthwork-balance.md`
- 方格法土方計算書: `domain/earthwork-gridsheet.md`
- Project-wide guard rails this skill inherits (Tool Call Data Honesty, Domain Method Compliance,
  Active State Re-Anchoring, Domain vs Skill layering): repo root `CLAUDE.md`.
