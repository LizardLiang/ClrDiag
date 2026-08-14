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
clrdiag --dap                    # 非互動除錯：印出每次中斷的堆疊/區域變數/監看，Ctrl+C 結束
clrdiag --send '<json>'          # 對本機專案的除錯指令管道送一個指令並印出回覆
clrdiag --render                 # 把九個面板渲染成純文字
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
| `dapEnabled`      | 是否啟用除錯功能（spawn netcoredbg、開具名管道）。預設 `true`             |
| `dapAdapterPath`  | netcoredbg 執行檔路徑。省略時：`PATH` → mason 預設安裝路徑                |
| `dapBreakpoints`  | 啟動時載入的中斷點清單，格式 `"路徑:行號"`（見下方「除錯」一節）          |
| `dapWatches`      | 啟動時載入的監看運算式清單                                                |

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

九個面板都有編號：上排三個固定顯示 build / serve / process 狀態，
中間的主區是六個分頁，分頁列就在主區上方（作用中的分頁以 aqua 粗體加底線標示）。
按數字鍵選取面板或切換分頁，**再按同一個數字鍵就放大**（隱藏上排、佔滿整個畫面），
`Esc` 或同號鍵還原。主鍵盤上排數字與數字鍵盤都可以用。

上排面板：

| 鍵  | 面板    | 內容                                                                  |
| --- | ------- | --------------------------------------------------------------------- |
| `0` | build   | 建置設定與最近一次結果；放大後列出**全部**錯誤（完整訊息不截斷）與警告 |
| `1` | serve   | 伺服器狀態與健康探測；放大後是完整探測記錄表與延遲統計                |
| `2` | process | 附加行程的記憶體／CPU；放大後是全部計數器與逐秒取樣歷史               |

主區分頁：

| 鍵  | 分頁   | 內容                                                                 |
| --- | ------ | -------------------------------------------------------------------- |
| `3` | 記憶體 | 私有／工作集／受控堆疊／Gen2／LOH／CPU 走勢，GC 次數，成長量         |
| `4` | 堆疊   | 型別直方圖、與基準快照的差異、根參考鏈                               |
| `5` | 執行緒 | 受控執行緒清單與呼叫堆疊（● 標記自己的程式碼）                       |
| `6` | 記錄   | 設定解析結果、建置與伺服器輸出                                       |
| `7` | 輸出   | 應用程式的 `Debug.WriteLine` / `Trace.WriteLine`（OutputDebugString） |
| `8` | 偵錯   | 中斷點／監看清單；中斷後改成呼叫堆疊 + 區域變數／監看結果（見下方「除錯」一節） |

被選取的面板框線會變粗；放大 build / serve 面板時 `↑↓` 捲動它的內容
（此時主區是那個面板的內容，分頁列會一併收起）。

最下面是單列狀態列：左邊是目前面板的按鍵與全域按鍵，右邊永遠是最新一則狀態訊息。
終端機太窄時按鍵提示會先讓位、狀態訊息留到最後。完整按鍵表按 `?`（會寫進 `6` 記錄分頁）。

| 鍵                | 動作                                                |
| ----------------- | --------------------------------------------------- |
| `b` / `c`         | 建置 / 切換建置設定                                  |
| `s` / `x`         | 啟動 / 停止伺服器（需要 `serveCommand`）             |
| `Shift+S`         | 準備／取消「下次 `s` 或 `r` 在除錯器下啟動」（見下方「除錯」一節） |
| `r`               | 停止 → 建置 → 啟動，並保留監看歷史                   |
| `n`               | 取受控堆疊快照                                       |
| `Shift+T`         | 只更新執行緒堆疊（比完整快照快很多）                 |
| `d` / `D`         | 設為比較基準 / 清除基準                              |
| `o` / `/`         | 切換排序 / 過濾型別（`Esc` 清除）                    |
| `f`               | 對選取型別搜尋 GC 根參考鏈                           |
| `e`               | 匯出 CSV                                             |
| `a`               | 自動快照（每 5 分鐘，長時間追蹤成長用）              |
| `p`               | 切換監看的行程                                       |
| `w`               | 新增／移除監看運算式（已存在的會被移除，行內輸入）   |
| `F5`              | 續行                                                  |
| `F10`             | 下一步（step over）                                  |
| `F11`             | 進入函式（step in）                                  |
| `Shift+F11`       | 跳出函式（step out）                                 |
| `F6`              | 暫停                                                  |
| `q`               | 離開                                                  |

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

## 除錯（.NET 8+，中斷點）

ClrDiag 是 .NET 8+ 目標（Windows x64）的除錯前端：它自己 spawn
[netcoredbg](https://github.com/Samsung/netcoredbg)（Samsung，MIT 授權）並直接驅動——**ClrDiag
是唯一的 DAP（Debug Adapter Protocol）客戶端**，沒有代理、沒有第二個觀察者。
Neovim（或任何送 `--send` 的客戶端）不是 DAP 客戶端，只透過專案範圍的具名管道送「中斷點在哪」
「監看什麼」「續行/單步」這類意圖過去；中斷後的呼叫堆疊、區域變數、監看結果一律顯示在 ClrDiag
自己的 `8 偵錯` 分頁。這與 `n` 鍵的堆疊快照（ClrMD + Windows PSS 行程複本）是兩條獨立路徑：
快照不會讓目標進入除錯狀態，除錯階段也不會擋到快照——兩者可以同時使用。

### 需求與設定

- Windows x64，目標行程 64 位元、.NET 8 以上（與其他功能一致，`--list` 只會列出 64 位元受控行程）。
- 需要安裝 [netcoredbg](https://github.com/Samsung/netcoredbg)。解析順序：
  `clrdiag.json` 的 `dapAdapterPath` → `PATH` → mason 的預設安裝路徑
  （`%LOCALAPPDATA%\nvim-data\mason\packages\netcoredbg\netcoredbg\netcoredbg.exe`）。
  找不到會在 `6 記錄` 印出明確的錯誤，不會靜默失敗。
- `dapEnabled: false` 可整個關閉除錯功能（不 spawn 任何東西、不開具名管道）。
- `dapBreakpoints` / `dapWatches` 是**啟動時的初始清單**，格式見上方設定檔表格；
  執行期用 Neovim 或 TUI（`w` 鍵）新增／移除的變更不會寫回設定檔——與 `buildConfiguration`
  等其他欄位一致，設定檔只在啟動時讀一次。路徑的 `/` 與 `\` 都接受（會統一正規化成 `\`
  再送給 netcoredbg 比對）；大小寫也不分。

### 使用方式

1. `clrdiag`（照常啟動）；`8` 切到偵錯分頁。netcoredbg 不會一開機就啟動——第一個中斷點動作
   （從 Neovim 或 `--send` 送 `setBreakpoint`）才會 spawn 它並附加到目前監看的行程。
2. 在 Neovim 設定中斷點（見下方設定），或直接 `clrdiag --send` 手動測試。
3. 命中中斷點時 `8` 分頁自動顯示呼叫堆疊（`●` 標記自己的程式碼，判斷方式與 `5 執行緒`
   分頁一致）＋選取框架的區域變數＋全部監看運算式的求值結果。`↑↓` 切換選取的框架。
4. `F5` 續行、`F10` 下一步、`F11` 進入函式、`Shift+F11` 跳出函式、`F6` 暫停——
   VS/VS Code 慣用的功能鍵，Neovim 那邊送同樣的指令一樣有效，兩邊看到的狀態一致。
5. `w` 在 TUI 裡新增／移除監看運算式（已存在的運算式再輸入一次會被移除）。
6. 未驗證的中斷點（例如原始碼路徑大小寫或正規化跟 PDB 對不起來）一律顯眼標示
   `○ 未驗證` 加上 adapter 回報的原因，不會悄悄失效。

**啟動路徑上的中斷點**（例如 `Program.cs` 最前面幾行）用 attach 搆不到，因為附加時程式早就
跑過去了：`Shift+S` 準備「下次 `s` 或 `r` 在除錯器下啟動」，之後按 `s`（或 `r` 重建並重啟）
會改用 DAP `launch` 啟動 `serveCommand`/`serveArguments`（原樣重用，`{port}` 等佔位符照舊），
serve 面板顯示 `RUNNING (debug)`；`x` 這時會走 DAP `terminate` 而不是直接砍行程。
偵錯目標的行程 id 學到後會自動接管 `ProcessMonitor`（狀態列與 `6 記錄` 會宣告切換，不會自動換回，
要換行程用 `p`）。

> ✅ **`dotnet run` 這類 wrapper 也支援**：如果 `serveCommand` 是 `dotnet run ...`
> （本文件範例的預設寫法），netcoredbg 的 `launch` 直接對它送命令只會附加到 `dotnet run`
> 這個外層行程——它另外開的子行程才是真正的 app，中斷點原本不會命中。`Shift+S` 現在會偵測
> 出這種 wrapper 寫法（`dotnet` + 第一個參數是 `run`），改走「啟動 wrapper（不受除錯器控制）
> → 用 Win32 Toolhelp32 直接查 wrapper 的直接子行程（比掃描全部行程快得多，排除掉
> `conhost.exe` 這類無關的主控台輔助行程）→ 一偵測到子行程就立刻對它送 DAP `attach`」。
> 子行程出現到除錯器接手的間隔壓在毫秒級，`Main`／DI wiring 這類啟動路徑上的中斷點可以
> 穩定命中；serve 面板一樣顯示 `RUNNING (debug)`，`x` 一樣走 DAP terminate。
> 找不到子行程、wrapper 提前結束、或附加本身失敗，一律在 `6 記錄` 印出明確原因並清掉
> wrapper 行程，不會悄悄退化成「附加到 wrapper、中斷點全部不會命中」。
>
> 殘餘限制：偵測子行程之後仍有一段（通常個位數毫秒）追上去的時間，不是行程建立時的強制
> 暫停——極早、只有一兩行就執行完、中間沒有其他工作的啟動路徑仍可能撲空；只認得
> `serveCommand` 是 `dotnet` 且第一個參數是 `run` 的寫法，其他種類的 wrapper（例如批次檔、
> PowerShell 腳本）仍會直接附加到 wrapper 本身。真的撲空或用的是其他 wrapper，
> 一樣可以照原本的作法把 `serveCommand`/`serveArguments` 改指向建置好的組件本身，
> 例如 `"dotnet", ["bin/Debug/net8.0/MyApp.dll"]`，或直接指向 apphost（`MyApp.exe`）——
> 這樣完全沒有子行程可找，netcoredbg 直接 launch 目標本身。
> 一般 `s`/`r`（不經除錯器）不受影響。

**堆疊面板設定中斷點**（從 `4 堆疊` 選取型別直接下中斷點）目前沒有做——規劃書把它列為可選的
延伸目標，核心切面只做「編輯器設中斷點、ClrDiag 顯示結果」這件事。

### Neovim 設定

Neovim 客戶端獨立成自己的 repo：**[LizardLiang/clrdiag.nvim](https://github.com/LizardLiang/clrdiag.nvim)**
（MIT）。不依賴任何外部套件，唯一需求是 `clrdiag` 在 `PATH` 上——管道名稱由
`clrdiag --pipe-name` 問出來，特意不在 Lua 端另外實作一份雜湊邏輯。

```lua
{
  "LizardLiang/clrdiag.nvim",
  ft = "cs",
  opts = {
    -- root = "C:/path/to/project",  -- 省略時讓 ClrDiag 自行判斷（往上找 clrdiag.json / .git）
    -- keymaps = false,             -- 傳 false 完全自己接鍵；預設鍵見下
    -- notify_on_halt = true,       -- 中斷時是否跳通知
    -- jump_on_halt = false,        -- 中斷時是否自動把游標跳過去（含跨檔案：還沒開的檔案會自動開起來）
    -- icons = {                    -- gutter 圖示，可個別覆寫
    --   breakpoint = "●",            -- 已綁定（verified）的中斷點
    --   breakpoint_unverified = "○", -- 尚未綁定的中斷點（刻意跟已綁定的圖示不同）
    --   stop = "▶",                  -- 目前中斷所在行
    --   statusline_pause = "⏸",      -- statusline() 用的前綴圖示
    -- },
    -- highlights = {               -- 對應的 highlight group 名稱，可個別覆寫成自己的顏色
    --   breakpoint = "ClrDiagBreakpointSign",
    --   breakpoint_unverified = "ClrDiagBreakpointUnverifiedSign",
    --   stop = "ClrDiagStopSign",
    --   stop_line = "ClrDiagStopLine",
    -- },
  },
}
```

> LazyVim 使用者注意：`dap.core` extra 已經佔住 `<leader>d*`，其中 `<leader>db` 正好是
> nvim-dap 的切換中斷點，會跟下面的預設鍵直接對撞。傳 `keymaps = false` 再自己接一組
> （例如 `<leader>D*`）就能避開。

預設鍵(`opts.keymaps` 可覆寫個別項目，或整組傳 `false`)：

| 鍵                | 動作                             |
| ----------------- | -------------------------------- |
| `<leader>db`       | 切換游標所在行的中斷點           |
| `<leader>dw`       | 監看游標下的字（已存在會移除）   |
| `<F5>`             | 續行                             |
| `<F10>`            | 下一步                           |
| `<F11>`            | 進入函式                         |
| `<S-F11>`          | 跳出函式                         |

也提供對應的使用者指令（`:ClrdiagBreakpoint`、`:ClrdiagWatch [expr]`、`:ClrdiagWatchWord`、
`:ClrdiagRemoveWatch [expr]`、`:ClrdiagContinue`、`:ClrdiagStepOver`、`:ClrdiagStepIn`、
`:ClrdiagStepOut`、`:ClrdiagPause`、`:ClrdiagConnect`、`:ClrdiagDisconnect`）與
`require("clrdiag").state()`（最近一次收到的階段狀態，可接 statusline）。

**gutter 上的中斷點與中斷指標**：ClrDiag 每次回覆／推播都會附上完整的中斷點清單與目前的
階段狀態，Neovim 端據此在已開啟的緩衝區 gutter 畫出對應的 sign（不維護本地快取，全部
以 ClrDiag 那份為準，包含移除）——已綁定的中斷點是實心圓 `●`，尚未綁定（`verified: false`）
的是空心圓 `○`，一眼就能看出差別；目前中斷的那一行則會多一個 `▶` 加整行底色，程式離開
那一行（續行、單步、結束）就會立刻清掉，不會留著一個過期的指標。這些 sign 用專屬的
sign group（`ClrDiagBreakpoints`、`ClrDiagStop`），不會跟 nvim-dap 或其他外掛的 sign
互相干擾。

中斷發生時（真正「新中斷」的那一刻，同一個中斷狀態重複推播不會再吵一次）會跳一則通知，
內容是原因跟精簡的 `檔名:行號`；`opts.notify_on_halt = false` 可以關掉。預設不會自動跳
游標過去——如果想要中斷時直接跳到該行，設定 `opts.jump_on_halt = true`；那個檔案已經開在
某個緩衝區就直接重用（有視窗就切過去，沒視窗就顯示在目前視窗，不會另外分割或開重複的
緩衝區），還沒開的話會自動載入並跳過去——單步跨進另一個檔案本來就是最常見的情境。只有
檔案在磁碟上真的不存在或讀取失敗，通知裡才會說明「檔案無法開啟」而不是悄悄什麼都不做。

想接進 statusline 的話用 `require("clrdiag").statusline()`：中斷時回傳類似
`⏸ Program.cs:32` 的字串，沒中斷則回傳空字串。

**VS Code** 的專屬擴充套件是規劃中的後續階段；在那之前綁一個鍵跑一個 task 呼叫
`clrdiag --send '{"cmd":"setBreakpoint","path":"${file}","line":${lineNumber}}'` 就能設中斷點，
畫面一樣在 ClrDiag 裡看。

### 具名管道協定（文件化的契約）

管道名稱依專案根目錄推導：`\\.\pipe\clrdiag-<根目錄小寫路徑的 SHA-256 前 12 hex 碼>`，
同一個專案每次啟動都拿到同一個名字，也讓管道名稱兼作「這是哪個執行個體」的辨識——
沒有連接埠、沒有網路曝露面，這是選具名管道而非 TCP loopback 的理由。
不想自己算雜湊就用 `clrdiag --pipe-name`（`--root` 可指定專案根目錄，省略時規則與其他指令一致）。

協定是換行分隔的 JSON，雙向：客戶端送一個指令物件，ClrDiag 立刻回一則目前的階段狀態；
階段狀態改變（中斷／恢復／結束）時，即使沒有新指令送進來，也會不待請求主動推播給所有已連線
的客戶端——一連上就會先收到一次目前狀態，不是任何指令的回覆。

指令（`cmd` 欄位）：

| `cmd`             | 其他欄位             | 說明                         |
| ----------------- | -------------------- | ---------------------------- |
| `setBreakpoint`    | `path`, `line`        | 新增中斷點（冪等）           |
| `clearBreakpoint`  | `path`, `line`        | 移除中斷點                   |
| `addWatch`         | `expression`          | 新增監看運算式（冪等）       |
| `removeWatch`      | `expression`          | 移除監看運算式               |
| `continue`         | —                     | 續行                         |
| `stepOver`         | —                     | 下一步                       |
| `stepIn`           | —                     | 進入函式                     |
| `stepOut`          | —                     | 跳出函式                     |
| `pause`            | —                     | 暫停                         |

`path` 逐字比對（不分大小寫，Windows 路徑），沒有正規化——這正是為什麼未驗證的中斷點
一定要顯眼標示：來源路徑跟 PDB 對不起來是中斷點悄悄失效最常見的原因。

狀態回覆／推播的形狀（`sessionState` 是 `Idle`/`Connecting`/`Running`/`Halted`/`Terminated`/`Failed`
其中之一；`threadId`/`stopReason`/`location`/`watchResults` 只在 `Halted` 且已完成擷取時出現）：

```jsonc
{
  "type": "state",
  "sessionState": "Halted",
  "pid": 12345,
  "launchMode": false,
  "threadId": 1,
  "stopReason": "breakpoint",
  "location": { "path": "C:\\App\\Program.cs", "line": 42 },
  "watchResults": [
    { "expression": "counter", "value": "7", "timedOut": false, "error": null }
  ],
  "breakpoints": [
    { "path": "C:\\App\\Program.cs", "line": 42, "verified": true, "message": null }
  ],
  "watches": [ "counter" ]
}
```

## 找記憶體洩漏的流程

1. `clrdiag`，讓它掛上執行中的行程（或按 `s` 啟動）。
2. 按 `n` 取第一次快照，再按 `d` 設為基準。
3. 操作要測的功能。
4. 再按 `n`。表格會多出 `Δ MB` / `Δ 數量`，按 `o` 切到 SizeDelta 排序，成長最多的型別排最前。
5. 對可疑型別按 `f` 找出握著它的參考鏈。
   - 回報「沒有任何 GC 根 → 屬於等待回收的垃圾」= 不是洩漏，只是還沒被 GC。
   - 出現 `StrongHandle` / 靜態欄位之類的根 = 真的被握住。

## 實作重點與已知限制

- **堆疊快照（`n` 鍵）不是除錯器，沒有中斷點。** 走 ClrMD + Windows PSS 行程快照
  （`CreateSnapshotAndAttach`），目標行程不會進入除錯狀態，工具異常結束也不會把目標一起帶走。
  需要中斷點／逐步執行時用 `8 偵錯` 分頁（見上方「除錯」一節，.NET 8+ 專用）——
  兩條路徑互不影響，可以同時用。
- **必須是 64 位元目標行程**：ClrMD 要載入同位元數的 DAC，32 位元行程不會出現在 `--list`；
  除錯功能（netcoredbg）同樣要求 64 位元、.NET 8+ 的受控行程。
- **除錯階段中 `7 輸出` 可能安靜下來**：附加了偵錯器之後，目標行程的 `OutputDebugString`
  會被作業系統直接送去給偵錯器，而不是 DBWIN 緩衝區，`7 輸出` 這時可能收不到新訊息。
  已知限制，沒有解法——這是 Windows 偵錯 API 的行為，不是攔截層的錯。
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
