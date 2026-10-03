-- DawSync for REAPER
--
-- REAPER の中で動き、同じ PC の DawSync アプリとやりとりする。やりとりの中身は Live の Remote Script
-- （multi.py）とまったく同じなので、アプリからは Live と同じに見える。
-- REAPER の Lua には通信の機能が無いので、ファイルでやりとりする（アプリ側の ReaperRelay が TCP に中継する）:
--   <AppData>/DawSync/reaper-ipc/<ポート>/in/   アプリ → REAPER（1 行 1 JSON のファイル）
--                                                    out/  REAPER → アプリ
--                                                    reaper_alive  REAPER が動いている印（時刻）
--                                                    app_alive     アプリがつながっている印（時刻 と 接続ごとの番号）
-- REAPER の起動時に Scripts/__startup.lua から読み込まれる（アプリの「REAPER にスクリプトを入れる」で設定される）。

local VERSION = "1.0.4"
local PROTOCOL = 2
local APP_PORT = 47400
-- アプリの印（app_alive）がこれだけ古くなったら切れたとみなす。大きなプロジェクトでは REAPER の画面の処理が
-- しばらく止まることがあるので、余裕を持たせる（アプリ側の ReaperRelay も同じ長さ）
local ALIVE_TIMEOUT = 10

-- __startup.lua から動くと、Lua の状態を他のスクリプトと共有する。require だと同じ名前（json など）の
-- 別のモジュールと取り違えるので、このファイルの隣のファイルを直接読み込む
local script_dir = debug.getinfo(1, "S").source:match("^@(.*[\\/])") or ""
local json = dofile(script_dir .. "json.lua")
-- model.lua には同じ json を渡す（json.null や配列の印は、同じ json でないと通じない）
local Model = assert(loadfile(script_dir .. "model.lua"))(json)

-- やりとりの場所は REAPER の設定フォルダではなく、アプリと決めた固定の場所（ポータブル版の REAPER でも見つかるように）
local function ipc_root()
  local os_name = reaper.GetOS()
  if os_name:match("^Win") then return (os.getenv("APPDATA") or "") .. "/DawSync/reaper-ipc" end
  if os_name:match("OSX") or os_name:match("macOS") then
    return (os.getenv("HOME") or "") .. "/Library/Application Support/DawSync/reaper-ipc"
  end
  return (os.getenv("HOME") or "") .. "/.config/DawSync/reaper-ipc"
end
local root = ipc_root()

local function read_file(path)
  local f = io.open(path, "rb")
  if not f then return nil end
  local s = f:read("a")
  f:close()
  return s
end

local function write_file(path, text)
  local tmp = path .. ".tmp"
  local f = io.open(tmp, "wb")
  if not f then return false end
  f:write(text)
  f:close()
  os.remove(path)
  return os.rename(tmp, path)
end

local log_path = root .. "/reaper.log"
local function log(message)
  local f = io.open(log_path, "ab")
  if f then
    f:write(os.date("%H:%M:%S ") .. message .. "\n")
    f:close()
  end
end

-- つなぐ先のポート。テスト用に、ipc/debug と ipc/port（"ポート 期限のミリ秒"）があれば期限までそちらを使う
-- テストで REAPER を起動するときだけ、環境変数と印のファイルの両方で有効にする
-- （普段の REAPER で、ほかのプログラムが一時フォルダにファイルを置くだけで REAPER を操作できないように）
local function debug_enabled()
  return os.getenv("DAWSYNC_DEBUG") == "1" and read_file(root .. "/debug") ~= nil
end

local function app_port()
  if debug_enabled() then
    local text = read_file(root .. "/port")
    if text then
      local port, expiry = text:match("(%d+)%s+(%d+)")
      if port and tonumber(expiry) > os.time() * 1000 then return tonumber(port) end
    end
  end
  return APP_PORT
end

-- ---------------------------------------------------------------- state

local port, base, in_dir, out_dir
local model
local project, project_path
local connected, app_session = false, nil
local app_seen = 0           -- 最後にアプリの印を読めた時刻（ファイルは置き直されるので、一瞬読めないことがある）
local pending = {}          -- key -> { 変えた項目の集合, ... }
local outgoing = {}
local out_seq = 0
local dirty = true
local last_count = -1
local last_check, last_heartbeat, last_alive_check = 0, 0, 0
local debug_mode = false
-- 同時に 2 つ以上動かさない: 起動するたびに REAPER の共有メモリ（ExtState）に自分の印を書き、
-- 自分より新しい印に変わっていたら止まる（__startup.lua から動いているときにアクションから実行した、
-- 新しいスクリプトを入れて動かし直した、など）
local instance = string.format("%.6f-%d", reaper.time_precise(), math.random(1, 1 << 30))

local function send(msg)
  outgoing[#outgoing + 1] = json.encode(msg)
end

local function flush_outgoing()
  if #outgoing == 0 then return end
  out_seq = out_seq + 1
  write_file(string.format("%s/%012d.json", out_dir, out_seq), table.concat(outgoing, "\n") .. "\n")
  outgoing = {}
end

local function warn(message)
  log("warn: " .. message)
  send({ t = "warn", msg = message })
end

local function setup_dirs()
  port = app_port()
  base = root .. "/" .. port
  in_dir, out_dir = base .. "/in", base .. "/out"
  reaper.RecursiveCreateDirectory(in_dir, 0)
  reaper.RecursiveCreateDirectory(out_dir, 0)
  -- 前回の残りを消す
  for _, dir in ipairs({ out_dir }) do
    reaper.EnumerateFiles(dir, -1)
    local names, i = {}, 0
    while true do
      local name = reaper.EnumerateFiles(dir, i)
      if not name then break end
      names[#names + 1] = name
      i = i + 1
    end
    for _, name in ipairs(names) do os.remove(dir .. "/" .. name) end
  end
end

-- 今のプロジェクト（タブ）とそのファイル。同じタブで別の .rpp を開いた・新規にしたときは、ReaProject* が
-- 変わらないことがあるので、ファイルの場所でも見分ける
local function current_project()
  local proj, path = reaper.EnumProjects(-1, "")
  return proj, path or ""
end

local function attach()
  project, project_path = current_project()
  model = Model.new(log, warn)
  pending = {}
  dirty = true
  last_count = -1
end

local function send_hello()
  local version = reaper.GetAppVersion()
  send({ t = "hello", proto = PROTOCOL, script = "reaper-" .. VERSION, live = "REAPER " .. version, daw = "reaper" })
  send({ t = "api", info = { daw = "reaper", version = version } })
end

local function debug_dump()
  if not debug_mode then return end
  write_file(base .. "/state.json", json.encode(model.last))
end

local function flush_local(force)
  if not connected then return end
  local now = reaper.time_precise()
  local count = reaper.GetProjectStateChangeCount(0)
  -- 変更の番号が変わったときだけ読み直す（念のため 5 秒に 1 回は見る）
  if not force and count == last_count and now - last_check < 5.0 then return end
  if not force and now - last_check < 0.1 then return end
  last_count, last_check = count, now
  local ops = model:collect_changes()
  if #ops > 0 then
    for _, o in ipairs(ops) do
      local fields = {}
      if type(o.v) == "table" and type(o.v.delta) == "table" then
        for _, f in ipairs(o.v.delta.f or {}) do fields[f] = true end
      else
        fields["*"] = true
      end
      pending[o.k] = pending[o.k] or {}
      table.insert(pending[o.k], fields)
    end
    send({ t = "ops", ops = json.array(ops) })
  end
  debug_dump()
end

local function transaction(name, fn)
  reaper.Undo_BeginBlock2(0)
  reaper.PreventUIRefresh(1)
  local ok, err = pcall(fn)
  reaper.PreventUIRefresh(-1)
  reaper.UpdateArrange()
  reaper.Undo_EndBlock2(0, name, -1)
  if not ok then log("error: " .. tostring(err)) end
end

local function handle(msg)
  local kind = msg.t
  if kind == "apply" then
    flush_local(true)
    transaction("DAW Sync: 相手の変更", function()
      model:apply(msg.ops or {}, msg.force == true, pending)
    end)
    last_count = reaper.GetProjectStateChangeCount(0)
    debug_dump()
  elseif kind == "ack" then
    for _, key in ipairs(msg.keys or {}) do
      local waiting = pending[key]
      if waiting and #waiting > 0 then table.remove(waiting, 1) end
      if waiting and #waiting == 0 then pending[key] = nil end
    end
  elseif kind == "busy" then
    -- 同じ PC で別の DAW が先にアプリにつながっている（アプリ側の中継が 2 秒ごとにつなぎ直す）
    log("waiting: another DAW (" .. tostring(msg.daw) .. ") is connected to the app")
  elseif kind == "reset" then
    pending = {}
  elseif kind == "snapshot_req" then
    flush_local(true)
    send({ t = "snapshot", id = msg.id, ops = json.array(model:snapshot()) })
  elseif kind == "blank" then
    transaction("DAW Sync: まっさらにする", function() model:make_blank() end)
    model:collect_changes() -- まっさらにした変更そのものは送らない
    last_count = reaper.GetProjectStateChangeCount(0)
    send({ t = "blanked", id = msg.id })
  elseif kind == "adopt" then
    model:adopt(type(msg.map) == "table" and msg.map or {})
    model:collect_changes()
    send({ t = "adopted", id = msg.id })
  end
end

local function read_inbox()
  reaper.EnumerateFiles(in_dir, -1)
  local names, i = {}, 0
  while true do
    local name = reaper.EnumerateFiles(in_dir, i)
    if not name then break end
    if name:match("%.json$") then names[#names + 1] = name end
    i = i + 1
  end
  table.sort(names)
  for _, name in ipairs(names) do
    local path = in_dir .. "/" .. name
    local text = read_file(path)
    -- 消せたものだけ扱う（消せないまま扱うと次の回にもう一度扱ってしまい、ack が 2 回効くなどする）。
    -- 順番を崩さないよう、読めない・消せないものがあったら、そこから先は次の回に
    if not text or not os.remove(path) then break end
    if connected then
      for line in text:gmatch("[^\n]+") do
        local ok, msg = pcall(json.decode, line)
        if ok and type(msg) == "table" then
          local ok2, err = pcall(handle, msg)
          if not ok2 then log("error handling " .. tostring(msg.t) .. ": " .. tostring(err)) end
        end
      end
    end
  end
end

-- 開発用: ipc/debug があるとき、<ポート>/cmd.lua を置くと REAPER の中で実行する（テストで REAPER 側の編集をするため）
local function debug_commands()
  if not debug_mode then return end
  local path = base .. "/cmd.lua"
  local code = read_file(path)
  if not code then return end
  os.remove(path)
  local fn, err = load(code, "cmd", "t", setmetatable({ reaper = reaper }, { __index = _G }))
  if not fn then log("cmd error: " .. err) return end
  local ok, err2 = pcall(fn)
  log(ok and "cmd ok" or ("cmd failed: " .. tostring(err2)))
end

local function tick()
  local now = reaper.time_precise()
  if now - last_heartbeat >= 1.0 then
    last_heartbeat = now
    debug_mode = debug_enabled()
    if app_port() ~= port then
      setup_dirs()
      connected = false
    end
    write_file(base .. "/reaper_alive", tostring(os.time()))
  end
  if reaper.GetExtState("DawSync", "instance") ~= instance then
    log("stopped (a newer script was started)")
    return  -- defer しない = このスクリプトは終わる
  end

  -- 別のプロジェクト（タブ）に切り替わった、または同じタブで別のファイルを開いた・新規にした
  local proj, path = current_project()
  if proj ~= project or path ~= project_path then
    attach()
    if connected then send_hello() end
  end

  if now - last_alive_check >= 0.3 then
    last_alive_check = now
    local text = read_file(base .. "/app_alive") or ""
    local t, session = text:match("(%d+)%s+(%S+)")
    if t ~= nil and os.time() - tonumber(t) <= ALIVE_TIMEOUT then app_seen = now end
    local alive = now - app_seen <= ALIVE_TIMEOUT
    if alive and session and (not connected or session ~= app_session) then
      connected, app_session = true, session
      pending = {}
      send_hello()
      log("connected to the app")
    elseif not alive and connected then
      connected = false
      pending = {}
      log("disconnected from the app")
    end
  end

  local ok, err = pcall(function()
    debug_commands()
    -- 届いた変更を反映する前に、自分の変更を先に送る（ユーザーの操作が上書きされて消えないように）
    flush_local(false)
    read_inbox()
  end)
  if not ok then log("error: " .. tostring(err)) end
  flush_outgoing()
  reaper.defer(tick)
end

math.randomseed(os.time())
reaper.SetExtState("DawSync", "instance", instance, false)
setup_dirs()
attach()
log("DAW Sync for REAPER " .. VERSION .. " loaded (port " .. port .. ")")
reaper.atexit(function() os.remove(base .. "/reaper_alive") end)
tick()
