# Revit（REVIT_MCP_study）：Alex 決策紀錄（從 C:\Temp 搬進 repo）

> 建立：2026-10-31（台北時間）。依 Alex 2026-10-31 09:22 規則：「所有規格、決策、報告都寫進 repo 的 docs/，不要只寫在 C:\Temp」，讓 Claude／Codex／agy 都看得到。
> 來源：`C:\Temp\grokbot_sessions\BACKLOG.md`（派工 SSOT，含「決策紀錄」段）與 `C:\Temp\grokbot_sessions\ROUTINE_STATUS_NOTE.txt`（`[PARENT]` 行＝主 agent 轉述 Alex 的決定）。來源檔快照時間 2026-10-31 09:2x；C:\Temp 原檔未刪、未改。
> 做法：用關鍵字自動挑出含 Alex 決定／指示的行、以及排程「依 Alex 既有原則代決」的行，**原文照抄不改字**（只把誤成 tab 的 `\t` 還原），依日期排序；同一行若涉及多個專案，會同時出現在各專案的紀錄。
> 注意：這是**收集**，不代表已寫進 SPEC.md／ADR.md。補進規格時請照 SPEC FIRST，只往後追加並標「補記（決策日期，來源）」。日期取自該行文字；行內沒有日期時沿用上一個有日期的行（可能有一天誤差）。
> 類型：「Alex 決定／指示／待決」＝Alex 本人拍板、主 agent 轉述，或標明「待 Alex」的事項（要看原文判斷是已決還是待決）；「排程代決」＝派工排程依 Alex 既有原則先做的決定（早上摘要時已告知 Alex，Alex 可推翻）；「主 agent 紀錄」＝主 agent 的派工紀錄行。

共 28 筆。

## 2026-10-21

- ［Alex 決定／指示／待決］- Questions (12): carried 1 Revit export (rec A MCP batch), 2 3D source (rec A viewer snapshot), 3 pin=issue (rec A, built), 4 share login (rec A, built), 5 translation (rec A none), 6 old markups after confirm (rec A, built); new N1 status customisation (rec A names/colours only), N2 category tree attach (rec A mind-map node), N3 who triggers 待複核 (rec A assignee only), N4 bid-deck images in repo (rec A remove), N5 status change in panel (rec B later), N6 big uploads (rec B signed URL 25MB). Mermaid diagrams for each are in imrm_drawbuild_report.md 「需要 Alex 決定」.  
  — 來源：`ROUTINE_STATUS_NOTE.txt` 第 121 行
- ［Alex 決定／指示／待決］[07:41 PARENT] ALEX DECIDED drawmark/drawbuild Q1 Revit export = A: batch export of plan/elevation/section sheets via Revit MCP, auto-upload into 圖說畫記.  
  — 來源：`ROUTINE_STATUS_NOTE.txt` 第 159 行
- ［Alex 決定／指示／待決］[07:42 PARENT] ALEX DECIDED drawmark Q2 3D views = B: export Revit 3D views (bird's-eye / eye-level) in the same batch as sheets; not web-viewer snapshots.  
  — 來源：`ROUTINE_STATUS_NOTE.txt` 第 161 行

## 2026-10-22

- ［Alex 決定／指示／待決］- imexportbundle | done（09:29 imexportbundler2v Codex PASS：F2 文件已一致，33/33＋13/13；delivered；imexportbundleq 可問 Alex）（09:01 IMEXPORTBUNDLER2_DONE 6a1158b 只改 spec §2.2＋ADR D-546，33/33；imexportbundler2v Codex gpt-6.1-sol medium 09:26 已發） | C:\temp\imexportbundle\wt off main fb1dbb0（node_modules junction）| sonnet medium | 誰都能做（ape 建、7.alex 或 Codex 驗） | SPEC FIRST D-545～549、無 migration；版本包規格（.glb＋圖說 PDF＋參數 JSON＋manifest）＋可點 mock /mock/exportbundle；不寫正式站、不改 Revit；imrevitexporter 併入 | [PARENT 07:26]  
  — 來源：`BACKLOG.md` 第 90 行
- ［Alex 決定／指示／待決］- rm_3dreview | wait-alex（07:00 起一題一題問，附 mock 截圖） | | | | 三個 3D 相關 mock 待 Alex 審：協調版／提送版 3D（APS，public/mock/aps3d）、技師在 3D 上標註開議題（public/mock/3dissue）、Revit 跳轉（docs/spec/revit-link.md）；APS 另需 Alex 開開發者帳號（S2） |  
  — 來源：`BACKLOG.md` 第 102 行
- ［Alex 決定／指示／待決］[PARENT 09:30] Alex 問：線上模型能否保留 Revit 參數（門窗寬高、牆長度/面積/體積/高度等）。spec viewer3d.md 第 5 點已規劃「點構件顯示 userData/extras」。新增 imrevitparams（sonnet high，Claude-only，SPEC FIRST）：(0) 先檢查目前 CPC 上傳的 GLB 裡 extras/userData 實際有沒有參數，結果寫進報告；(1) 調查現成做法（glTF exporter 寫 extras、IFC 屬性集、Speckle、That Open/xeokit 屬性面板），授權要查；(2) 用 REVIT_MCP_study export_web_compare 另出依 UniqueId 對應的參數 JSON（類型、族群、寬、高、長、面積、體積、樓層、材料），網頁點構件右側顯示白話中文參數表，單位用 mm/m²/m³；(3) 與 imrevitviews 合併規劃。研究結論先交 parent 給 Alex 看可點選 demo。  
  — 來源：`ROUTINE_STATUS_NOTE.txt` 第 599 行
- ［Alex 決定／指示／待決］[PARENT 09:31] Alex 回報：中埔案 Revit 用「沖孔板貼圖＋半透明」模擬沖孔，但 3D 檢視器寫實模式沒表現出來。新增 imperforated（sonnet medium，Claude-only，排在 imphaserounds 後、優先度高）：(0) 先檢查 CPC GLB 裡沖孔板材質是否帶 baseColorTexture/alpha/opacity（glTF alphaMode BLEND/MASK）與 KHR_materials_transmission，報告實際內容；(1) 若匯出時遺失，查 Revit→glTF 匯出器如何保留貼圖與透明度（找現成 OSS 做法、查授權）；(2) 若 GLB 有但 viewer 沒用，修 three.js 寫實模式材質（尊重 alphaMode、transparent、depthWrite、雙面），沖孔板用 alphaTest/MASK 貼圖讓孔真的透光；(3) 截圖與 Revit 同視角對照。完成驗證後併入下一版。  
  — 來源：`ROUTINE_STATUS_NOTE.txt` 第 601 行
- ［Alex 決定／指示／待決］[PARENT 12:09] Alex 決定 3D 檢視器研究（docs/spec/viewer-research.md，branch viewresearch 64e1597+d156dc1）後續：(1) 新項目 imviewmust（sonnet high，Claude-only，spec first，base imintegrate3 通過後）：D 段「必做 16 項」中尚未在 prod（viewfix 已含剖面切口填色、人視角漫遊、物理開關、回全景、設定面板）的全部實作，含 N01 慣性平滑、N02 縮放朝滑鼠遠快近慢、N04 相機不穿牆不入地、W05 掉落保護、修手機搖桿擋畫面；P01 模型樹與 imrevitparams 協調（需 GLB 樓層資料）。先逐項列出 16 項現況表再做。(2) 新項目 imviewpick（sonnet low）：把 public/mock/viewerfeatures 勾選頁改成只列「建議 19＋可選 13」、每項白話一句無英文術語，掛到預覽站 http://msi:3200 可存答案為 viewerfeatures_answers.json，完成後交 parent 傳連結給 Alex。若 BACKLOG 無此兩行先加入。  
  — 來源：`ROUTINE_STATUS_NOTE.txt` 第 650 行
- ［Alex 決定／指示／待決］[PARENT 22:09] Alex 決定：Revit 端參數匯出工具（Revit add-in，一次匯出 GLB＋參數檔）要等 imrevitparamsv 驗證通過、參數格式定案後才開工。請在 BACKLOG 新增 imrevitexporter（依賴 imrevitparams 通過），之後依一般流程派工（SPEC FIRST，可開關 Revit 於 MSI 測試）。  
  — 來源：`ROUTINE_STATUS_NOTE.txt` 第 779 行
- `00:41` ［Alex 決定／指示／待決］imphaserounds | done（12:12 imphaseroundsrv PASS，delivered）（前：verify（12:05 IMPHASEROUNDSR_DONE：spec 6de50b6＋910b254，6 項已修、1545 test/build/e2e 過 → imphaseroundsrv 7.alex sonnet medium，launcher 34276，claude 52304）（11:33 imphaseroundsv FAIL：drawings 總覽與 3D 沿用提示仍是裸代碼等 6 項 → imphaseroundsr 8.ape sonnet medium，退回第 1 次）（前：verify（11:03 IMPHASEROUNDS_DONE：spec 023899d＋441a92b＋86fde6b，lint/tsc/1545 test/build、e2e rounds 26 項過；正式庫只列清單 imphaserounds_proddata.txt；9 題待 Alex；11:10 imphaseroundsv 7.alex sonnet high，launcher 49104）（10:05 8.ape sonnet high，launcher 60992，wt C:\temp\imphaserounds\wt off 16074ac，併 imsetcodelabel，D-322～D-327、migration 0070）（原 [PARENT 09:27] 最優先：ape 10:00 重置後第一個開，sonnet high，基底 16074ac，imsetcodelabel 併入一起做；正式資料改動先給 Alex 看清單）| 新 worktree off 16074ac | sonnet high | anyone | (1) 中埔案模型／圖面階段改為「規劃階段」（目前誤歸基本設計；在測試庫與種子資料修正，正式庫資料改動列清單等 Alex OK）；(2) 新概念「輪次」：每次溝通會議＝一輪（例：5/21 溝通會議 … 10/1 第 6 次溝通會議，全屬規劃階段），每輪把 簡報＋會議記錄＋Revit 模型版本＋圖面版本 綁在一起，模型／圖面預設對應到最新一輪；(3) 會議後下一輪同事開始出新模型圖面時，自動產生下一輪的「準備中」簡報占位（尚未發生的狀態，有專屬外觀：虛線框／淡色／「準備中・預計 X 月 X 日會議」，顯示已累積的新圖與模型），會議日後轉為正式；與簡報生成模組 rm_ppt 的資料契約對接（占位就是生成模組的輸入）；(4) 檢視器「圖面／模型」切換從右上角移到左上角；(5) 交付 spec docs/spec/rounds.md、mock、桌機＋手機截圖；不捲動；所有欄位可編輯 | Alex 2026-10-22 00:41；D-260–D-265；不 deploy、不 push  
  — 來源：`BACKLOG.md` 第 184 行
- `00:43` ［Alex 決定／指示／待決］imrevitviews | queued (after imintegrate; SPEC FIRST; can run with Revit open, consent to open/close Revit) | C:\Project\issue-manager (branch off imintegrate) + C:\Project\REVIT_MCP_study | sonnet high | Claude-only (Revit MCP) | 匯出 Revit 已設定的 3D 視圖（鳥瞰 3D 與 Camera View 人視）：每個視圖的相機（位置、目標、上方向、FOV／透視或正交、剖面框）＋ Revit PNG，延用 export_web_compare 的 camera.json；線上檢視器左側「Revit 視角」清單，點選後相機平滑飛到該視角，可繼續轉動與漫遊，旁邊可開 Revit 截圖對照；校準透視與 FOV（之前網頁約 2 倍放大）並附 Revit 截圖 vs 網頁截圖的對照；Views 與 Sheets 都支援：Sheets 為主線（照 D-121 資料契約，PDF＋PNG＋metadata），2D Views 只匯出使用者勾選的；中埔案做實測；隸屬對應 imphaserounds 的輪次 | 第 0 步（Alex 00:52）：先調查現成外掛與開源最佳實踐（Speckle Revit connector、pyRevit、Revit 內建 PDF 批次匯出、GitHub 上的 Revit glTF/IFC 匯出器與 camera/view 匯出、APS Model Derivative views/sheets），比較授權可否商用、是否維護、能否取到相機與圖紙 metadata，寫入 docs/spec/revit-views-oss.md，每項標「直接用／拿來改／自己做」並附網址；能用現成就不重寫，找不到才在 REVIT_MCP_study 開發 ；加項（Alex 00:53）：網頁版「索引圖」子母畫面——看剖面時小窗顯示平面圖上的剖面線位置與方向；看 3D／camera view 時平面上顯示相機位置、視線方向、視野扇形（寬度＝FOV）、遠近裁剪；剖面也標在平面上；點小窗可跳視角、拖曳可調；完整參照 Alex 自己的 Revit 視角工具 C:\Project\REVIT_MCP_study 分支 origin/feat/camera-manager（MCP/UI/KeyPlanSection.cs、CameraPreviewWindow.cs、docs/camera-manager/spec.zh-TW.md、adr、roadmap、lessons L-033；PlanProjection.cs 平面投影），重用其規則與預設值（例：剖面轉 90°、眼前 1.5m 牆門窗略過、剖面線截入裁剪框、虛線加粗）；並調查 GitHub 上網頁 minimap／key plan／camera frustum on plan 的最佳實踐（three.js 第二相機正交俯視、xeokit NavCube／StoreyViews／plan view、That Open Plans 等）| Alex 2026-10-22 00:43；D-266–D-271；不 deploy、不 push  
  — 來源：`BACKLOG.md` 第 186 行
- `00:44` ［主 agent 紀錄］[PARENT] 2026-10-22 00:44 added imphaserounds (D-260–D-265). Next free D-266. 07:00 question list gains: Revit 出圖方式 (D-121: 1 MCP 批次 recommended / 2 Revit 按鈕 / 3 手動 / 4 APS).  
  — 來源：`ROUTINE_STATUS_NOTE.txt` 第 490 行
- `00:46` ［主 agent 紀錄］[PARENT] 2026-10-22 00:46 added imrevitviews (D-266–D-271). Next free D-272.  
  — 來源：`ROUTINE_STATUS_NOTE.txt` 第 491 行
- `09:33` ［排程代決（依 Alex 既有原則）］- 2026-10-22 09:33 [ROUTINE] imsnaplabelsr 完成（IMSNAPLABELSR_DONE，79ed27a spec＋d080579，statusKey 查進度選項名稱、查不到顯示「（已刪除的選項）」）；截圖看過：細分進度顯示「我的審圖中」，其他欄位中文 → imsnaplabelsrv 交 7.alex sonnet medium（ape 做、alex 驗）。imcpcdash 完成（IMCPCDASH_DONE）→ done，交 parent 回報 Alex（8 題）。照 [PARENT 09:27/09:30/09:31]：imphaserounds 改最優先（ape 10:00 後第一個開，sonnet high，併 imsetcodelabel），新增 todo imrevitparams、imperforated。ape 5h 77%（10:00 重置）先不開新建置；Codex 100% 至 13:19。  
  — 來源：`BACKLOG.md` 第 225 行
- `10:05` ［排程代決（依 Alex 既有原則）］- 2026-10-22 10:05 [ROUTINE] ape 10:00 已重置（5h 0%、週 25%）→ 照 [PARENT 09:27] 開 imphaserounds（8.ape sonnet high，off 16074ac，併 imsetcodelabel；D-260～D-265 在 16074ac docs 查無使用，但為避免與舊預留混淆改給 D-322～D-327、migration 0070）；rm_aichatr 同開於 8.ape sonnet medium（接續既有 worktree，不重做，0069）。imintegrate3（7.alex high）仍在跑，驗證 imintegrate3v 會交 8.ape。imperforated／imrevitparams／imreviewpeople 等 imintegrate3 完成當基底。alex 5h 10% 週 77% 留驗證；Codex 5h 100% 至 13:19。下一個空號 D-328／migration 0071。  
  — 來源：`BACKLOG.md` 第 228 行
- `13:46` ［排程代決（依 Alex 既有原則）］- 2026-10-22 13:46 排程：imperforated 完成（95ad2e9＋373616f）→ verify，開 imperforatedv（7.alex sonnet medium，建造者 8.ape）；截圖看過：展示 GLB 著色模式修後孔洞透光、CPC 寫實手機正常；截圖頁是測試頁，交驗證者補拍正式頁。結論：正式 GLB 本身沒有透光資訊，需 Revit／匯出端處理。自行決定：不在檢視器畫假孔（選項 c），照「不誤導」原則；其餘 3 題列 imperforatedq 等 Alex。開 immapdrafthide（8.ape sonnet low）。  
  — 來源：`BACKLOG.md` 第 251 行
- `13:58` ［排程代決（依 Alex 既有原則）］- 2026-10-22 13:58 排程：imperforatedv PASS → imperforated done（GLB 確認無透光資訊，屬 Revit 匯出端問題；檢視器不畫假孔維持）；imperforatedq 三題等 Alex。rm_notifyr（Codex）完成 → 開 rm_notifyrv（7.alex sonnet medium，跨 harness 驗證）。  
  — 來源：`BACKLOG.md` 第 255 行
- `21:15` ［排程代決（依 Alex 既有原則）］- 2026-10-22 21:15 [ROUTINE] ptedit（併 ptm7）完成 PTEDIT_DONE → verify，pteditv 交非 ape（Codex 23:25 後）。IM 仍有可派：immeetbrief3（immeetbrief2 已於 06:24 驗證通過，前幾輪漏派）與 imrevitparams（研究先行）→ 都在 8.ape 開跑。immeetbrief3 基底用 immeetbrief2 分支 327ce32（immeetbrief2 尚未整合上正式站）。  
  — 來源：`BACKLOG.md` 第 301 行
- `22:00` ［排程代決（依 Alex 既有原則）］- 2026-10-22 22:00 [ROUTINE] immeetbrief3 完成（IMMEETBRIEF3_DONE，D-400～402、mig 0083；自動定格預設關、打開時檢查＋每日 18:00 排程、橫幅寫明不是準時鬧鐘；定格後已結案加註；復原通知不露欄位代碼；1560 單元、61 e2e 過）與 imrevitparams 完成（IMREVITPARAMS_DONE，D-403～405、mig 0084 未用；CPC GLB 目前沒有尺寸參數，改用另存參數檔＋IFC 轉換器，真實 CPC 本體命中 2,097／2,119；Revit 端 C# 只寫規格未實作；示範頁 /demo/revit-params）→ 都 verify；截圖看過正常（imrevitparams 手機欄位編輯器列表到底、3D 沒選取高亮，交驗證判斷）。兩者 ape 建、需非 ape 驗證：alex 週 81% 擋 → 排 Codex 23:25 重置後（rm_pptrv 仍第一）。天數用日曆天不跳假日交驗證判斷。沒有新啟動：IM 只剩 rm_manuals（等整合）。額度 21:55 ape 5h 52% 週 40%。  
  — 來源：`BACKLOG.md` 第 303 行
- `22:24` ［Alex 決定／指示／待決］- 2026-10-22 22:24 排程：依 [PARENT 22:09～22:19] 新增 rm_pptsheetmap、rm_ppttheme（皆 8.ape sonnet medium 22:22 開跑，ape 5h 52%）與 imrevitexporter（blocked 等 imrevitparamsv）；rm_pptq 兩題已答、關閉；imcpcpdf 經 Alex 核准：imcpcpdfv 通過後 6 份 PDF 可上傳正式站 CPC 專案（僅此）。rm_ppttheme 基底選 rm_ppt 最新 9fddc15、rm_pptsheetmap 基底選 immarkupcompare 532bf13 以重用對照畫面（調度者自行決定）。  
  — 來源：`BACKLOG.md` 第 304 行

## 2026-10-23

- ［Alex 決定／指示／待決］- imperforatedq | wait-alex | | | | imperforated 的 Alex 題：(1) 沖孔板補透光用 a 改 RevitMCP 匯出器輸出 Cutouts→MASK（需 Alex 指出匯出器原始碼位置）或 b 在 Revit 把 102 片「Aluminium Sheet」建成真孔幾何（選項 c 檢視器畫假孔已依「不誤導」排除）；(2) 確認那 102 片 Aluminium Sheet 就是沖孔板；(3) Revit 同視角對照圖放哪 | 排程 13:46 整理  
  — 來源：`BACKLOG.md` 第 42 行
- ［Alex 決定／指示／待決］[PARENT 07:26] Alex 開新項目 imexportbundle（8.ape sonnet medium，SPEC FIRST＋可點 demo /mock/exportbundle）：Revit 整合輸出外掛規格。一鍵輸出「版本包」= .glb 模型 + 圖說 PDF（Revit 內建 PDF 匯出）+ 參數 JSON（面積表、房間、樓層，沿用已驗證的 imrevitparams 格式）+ manifest（版本號、時間、輸出人、Revit 檔名、各檔雜湊）。Issue Manager 收到後以同一版本號一起更新 3D、圖說、面積表；版本不一致時介面明確標示。未來 APS 來源沿用同格式，介面標示來源（Revit 外掛／APS）。imrevitexporter 範圍併入此項（原只輸出參數）。demo 只是 mock，不寫正式站、不改 Revit。完成後照常另一帳號驗證，問題列給 Alex（含成本）。Alex 想法：參數跟著模型版本走、盡可能連動、版本控制可直接對應。  
  — 來源：`ROUTINE_STATUS_NOTE.txt` 第 908 行
- `00:40` ［排程代決（依 Alex 既有原則）］- 2026-10-23 00:40 排程：immarkupcomparev PASS→immarkupcompare done；imcpcpdfv PASS→imcpcpdf done（下一步 imcpcprod 排在 ape 01:00 後）；pteditv FAIL→pteditr 待 ape 01:00 後，F02 範圍列 pteditq 問 Alex，其餘 F01/F03～F06 照不誤導／可用原則直接修；immeetbrief3v FAIL→immeetbrief3r（ape sonnet high，D-427～428，mig 0091）；rm_pptr2 提前在 ape 5h 重置前 25 分開（用滿本窗）；rm_pptsheetmapr、rm_pptthemer2 完成→驗證開在 7.alex；imrevitparamsv 開在 7.alex。Codex 今日 1 個份額已用（immeetbrief3v），不再開。下一個空號 D-429、migration 0092。  
  — 來源：`BACKLOG.md` 第 316 行
- `00:51` ［Alex 決定／指示／待決］- 10-23 00:51 imrevitparamsv、rm_pptsheetmaprv、rm_pptthemer2v 三項 PASS→done。imrevitparams Q2～Q5 依 ALEX RULE 代決（欄位名稱先個人 localStorage、權限延 imperms；預設只顯示標準欄；長度預設 mm；參數檔另存）。rm_pptsheetmap F7 判低優先、不退回，正式版修。真正 Alex 題列 rm_pptthemeq／rm_pptsheetmapq／imrevitparamsq。  
  — 來源：`BACKLOG.md` 第 317 行
- `07:26` ［Alex 決定／指示／待決］- imrevitexporter | done（10-23 07:26 Alex 決定併入 imexportbundle） | C:\Project\REVIT_MCP_study＋issue-manager 新 worktree | sonnet high | 只限 Claude | SPEC FIRST；Revit add-in 一次匯出 GLB＋參數檔（格式照 imrevitparams）；可在 MSI 開關 Revit 測試 | [PARENT 22:09]  
  — 來源：`BACKLOG.md` 第 77 行
- `07:35` ［排程代決（依 Alex 既有原則）］- 2026-10-23 07:35 [ROUTINE] 依 [PARENT 07:26～07:28] 開 imexportbundle（D-545～549，off main，sonnet medium）、ptedit2（葡萄牙，sonnet medium）、rs_apscost（研究，sonnet low），皆 8.ape；imrevitexporter 併入 imexportbundle；pteditq 已答 (b)。C: 開跑後降到 7.4 GB（< 8 GB），下一輪起每輪最多開 1 件。ape 週額 47% 於 10:00 重置，大多會過期 → 用滿。下一個 D-550、migration 0099。  
  — 來源：`BACKLOG.md` 第 363 行
- `08:16` ［Alex 決定／指示／待決］- 2026-10-23 08:16 [ROUTINE] [PARENT 08:03] imreviewpeopleq 關閉（Sir→詹世州、不覆蓋、0081 帶預設、帳號另問）；[PARENT 08:04] imcpcwriteq 關閉（0073＋0075、fingerprint 工具）。imcpcprod2v PASS：正式站 PDF 上傳與 imcpcwrite 寫入合併成一次正式站作業（備份一次），等 imcpcwritev；因驗證者查到「文件模組不能刪除文件、上傳後第 1 版永遠留在歷程」，imcpcprod2q Q1（A9 殘留 Markdown 符號）改為上傳前請 Alex 答（A 照傳成本 0／B 先修約 30 分鐘、8.ape 5h 約 5%）。imexportbundlev FAIL（F1 誤報已匯入）→ imexportbundler 8.ape sonnet medium（重驗派另一帳號），D-550。imexportbundleq 依驗證者分類：問 Alex Q1 版本號、Q5 舊版保留（先查 Supabase 台幣價再問），Q3 只通知，Q2（等 Revit 實機驗證）與 Q4（交 imperms）不問。ptedit2 DONE → ptedit2v 派 Codex（Codex 週額度已於 10-23 重置，週 1%），其 3 題（部署時機、R2、方案名可編輯）驗證後再整理。rs_apscostr 完成（只改文字）→ rs_apscost 維持 done。下一個 D-551，migration 0099。  
  — 來源：`BACKLOG.md` 第 367 行

## 2026-10-31

- `01:12` ［Alex 決定／指示／待決］- [PARENT 10-31 01:12] Alex 01:08 指示「issue manager roadmap 做完、持續開發」。imdispatch 照額度持續派，不空轉；ROADMAP（issue-manager/docs/ROADMAP.md）57 項 ⬜ 中，跳過 🙋／🔌（等 Alex 或外部），其餘依序：①已實作未併：imcalkanban（D-664）、imdocsrail（D-663）、immindmap3（D-662）、imgoal（D-720）、imphases（D-725/726）、im3dnav 系列 → 驗證 PASS 後排下一次整合分支（只 commit，部署仍等 Alex）②其餘頁面轉卡片＋data-help 導覽（ROADMAP 第17行）③操作手冊 docs/guides 同事／技師／業主三份（第165行）④路線圖第二輪 D-272～277（第164行）⑤身高預設組 imeyeheight（第39行）⑥依審核決定轉換分組方塊（第90行）⑦Revit 版本包（第147行）。每項完成勾 ROADMAP＋CHANGELOG 同 commit。CPC 階段寫正式庫（imphases --apply）等 Alex 填規劃核定日與文號，勿自行寫。  
  — 來源：`BACKLOG.md` 第 727 行

