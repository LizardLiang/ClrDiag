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

clrdiag --install-skill global   # 安裝 Claude Code 技能（見「安裝 Claude Code 技能」一節）
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

專案選擇（見下方「專案自動偵測」）：

```powershell
clrdiag --projects               # 列出工作目錄底下偵測到的專案（★ 預設、◎ 上次）
clrdiag --project Web            # 以名稱或相對路徑指定專案，可搭配任何模式
clrdiag --pick                   # 不使用預設專案，一律顯示專案選單
clrdiag --set-default Web        # 設定這個工作目錄的預設專案
clrdiag --clear-default          # 清除這個工作目錄的預設專案
```

### 專案自動偵測

沒有 `--root` 或 `--config` 時，clrdiag 往下掃描工作目錄（深度 5），找出所有 `.sln`、`.slnx`、`.csproj`、`.vbproj`。`bin`、`obj`、`node_modules`、`packages`、以 `.` 開頭的資料夾，以及目錄連接點（junction）與符號連結不掃描。OneDrive 同步根目錄與其下的子資料夾雖然帶有 reparse point 屬性，但不是連結，照常掃描。無法讀取連結目標的資料夾不掃描，clrdiag 印出略過的資料夾數。掃描最多檢查 20,000 個資料夾；另有 10 秒的時間上限，用在慢速網路磁碟，以及 OneDrive 中只存放在雲端（尚未下載到本機）的資料夾——這類資料夾可能讓掃描用完 10 秒，掃描提早結束。clrdiag 在處理每個資料夾之前檢查這個上限；讀取單一資料夾的系統呼叫停住時，clrdiag 等到該呼叫回傳，因此實際等待時間可能超過 10 秒。達到任一上限時 clrdiag 印出提示，清單可能不完整，這時請用 `--project` 或 `--root`。

clrdiag 依下列順序決定要用哪個專案：

1. `--project <名稱>`：依完整相對路徑 → 檔名 → 相對路徑的一部分比對。名稱空白、沒有唯一結果，或工作目錄底下沒有任何專案時，列出候選並以結束碼 2 結束。
2. 找不到任何專案：沿用往上尋找專案根目錄的行為。
3. `--pick`（只在互動模式）：顯示選單。
4. 預設專案：直接使用，不顯示選單。
5. 只找到一個專案：直接使用。
6. 互動模式：顯示選單。上次執行的專案排第一個；輸入文字可依名稱篩選。選定後會詢問「設為預設？」。選最後一項「取消」時不啟動儀表板、不寫入記錄，以結束碼 0 結束。
7. 批次模式（`--snapshot`、`--send` 等，或輸入輸出被重新導向、終端機不支援互動）：使用上次執行的專案；沒有記錄時列出候選，結束碼 2。批次模式不會顯示選單。
   - `--list` 例外：沒有記錄時不列出候選，改用往上搜尋得到的專案根目錄設定，照常列出行程，並在標準錯誤提示「找到多個專案」。記錄的預設或上次執行專案已不存在時，提示中一併說明，記錄不變更。`--project` 沒有唯一符合時，`--list` 仍以結束碼 2 結束。

選定專案後，Root 是專案檔所在資料夾，建置目標是該檔案。從該資料夾往上找到 `clrdiag.json` 時，Root 是設定檔所在資料夾；設定檔有 `buildProject` 時以設定檔為準，儀表板狀態列會同時列出實際的建置目標。

以下模式不做專案偵測：

- `--root`、`--config`：直接使用指定的根目錄或設定檔。
- `--init`、`--install-skill`：寫入往上搜尋得到的專案根目錄。
- `--output`：不讀專案設定。

`--project`／`--pick` 搭配上述模式，或 `--root`／`--config` 搭配 `--projects`、`--set-default`、`--clear-default` 時，不生效的旗標會以警告列出。

互動儀表板以黃色在主控台印出警告與提示；其他模式把它們以純文字寫到標準錯誤（stderr），標準輸出只留給批次結果。例如 `--pipe-name` 的標準輸出只有管道名稱。

參數錯誤（未知參數、`--project`／`--set-default` 缺少值、`--install-skill` 範圍缺漏或拼錯）的訊息與用法，以及 `clrdiag.json` 載入失敗的訊息，都寫到標準錯誤，結束碼 2。

`--project`、`--set-default` 比對失敗時的錯誤與候選清單、`--set-default`／`--clear-default` 寫入記錄失敗，以及 `--projects` 找不到任何專案的訊息，都寫到標準錯誤，結束碼不變。`--projects` 的表格與設定、清除成功的訊息寫到標準輸出。

所有非互動模式的標準輸出與標準錯誤都是 UTF-8（不含 BOM），不受主控台字碼頁（例如 Big5 950）影響。非互動模式是指有下列任一旗標的執行：`--snapshot`、`--threads`、`--roots`、`--render`、`--output`、`--dap`、`--list`、`--init`、`--build`、`--export`、`--send`、`--pipe-name`、`--install-skill`、`--projects`、`--set-default`、`--clear-default`。參數錯誤的訊息也是 UTF-8。`--help` 的標準輸出重新導向（例如 `clrdiag --help > help.txt`）時也是 UTF-8。互動儀表板，以及印到終端機的 `--help`，沿用主控台字碼頁。

預設專案與上次執行的專案存在 `%LOCALAPPDATA%\clrdiag\projects.json`，以工作目錄為鍵。只有互動儀表板會記錄上次執行的專案。記錄的檔案已刪除時，下一次互動執行在選定專案後一次移除這些記錄；在選單選「取消」時不寫入。每次寫入時，clrdiag 只保留最近變更的 100 筆記錄，不檢查工作目錄是否存在，所以離線的隨身碟、網路磁碟或 VPN 路徑的記錄會保留。檔案無法讀取時不寫入；JSON 損毀時先改名為 `projects.json.<時間>.bak` 再寫入新檔，最多保留 3 份備份。

已知限制：`--send` 等批次指令依「預設專案 → 上次執行」決定專案。工作目錄設有預設專案 A，而儀表板是用 `--pick` 選 B 啟動時，`clrdiag --send` 連到的是 A 的管道。這時請加上 `--project B`。

## 設定檔 clrdiag.json

放在專案根目錄（會從目前目錄往上尋找），全部欄位都可省略：

| 欄位              | 用途                                                                      |
| ----------------- | ------------------------------------------------------------------------- |
| `buildProject`    | 建置目標（.sln / 專案檔）。省略時自動找根目錄的 .sln → .csproj             |
| `buildCommand`    | 建置執行檔。省略時：SDK 專案用 `dotnet`，舊式專案用 vswhere 找到的 MSBuild |
| `buildArguments`  | 建置參數，可用 `{project}` `{config}` `{root}` `{port}` 佔位符             |
| `configurations`  | `c` 鍵可循環的建置設定                                                    |
| `serveCommand`    | 啟動伺服器的執行檔。省略時依專案類型推斷（見下方「省略 serveCommand 時的推斷」）；無法推斷時 **`s`/`r` 鍵停用**，只能附加到既有行程 |
| `serveArguments`  | 啟動參數，同樣支援佔位符                                                  |
| `port`            | 預設連接埠（`--port` 可覆寫）                                             |
| `probeUrl`        | 健康探測網址，支援 `{port}`                                               |
| `processNames`    | 伺服器行程的映像名稱（不含 .exe）。留空 = 只靠連接埠辨識：儀表板、`s` 鍵與不加 `--pid` 的批次指令只接管**監聽 `port` 的受控行程**（64 位元），名稱不限；`--list` 與 `p` 只是列出所有載入 CLR 的行程讓你自己挑。寫了名稱 = 多一道條件，行程名稱必須在清單內。IIS Express／w3wp 這類 HTTP.sys 站台的連接埠監聽記在系統行程（PID 4）名下，一律必須寫名稱（沒寫時啟動與接管都會立即失敗並在 `6 記錄` 說明）；接管既有行程時以命令列的 `/port:` 比對連接埠：`/port:` 是別的連接埠的排除，沒有 `/port:` 的行程只在剛好一個（讀得到命令列、64 位元、已載入 CLR）時作為備援接管，有多個則都不接管。舊式 ASP.NET 專案省略時推斷為 `iisexpress`。`null` 視為空陣列 |
| `appNamespaces`   | 視為「自己程式碼」的命名空間前綴。留空 = 以「非框架」近似判斷              |
| `reportDirectory` | CSV 輸出目錄                                                              |
| `dapEnabled`      | 是否啟用除錯功能（spawn netcoredbg、開具名管道）。預設 `true`             |
| `dapAdapterPath`  | netcoredbg 執行檔路徑。省略時：`PATH` → mason 預設安裝路徑                |
| `dapBreakpoints`  | 啟動時載入的中斷點清單，格式 `"路徑:行號"`（見下方「除錯」一節）          |
| `dapWatches`      | 啟動時載入的監看運算式清單                                                |

### 省略 serveCommand 時的推斷

設定檔沒有 `serveCommand` 時，clrdiag 依選定的專案檔推斷啟動方式，並在 `6 記錄` 寫一行說明推斷了什麼、來源是哪個檔案。設定檔明確寫的 `serveCommand`、`serveArguments`、`port`、`processNames` 一律優先，推斷只補沒寫的欄位；已寫 `serveCommand` 時完全不推斷。

| 專案類型 | 推斷的啟動方式 | 連接埠來源 |
| -------- | -------------- | ---------- |
| `Sdk="Microsoft.NET.Sdk.Web"`（屬性或 `<Sdk Name=…/>`） | `dotnet run --project {project} --urls http://localhost:{port}`（除錯時與手寫的 `dotnet run` 一樣當作 wrapper 處理） | `Properties/launchSettings.json` 第一個有 http `applicationUrl` 的 profile 的連接埠（只有 https 的 profile 略過），沒有則 5000 |
| 舊式 ASP.NET Web 應用程式（`ProjectTypeGuids` 含 `{349c5851-65df-11da-9384-00065b846f21}`） | `iisexpress.exe /path:<專案資料夾> /port:{port}`；`processNames` 預設 `["iisexpress"]` | `IISUrl` 的 http 連接埠（`<專案檔>.user` 優先，其次專案檔）→ `DevelopmentServerPort`（`.user` 優先，其次專案檔）→ 5000 |
| 其他（主控台、類別庫、`.sln`／`.slnx`） | 不推斷，只能附加到既有行程，原因寫在 `6 記錄` | 5000 |

舊式專案在 `Program Files` 與 `Program Files (x86)` 的 `IIS Express` 資料夾都找不到 `iisexpress.exe` 時不推斷，原因寫在 `6 記錄`。clrdiag 不讀 `Web.config` 與連線字串。推斷出的 IIS Express 指令不是 `dotnet run` wrapper，除錯啟動（netcoredbg 只支援 .NET Core）不適用舊式專案。

因為推斷在載入設定時做一次，所有模式看到同一份結果：舊式 ASP.NET 專案沒有 `clrdiag.json` 時，`processNames` 變成 `iisexpress`，連接埠為推斷值（例如 GSS.HAS.Web 是 58649）。`6 記錄` 的推斷說明會列出實際指令（含 `/path:`）與連接埠（用 `--port` 覆寫時會註明）。`clrdiag --init` 產生的範本不寫 `port` 與 `processNames`（寫了就會覆蓋推斷），要覆蓋再自己加。

### 辨識與停止伺服器行程

啟動（`s`）、接管既有行程（儀表板啟動與 `s` 遇到連接埠已被監聽）與不加 `--pid` 的批次指令用同一套規則：

- 行程由監聽 `port` 的行程決定，必須是 64 位元、已載入 CLR 的受控行程；設定了 `processNames` 時名稱也要符合。
- 啟動時，監聽者必須是啟動的行程本身或它的後代行程。`dotnet run` 的 MSBuild、編譯伺服器等不監聽連接埠，不會被當成伺服器。
- HTTP.sys 站台（IIS Express、w3wp）一律要設定 `processNames`。啟動時，從啟動的行程與後代行程挑名稱符合、64 位元的行程（只看名稱，不要求已載入 CLR）；命令列 `/port:` 寫了別的連接埠的行程服務別的站台，排除。接管既有行程時，依名稱找行程並比對命令列的 `/port:`：
  - `/port:` 等於 `port` 的行程是證據；它是 32 位元或還沒載入 CLR（站台尚未被請求）時，回報該原因，不接管別的行程。
  - `/port:` 是別的連接埠的行程排除。
  - 沒有任何行程指定 `/port:` 等於 `port` 時，命令列沒有 `/port:`（且讀得到命令列）、64 位元、已載入 CLR 的行程剛好一個才接管；有多個則都不接管。w3wp 的命令列沒有 `/port:`，同一個 w3wp 也可能服務別的站台，這個備援無法分辨，不確定時請用 `p` 或 `--pid` 指定。
  - 讀不到命令列的行程不列入備援。
- 啟動時 HTTP.sys 擁有該連接埠、而且沒有名稱符合並服務此連接埠的行程（例如本機 IIS 佔用 80），沒有既有伺服器可接管，照常啟動。
- 連接埠沒人監聽、查詢失敗、有多個候選、擁有者不是受控行程或是 32 位元行程（例如只有 x86 的 IIS Express）時，一律不接管，原因寫在 `6 記錄`；可用 `p` 或 `--pid` 指定。啟動時遇到 32 位元的伺服器行程（例如只有 x86 的 IIS Express）會立即失敗並說明，不等到逾時。
- 啟動時 HTTP.sys 已佔用該連接埠、而照常啟動成功時，`6 記錄` 會警告：探測網址可能連到別的站台（例如本機 IIS 的站台），探測結果不一定代表啟動的行程。
- 除錯階段進行中（`6 記錄` 顯示接管除錯目標）按 `s` 不會啟動：請先停止除錯階段（`x`）。前一次啟動的行程仍在執行時也不會再啟動，請先按 `x`。
- 啟動後最多等 30 秒讓伺服器開始監聽；逾時或失敗只會結束「這一次」啟動的行程與它的後代行程。後代行程依行程快照的父 PID 連結找出，並逐層核對父子的建立時間（子行程的建立時間早於父行程，代表 PID 已被別的行程重複使用，不算後代；讀不到父行程建立時間的連結不採用）。啟動的行程已結束時，仍用持有的行程物件讀到的建立時間核對，記著它舊 PID 的更早行程與它們的子行程不會被結束；中間行程已先結束的孫行程找不到，不會被結束。

`x` 停止只依 PID（連同後代行程）進行：先送正常關閉訊號，沒反應再強制結束；絕不依映像名稱結束行程，所以不會動到同名的其他 `iisexpress`／`dotnet`。確認行程真的結束才顯示已停止，否則在 `6 記錄` 警告、狀態列顯示失敗並保留監看（`r` 重建重啟也會中止）。結束前會核對行程建立時間，兩種情形都不會動到那個 PID：建立時間與記錄不符（PID 已被別的行程重複使用）時，視為原本的行程已結束，不動新的行程也不警告；讀不到建立時間（例如沒有權限）時無法確認是原本的行程，在 `6 記錄` 警告一次（每個 PID 一次），請你確認後自行結束。批次指令（沒有 `--pid`）只依連接埠辨識，不會挑到別的行程；名稱找不到時不退回任一受控行程。

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

儀表板畫在終端機的替代畫面緩衝區（`vim`／`less` 用的那一塊）。不論用 `q`、`Ctrl+C`
還是發生例外離開，都會切回原本的畫面，終端機內容與捲動歷史保持原樣，儀表板不留在捲動歷史裡。

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
| `status`           | —                     | 只讀取目前狀態，不做任何動作 |

`path` 在比對前只做一件正規化：`/` 一律換成 `\`（`DapSessionService.NormalizePath`），
之後不分大小寫逐字比對。其餘一概不處理——相對路徑不會展開成絕對路徑，`..` 不會摺疊，
短檔名不會還原。這正是為什麼未驗證的中斷點一定要顯眼標示：來源路徑跟 PDB 對不起來是
中斷點悄悄失效最常見的原因。

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

## 安裝 Claude Code 技能

技能檔（`SKILL.md` 與 `references/`）內嵌在組件裡，`--install-skill` 會把它們裝好，讓 Claude Code
知道怎麼驅動這個工具：

```powershell
clrdiag --install-skill global                  # 裝到 %USERPROFILE%\.claude\skills\clrdiag
clrdiag --install-skill local                   # 裝到 <專案根目錄>\.claude\skills\clrdiag
clrdiag --install-skill local --root C:\proj    # 指定專案根目錄
```

範圍是必填，只能是 `global` 或 `local`。`local` 用的專案根目錄跟其他指令同一套解析邏輯，
所以可以搭配 `--root`。安裝完要開新的 Claude Code 工作階段才會載入。

**運作方式**：技能檔先解壓到固定快取路徑
`%LOCALAPPDATA%\clrdiag\skills\clrdiag`，安裝位置只放一個指向該快取的連結。
快取路徑刻意不含版本號，所以升級 clrdiag 後只要重跑一次 `--install-skill`，
全域與每個專案的連結都會同時看到新內容，不必逐一重裝。

**連結型式**：優先建立符號連結（symlink）；沒有開發人員模式或系統管理員權限時會自動退回
目錄連接點（junction）。junction 不需要提權，權限受限的公司電腦也裝得起來。
實際用了哪一種會印在成功訊息裡。兩種都失敗時會印出原因並以非 0 結束碼結束。

**安裝位置已經有東西**：

| 現況              | 行為                                             |
| ----------------- | ------------------------------------------------ |
| 不存在            | 直接建立連結（缺的上層目錄會一併建立）           |
| 已經是連結        | 刪掉舊連結後重建，重跑安裝是冪等的               |
| 是真實目錄或檔案  | **拒絕並以非 0 結束**，除非加上 `--force`         |

`--force` 會遞迴刪除該位置的真實內容再建立連結，並印出刪掉了什麼。
沒有 `--force` 就絕不會刪掉真實檔案。

## Publish Without The SDK

To distribute a Windows x64 single-file executable that does not require the .NET SDK:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\publish\win-x64
.\artifacts\publish\win-x64\clrdiag.exe --list
```
