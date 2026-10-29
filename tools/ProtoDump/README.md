# ProtoDump

將 protobuf `FileDescriptorSet` 還原為 `.proto`，或讀取既有 C# Reflection 反編譯文字中的常數 base64 descriptor。工具不載入或執行 client assembly。

```powershell
dotnet build tools/ProtoDump/ProtoDump.csproj -c Release
dotnet tools/ProtoDump/bin/Release/net10.0/ProtoDump.dll <descriptor.pb> <新的輸出目錄>
```

匯出內容保留 message／field／enum／enum value／file／service／method／oneof／extension range 的可表示 options、reserved names／ranges、extension 宣告、public／weak import、default、json_name、proto3 optional、map 與 packed。自訂 option 以 descriptor 的 extendee、欄位編號與型別解析，不依名稱推定儲存或同步行為；整數、字串等 scalar 與有 schema 的 aggregate 使用相同路徑。

未知 option 欄位、缺少 schema 的 option message／enum、未解析的 `uninterpreted_option`、不支援的 editions／group、map 或 synthetic oneof 無法以原語法表示的中繼資料，均明確拒絕。所有檔案先在記憶體完成轉換；option 轉換失敗時不寫入部分 `.proto`。

`map_entry=true` 由 `map<K,V>` 語法交給 protoc 重建；proto3 optional 的 synthetic oneof 也由編譯器重建。這可能改變 descriptor 的宣告排列順序，完整名稱、欄位編號、oneof 關係與 options 的語意必須一致。

## 往返回歸

測試需要 Python 的 `google.protobuf` 與既有 `protoc`；不下載工具、不啟動 server。輸出應指定獨立 LAB 目錄。

```powershell
python tools/ProtoDump/tests/roundtrip.py `
  --tool tools/ProtoDump/bin/Release/net10.0/ProtoDump.dll `
  --protoc <protoc.exe> `
  --include <含google/protobuf的根目錄> `
  --work <新的LAB目錄> `
  --descriptor <選用的完整client-descriptor.pb>
```

合成 schema 覆蓋負數與 repeated packed 自訂 options、字串跳脫與中文、aggregate、message／oneof／enum value／method 選項、reserved、extension range、map、proto3 optional、default 與 json_name。另注入未宣告的 option，驗證工具拒絕且不產生 `.proto`。選用的完整 descriptor 會逐一比對所有來源檔案的語意；相依 WKT 的增加另列，不混稱新增遊戲訊息。

回歸比較以完整名稱／欄位編號辨識宣告，保留 enum value 順序；只忽略來源位置與宣告排列順序。options 以該次 descriptor pool 解碼，仍含未知位元組就拒絕比較，避免把無法辨識的資料當成相同。
