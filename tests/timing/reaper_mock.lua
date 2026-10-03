-- REAPER の API のうち、テンポマーカーとプロジェクトマーカーの読み書きに使うものだけをまねたもの（test_timing.py 用）。
-- テンポマーカーは秒の位置に置き、linear なマーカーからは次のマーカーまで時間に対して直線でテンポが変わるとする。
-- 拍子（num/den）が 0 のマーカーは拍子を変えない。アイテムは無い。
local R = {}
local tempo = {}     -- { time, bpm, num, den, linear }
local markers = {}   -- { id, pos, name, color }
local project_bpm, project_num, project_den = 120, 4, 4

local function sort_tempo() table.sort(tempo, function(a, b) return a.time < b.time end) end

-- i 番目のマーカーから次のマーカーまでの、1 秒あたりの BPM の変化
local function slope(i)
  local m, n = tempo[i], tempo[i + 1]
  if m.linear and n and n.time > m.time then return (n.bpm - m.bpm) / (n.time - m.time) end
  return 0
end

-- 区間の並び: { start, bpm, slope, qn（区間の始まりの拍） }
local function segments()
  local segs, qn = {}, 0
  local start, bpm, k = 0, project_bpm, 0
  if tempo[1] and tempo[1].time <= 0 then start, bpm, k = 0, tempo[1].bpm, slope(1) end
  local first = (tempo[1] and tempo[1].time <= 0) and 2 or 1
  segs[1] = { start = start, bpm = bpm, k = k, qn = 0 }
  for i = first, #tempo do
    local s = segs[#segs]
    local dt = tempo[i].time - s.start
    qn = s.qn + (s.bpm * dt + s.k * dt * dt / 2) / 60
    segs[#segs + 1] = { start = tempo[i].time, bpm = tempo[i].bpm, k = slope(i), qn = qn }
  end
  return segs
end

function R.TimeMap2_timeToQN(_, t)
  local segs = segments()
  local s = segs[1]
  for _, x in ipairs(segs) do if x.start <= t then s = x end end
  local dt = t - s.start
  return s.qn + (s.bpm * dt + s.k * dt * dt / 2) / 60
end

function R.TimeMap2_QNToTime(_, q)
  local segs = segments()
  local s = segs[1]
  for _, x in ipairs(segs) do if x.qn <= q then s = x end end
  local dq = (q - s.qn) * 60
  if math.abs(s.k) < 1e-12 then return s.start + dq / s.bpm end
  return s.start + (-s.bpm + math.sqrt(s.bpm * s.bpm + 2 * s.k * dq)) / s.k
end

function R.TimeMap2_GetDividedBpmAtTime(_, t)
  local segs = segments()
  local s = segs[1]
  for _, x in ipairs(segs) do if x.start <= t then s = x end end
  return s.bpm + s.k * (t - s.start)
end

function R.TimeMap_GetTimeSigAtTime(_, t)
  local num, den = project_num, project_den
  for _, m in ipairs(tempo) do
    if m.time <= t + 1e-12 and m.num > 0 then num, den = m.num, m.den end
  end
  return num, den, R.TimeMap2_GetDividedBpmAtTime(0, t)
end

function R.CountTempoTimeSigMarkers() return #tempo end

function R.GetTempoTimeSigMarker(_, i)
  local m = tempo[i + 1]
  if not m then return false, 0, 0, 0, 0, 0, 0, false end
  return true, m.time, 0, 0, m.bpm, m.num, m.den, m.linear
end

function R.SetTempoTimeSigMarker(_, i, time, measure, beat, bpm, num, den, linear)
  assert(time >= 0 and measure == -1 and beat == -1, "timepos only")
  local m = { time = time, bpm = bpm, num = num, den = den, linear = linear and true or false }
  if i < 0 then tempo[#tempo + 1] = m else tempo[i + 1] = m end
  sort_tempo()
  return true
end

function R.DeleteTempoTimeSigMarker(_, i)
  table.remove(tempo, i + 1)
  return true
end

local function sort_markers() table.sort(markers, function(a, b) return a.pos < b.pos end) end

function R.CountProjectMarkers() return #markers, #markers, 0 end

function R.EnumProjectMarkers3(_, i)
  local m = markers[i + 1]
  if not m then return 0 end
  return i + 1, false, m.pos, m.pos, m.name, m.id, m.color
end

function R.AddProjectMarker2(_, isrgn, pos, _, name, want, color)
  assert(not isrgn)
  local id = 0
  for _, m in ipairs(markers) do id = math.max(id, m.id) end
  id = want >= 0 and want or id + 1
  markers[#markers + 1] = { id = id, pos = pos, name = name, color = color }
  sort_markers()
  return id
end

function R.DeleteProjectMarker(_, id, isrgn)
  assert(not isrgn)
  for n, m in ipairs(markers) do
    if m.id == id then table.remove(markers, n) return true end
  end
  return false
end

local function set_marker(id, pos, name, color, clear)
  for _, m in ipairs(markers) do
    if m.id == id then
      m.pos, m.color = pos, color
      -- 本物と同じく、空の名前は「変えない」。消すのは flags の 1
      if clear then m.name = "" elseif name ~= "" then m.name = name end
      sort_markers()
      return true
    end
  end
  return false
end

function R.SetProjectMarker3(_, id, isrgn, pos, _, name, color)
  assert(not isrgn)
  return set_marker(id, pos, name, color, false)
end

function R.SetProjectMarker4(_, id, isrgn, pos, _, name, color, flags)
  assert(not isrgn)
  return set_marker(id, pos, name, color, (flags & 1) == 1)
end

function R.CountMediaItems() return 0 end
function R.UpdateTimeline() end

-- テスト用
R.mock = {
  tempo = function() return tempo end,
  markers = function() return markers end,
  reset = function(bpm, num, den)
    tempo, markers = {}, {}
    project_bpm, project_num, project_den = bpm or 120, num or 4, den or 4
  end,
}

return R
