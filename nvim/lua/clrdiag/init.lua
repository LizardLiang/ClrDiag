-- clrdiag.nvim — 極簡的 ClrDiag 除錯指令傳送端。
--
-- ClrDiag 是唯一的 DAP 客戶端：它自己 spawn netcoredbg 並直接驅動，畫面（呼叫堆疊、
-- 區域變數、監看結果）全部顯示在 ClrDiag 的偵錯分頁。這支模組不做除錯器該做的事，
-- 只負責把「中斷點在哪」「監看什麼」「續行/單步」這類意圖，透過專案範圍的具名管道
-- 送給 ClrDiag——沒有 nvim-dap，沒有 DAP adapter 設定，不依賴任何外部 Neovim 套件。
--
-- 需求：clrdiag 執行檔要在 PATH 上。管道名稱由 `clrdiag --pipe-name` 問出來，
-- 刻意不在這裡另外實作一份雜湊邏輯——那樣兩邊的演算法遲早會兜不起來，
-- 而管道名稱本來就是 ClrDiag 自己在決定的。
--
-- 唯一的例外：中斷點與目前中斷位置會用 sign 標在 Neovim 這邊的 gutter，並在中斷時跳出
-- 通知。這兩者都是純粹的「畫面提示」，不影響上面的分工——中斷點清單、是否中斷、停在
-- 哪一行，全部以 ClrDiag 推播／回覆的內容為單一事實來源，Neovim 端不做任何本地判斷或
-- 快取，signs 使用專屬的 sign group，不會跟 nvim-dap 或其他外掛互相干擾。

local M = {}

local state = {
  channel = nil, -- vim.fn.sockconnect 回傳的 channel id；nil 代表尚未連線
  pipe_name = nil,
  buf = "", -- 尚未湊成完整一行的殘餘資料
  session = nil, -- 最近一次收到的階段狀態（json_decode 後的 table）
  last_halt_key = nil, -- 上一次「中斷」通知過的位置識別碼，避免同一個中斷點重複跳通知
  signs_ready = false, -- sign_define／預設 highlight 是否已經註冊過
}

--- 使用者可透過 M.setup(opts) 覆寫的預設值，見檔案底部的 M.setup。
local config = {
  icons = {
    breakpoint = "●", -- 已綁定（verified）的中斷點
    breakpoint_unverified = "○", -- 尚未綁定的中斷點；刻意用不同圖示，不能跟已綁定的長得一樣
    stop = "▶", -- 目前中斷所在行
    statusline_pause = "⏸", -- M.statusline() 用的前綴圖示
  },
  highlights = {
    breakpoint = "ClrDiagBreakpointSign",
    breakpoint_unverified = "ClrDiagBreakpointUnverifiedSign",
    stop = "ClrDiagStopSign",
    stop_line = "ClrDiagStopLine",
  },
  notify_on_halt = true, -- 中斷時是否跳通知
  jump_on_halt = false, -- 中斷時是否自動把游標跳過去（預設關閉，使用者選擇全程只看 ClrDiag 的 TUI）
}

-- 中斷點與目前中斷位置各自用獨立的 sign group，避免跟 nvim-dap（LazyVim dap.core）或
-- 其他外掛的 sign 互相蓋掉。
local BREAKPOINT_SIGN_GROUP = "ClrDiagBreakpoints"
local STOP_SIGN_GROUP = "ClrDiagStop"
local BP_SIGN_VERIFIED = "ClrDiagBreakpointVerified"
local BP_SIGN_UNVERIFIED = "ClrDiagBreakpointUnverified"
local STOP_SIGN_NAME = "ClrDiagStopSign"

local function log(msg, level)
  vim.schedule(function()
    vim.notify("[clrdiag] " .. msg, level or vim.log.levels.INFO)
  end)
end

--- 用 clrdiag --pipe-name 問出目前專案對應的管道名稱；--root 讓呼叫端可以指定專案根目錄
--- （不指定時 ClrDiag 依自己的規則往上找 clrdiag.json / .git，與互動模式一致）。
local function resolve_pipe_name(root)
  local cmd = { "clrdiag", "--pipe-name" }
  if root and root ~= "" then
    cmd[#cmd + 1] = "--root"
    cmd[#cmd + 1] = root
  end

  local output = vim.fn.system(cmd)
  if vim.v.shell_error ~= 0 then
    return nil, vim.trim(output)
  end

  local name = vim.trim(output)
  if name == "" then
    return nil, "clrdiag --pipe-name 沒有輸出"
  end

  return name
end

--- 把路徑正規化成小寫、正斜線的形式，用來比對 ClrDiag 送來的 Windows 路徑跟
--- Neovim 緩衝區名稱（緩衝區開啟方式不同，分隔符號跟大小寫都可能不一致）。
local function normalize_path(path)
  if not path or path == "" then
    return nil
  end
  return (path:gsub("\\", "/")):lower()
end

--- 在目前已開啟（載入）的緩衝區裡找出對應這個路徑的那一個；找不到就回傳 nil。
local function find_loaded_buf(path)
  local target = normalize_path(path)
  if not target then
    return nil
  end

  for _, buf in ipairs(vim.api.nvim_list_bufs()) do
    if vim.api.nvim_buf_is_loaded(buf) then
      local name = vim.api.nvim_buf_get_name(buf)
      if name ~= "" and normalize_path(name) == target then
        return buf
      end
    end
  end

  return nil
end

--- 註冊 sign 定義與預設 highlight（只做一次；用 default=true 讓使用者的 colorscheme
--- 或後續 M.setup(opts) 都能覆寫）。
local function ensure_signs_defined()
  if state.signs_ready then
    return
  end
  state.signs_ready = true

  vim.api.nvim_set_hl(0, config.highlights.breakpoint, { default = true, link = "DiagnosticSignError" })
  vim.api.nvim_set_hl(0, config.highlights.breakpoint_unverified, { default = true, link = "DiagnosticSignWarn" })
  vim.api.nvim_set_hl(0, config.highlights.stop, { default = true, link = "DiagnosticSignInfo" })
  vim.api.nvim_set_hl(0, config.highlights.stop_line, { default = true, link = "Visual" })

  vim.fn.sign_define(BP_SIGN_VERIFIED, { text = config.icons.breakpoint, texthl = config.highlights.breakpoint })
  vim.fn.sign_define(
    BP_SIGN_UNVERIFIED,
    { text = config.icons.breakpoint_unverified, texthl = config.highlights.breakpoint_unverified }
  )
  vim.fn.sign_define(STOP_SIGN_NAME, {
    text = config.icons.stop,
    texthl = config.highlights.stop,
    linehl = config.highlights.stop_line,
  })
end

--- 用 ClrDiag 送來的中斷點清單重新畫過整批 sign（先清空再重放，天然支援移除）。
--- 這裡刻意不維護本地快取——decoded.breakpoints 每次都是完整清單，單一事實來源。
local function sync_breakpoint_signs(breakpoints)
  ensure_signs_defined()
  vim.fn.sign_unplace(BREAKPOINT_SIGN_GROUP)

  for _, bp in ipairs(breakpoints or {}) do
    local buf = find_loaded_buf(bp.path)
    if buf then
      local verified = bp.verified ~= false
      local sign_name = verified and BP_SIGN_VERIFIED or BP_SIGN_UNVERIFIED
      pcall(vim.fn.sign_place, 0, BREAKPOINT_SIGN_GROUP, sign_name, buf, { lnum = bp.line, priority = 10 })
    end
  end
end

--- 把「目前中斷在哪」的 sign 對齊到最新狀態；沒有中斷就直接清掉，
--- 避免殘留一個已經離開的中斷位置的指標（比沒有指標還誤導人）。
local function sync_stop_sign(decoded)
  ensure_signs_defined()
  vim.fn.sign_unplace(STOP_SIGN_GROUP)

  if decoded.sessionState ~= "Halted" or not decoded.location then
    return
  end

  local buf = find_loaded_buf(decoded.location.path)
  if buf then
    -- 優先權設得比中斷點 sign 高：停在中斷點那一行時，要看到的是「停在這裡」的箭頭，
    -- 不是中斷點本身的圓點。
    pcall(vim.fn.sign_place, 0, STOP_SIGN_GROUP, STOP_SIGN_NAME, buf, { lnum = decoded.location.line, priority = 20 })
  end
end

--- 把游標（跟焦點視窗）移到中斷位置：這個檔案已經開在某個緩衝區就直接重用（有視窗就切
--- 過去，沒視窗就顯示在目前視窗，不會另外開分割視窗或重複的緩衝區）；還沒開的話就地載入——
--- 跳進另一個檔案本來就是「單步」最常見的情境，理當自動開檔，不該要求使用者自己先手動
--- 開好。只有檔案在磁碟上真的不存在或讀取失敗，才會回傳 false 讓呼叫端顯示警告。
local function jump_to_location(location)
  local buf = find_loaded_buf(location.path)

  if not buf then
    if vim.fn.filereadable(location.path) == 0 then
      return false
    end

    -- bufadd 只建立緩衝區物件，不代表檔案已經讀進來；bufload 才是實際讀檔，
    -- 讀取失敗（例如權限問題）時 buffer 不會進入 loaded 狀態，藉此偵測「開不了」。
    local addOk, added = pcall(vim.fn.bufadd, location.path)
    if not addOk or not added or added == 0 then
      return false
    end

    local loadOk = pcall(vim.fn.bufload, added)
    if not loadOk or not vim.api.nvim_buf_is_loaded(added) then
      return false
    end

    buf = added
  end

  local winid = vim.fn.bufwinid(buf)
  if winid == -1 then
    vim.api.nvim_set_current_buf(buf)
    winid = vim.api.nvim_get_current_win()
  else
    vim.api.nvim_set_current_win(winid)
  end

  pcall(vim.api.nvim_win_set_cursor, winid, { location.line, 0 })
  return true
end

--- 處理「剛進入 Halted（或停在新位置）」這個瞬間：跳通知、視需要跳游標。
--- 用位置＋原因湊成的 key 去重，同一個中斷狀態重複推播時不會再吵一次。
local function handle_halt(decoded)
  local location = decoded.location
  local halt_key = table.concat({
    normalize_path(location.path) or "",
    tostring(location.line),
    decoded.stopReason or "",
  }, "|")

  if halt_key == state.last_halt_key then
    return
  end
  state.last_halt_key = halt_key

  local jump_failed = false
  if config.jump_on_halt then
    jump_failed = not jump_to_location(location)
  end

  if config.notify_on_halt then
    local basename = vim.fn.fnamemodify(location.path or "?", ":t")
    local msg = string.format("中斷於 %s:%s（%s）", basename, tostring(location.line), decoded.stopReason or "?")
    if jump_failed then
      msg = msg .. "－檔案無法開啟，無法自動跳轉"
    end
    log(msg, jump_failed and vim.log.levels.WARN or vim.log.levels.INFO)
  end
end

local function handle_line(line)
  if line == "" then
    return
  end

  local ok, decoded = pcall(vim.fn.json_decode, line)
  if not ok or type(decoded) ~= "table" then
    log("收到無法解析的訊息: " .. line, vim.log.levels.WARN)
    return
  end

  state.session = decoded

  sync_breakpoint_signs(decoded.breakpoints)

  -- handle_halt 可能會（jump_on_halt 開著時）把停在的檔案開成新緩衝區，一定要排在
  -- sync_stop_sign 前面——停止 sign 是照緩衝區放的，緩衝區還沒存在就放不進去。
  if decoded.sessionState == "Halted" and decoded.location then
    handle_halt(decoded)
  else
    state.last_halt_key = nil
    if decoded.error then
      log(tostring(decoded.error), vim.log.levels.ERROR)
    end
  end

  sync_stop_sign(decoded)

  if type(M.on_state) == "function" then
    pcall(M.on_state, decoded)
  end
end

-- sockconnect 的 on_data 逐行切好，最後一個元素可能是還沒收到換行的殘餘資料
-- （與 jobstart 的 on_stdout 同一套慣例）；串流關閉時最後一次呼叫的 data 是 { "" }。
local function on_data(_, data)
  if #data == 1 and data[1] == "" then
    state.channel = nil
    state.buf = ""
    log("與 ClrDiag 的連線已中斷", vim.log.levels.WARN)
    return
  end

  for i, chunk in ipairs(data) do
    if i == #data then
      state.buf = state.buf .. chunk
    else
      handle_line(state.buf .. chunk)
      state.buf = ""
    end
  end
end

--- 連上目前專案的除錯指令管道；已連線時直接略過並回傳 true。
---@param root string|nil 專案根目錄；nil 讓 ClrDiag 自行判斷
function M.connect(root)
  if state.channel then
    return true
  end

  local pipe_name, err = resolve_pipe_name(root)
  if not pipe_name then
    log("找不到管道名稱（clrdiag 是否在 PATH 上？）: " .. tostring(err), vim.log.levels.ERROR)
    return false
  end

  state.pipe_name = pipe_name
  local address = "\\\\.\\pipe\\" .. pipe_name
  local ok, channel = pcall(vim.fn.sockconnect, "pipe", address, { on_data = on_data })
  if not ok or channel == 0 then
    log(
      "連線失敗：確認 ClrDiag 是否正在這個專案目錄下執行、且未以 dapEnabled:false 停用除錯功能",
      vim.log.levels.ERROR
    )
    return false
  end

  state.channel = channel
  log("已連線 " .. pipe_name)
  return true
end

function M.disconnect()
  if state.channel then
    pcall(vim.fn.chanclose, state.channel)
    state.channel = nil
    state.buf = ""
  end

  -- 斷線後 ClrDiag 這個單一事實來源就不在了，殘留的 sign 只會誤導人，一併清掉。
  state.last_halt_key = nil
  pcall(vim.fn.sign_unplace, BREAKPOINT_SIGN_GROUP)
  pcall(vim.fn.sign_unplace, STOP_SIGN_GROUP)
end

local function send(command)
  if not M.connect() then
    return
  end

  local ok, err = pcall(vim.fn.chansend, state.channel, vim.fn.json_encode(command) .. "\n")
  if not ok then
    log("送出指令失敗: " .. tostring(err), vim.log.levels.ERROR)
  end
end

--- 在游標所在行新增中斷點(冪等：ClrDiag 那邊同一個檔案同一行已存在就當作成功，不會重複)。
--- 清除請用 M.clear_breakpoint()；這支模組刻意不維護本地的中斷點快取——單一事實來源永遠是
--- ClrDiag 自己的清單（連線時／每次指令回覆都會附上），避免兩邊狀態兜不起來。
function M.toggle_breakpoint()
  local path = vim.api.nvim_buf_get_name(0)
  local line = vim.api.nvim_win_get_cursor(0)[1]
  if path == "" then
    log("目前緩衝區沒有對應的檔案", vim.log.levels.WARN)
    return
  end

  send({ cmd = "setBreakpoint", path = path, line = line })
end

function M.clear_breakpoint()
  local path = vim.api.nvim_buf_get_name(0)
  local line = vim.api.nvim_win_get_cursor(0)[1]
  if path == "" then
    return
  end

  send({ cmd = "clearBreakpoint", path = path, line = line })
end

--- 把游標下的單字加為監看運算式；已存在的監看運算式用 M.remove_watch() 移除。
function M.add_watch_word()
  local word = vim.fn.expand("<cword>")
  if word == "" then
    log("游標下沒有可用的字", vim.log.levels.WARN)
    return
  end

  send({ cmd = "addWatch", expression = word })
end

--- @param expression string|nil 省略時跳出輸入框詢問
function M.add_watch(expression)
  expression = expression or vim.fn.input("監看運算式: ")
  if expression == "" then
    return
  end

  send({ cmd = "addWatch", expression = expression })
end

--- @param expression string|nil 省略時跳出輸入框詢問
function M.remove_watch(expression)
  expression = expression or vim.fn.input("移除監看運算式: ")
  if expression == "" then
    return
  end

  send({ cmd = "removeWatch", expression = expression })
end

function M.continue()
  send({ cmd = "continue" })
end

function M.step_over()
  send({ cmd = "stepOver" })
end

function M.step_in()
  send({ cmd = "stepIn" })
end

function M.step_out()
  send({ cmd = "stepOut" })
end

function M.pause()
  send({ cmd = "pause" })
end

--- 目前已知的階段狀態（最近一次收到的推播或回覆），供需要完整 table 的呼叫端讀取。
function M.state()
  return state.session
end

--- 給 statusline 用的簡短字串；中斷時回傳「⏸ 檔名:行號」，沒中斷則回傳空字串。
function M.statusline()
  local session = state.session
  if not session or session.sessionState ~= "Halted" or not session.location then
    return ""
  end

  local basename = vim.fn.fnamemodify(session.location.path or "?", ":t")
  return string.format("%s %s:%s", config.icons.statusline_pause, basename, tostring(session.location.line))
end

--- 註冊使用者指令，並視 opts.keymaps 決定是否一併掛預設按鍵（傳 false 可完全自己接）。
---@param opts table|nil { root, keymaps, icons, highlights, notify_on_halt, jump_on_halt }
---  icons: { breakpoint, breakpoint_unverified, stop, statusline_pause }
---  highlights: { breakpoint, breakpoint_unverified, stop, stop_line }
---  notify_on_halt: boolean，中斷時是否跳通知（預設 true）
---  jump_on_halt: boolean，中斷時是否自動跳游標過去（預設 false——使用者選擇全程只看 ClrDiag 的 TUI）
function M.setup(opts)
  opts = opts or {}

  if opts.icons then
    config.icons = vim.tbl_extend("force", config.icons, opts.icons)
  end
  if opts.highlights then
    config.highlights = vim.tbl_extend("force", config.highlights, opts.highlights)
  end
  if opts.notify_on_halt ~= nil then
    config.notify_on_halt = opts.notify_on_halt
  end
  if opts.jump_on_halt ~= nil then
    config.jump_on_halt = opts.jump_on_halt
  end

  ensure_signs_defined()

  vim.api.nvim_create_user_command("ClrdiagConnect", function()
    M.connect(opts.root)
  end, {})
  vim.api.nvim_create_user_command("ClrdiagDisconnect", M.disconnect, {})
  vim.api.nvim_create_user_command("ClrdiagBreakpoint", M.toggle_breakpoint, {})
  vim.api.nvim_create_user_command("ClrdiagClearBreakpoint", M.clear_breakpoint, {})
  vim.api.nvim_create_user_command("ClrdiagWatch", function(cmdopts)
    M.add_watch(cmdopts.args ~= "" and cmdopts.args or nil)
  end, { nargs = "?" })
  vim.api.nvim_create_user_command("ClrdiagWatchWord", M.add_watch_word, {})
  vim.api.nvim_create_user_command("ClrdiagRemoveWatch", function(cmdopts)
    M.remove_watch(cmdopts.args ~= "" and cmdopts.args or nil)
  end, { nargs = "?" })
  vim.api.nvim_create_user_command("ClrdiagContinue", M.continue, {})
  vim.api.nvim_create_user_command("ClrdiagStepOver", M.step_over, {})
  vim.api.nvim_create_user_command("ClrdiagStepIn", M.step_in, {})
  vim.api.nvim_create_user_command("ClrdiagStepOut", M.step_out, {})
  vim.api.nvim_create_user_command("ClrdiagPause", M.pause, {})

  if opts.keymaps == false then
    return
  end

  local km = opts.keymaps or {}
  vim.keymap.set("n", km.toggle_breakpoint or "<leader>db", M.toggle_breakpoint, { desc = "ClrDiag: 切換中斷點" })
  vim.keymap.set("n", km.watch_word or "<leader>dw", M.add_watch_word, { desc = "ClrDiag: 監看游標下的字" })
  vim.keymap.set("n", km.continue or "<F5>", M.continue, { desc = "ClrDiag: 續行" })
  vim.keymap.set("n", km.step_over or "<F10>", M.step_over, { desc = "ClrDiag: 下一步" })
  vim.keymap.set("n", km.step_in or "<F11>", M.step_in, { desc = "ClrDiag: 進入函式" })
  vim.keymap.set("n", km.step_out or "<S-F11>", M.step_out, { desc = "ClrDiag: 跳出函式" })
end

return M
