# Genesis 訊息型別與編解碼前置條件

權威來源為唯讀 `G:\Genesis Launcher\ROO_PC`，以 Genesis 140083／2026.10.30.1 的原件綁定。
`Assembly-CSharp.dll` SHA-256 為 `5627e799d0b8d1c0ab5843d3149cdeb9c89425daa1442a36253a351fbc207bbe`，
`xlua.dll` 為 `c189e0fdfb23782d381dfd761a4d746c1e195508ed8eecbe25f23a39bfc7309a`。
這份證據不與舊 Asia 客戶端的訊息清冊混用，也不代表已取得原服封包。

目前有 34 個缺失型別、35 個 request／response slot，涉及 25 個 OPCODE；其中 request 為 13 個、
response 為 22 個。缺少的是具名型別的欄位編號與 wire type，不能由 enum、訊息名稱或對側結構推造。

## 省略 body 仍經過型別編碼

`NW_NetworkManager.SendMessageToGameServer` 把省略的 body 換成空 table，但仍以 map 的 `req_name`
呼叫 `pb.encode_unsafe`，備援路徑也呼叫 `pb.encode`。因此某個 sender 沒有傳參數，不能單獨證明
該 request 有零欄位，或可以略過具名型別直接送出 header。

本輪 IDA 只新增追查兩個編碼入口，沒有重跑已閉合的 descriptor 備援分析：

- `encode` 入口 `0x180096200`：`0x180096259` 取得型別，`0x180096282` 傳入型別存在條件與錯誤文字給檢查 helper，`0x180096293` 才檢查 table。
- `encode_unsafe` 入口 `0x180096520`：對應順序為 `0x180096579`、`0x1800965a2`、`0x1800965b3`。

上述是靜態呼叫順序；本輪沒有另外追查檢查 helper 的完整內部，也沒有實測原服的錯誤結果。
可確定的是，這兩條可見路徑沒有提供把缺失型別直接視為空封包的依據。

## 接收分派與其他邊界

`NW_ServerClientHandler` 的 `NoDecodeTable` 有 16 個 literal 項目，完整解析後與缺失的 25 個 OPCODE
交集為零。一般接收路徑以 `res_name` 呼叫 `decode_unsafe` 後才交給 consumer；
`RegisterDirectHandler` 的存在，不能證明任一缺失訊息已使用免解碼分派。

`QueryStallLine` 的 33402 省略 body，仍受上述具名編碼邊界限制。
76009 的既有 request descriptor 確為零欄位，但缺失的是 response，不能把兩側互換。
83244 已知的 response consumer 也不能提供缺失 request 的欄位。

`RO3.PingTiming.HeartbeatTiming.Prepare` 在 OPCODE 不等於 1 時直接回傳原 bytes；這 25 個目標均不等於 1，
該方法沒有提供其隱藏 schema。Managed 字面查詢命中的 `System.Text.Json.get_GeneratedSerializerOptions`
屬框架序列化功能，不能當成遊戲的 GeneratedCoder。

原件 `MG_ServerClientMsg_DEF` 目前是空模組。既有日誌沒有查到指定訊息的可用記錄，且日誌世代未綁定；
這不是原服從不發送它們的證明。沒有擷取新封包、執行遊戲、附掛程序或執行目標 Lua。

本輪新增可證完整 schema 為零，現有缺 descriptor 的拒絕機制保持不變。要解除這些未知，仍需要
可驗證的型別註冊資料、帶欄位編號的 descriptor，或原服實發證據；不能用 alias、空回覆或忽略解碼假成功。
