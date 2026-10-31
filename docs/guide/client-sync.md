# 啟服前同步玩家客戶端

`RO3Emu.exe start`／`host` 可明確啟用 `ClientSync`。未啟用的舊設定及獨立 `Ro3Emu.Server` 維持原模式；程式會提示尚未設定發行配對。此功能不會自行更動現有正式機設定。

獨立 LAB 可把 `client-sync.lab.settings.json` 的內容放入 LAB home 的 `RO3Emu.settings.json`，把 `client-compatibility.v1.0.24.json` 放在同一 home，設定獨立客戶端複本與 RSA 3072 位元以上的 LAB 簽署私鑰。所有相對設定路徑以 `--home` 為準。範例必須搭配實際版本為 `1.0.24` 的伺服器發行檔；原始碼預設開發版本不同時會拒啟。金鑰應另行準備，不隨範例或 GitHub 提供。

流程依序為：

1. 讀取發行配對的 ServerVersion 與 RequiredGenesisSequence，選填 ReleaseId／BuildId；不使用資料夾名稱推版本。
2. 版本不符或原始檔 SHA 不符時，共用 `RO3Emu.exe update` 的 InstallFlow。先驗證官方簽署 manifest 符合配對，才做安裝計畫或寫入；若官方來源只提供另一個更新版，直接停止。
3. 完整核對來源 SHA，使用同一 server URL／gate 公鑰重新修補，驗證／匯出資料。匯出資料另綁 `client-source.json` 的來源、export 戳記及核心資料實際檔案集合／SHA；缺少舊來源契約時先完整重匯，缺表或 full fingerprint 不符則拒啟。已整理資料維持既有 `curated-import.json` 完整契約。
4. 已有相同 Genesis identity、patch 指紋及 serverVersion 時，重新驗簽及驗證選中套件完整 chunks／整檔 SHA 後重用。改版才建立同卷完整更新站候選；保留原有各通道、公告、登入器與所有 chunks。不可變內容優先用 hardlink 重用，失敗才複製。
5. 原 repo 外的穩定發行鎖與各 Publisher 入口共用，從讀原通道、複製及候選驗證持有至切換完成，避免覆蓋同時發布的新版。候選簽章、來源、每塊原始 SHA、整檔 SHA 及各通道相依檔都通過後，切換整個 snapshot；所有路徑及祖先拒絕 symlink／junction，候選只能是同 parent 的生成名稱。切換為兩次目錄 Move，中間可能短暫沒有原路徑；第二次失敗會嘗試還原，還原本身失敗時需人工處理。舊目錄保留為 `.rollback-*`；失敗保留候選、不開服。已有使用中的伺服器連接埠時，先停止同步流程，不會自動中斷它。
6. 啟動伺服器，帶入 `ClientRelease.Required=true` 與新套件契約位置。Server 在建立資料庫及開始監聽前核對契約版本與核心資料完整性；`GET /ro3emu/client-release` 只回必要套件的公開欄位，不回設定路徑或私鑰。嚴格流程不會在已產生套件後重複修補；啟動及 R 重新開遊戲均傳入本次已驗 Genesis sequence，供原生版本文字顯示。
7. 玩家登入器在每次啟動與按下開始遊戲時，重新取得正式機契約、驗證簽署通道及 manifest，核對 Genesis 身分、修補指紋、伺服器版本與完整檔案 SHA。缺檔或損壞即同步，完成後再次確認正式機仍要求同一套件；無法確認時停止啟動。遊戲執行中只讀取及檢查，需改動檔案時要求先關閉遊戲；同版最多四開與成功啟動後關閉登入器的行為維持。

玩家正式設定必須使用 `requireServerClientRelease: true`、`requireSignedChannel: true` 與既有受信公鑰。原始碼內的未簽署 loopback 範例明確關閉契約，不能拿來取代正式設定。登入畫面顯示的 Build 是本次驗過的 Genesis sequence；版本相容性仍由完整契約與檔案內容決定。

整套啟用時，須先備份既有設定，將原本直接啟動 `Ro3Emu.Server.exe` 的入口改為整合版 `RO3Emu.exe host --no-launch --yes --home <home> --client <伺服器端專用客戶端> --public <既有伺服器 IP>`，保留現有連接埠與站台設定。新登入器須在伺服器契約及對應簽署套件就緒後，才透過既有自我更新通道提供給玩家；只換登入器、未先準備伺服器端契約會停止啟動。不要將範例直接覆寫正式設定。

完整候選準備只更新本機設定所指向的公開目錄。外部 HTTP 站台的目錄指向及正式機啟用仍應依部署流程驗證；本文件不表示任何正式機已部署或玩家端已驗收。
