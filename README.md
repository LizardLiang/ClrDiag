# clrdiag — 終端機版 .NET 記憶體 / 執行緒診斷主控台

不需要 Visual Studio 就能做到 VS「診斷工具」視窗裡**不需中斷程式**的那些事：
記憶體走勢、受控堆疊快照與比較、型別直方圖、根參考鏈、執行緒呼叫堆疊，
外加把建置與開發伺服器的啟停收進同一個畫面。

工具本身**不綁定任何專案**：沒有設定檔也能監看、快照、分析任何載入 CLR 的行程
（.NET Framework 4.5+ 與 .NET Core / .NET 5+ 都可以，但目標行程必須是 64 位元）。
要用建置 / 啟動伺服器功能時，才需要在專案根目錄放一份 `clrdiag.json`。

## Prerequisites

- Windows x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for building, running from source, and installing as a .NET tool.

The target process must also be 64-bit. Diagnosing .NET Framework 4.5+, .NET Core, and .NET 5+ applications does not require modifying the target project.

## Quick Start

Build and run directly from this directory. The parent solution, its configuration, justfile, and Visual Studio are not required:

```powershell
dotnet build -c Release
dotnet run -c Release -- --list
dotnet run -c Release -- --pid 12345
```

## 使用

```powershell
clrdiag                 # 互動式儀表板
clrdiag --list          # 列出可監看的受控行程
clrdiag --pid 12345     # 直接監看某個行程
clrdiag --init          # 產生 clrdiag.json 範本
```

批次模式（不進互動介面，適合腳本、報告、問題回報）：

```powershell
clrdiag --snapshot --top 30      # 型別直方圖
clrdiag --threads                # 所有受控執行緒呼叫堆疊
clrdiag --roots "MyApp.Cache"    # 這個型別被哪個 GC 根握住
clrdiag --export                 # 快照輸出成 CSV
clrdiag --build Release          # 依設定建置一次
clrdiag --output [--pid N]       # 串流應用程式的 Debug/Trace 輸出，Ctrl+C 結束
clrdiag --render                 # 把八個面板渲染成純文字
```

## 設定檔 clrdiag.json

放在專案根目錄（會從目前目錄往上尋找），全部欄位都可省略：

| 欄位              | 用途                                                                      |
| ----------------- | ------------------------------------------------------------------------- |
| `buildProject`    | 建置目標（.sln / 專案檔）。省略時自動找根目錄的 .sln → .csproj             |
| `buildCommand`    | 建置執行檔。省略時：SDK 專案用 `dotnet`，舊式專案用 vswhere 找到的 MSBuild |
| `buildArguments`  | 建置參數，可用 `{project}` `{config}` `{root}` `{port}` 佔位符             |
| `configurations`  | `c` 鍵可循環的建置設定                                                    |
| `serveCommand`    | 啟動伺服器的執行檔。**省略時 `s`/`r` 鍵停用**，只能附加到既有行程          |
| `serveArguments`  | 啟動參數，同樣支援佔位符                                                  |
| `port`            | 預設連接埠（`--port` 可覆寫）                                             |
| `probeUrl`        | 健康探測網址，支援 `{port}`                                               |
| `processNames`    | 要尋找的行程名稱。留空 = 掃描所有載入 CLR 的行程                          |
| `appNamespaces`   | 視為「自己程式碼」的命名空間前綴。留空 = 以「非框架」近似判斷              |
| `reportDirectory` | CSV 輸出目錄                                                              |

其他常見情境：

```jsonc
// ASP.NET Core 自架
{ "serveCommand": "dotnet",
  "serveArguments": [ "run", "--project", "{project}", "--urls", "http://localhost:{port}" ],
  "port": 5000, "appNamespaces": [ "MyApp." ] }

// 監看 IIS 的工作行程（不由本工具啟動）
{ "processNames": [ "w3wp" ], "probeUrl": "https://localhost/health" }
```

## 畫面與按鍵

八個面板都有編號：上排三個固定顯示 build / serve / process 狀態，中間是可切換的五個檢視。
按數字鍵選取面板，**再按同一個數字鍵就放大**（隱藏上排、佔滿整個畫面），`Esc` 或同號鍵還原。
主鍵盤上排數字與數字鍵盤都可以用。

| 鍵  | 面板    | 內容                                                                 |
| --- | ------- | -------------------------------------------------------------------- |
| `0` | build   | 建置設定與最近一次結果；放大後列出**全部**錯誤（完整訊息不截斷）與警告 |
| `1` | serve   | 伺服器狀態與健康探測；放大後是完整探測記錄表與延遲統計               |
| `2` | process | 附加行程的記憶體／CPU；放大後是全部計數器與逐秒取樣歷史              |
| `3` | 記憶體  | 私有／工作集／受控堆疊／Gen2／LOH／CPU 走勢，GC 次數，成長量         |
| `4` | 堆疊    | 型別直方圖、與基準快照的差異、根參考鏈                               |
| `5` | 執行緒  | 受控執行緒清單與呼叫堆疊（● 標記自己的程式碼）                       |
| `6` | 記錄    | 設定解析結果、建置與伺服器輸出                                       |
| `7` | 輸出    | 應用程式的 `Debug.WriteLine` / `Trace.WriteLine`（OutputDebugString） |

被選取的面板框線會變粗；放大 build / serve 面板時 `↑↓` 捲動它的內容。

| 鍵        | 動作                                     |
| --------- | ---------------------------------------- |
| `b` / `c` | 建置 / 切換建置設定                      |
| `s` / `x` | 啟動 / 停止伺服器（需要 `serveCommand`） |
| `r`       | 停止 → 建置 → 啟動，並保留監看歷史       |
| `n`       | 取受控堆疊快照                           |
| `Shift+T` | 只更新執行緒堆疊（比完整快照快很多）     |
| `d` / `D` | 設為比較基準 / 清除基準                  |
| `o` / `/` | 切換排序 / 過濾型別（`Esc` 清除）        |
| `f`       | 對選取型別搜尋 GC 根參考鏈               |
| `e`       | 匯出 CSV                                 |
| `a`       | 自動快照（每 5 分鐘，長時間追蹤成長用）  |
| `p`       | 切換監看的行程                           |
| `q`       | 離開                                     |

## 應用程式輸出（OutputDebugString）

Visual Studio「輸出視窗」在終端機工作流裡缺掉的那一塊：應用程式自己寫的
`Debug.WriteLine` / `Trace.WriteLine`（都是走 Win32 的 `OutputDebugString`），
沒有偵錯器附加時原本會直接消失，現在會被獨立收進按鍵 `7` 的輸出檢視。

- **`7`** 切到輸出檢視；**`g`** 切換「只看附加的 PID」／「全部行程（多一欄 PID）」，
  切換不會遺失歷史（攔截層一律全收，只有顯示層依範圍過濾）。`/` 過濾文字、`Esc` 清除，與其他檢視共用。
- 緩衝區固定 5000 筆（獨立於「4 記錄」的 2000 筆，容量不可調整），滿了覆蓋最舊的，
  標頭會顯示已丟棄筆數。
- **非互動串流**：`clrdiag --output [--pid 12345]`，逐行印到主控台、可直接 `> file` 導向保存
  （TUI 的緩衝區離開就消失，要保存就用這個）；`Ctrl+C` 結束。本 repo 可用 `just diag-output`。
- **DBWIN 同一時間只能有一個監聽者**，與 SysInternals DebugView 等工具互斥；
  誰先啟動誰就攔到，慢的一方會在「4 記錄」看到警告（互動模式）或印出原因並以非零碼結束
  （`--output`），其餘功能不受影響。
- **Release / Testing 建置會把 `Debug.WriteLine` 編譯移除**，屆時檢視只剩 `Trace.*` 的訊息；
  空狀態文案會提醒這件事，不要誤以為功能壞了。
- 同時監聽本機工作階段與 `Global\` 兩組具名物件，跨工作階段（例如附加到以服務身分執行的
  w3wp）才需要後者；`just dev` 這種同一使用者工作階段的情境本機那組就夠用。

## 找記憶體洩漏的流程

1. `clrdiag`，讓它掛上執行中的行程（或按 `s` 啟動）。
2. 按 `n` 取第一次快照，再按 `d` 設為基準。
3. 操作要測的功能。
4. 再按 `n`。表格會多出 `Δ MB` / `Δ 數量`，按 `o` 切到 SizeDelta 排序，成長最多的型別排最前。
5. 對可疑型別按 `f` 找出握著它的參考鏈。
   - 回報「沒有任何 GC 根 → 屬於等待回收的垃圾」= 不是洩漏，只是還沒被 GC。
   - 出現 `StrongHandle` / 靜態欄位之類的根 = 真的被握住。

## 實作重點與已知限制

- **不是除錯器，沒有中斷點。** 走 ClrMD + Windows PSS 行程快照（`CreateSnapshotAndAttach`），
  目標行程不會進入除錯狀態，工具異常結束也不會把目標一起帶走。
  需要中斷點／逐步執行時用 VS Code 的 `"type": "clr"` attach（64 位元 + portable PDB）。
- **必須是 64 位元目標行程**：ClrMD 要載入同位元數的 DAC，32 位元行程不會出現在 `--list`。
- **快照成本**實測約 2.2 µs／物件：10 萬物件 0.6 秒、236 萬物件 5 秒、580 萬物件 6–8 秒。
  期間 UI 不卡（背景執行緒），但目標行程會被 PSS 複製一次。
- **`Gen0 預算`** 來自 `.NET CLR Memory\Gen 0 heap size` 計數器，它回報的是 gen0 配置預算
  而非存活大小，數字很大是正常的。Gen1／Gen2／LOH 才是實際大小。
- **請求速率不是計數器來的**：很多機器上 `ASP.NET Applications` 類別沒有任何執行個體，
  因此 serve 面板顯示的是每 5 秒對 `probeUrl` 探測的狀態碼與延遲。
- **執行緒狀態是推測值**：以最上層框架的特徵字串分類（`lock-wait`／`db`／`network`…），
  ClrMD 4 已移除 `BlockingObjects`，無法直接得知等待中的鎖物件。
- **`appNamespaces` 留空時**的 ● 標記是「非框架程式碼」，會把第三方套件也算進來；
  想精確標記自己的組件就把前綴填上。

## Install As A Tool

Create a local NuGet tool package and install it. `--tool-path` keeps the installation in this directory for verification; use `--global` when ready.

```powershell
dotnet pack -c Release --output .\artifacts\packages
dotnet tool install --tool-path .\.tools --add-source .\artifacts\packages ClrDiag.Console
.\.tools\clrdiag --list
```

For a global installation:

```powershell
dotnet tool install --global --add-source .\artifacts\packages ClrDiag.Console
clrdiag --list
```

After installation, run `clrdiag` from any .NET project directory. Run `clrdiag --init` only when that project needs build or server integration.

## Publish Without The SDK

To distribute a Windows x64 single-file executable that does not require the .NET SDK:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\publish\win-x64
.\artifacts\publish\win-x64\clrdiag.exe --list
```
