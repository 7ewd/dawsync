-- REAPER のプロジェクトを「キー → 値」の集まりとして読み書きする。キーと値の形は Live 用の model.py と同じ。
--
--   tempo, sig
--   t/<id>                  {"o": 並び順, "k": midi|audio|group, "g": 入っているフォルダ（グループ）の id}
--   t/<id>/name|color       （ミキサーは各自のものなので同期しない）
--   c/a/<トラック id>/<開始位置>  アイテム（= アレンジメントのクリップ）{"k", "len", "dur", "n" or "file", "p"}
--
-- 位置・長さは拍（四分音符 = QN）。REAPER は秒で持っているので、TimeMap で変換する。
-- REAPER のトラックには MIDI / オーディオの区別が無いので、相手から来たトラックは種類を P_EXT に覚えておき、
-- こちらで作ったトラックは中身（MIDI のアイテムがあるか）で決める。フォルダはグループとして扱う。
-- 変更は GetProjectStateChangeCount で気づき、全部読み直して前回と比べる（Bitwig の拡張と同じやり方）。

local json = require("json")

local M = {}
M.__index = M

local NOTE_DEFAULTS = { 60, 0, 0.25, 100, 0, 1, 0, 64 }
local KIND_KEY = "P_EXT:abletonmulti_kind"

-- ---------------------------------------------------------------- helpers

local function round(x, digits)
  digits = digits or 5
  local f = 10 ^ digits
  local r = x >= 0 and math.floor(x * f + 0.5) / f or -math.floor(-x * f + 0.5) / f
  if r == 0 then r = 0.0 end
  return r
end
M.round = round

local function time_key(t)
  local s = string.format("%.4f", t)
  s = s:gsub("0+$", ""):gsub("%.$", "")
  if s == "-0" then s = "0" end
  return s
end
M.time_key = time_key

local function split(key)
  local parts = {}
  for p in key:gmatch("[^/]+") do parts[#parts + 1] = p end
  return parts
end

local function priority(key)
  local p = split(key)
  if #p == 2 and (p[1] == "t" or p[1] == "r" or p[1] == "s") then return 0 end
  if #p == 2 and p[1] == "d" then return 1 end
  if p[1] == "c" then return 2 end
  return 3
end
M.priority = priority

local function full_row(r)
  local row = {}
  for i = 1, 8 do
    local v = r and r[i]
    if v == nil or v == json.null then v = NOTE_DEFAULTS[i] end
    row[i] = v
  end
  return json.array(row)
end

local function note_ident(r)
  return string.format("%d@%s+%s", math.floor(r[1] + 0.5), tostring(round(r[2])), tostring(round(r[3])))
end

local function row_less(a, b)
  for i = 1, math.min(#a, #b) do
    if a[i] ~= b[i] then return a[i] < b[i] end
  end
  return #a < #b
end

local function color_to_int(native)
  if not native or native == 0 or (native & 0x1000000) == 0 then return nil end
  local r, g, b = reaper.ColorFromNative(native & 0xFFFFFF)
  return (r << 16) | (g << 8) | b
end

local function int_to_color(rgb)
  rgb = math.floor(rgb)
  return reaper.ColorToNative((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF) | 0x1000000
end

local function copy(t)
  local c = {}
  for k, v in pairs(t) do c[k] = v end
  if json.is_array(t) then json.array(c) end
  return c
end

-- ---------------------------------------------------------------- 変わったところ（delta）
-- クリップの変更は値全体に加えて「変わったところ」も送る（Live の model.py の with_delta と同じ形）:
--   delta = {"f": 変わった項目（"dur" や "p.name"）, "a": 足したノート, "d": 消したノート}

local function row_diff(new_rows, old_rows)
  local old = {}
  for _, r in ipairs(old_rows or {}) do old[#old + 1] = full_row(r) end
  local added = json.array({})
  for _, r in ipairs(new_rows or {}) do
    local row = full_row(r)
    local found
    for i, o in ipairs(old) do
      if json.same(o, row) then found = i break end
    end
    if found then table.remove(old, found) else added[#added + 1] = row end
  end
  return added, json.array(old)
end

function M.with_delta(key, value, previous)
  if key:sub(1, 2) ~= "c/" or type(value) ~= "table" or type(previous) ~= "table" or value.k ~= previous.k then
    return value
  end
  local fields = json.array({})
  local names = {}
  for k in pairs(value) do names[k] = true end
  for k in pairs(previous) do names[k] = true end
  local sorted = {}
  for k in pairs(names) do if k ~= "n" and k ~= "p" and k ~= "delta" then sorted[#sorted + 1] = k end end
  table.sort(sorted)
  for _, k in ipairs(sorted) do
    if not json.same(value[k], previous[k]) then fields[#fields + 1] = k end
  end
  local vp, pp = value.p or {}, previous.p or {}
  local props = {}
  for k in pairs(vp) do props[k] = true end
  for k in pairs(pp) do props[k] = true end
  sorted = {}
  for k in pairs(props) do sorted[#sorted + 1] = k end
  table.sort(sorted)
  for _, k in ipairs(sorted) do
    if not json.same(vp[k], pp[k]) then fields[#fields + 1] = "p." .. k end
  end
  local delta = { f = fields }
  if value.k == "midi" then
    delta.a, delta.d = row_diff(value.n, previous.n)
  end
  local out = copy(value)
  out.delta = delta
  return out
end

local function apply_row_delta(rows, added, removed)
  local result = {}
  for _, r in ipairs(rows or {}) do result[#result + 1] = full_row(r) end
  for _, r in ipairs(removed or {}) do
    local ident = note_ident(full_row(r))
    for i, x in ipairs(result) do
      if note_ident(x) == ident then table.remove(result, i) break end
    end
  end
  for _, r in ipairs(added or {}) do
    local row = full_row(r)
    local ident = note_ident(row)
    for i = #result, 1, -1 do
      if note_ident(result[i]) == ident then table.remove(result, i) end
    end
    result[#result + 1] = row
  end
  table.sort(result, row_less)
  return json.array(result)
end

function M.merge_delta(current, incoming)
  local delta = incoming.delta or {}
  local result = copy(current)
  result.delta = nil
  result.p = copy(current.p or {})
  for _, f in ipairs(delta.f or {}) do
    if f:sub(1, 2) == "p." then
      local name = f:sub(3)
      local ip = incoming.p or {}
      result.p[name] = ip[name]
    else
      result[f] = incoming[f]
    end
  end
  if delta.a or delta.d then
    result.n = apply_row_delta(current.n, delta.a, delta.d)
  end
  return result
end

-- ---------------------------------------------------------------- model

function M.new(log, warn)
  local self = setmetatable({}, M)
  self.log, self.warn = log, warn
  self.ids = {}          -- トラックの GUID -> id
  self.used = {}
  self.order = {}        -- id -> 並び順
  self.last = {}         -- key -> 最後に送った／反映した値
  self.orphans = {}
  self.warned = {}
  self.tag = string.format("%06x", math.random(0, 0xFFFFFF))
  self.counter = 0
  self.initialized = false
  self.baseline_next = true   -- 最初に読んだ状態は送らない（アプリが必要ならスナップショットを取る）
  self.tracks = {}       -- id -> MediaTrack（最後に読んだとき）
  self.parents = {}      -- id -> 親フォルダの id
  self.levels = {}
  return self
end

function M:warn_once(code, message)
  if not self.warned[code] then
    self.warned[code] = true
    self.warn(message)
  end
end

function M:new_id()
  self.counter = self.counter + 1
  return self.tag .. self.counter
end

function M:assign(guid, fallback)
  local id = self.ids[guid]
  if not id then
    if not self.initialized and not self.used[fallback] then id = fallback else id = self:new_id() end
    self.ids[guid] = id
    self.used[id] = true
  end
  return id
end

-- 最長増加部分列（並べ替えで「動かなかった」ものを見つける。model.py の _lis_indices と同じ）
local function order_less(a, b)
  if a[1] ~= b[1] then return a[1] < b[1] end
  return a[2] < b[2]
end

local function lis(keys)
  local tails, tails_idx, prev = {}, {}, {}
  for idx = 1, #keys do
    local k = keys[idx]
    if k then
      local lo, hi = 1, #tails + 1
      while lo < hi do
        local mid = (lo + hi) // 2
        if order_less(tails[mid], k) then lo = mid + 1 else hi = mid end
      end
      prev[idx] = lo > 1 and tails_idx[lo - 1] or nil
      tails[lo], tails_idx[lo] = k, idx
    end
  end
  local result, i = {}, tails_idx[#tails_idx]
  while i do
    result[i] = true
    i = prev[i]
  end
  return result
end

function M:fix_orders(list)
  local keys = {}
  for n, id in ipairs(list) do
    keys[n] = self.order[id] and { self.order[id], id } or false
  end
  local keep = lis(keys)
  local prev
  for n, id in ipairs(list) do
    if keep[n] then
      prev = self.order[id]
    else
      local nxt
      for j = n + 1, #list do
        if keep[j] then nxt = self.order[list[j]] break end
      end
      local o
      if not prev and not nxt then o = n - 1
      elseif not prev then o = nxt - 1
      elseif not nxt then o = prev + 1
      else o = (prev + nxt) / 2 end
      self.order[id] = o
      prev = o
    end
  end
end

local function track_kind(track)
  local _, stored = reaper.GetSetMediaTrackInfo_String(track, KIND_KEY, "", false)
  if reaper.GetMediaTrackInfo_Value(track, "I_FOLDERDEPTH") == 1 then return "group" end
  if stored == "midi" or stored == "audio" or stored == "group" then return stored end
  -- こちらで作ったトラック: MIDI のアイテムがあるか空なら MIDI、オーディオだけならオーディオ
  local items = reaper.CountTrackMediaItems(track)
  if items == 0 then return "midi" end
  for i = 0, items - 1 do
    local take = reaper.GetActiveTake(reaper.GetTrackMediaItem(track, i))
    if take and reaper.TakeIsMIDI(take) then return "midi" end
  end
  return "audio"
end

-- ---------------------------------------------------------------- read

function M:read_all()
  local state = {}
  local tnum, tden, bpm = reaper.TimeMap_GetTimeSigAtTime(0, 0)
  state.tempo = round(bpm, 3)
  state.sig = json.array({ tnum, tden })

  local count = reaper.CountTracks(0)
  local list, tracks = {}, {}
  for i = 0, count - 1 do
    local track = reaper.GetTrack(0, i)
    local id = self:assign(reaper.GetTrackGUID(track), "t" .. i)
    list[#list + 1] = id
    tracks[#tracks + 1] = track
  end
  self:fix_orders(list)
  self.initialized = true

  -- フォルダ（グループ）の親子
  self.tracks, self.parents, self.levels = {}, {}, {}
  local stack = {}
  for n, id in ipairs(list) do
    local track = tracks[n]
    self.tracks[id] = track
    self.parents[id] = stack[#stack]
    self.levels[id] = #stack
    local depth = math.floor(reaper.GetMediaTrackInfo_Value(track, "I_FOLDERDEPTH"))
    if depth == 1 then
      stack[#stack + 1] = id
    elseif depth < 0 then
      for _ = 1, -depth do stack[#stack] = nil end
    end
  end

  for n, id in ipairs(list) do
    local track = tracks[n]
    local key = "t/" .. id
    state[key] = { o = self.order[id], k = track_kind(track), g = self.parents[id] or json.null }
    local _, name = reaper.GetSetMediaTrackInfo_String(track, "P_NAME", "", false)
    state[key .. "/name"] = name
    local color = color_to_int(math.floor(reaper.GetMediaTrackInfo_Value(track, "I_CUSTOMCOLOR")))
    if color then state[key .. "/color"] = color end
    if state[key].k ~= "group" then self:read_items(id, track, state) end
  end

  -- 消えたトラックの id は忘れる
  local alive = {}
  for n = 1, #tracks do alive[reaper.GetTrackGUID(tracks[n])] = true end
  for guid in pairs(self.ids) do
    if not alive[guid] then self.ids[guid] = nil end
  end
  return state
end

function M:read_items(tid, track, state)
  for i = 0, reaper.CountTrackMediaItems(track) - 1 do
    local item = reaper.GetTrackMediaItem(track, i)
    local start = reaper.TimeMap2_timeToQN(0, reaper.GetMediaItemInfo_Value(item, "D_POSITION"))
    local value = self:read_item(item)
    if value then state["c/a/" .. tid .. "/" .. time_key(start)] = value end
  end
end

-- アイテム 1 つを Live のクリップの形で読む
function M:read_item(item)
  local take = reaper.GetActiveTake(item)
  if not take then return nil end
  local pos = reaper.GetMediaItemInfo_Value(item, "D_POSITION")
  local len = reaper.GetMediaItemInfo_Value(item, "D_LENGTH")
  local start_qn = reaper.TimeMap2_timeToQN(0, pos)
  local dur = round(reaper.TimeMap2_timeToQN(0, pos + len) - start_qn)
  local looping = reaper.GetMediaItemInfo_Value(item, "B_LOOPSRC") == 1
  local _, name = reaper.GetSetMediaItemTakeInfo_String(take, "P_NAME", "", false)
  local props = { name = name, looping = looping }
  local color = color_to_int(math.floor(reaper.GetMediaItemInfo_Value(item, "I_CUSTOMCOLOR")))
  if color then props.color = color end

  if reaper.TakeIsMIDI(take) then
    -- ノートの位置は「クリップの中身の頭」からの拍。中身の頭 = PPQ 0 の位置
    local base = reaper.MIDI_GetProjQNFromPPQPos(take, 0)
    local sm = round(start_qn - base)
    local src_len, is_qn = reaper.GetMediaSourceLength(reaper.GetMediaItemTake_Source(take))
    local loop_len = is_qn and src_len or dur
    props.sm, props.ls, props.le = sm, 0.0, round(loop_len)
    props.em = looping and round(loop_len) or round(sm + dur)
    if not looping then props.ls, props.le = sm, props.em end
    local rows = {}
    local _, notes = reaper.MIDI_CountEvts(take)
    for n = 0, notes - 1 do
      local _, _, muted, s, e, _, pitch, vel = reaper.MIDI_GetNote(take, n)
      local qs = reaper.MIDI_GetProjQNFromPPQPos(take, s) - base
      local qe = reaper.MIDI_GetProjQNFromPPQPos(take, e) - base
      rows[#rows + 1] = json.array({ pitch, round(qs), round(qe - qs), vel, muted and 1 or 0, 1, 0, 64 })
    end
    table.sort(rows, row_less)
    local length = looping and props.le - props.ls or props.em - props.sm
    return { k = "midi", len = round(length), dur = dur, n = json.array(rows), p = props }
  end

  local source = reaper.GetMediaItemTake_Source(take)
  local file = reaper.GetMediaSourceFileName(source)
  if (not file or file == "") and reaper.GetMediaSourceParent(source) then
    file = reaper.GetMediaSourceFileName(reaper.GetMediaSourceParent(source))
  end
  local pitch = reaper.GetMediaItemTakeInfo_Value(take, "D_PITCH")
  local coarse = math.floor(pitch + 0.5)
  props.pc = coarse
  props.pf = round((pitch - coarse) * 100, 2)
  return { k = "audio", len = dur, dur = dur, file = file, p = props }
end

-- ---------------------------------------------------------------- changes

local function op(key, value)
  return { k = key, v = value == nil and json.null or value }
end

function M:collect_changes()
  local state = self:read_all()
  local baseline = self.baseline_next
  self.baseline_next = false
  local ops = {}
  for key, before in pairs(self.last) do
    if state[key] == nil then
      self.last[key] = nil
      if not baseline and before ~= json.null then
        local p = split(key)
        if #p == 2 and p[1] == "t" then ops[#ops + 1] = op(key, nil)
        elseif p[1] == "c" and state["t/" .. p[3]] then ops[#ops + 1] = op(key, nil) end
      end
    end
  end
  for key, value in pairs(state) do
    local before = self.last[key]
    if before == nil or not json.same(before, value) then
      self.last[key] = value
      if not baseline then ops[#ops + 1] = op(key, M.with_delta(key, value, before)) end
    end
  end
  table.sort(ops, function(a, b)
    local pa, pb = priority(a.k), priority(b.k)
    if pa ~= pb then return pa < pb end
    return a.k < b.k
  end)
  return ops
end

function M:snapshot()
  local state = self:read_all()
  self.last = {}
  local ops = {}
  for key, value in pairs(state) do
    self.last[key] = value
    ops[#ops + 1] = op(key, value)
  end
  table.sort(ops, function(a, b)
    local pa, pb = priority(a.k), priority(b.k)
    if pa ~= pb then return pa < pb end
    return a.k < b.k
  end)
  return ops
end

function M:adopt(mapping)
  local renamed = {}
  for _, id in pairs(self.ids) do
    local to = mapping[id]
    renamed[id] = type(to) == "string" and to or self:new_id()
  end
  for guid, id in pairs(self.ids) do self.ids[guid] = renamed[id] end
  local order = {}
  for id, o in pairs(self.order) do
    if renamed[id] then order[renamed[id]] = o end
  end
  self.order = order
  for _, id in pairs(self.ids) do self.used[id] = true end
  self.last = {}
  self.baseline_next = true
end

-- まっさらにする（トラック 1 本、テンポ 120、4/4）
function M:make_blank()
  for i = reaper.CountTracks(0) - 1, 0, -1 do reaper.DeleteTrack(reaper.GetTrack(0, i)) end
  reaper.InsertTrackAtIndex(0, true)
  reaper.GetSetMediaTrackInfo_String(reaper.GetTrack(0, 0), KIND_KEY, "midi", true)
  for i = reaper.CountTempoTimeSigMarkers(0) - 1, 1, -1 do reaper.DeleteTempoTimeSigMarker(0, i) end
  self:set_tempo(120, 4, 4)
  self.last = {}
  self.baseline_next = true
end

function M:set_tempo(bpm, num, den)
  if reaper.CountTempoTimeSigMarkers(0) > 0 then
    local _, timepos = reaper.GetTempoTimeSigMarker(0, 0)
    if timepos < 1e-9 then
      reaper.SetTempoTimeSigMarker(0, 0, 0, -1, -1, bpm, num, den, false)
      reaper.UpdateTimeline()
      return
    end
  end
  reaper.SetTempoTimeSigMarker(0, -1, 0, -1, -1, bpm, num, den, false)
  reaper.UpdateTimeline()
end

-- ---------------------------------------------------------------- apply

-- 届いた変更を反映する。pending: key -> 送ったがまだ戻ってきていない変更それぞれの「変えた項目」の集合（"*" は全部）
function M:apply(raw_ops, force, pending)
  local ops = {}
  for _, o in ipairs(raw_ops) do
    local key, value = o.k, o.v
    local waiting = pending[key]
    if force or not waiting or #waiting == 0 then
      ops[#ops + 1] = o
    elseif type(value) == "table" and type(value.delta) == "table" then
      local mine = {}
      for _, set in ipairs(waiting) do for f in pairs(set) do mine[f] = true end end
      if not mine["*"] then
        local fields = json.array({})
        for _, f in ipairs(value.delta.f or {}) do
          if not mine[f] then fields[#fields + 1] = f end
        end
        local v2 = copy(value)
        v2.delta = copy(value.delta)
        v2.delta.f = fields
        ops[#ops + 1] = { k = key, v = v2 }
      end
    end
  end
  if #ops == 0 then return end

  self:read_all()
  self.parents_before = copy(self.parents)
  -- 並び順は先に全部更新する
  for _, o in ipairs(ops) do
    local p = split(o.k)
    if #p == 2 and p[1] == "t" and type(o.v) == "table" then self.order[p[2]] = o.v.o or 0 end
  end
  table.sort(ops, function(a, b)
    local pa, pb = priority(a.k), priority(b.k)
    if pa ~= pb then return pa < pb end
    -- トラックは作ってから消す。クリップは消してから作る
    local va, vb = a.v ~= json.null and a.v ~= nil, b.v ~= json.null and b.v ~= nil
    if va ~= vb then
      if pa == 0 then return va else return not va end
    end
    local oa = type(a.v) == "table" and a.v.o or 0
    local ob = type(b.v) == "table" and b.v.o or 0
    if oa ~= ob then return oa < ob end
    return a.k < b.k
  end)

  local selected = self:save_selection()
  local applied = {}
  local structure = false
  self:find_moves(ops)
  for _, o in ipairs(ops) do
    local ok, err = pcall(function()
      if self:apply_one(o.k, o.v) then applied[o.k] = true end
    end)
    if not ok then self.log("failed to apply " .. o.k .. ": " .. tostring(err)) end
    if priority(o.k) == 0 then structure = true end
  end
  if structure then self:layout() end
  self:apply_orphans(applied)
  self:restore_selection(selected)
  self.moves, self.moved_away = {}, {}

  -- 反映した結果を「最後の値」にする（送り返さない）
  local state = self:read_all()
  for key in pairs(applied) do self.last[key] = state[key] end
  for key, value in pairs(state) do
    if self.last[key] == nil then self.last[key] = value end
  end
  for key in pairs(self.last) do
    if state[key] == nil then self.last[key] = nil end
  end
end

function M:apply_one(key, value)
  if value == json.null then value = nil end
  local p = split(key)
  if key == "tempo" then
    local num, den = reaper.TimeMap_GetTimeSigAtTime(0, 0)
    self:set_tempo_keeping_items(value or 120, num, den)
    return true
  elseif key == "sig" then
    if type(value) ~= "table" then return false end
    local _, _, bpm = reaper.TimeMap_GetTimeSigAtTime(0, 0)
    self:set_tempo_keeping_items(bpm, math.floor(value[1]), math.floor(value[2]))
    return true
  elseif p[1] == "t" then
    if #p == 2 then return self:apply_track(p[2], value) end
    local track = self.tracks[p[2]]
    if not track then
      if value ~= nil then self.orphans[key] = value end
      return false
    end
    if p[3] == "name" then
      reaper.GetSetMediaTrackInfo_String(track, "P_NAME", value or "", true)
    elseif p[3] == "color" and value then
      reaper.SetMediaTrackInfo_Value(track, "I_CUSTOMCOLOR", int_to_color(value))
    end
    return true -- ミュート・ソロ・音量・パン・センドは同期しない
  elseif p[1] == "c" then
    if #p < 4 or p[2] ~= "a" then
      if value then self:warn_once("session", "REAPER にはクリップランチャー（セッション）が無いので、そのクリップは同期できません") end
      return false
    end
    if not self.tracks[p[3]] then
      if value then self.orphans[key] = value end
      return false
    end
    self:apply_clip(p[3], p[4], value)
    return true
  end
  return false -- リターン・シーン・デバイスは REAPER では扱わない
end

function M:apply_orphans(applied)
  local ready = {}
  for key in pairs(self.orphans) do
    local p = split(key)
    if (p[1] == "t" and self.tracks[p[2]]) or (p[1] == "c" and self.tracks[p[3]]) then ready[#ready + 1] = key end
  end
  table.sort(ready, function(a, b) return priority(a) < priority(b) end)
  for _, key in ipairs(ready) do
    local value = self.orphans[key]
    self.orphans[key] = nil
    if self:apply_one(key, value) then applied[key] = true end
  end
end

-- テンポを変えても、アイテムの位置・長さは拍のまま（Live と同じ）にする
function M:set_tempo_keeping_items(bpm, num, den)
  local items = {}
  for i = 0, reaper.CountMediaItems(0) - 1 do
    local item = reaper.GetMediaItem(0, i)
    local pos = reaper.GetMediaItemInfo_Value(item, "D_POSITION")
    local len = reaper.GetMediaItemInfo_Value(item, "D_LENGTH")
    items[#items + 1] = { item, reaper.TimeMap2_timeToQN(0, pos), reaper.TimeMap2_timeToQN(0, pos + len) }
  end
  self:set_tempo(bpm, num, den)
  for _, it in ipairs(items) do
    local s = reaper.TimeMap2_QNToTime(0, it[2])
    reaper.SetMediaItemInfo_Value(it[1], "D_POSITION", s)
    reaper.SetMediaItemInfo_Value(it[1], "D_LENGTH", reaper.TimeMap2_QNToTime(0, it[3]) - s)
  end
end

-- ---------------------------------------------------------------- tracks

function M:apply_track(id, value)
  local track = self.tracks[id]
  if not value then
    if track then
      -- フォルダを消すとき、中のトラックは残す（消すなら個別に届く）
      reaper.DeleteTrack(track)
      self.tracks[id] = nil
    end
    self.order[id] = nil
    self:read_all()
    return true
  end
  self.order[id] = value.o or 0
  self.desired_parent = self.desired_parent or {}
  local g = value.g
  if g == json.null then g = nil end
  self.desired_parent[id] = g or false  -- false = いちばん上（どのフォルダにも入っていない）
  local kind = value.k or "midi"
  if not track then
    reaper.InsertTrackAtIndex(reaper.CountTracks(0), true)
    track = reaper.GetTrack(0, reaper.CountTracks(0) - 1)
    local guid = reaper.GetTrackGUID(track)
    self.ids[guid] = id
    self.used[id] = true
    self.tracks[id] = track
  end
  reaper.GetSetMediaTrackInfo_String(track, KIND_KEY, kind, true)
  return true
end

-- 並び順と親子（フォルダ）を、届いた状態に合わせて並べ直す
function M:layout()
  self.desired_parent = self.desired_parent or {}
  -- 親: 届いた値があればそれ、無ければ反映を始める前の親（フォルダを消すと深さがずれるので、今の状態は使わない）
  local before = self.parents_before or self.parents
  local state_parent = {}
  for id in pairs(self.tracks) do
    local wanted = self.desired_parent[id]
    if wanted ~= nil then
      state_parent[id] = wanted or nil
    else
      state_parent[id] = before[id]
    end
  end
  -- 親を持たないもの（ルート）と、親ごとの子を並び順で並べ、深さ優先でたどる
  local children, roots = {}, {}
  for id in pairs(self.tracks) do
    local parent = state_parent[id]
    if parent and self.tracks[parent] then
      children[parent] = children[parent] or {}
      table.insert(children[parent], id)
    else
      roots[#roots + 1] = id
    end
  end
  local function by_order(a, b)
    local oa, ob = self.order[a] or 0, self.order[b] or 0
    if oa ~= ob then return oa < ob end
    return a < b
  end
  local flat, levels = {}, {}
  local function visit(id, level)
    flat[#flat + 1] = id
    levels[#levels + 1] = level
    local list = children[id] or {}
    table.sort(list, by_order)
    for _, c in ipairs(list) do visit(c, level + 1) end
  end
  table.sort(roots, by_order)
  for _, id in ipairs(roots) do visit(id, 0) end

  -- 並べ替え（選択を使うので、あとで戻す）
  for i, id in ipairs(flat) do
    local track = self.tracks[id]
    if reaper.GetTrack(0, i - 1) ~= track then
      reaper.SetOnlyTrackSelected(track)
      reaper.ReorderSelectedTracks(i - 1, 0)
    end
  end
  -- フォルダの深さ: 次のトラックとの深さの差（最後は 0 に戻す）
  for i, id in ipairs(flat) do
    local nxt = levels[i + 1] or 0
    reaper.SetMediaTrackInfo_Value(self.tracks[id], "I_FOLDERDEPTH", nxt - levels[i])
  end
  self.desired_parent = {}
  self:read_all()
end

-- ---------------------------------------------------------------- selection

function M:save_selection()
  local sel = { tracks = {}, items = {} }
  for i = 0, reaper.CountSelectedTracks(0) - 1 do sel.tracks[#sel.tracks + 1] = reaper.GetSelectedTrack(0, i) end
  for i = 0, reaper.CountSelectedMediaItems(0) - 1 do sel.items[#sel.items + 1] = reaper.GetSelectedMediaItem(0, i) end
  return sel
end

function M:restore_selection(sel)
  for i = reaper.CountTracks(0) - 1, 0, -1 do reaper.SetTrackSelected(reaper.GetTrack(0, i), false) end
  for _, t in ipairs(sel.tracks) do
    if reaper.ValidatePtr(t, "MediaTrack*") then reaper.SetTrackSelected(t, true) end
  end
  for i = reaper.CountMediaItems(0) - 1, 0, -1 do reaper.SetMediaItemSelected(reaper.GetMediaItem(0, i), false) end
  for _, item in ipairs(sel.items) do
    if reaper.ValidatePtr(item, "MediaItem*") then reaper.SetMediaItemSelected(item, true) end
  end
end

-- ---------------------------------------------------------------- clips

local function find_item(track, key)
  for i = 0, reaper.CountTrackMediaItems(track) - 1 do
    local item = reaper.GetTrackMediaItem(track, i)
    if time_key(reaper.TimeMap2_timeToQN(0, reaper.GetMediaItemInfo_Value(item, "D_POSITION"))) == key then return item end
  end
  return nil
end

-- 同じトラックで「消す 1 つ」と「作る 1 つ」が組になっていたら移動として扱う（作り直さずに動かす）
function M:find_moves(ops)
  self.moves, self.moved_away = {}, {}
  local removed, added = {}, {}
  for _, o in ipairs(ops) do
    local p = split(o.k)
    if #p == 4 and p[1] == "c" and p[2] == "a" and self.tracks[p[3]] then
      local exists = find_item(self.tracks[p[3]], p[4]) ~= nil
      local has = o.v ~= nil and o.v ~= json.null
      if not has and exists then removed[p[3]] = removed[p[3]] or {} table.insert(removed[p[3]], p[4]) end
      if has and not exists then added[p[3]] = added[p[3]] or {} table.insert(added[p[3]], p[4]) end
    end
  end
  for tid, list in pairs(removed) do
    if #list == 1 and added[tid] and #added[tid] == 1 then
      self.moves["c/a/" .. tid .. "/" .. added[tid][1]] = list[1]
      self.moved_away["c/a/" .. tid .. "/" .. list[1]] = true
    end
  end
end

function M:apply_clip(tid, key_time, value)
  local track = self.tracks[tid]
  local key = "c/a/" .. tid .. "/" .. key_time
  local item = find_item(track, key_time)
  if not value then
    if item and not (self.moved_away or {})[key] then reaper.DeleteTrackMediaItem(track, item) end
    return
  end
  local start_qn = tonumber(key_time)
  if not item and self.moves and self.moves[key] then
    item = find_item(track, self.moves[key])
    if item then reaper.SetMediaItemInfo_Value(item, "D_POSITION", reaper.TimeMap2_QNToTime(0, start_qn)) end
  end
  if item and type(value.delta) == "table" then
    local current = self:read_item(item)
    if current and current.k == value.k then value = M.merge_delta(current, value) end
  end
  if item then
    local current = self:read_item(item)
    if not current or current.k ~= value.k or (value.k == "audio" and current.file ~= value.file) then
      reaper.DeleteTrackMediaItem(track, item)
      item = nil
    end
  end
  local dur = math.max(tonumber(value.dur) or tonumber(value.len) or 4, 0.25)
  local p = value.p or {}
  if not item then
    if value.k == "midi" then
      item = reaper.CreateNewMIDIItemInProj(track, start_qn, start_qn + dur, true)
    else
      local file = value.file
      if type(file) ~= "string" or not reaper.file_exists(file) then
        self:warn_once("nofile:" .. key, "サンプルファイルが届いていないのでアイテムを作れませんでした")
        return
      end
      item = reaper.AddMediaItemToTrack(track)
      local take = reaper.AddTakeToMediaItem(item)
      reaper.SetMediaItemTake_Source(take, reaper.PCM_Source_CreateFromFile(file))
      local s = reaper.TimeMap2_QNToTime(0, start_qn)
      reaper.SetMediaItemInfo_Value(item, "D_POSITION", s)
      reaper.SetMediaItemInfo_Value(item, "D_LENGTH", reaper.TimeMap2_QNToTime(0, start_qn + dur) - s)
    end
  else
    local s = reaper.TimeMap2_QNToTime(0, start_qn)
    local e = reaper.TimeMap2_QNToTime(0, start_qn + dur)
    if math.abs(reaper.GetMediaItemInfo_Value(item, "D_LENGTH") - (e - s)) > 1e-6 then
      reaper.SetMediaItemInfo_Value(item, "D_LENGTH", e - s)
    end
  end
  local take = reaper.GetActiveTake(item)
  if not take then return end
  if type(p.name) == "string" then reaper.GetSetMediaItemTakeInfo_String(take, "P_NAME", p.name, true) end
  if type(p.color) == "number" then reaper.SetMediaItemInfo_Value(item, "I_CUSTOMCOLOR", int_to_color(p.color)) end
  if p.looping ~= nil then reaper.SetMediaItemInfo_Value(item, "B_LOOPSRC", p.looping and 1 or 0) end
  if value.k == "midi" then
    self:write_notes(take, value.n or {})
  elseif p.pc ~= nil or p.pf ~= nil then
    reaper.SetMediaItemTakeInfo_Value(take, "D_PITCH", (tonumber(p.pc) or 0) + (tonumber(p.pf) or 0) / 100)
  end
  reaper.UpdateItemInProject(item)
end

-- ノートを rows の状態にする。変わっていないノートはそのまま残す
function M:write_notes(take, rows)
  local want = {}
  for _, r in ipairs(rows) do
    local row = full_row(r)
    local ident = note_ident(row)
    want[ident] = want[ident] or {}
    table.insert(want[ident], row)
  end
  local base = reaper.MIDI_GetProjQNFromPPQPos(take, 0)
  local _, count = reaper.MIDI_CountEvts(take)
  local remove = {}
  for n = 0, count - 1 do
    local _, sel, muted, s, e, chan, pitch, vel = reaper.MIDI_GetNote(take, n)
    local qs = reaper.MIDI_GetProjQNFromPPQPos(take, s) - base
    local qe = reaper.MIDI_GetProjQNFromPPQPos(take, e) - base
    local ident = note_ident({ pitch, qs, qe - qs })
    local bucket = want[ident]
    if bucket and #bucket > 0 then
      local row = table.remove(bucket, 1)
      local new_vel = math.max(1, math.min(127, math.floor(row[4] + 0.5)))
      local new_mute = row[5] ~= 0
      if new_vel ~= vel or new_mute ~= muted then
        reaper.MIDI_SetNote(take, n, sel, new_mute, s, e, chan, pitch, new_vel, true)
      end
    else
      remove[#remove + 1] = n
    end
  end
  for i = #remove, 1, -1 do reaper.MIDI_DeleteNote(take, remove[i]) end
  for _, bucket in pairs(want) do
    for _, row in ipairs(bucket) do
      local s = reaper.MIDI_GetPPQPosFromProjQN(take, base + row[2])
      local e = reaper.MIDI_GetPPQPosFromProjQN(take, base + row[2] + math.max(row[3], 1 / 1024))
      reaper.MIDI_InsertNote(take, false, row[5] ~= 0, s, e, 0, math.floor(row[1] + 0.5),
        math.max(1, math.min(127, math.floor(row[4] + 0.5))), true)
    end
  end
  reaper.MIDI_Sort(take)
end

return M
