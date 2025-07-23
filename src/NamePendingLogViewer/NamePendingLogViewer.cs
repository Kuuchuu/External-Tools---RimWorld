using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json;
using Verse;

namespace LiveLogWeb
{
    public class LogEntry
    {
        public long TimestampTicks { get; }
        public string Level { get; }
        public string Message { get; }

        public LogEntry(string level, string message)
        {
            TimestampTicks = DateTime.UtcNow.Ticks;
            Level = level;
            Message = message;
        }
    }

    public static class LogBuffer
    {
        private static readonly ConcurrentQueue<LogEntry> _entries = new ConcurrentQueue<LogEntry>();

        public static void Add(string level, string msg)
        {
            _entries.Enqueue(new LogEntry(level, msg));
        }

        public static IEnumerable<LogEntry> GetSince(long sinceTicks, string levelFilter = null)
        {
            foreach (var e in _entries)
            {
                if (e.TimestampTicks <= sinceTicks) continue;
                if (!string.IsNullOrEmpty(levelFilter)
                    && !e.Level.Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return e;
            }
        }
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Message), new Type[] { typeof(string) })]
    public static class MessagePatch
    {
        [HarmonyPrefix]
        public static void Prefix(string text)
        {
            LogBuffer.Add("Message", text ?? "");
        }
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Warning), new Type[] { typeof(string) })]
    public static class WarningPatch
    {
        [HarmonyPrefix]
        public static void Prefix(string text)
        {
            LogBuffer.Add("Warning", text ?? "");
        }
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Error), new Type[] { typeof(string) })]
    public static class ErrorPatch
    {
        [HarmonyPrefix]
        public static void Prefix(string text)
        {
            LogBuffer.Add("Error", text ?? "");
        }
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Message), new Type[] { typeof(object) })]
    public static class MessageObjectPatch
    {
        [HarmonyPrefix]
        public static void Prefix(object obj)
        {
            LogBuffer.Add("Message", obj?.ToString() ?? "");
        }
    }

    public class LiveLogMod : Mod
    {
        private readonly Thread _serverThread;

        public LiveLogMod(ModContentPack content) : base(content)
        {
            new Harmony("com.leoraaaaaaaaaaaaaaaaaaaaa.livelogweb").PatchAll();
            _serverThread = new Thread(ServerLoop) { IsBackground = true };
            _serverThread.Start();
        }

        private void ServerLoop()
        {
            var listener = new HttpListener();
            listener.Prefixes.Add("http://+:7788/");
            try { listener.Start(); }
            catch { return; }

            while (listener.IsListening)
            {
                var ctx = listener.GetContext();
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
            }

            listener.Close();
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            if (req.Url.AbsolutePath == "/")
            {
                var html = GetHtmlPage();
                var bytes = Encoding.UTF8.GetBytes(html);
                res.ContentType = "text/html; charset=UTF-8";
                res.OutputStream.Write(bytes, 0, bytes.Length);
            }
            else if (req.Url.AbsolutePath == "/logs")
            {
                long since = 0;
                long.TryParse(req.QueryString["since"], out since);
                var level = req.QueryString["level"];
                var entries = LogBuffer.GetSince(since, level);
                var json = JsonConvert.SerializeObject(entries);
                var buf = Encoding.UTF8.GetBytes(json);
                res.ContentType = "application/json";
                res.OutputStream.Write(buf, 0, buf.Length);
            }
            else
            {
                res.StatusCode = 404;
            }

            res.OutputStream.Close();
        }

        private string GetHtmlPage() =>
@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<title>RimWorld Live Log</title>
<style>
 body { font-family: monospace; margin:0; padding:0; }
 #controls { padding:10px; background:#333; color:#fff; }
 #log { height: calc(100vh - 50px); overflow:auto; padding:10px; background:#000; color:#0f0; }
 .entry { margin-bottom:4px; padding: 2px 4px; cursor: default; }
 .forbid-select { user-select: none; }
 .Message { color: #0f0; }
 .Warning { color: #ff0; }
 .Error { color: #f00; }
 .selected { background: #444 !important; }
 .even { background: #111; }
 .odd { background: #000; }
 .level-toggle { margin-right: 5px; padding: 2px 6px; background: #222; color: #fff; border: 1px solid #555; cursor: pointer; }
 .level-toggle.active { background: #555; }
 #contextMenu { position: absolute; display: none; background: #222; border: 1px solid #555; z-index: 1000; }
 #contextMenu div { padding: 5px 10px; color: #fff; cursor: pointer; }
 #contextMenu div:hover { background: #444; }
 .right-menu { float: right; }
</style>
</head>
<body>
<div id=""controls"">
 Since: <span id=""sinceDisplay"">0</span>
 <span>
  <button class=""level-toggle active"" data-level=""Message"">Message</button>
  <button class=""level-toggle active"" data-level=""Warning"">Warning</button>
  <button class=""level-toggle active"" data-level=""Error"">Error</button>
 </span>
 <label>Interval:
  <select id=""intervalSelect"">
    <option value=""100"">100ms</option>
    <option value=""500"">500ms</option>
    <option value=""1000"" selected>1s</option>
    <option value=""5000"">5s</option>
    <option value=""10000"">10s</option>
    <option value=""30000"">30s</option>
  </select>
 </label>
 <span class=""right-menu"">
  <button id=""copySelectedAlt"">Copy Selected</button>
  <button id=""clearSelectedAlt"">Clear Selected</button>
 </span>
</div>
<div id=""log""></div>
<div id=""contextMenu"">
  <div id=""copySelected"">Copy selected</div>
  <div id=""clearSelected"">Clear selection</div>
</div>
<script>
let since = 0;
let intervalId = null;
const seenKeys = new Set();
const logBox = document.getElementById('log');
const contextMenu = document.getElementById('contextMenu');
let lastClickedEntry = null;
let entryIndex = 0;

function fetchLogs() {
  const activeLevels = Array.from(document.querySelectorAll('.level-toggle.active')).map(btn => btn.dataset.level);
  const nearBottom = logBox.scrollHeight - logBox.scrollTop - logBox.clientHeight < 20;

  fetch(`/logs?since=${since}`)
    .then(r => r.json())
    .then(arr => {
      let added = false;
      for (const e of arr) {
        const key = `${e.TimestampTicks}|${e.Message}`;
        if (seenKeys.has(key)) continue;
        seenKeys.add(key);

        if (!activeLevels.includes(e.Level)) continue;

        const d = new Date(e.TimestampTicks / 1e4);
        const div = document.createElement('div');
        div.className = `entry ${e.Level} ${entryIndex++ % 2 === 0 ? 'even' : 'odd'}`;
        div.textContent = `[${d.toISOString()}][${e.Level}] ${e.Message}`;
        div.dataset.timestamp = e.TimestampTicks;
        logBox.appendChild(div);
        since = Math.max(since, e.TimestampTicks);
        added = true;
      }
      document.getElementById('sinceDisplay').textContent = since;
      if (added && nearBottom) logBox.scrollTop = logBox.scrollHeight;
    });
}

function startPolling() {
  if (intervalId) clearInterval(intervalId);
  const interval = parseInt(document.getElementById('intervalSelect').value, 10);
  intervalId = setInterval(fetchLogs, interval);
}

document.getElementById('intervalSelect').addEventListener('change', startPolling);

document.querySelectorAll('.level-toggle').forEach(btn => {
  btn.addEventListener('click', () => {
    btn.classList.toggle('active');
    since = 0;
    seenKeys.clear();
    logBox.innerHTML = '';
    entryIndex = 0;
  });
});

logBox.addEventListener('click', e => {
  if (!e.target.classList.contains('entry')) return;
  if (e.shiftKey && lastClickedEntry) {
    const entries = Array.from(document.querySelectorAll('.entry'));
    const start = entries.indexOf(lastClickedEntry);
    const end = entries.indexOf(e.target);
    const [min, max] = [Math.min(start, end), Math.max(start, end)];
    const shouldSelect = !lastClickedEntry.classList.contains('selected');
    for (let i = min; i <= max; i++) {
      entries[i].classList.toggle('selected', shouldSelect);
    }
  } else {
    e.target.classList.toggle('selected');
    lastClickedEntry = e.target;
  }
});

logBox.addEventListener('contextmenu', e => {
  if (!e.target.classList.contains('entry')) return;
  e.preventDefault();
  contextMenu.style.top = `${e.pageY}px`;
  contextMenu.style.left = `${e.pageX}px`;
  contextMenu.style.display = 'block';
});

document.addEventListener('click', () => contextMenu.style.display = 'none');

function copySelected() {
  if (intervalId) clearInterval(intervalId);
  const interval = parseInt(document.getElementById('intervalSelect').value, 10);
  intervalId = setInterval(fetchLogs, interval);
}

//document.getElementById('copySelected').addEventListener('click', () => {
//  const selected = Array.from(document.querySelectorAll('.entry.selected'));
//  selected.sort((a, b) => parseInt(a.dataset.timestamp) - parseInt(b.dataset.timestamp));
//  const text = selected.map(e => e.textContent).join('\n');
//  navigator.clipboard.writeText(text);
//});

['copySelected', 'copySelectedAlt'].forEach(id => {
  document.getElementById(id).addEventListener('click', () => {
    const selected = Array.from(document.querySelectorAll('.entry.selected'));
    selected.sort((a, b) => parseInt(a.dataset.timestamp) - parseInt(b.dataset.timestamp));
    const text = selected.map(e => e.textContent).join('\n');
    navigator.clipboard.writeText(text);
  });
});

['clearSelected', 'clearSelectedAlt'].forEach(id => {
  document.getElementById(id).addEventListener('click', () => {
    document.querySelectorAll('.entry.selected').forEach(e => e.classList.remove('selected'));
  });
});

//document.getElementById('clearSelected').addEventListener('click', () => {
//  document.querySelectorAll('.entry.selected').forEach(e => e.classList.remove('selected'));
//});

//document.addEventListener('keydown', function(event) {
//  if (event.shiftKey) {
//    const elements = document.querySelectorAll('.entry');
//    elements.forEach(element => {
//      element.classList.add('forbid-select');
//    });
//  }
//});

//document.addEventListener('keyup', function(event) {
//  if (!event.shiftKey) {
//    const elements = document.querySelectorAll('.entry');
//    elements.forEach(element => {
//      element.classList.remove('forbid-select');
//    });
//  }
//});

document.addEventListener('keydown', function(event) {
  if (event.shiftKey) {
    document.getElementById('log').classList.add('forbid-select');
  }
});

document.addEventListener('keyup', function(event) {
  if (!event.shiftKey) {
    document.getElementById('log').classList.remove('forbid-select');
  }
});

startPolling();
</script>
</body>
</html>";
    }
}
