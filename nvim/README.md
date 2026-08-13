# clrdiag.nvim

把中斷點、監看運算式與執行控制送給 [ClrDiag](../README.md) 的極簡 Neovim 客戶端。

**這不是 nvim-dap 的 adapter。** Neovim 只送意圖過去；呼叫堆疊、區域變數、監看結果一律顯示在
ClrDiag TUI 自己的 `8 偵錯` 分頁。Neovim 這邊只會有 gutter 上的 sign（中斷點、目前中斷位置）
與一則通知。

- 零依賴：只用 Neovim 0.9+ 的 stdlib（`vim.fn.sockconnect`）
- 走具名管道，管道名稱由 `clrdiag --pipe-name` 問出來（刻意不在 Lua 端另外實作一份雜湊）
- 唯一需求：`clrdiag` 要在 `PATH` 上（0.2.0 或更新）

## 安裝

`lua/clrdiag/` 在這個資料夾底下，所以 **plugin root 是 `nvim/`**，不是 repo 根目錄。

指向本機路徑（在 ClrDiag repo 裡開發時最方便）：

```lua
{
  dir = "/path/to/ClrDiag/nvim",
  main = "clrdiag",
  ft = "cs",
  opts = { jump_on_halt = true },
}
```

## 設定

```lua
opts = {
  root = nil,              -- 專案根目錄；nil = 讓 ClrDiag 自己往上找 clrdiag.json / .git
  keymaps = nil,           -- 傳 false 完全不掛預設按鍵，自己接
  jump_on_halt = false,    -- 中斷時把游標跳過去；檔案還沒開會自動開起來
  notify_on_halt = true,   -- 中斷時跳一則通知
  icons = {
    breakpoint = "●",              -- 已驗證的中斷點
    breakpoint_unverified = "○",   -- 送出去了但 netcoredbg 沒能綁上（通常是路徑或符號對不上）
    stop = "▶",                    -- 目前中斷的那一行
  },
  highlights = { ... },    -- 對應上面三個 sign 的 highlight group
}
```

**`root` 一定要跟 ClrDiag 那邊算出同一個目錄**，否則兩邊的管道名稱不同，永遠碰不到面，
而且不會有任何錯誤訊息。對不上的時候在專案根目錄跑一次 `clrdiag --init`，兩邊就會一致。

## 指令與 API

| 指令                      | API                            |
| ------------------------- | ------------------------------ |
| `:ClrdiagConnect`         | `connect(root)`                |
| `:ClrdiagDisconnect`      | `disconnect()`                 |
| `:ClrdiagBreakpoint`      | `toggle_breakpoint()`          |
| `:ClrdiagClearBreakpoint` | `clear_breakpoint()`           |
| `:ClrdiagWatch [expr]`    | `add_watch(expr)`              |
| `:ClrdiagWatchWord`       | `add_watch_word()`             |
| `:ClrdiagRemoveWatch`     | `remove_watch(expr)`           |
| `:ClrdiagContinue`        | `continue()`                   |
| `:ClrdiagStepOver`        | `step_over()`                  |
| `:ClrdiagStepIn`          | `step_in()`                    |
| `:ClrdiagStepOut`         | `step_out()`                   |
| `:ClrdiagPause`           | `pause()`                      |

`statusline()` 回傳中斷時的 `⏸ Program.cs:32`（沒中斷時回空字串）；`state()` 回傳最近一次
收到的完整階段狀態表。

## 已知限制

- `toggle_breakpoint()` 目前只會**新增**，不會移除——清除請用 `:ClrdiagClearBreakpoint`。
- 跳轉會接管目前的視窗，不會另開分割。
- 沒有本機中斷點快取：唯一事實來源永遠是 ClrDiag 的清單（每次回覆與推播都會附上）。
