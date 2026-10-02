-- 最小限の JSON（DawSync の REAPER 用スクリプトで使う）
-- 値は table（配列は json.array で作ったもの、または 1 から続く数字のキーだけのもの）/ string / number / boolean / json.null
local json = {}

json.null = setmetatable({}, { __tostring = function() return "null" end })
local ARRAY = setmetatable({}, { __mode = "k" })

-- 配列として書き出す table に印を付ける（空の table を [] にするため）
function json.array(t)
  t = t or {}
  ARRAY[t] = true
  return t
end

function json.is_array(t)
  if ARRAY[t] then return true end
  if type(t) ~= "table" then return false end
  local n = 0
  for k in pairs(t) do
    if type(k) ~= "number" then return false end
    n = n + 1
  end
  return n > 0 and n == #t
end

-- ---------------------------------------------------------------- 書き出し

local escapes = { ['"'] = '\\"', ['\\'] = '\\\\', ['\n'] = '\\n', ['\r'] = '\\r', ['\t'] = '\\t', ['\b'] = '\\b', ['\f'] = '\\f' }

local function quote(s)
  return '"' .. s:gsub('[%c"\\]', function(c)
    return escapes[c] or string.format("\\u%04x", c:byte())
  end) .. '"'
end

local function number(n)
  if n ~= n or n == math.huge or n == -math.huge then return "null" end
  if n == math.floor(n) and math.abs(n) < 1e15 then
    return string.format("%d", math.floor(n))
  end
  return string.format("%.12g", n)
end

function json.encode(v)
  local t = type(v)
  if v == nil or v == json.null then return "null" end
  if t == "boolean" then return v and "true" or "false" end
  if t == "number" then return number(v) end
  if t == "string" then return quote(v) end
  if t == "table" then
    local out = {}
    if json.is_array(v) then
      for i = 1, #v do out[#out + 1] = json.encode(v[i]) end
      return "[" .. table.concat(out, ",") .. "]"
    end
    local keys = {}
    for k in pairs(v) do keys[#keys + 1] = tostring(k) end
    table.sort(keys)
    for _, k in ipairs(keys) do out[#out + 1] = quote(k) .. ":" .. json.encode(v[k]) end
    return "{" .. table.concat(out, ",") .. "}"
  end
  return quote(tostring(v))
end

-- ---------------------------------------------------------------- 読み込み

local function utf8char(cp)
  if cp < 0x80 then return string.char(cp) end
  if cp < 0x800 then return string.char(0xC0 | (cp >> 6), 0x80 | (cp & 0x3F)) end
  if cp < 0x10000 then return string.char(0xE0 | (cp >> 12), 0x80 | ((cp >> 6) & 0x3F), 0x80 | (cp & 0x3F)) end
  return string.char(0xF0 | (cp >> 18), 0x80 | ((cp >> 12) & 0x3F), 0x80 | ((cp >> 6) & 0x3F), 0x80 | (cp & 0x3F))
end

function json.decode(s)
  local i = 1
  local function ws()
    i = s:find("[^ \t\r\n]", i) or (#s + 1)
  end
  local value

  local function str()
    i = i + 1
    local parts = {}
    while true do
      local j = s:find('["\\]', i)
      if not j then error("unterminated string") end
      parts[#parts + 1] = s:sub(i, j - 1)
      if s:sub(j, j) == '"' then
        i = j + 1
        return table.concat(parts)
      end
      local e = s:sub(j + 1, j + 1)
      if e == "u" then
        local cp = tonumber(s:sub(j + 2, j + 5), 16)
        i = j + 6
        if cp >= 0xD800 and cp <= 0xDBFF and s:sub(i, i + 1) == "\\u" then
          local lo = tonumber(s:sub(i + 2, i + 5), 16)
          cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00)
          i = i + 6
        end
        parts[#parts + 1] = utf8char(cp)
      else
        local map = { n = "\n", r = "\r", t = "\t", b = "\b", f = "\f" }
        parts[#parts + 1] = map[e] or e
        i = j + 2
      end
    end
  end

  function value()
    ws()
    local c = s:sub(i, i)
    if c == "{" then
      local obj = {}
      i = i + 1
      ws()
      if s:sub(i, i) == "}" then i = i + 1 return obj end
      while true do
        ws()
        local k = str()
        ws()
        i = i + 1 -- ':'
        obj[k] = value()
        ws()
        local d = s:sub(i, i)
        i = i + 1
        if d == "}" then return obj end
      end
    elseif c == "[" then
      local arr = json.array({})
      i = i + 1
      ws()
      if s:sub(i, i) == "]" then i = i + 1 return arr end
      while true do
        local v = value()
        arr[#arr + 1] = v
        ws()
        local d = s:sub(i, i)
        i = i + 1
        if d == "]" then return arr end
      end
    elseif c == '"' then
      return str()
    elseif s:sub(i, i + 3) == "true" then
      i = i + 4 return true
    elseif s:sub(i, i + 4) == "false" then
      i = i + 5 return false
    elseif s:sub(i, i + 3) == "null" then
      i = i + 4 return json.null
    else
      local j = s:find("[^%d%.eE%+%-]", i) or (#s + 1)
      local n = tonumber(s:sub(i, j - 1))
      if not n then error("bad json at " .. i) end
      i = j
      return n
    end
  end

  return value()
end

-- 値の比較（数値は値で、table は中身で比べる。null と nil は同じ）
function json.same(a, b)
  if a == json.null then a = nil end
  if b == json.null then b = nil end
  if type(a) == "number" and type(b) == "number" then return a == b end
  if type(a) ~= "table" or type(b) ~= "table" then return a == b end
  for k, v in pairs(a) do
    if not json.same(v, b[k]) then return false end
  end
  for k, v in pairs(b) do
    if a[k] == nil and v ~= json.null then return false end
  end
  return true
end

return json
