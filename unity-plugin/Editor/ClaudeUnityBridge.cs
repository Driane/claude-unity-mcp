using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.Net;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

[InitializeOnLoad]
public static class ClaudeUnityBridge
{
    private static HttpListener _listener;
    private static Thread _listenerThread;
    private static readonly ConcurrentQueue<(HttpListenerContext ctx, string body)> _queue
        = new ConcurrentQueue<(HttpListenerContext, string)>();
    private static readonly List<string> _logs = new List<string>();
    private const int MAX_LOGS = 200;
    private const string URL = "http://localhost:6400/";

    // Only the MCP server sends this header. A web page cannot add it to a
    // cross-origin request without a CORS preflight, which the bridge refuses,
    // so websites open in a browser cannot drive the Editor.
    private const string CLIENT_HEADER = "X-Claude-Unity-Bridge";

    static ClaudeUnityBridge()
    {
        Application.logMessageReceived += CaptureLog;
        EditorApplication.update += DrainQueue;
        EditorApplication.quitting += Shutdown;
        Start();
    }

    // ── Server lifecycle ─────────────────────────────────────────────────────

    static void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(URL);
            _listener.Start();
            _listenerThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ClaudeUnityBridge" };
            _listenerThread.Start();
            Debug.Log("[ClaudeUnity] Bridge listening on " + URL);
        }
        catch (Exception e)
        {
            Debug.LogError("[ClaudeUnity] Could not start HTTP server: " + e.Message);
        }
    }

    static void AcceptLoop()
    {
        while (_listener != null && _listener.IsListening)
        {
            try
            {
                var ctx = _listener.GetContext();
                // Accept only POST requests from the MCP server; preflights and
                // requests without the client header are refused.
                if (ctx.Request.HttpMethod != "POST" || ctx.Request.Headers[CLIENT_HEADER] != "1")
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    continue;
                }
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = reader.ReadToEnd();
                _queue.Enqueue((ctx, body));
            }
            catch { /* listener stopped */ }
        }
    }

    // Runs on the Unity main thread every frame
    static void DrainQueue()
    {
        while (_queue.TryDequeue(out var item))
        {
            string response;
            try   { response = Dispatch(item.body); }
            catch (Exception e) { response = Err(e.Message); }
            Reply(item.ctx, response);
        }
    }

    static void Shutdown()
    {
        EditorApplication.update -= DrainQueue;
        Application.logMessageReceived -= CaptureLog;
        try { _listener?.Stop(); _listener?.Close(); } catch { }
    }

    // ── Command dispatcher ────────────────────────────────────────────────────

    static string Dispatch(string body)
    {
        var action = ParseString(body, "action");
        var p = ParseObject(body, "params");

        return action switch
        {
            "get_status"        => CmdStatus(),
            "get_hierarchy"     => CmdHierarchy(),
            "create_gameobject" => CmdCreateGO(p),
            "delete_gameobject" => CmdDeleteGO(p),
            "write_script"      => CmdWriteScript(p),
            "add_component"     => CmdAddComponent(p),
            "set_transform"     => CmdSetTransform(p),
            "set_play_mode"     => CmdSetPlayMode(p),
            "save_scene"        => CmdSaveScene(),
            "get_logs"          => CmdGetLogs(p),
            _                   => Err($"Unknown action: {action}")
        };
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    static string CmdStatus()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        return Ok(D(
            "scene",        scene.name,
            "scenePath",    scene.path,
            "rootObjects",  scene.rootCount,
            "isPlaying",    EditorApplication.isPlaying,
            "unityVersion", Application.unityVersion
        ));
    }

    static string CmdHierarchy()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        var items = new List<object>();
        foreach (var go in roots) items.Add(BuildNode(go, ""));
        return Ok(D("hierarchy", items));
    }

    static Dictionary<string, object> BuildNode(GameObject go, string parentPath)
    {
        var path = parentPath.Length == 0 ? go.name : parentPath + "/" + go.name;
        var comps = new List<string>();
        foreach (var c in go.GetComponents<Component>())
            if (c != null) comps.Add(c.GetType().Name);
        var children = new List<object>();
        foreach (Transform child in go.transform)
            children.Add(BuildNode(child.gameObject, path));
        return D("name", go.name, "path", path, "active", go.activeSelf, "components", comps, "children", children);
    }

    static string CmdCreateGO(string p)
    {
        var name      = ParseString(p, "name");      if (name == null) return Err("name required");
        var primitive = ParseString(p, "primitive");
        var parentStr = ParseString(p, "parent");
        var pos       = ParseFloats(p, "position");

        GameObject go;
        if (!string.IsNullOrEmpty(primitive) && Enum.TryParse<PrimitiveType>(primitive, true, out var pt))
            go = GameObject.CreatePrimitive(pt);
        else
            go = new GameObject();
        go.name = name;

        if (!string.IsNullOrEmpty(parentStr))
        {
            var parent = GameObject.Find(parentStr);
            if (parent != null) go.transform.SetParent(parent.transform, false);
        }
        if (pos != null && pos.Length == 3)
            go.transform.position = new Vector3(pos[0], pos[1], pos[2]);

        Undo.RegisterCreatedObjectUndo(go, "Claude: Create " + name);
        return Ok(D("name", go.name, "path", GetPath(go)));
    }

    static string CmdDeleteGO(string p)
    {
        var path = ParseString(p, "path"); if (path == null) return Err("path required");
        var go = GameObject.Find(path);
        if (go == null) return Err("Not found: " + path);
        Undo.DestroyObjectImmediate(go);
        return Ok(D("deleted", path));
    }

    static string CmdWriteScript(string p)
    {
        var filename  = ParseString(p, "filename"); if (filename == null) return Err("filename required");
        var content   = ParseString(p, "content");  if (content  == null) return Err("content required");
        var subfolder = ParseString(p, "subfolder") ?? "";

        var dir = Path.Combine(Application.dataPath, "Scripts");
        if (!string.IsNullOrEmpty(subfolder)) dir = Path.Combine(dir, subfolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, filename), content, Encoding.UTF8);
        AssetDatabase.Refresh();

        var assetPath = "Assets/Scripts/" + (string.IsNullOrEmpty(subfolder) ? "" : subfolder + "/") + filename;
        return Ok(D("path", assetPath));
    }

    static string CmdAddComponent(string p)
    {
        var goPath = ParseString(p, "gameobject"); if (goPath == null) return Err("gameobject required");
        var cName  = ParseString(p, "component");  if (cName  == null) return Err("component required");
        var go = GameObject.Find(goPath); if (go == null) return Err("Not found: " + goPath);
        var type = FindType(cName); if (type == null) return Err("Type not found: " + cName);
        Undo.AddComponent(go, type);
        return Ok(D("added", type.Name, "to", GetPath(go)));
    }

    static string CmdSetTransform(string p)
    {
        var goPath = ParseString(p, "gameobject"); if (goPath == null) return Err("gameobject required");
        var go = GameObject.Find(goPath); if (go == null) return Err("Not found: " + goPath);
        Undo.RecordObject(go.transform, "Claude: Set Transform");
        var pos = ParseFloats(p, "position");
        var rot = ParseFloats(p, "rotation");
        var scl = ParseFloats(p, "scale");
        if (pos != null && pos.Length == 3) go.transform.position    = new Vector3(pos[0], pos[1], pos[2]);
        if (rot != null && rot.Length == 3) go.transform.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);
        if (scl != null && scl.Length == 3) go.transform.localScale  = new Vector3(scl[0], scl[1], scl[2]);
        return Ok(D("gameobject", goPath));
    }

    static string CmdSetPlayMode(string p)
    {
        var val = ParseString(p, "play");
        EditorApplication.isPlaying = val == "true";
        return Ok(D("isPlaying", EditorApplication.isPlaying));
    }

    static string CmdSaveScene()
    {
        EditorSceneManager.SaveOpenScenes();
        return Ok(D("saved", true));
    }

    static string CmdGetLogs(string p)
    {
        int count = 20;
        var cStr = ParseString(p, "count");
        if (cStr != null) int.TryParse(cStr, out count);
        lock (_logs)
        {
            var start = Math.Max(0, _logs.Count - count);
            return Ok(D("logs", _logs.GetRange(start, _logs.Count - start)));
        }
    }

    static void CaptureLog(string msg, string stack, LogType type)
    {
        lock (_logs)
        {
            _logs.Add($"[{type}] {msg}");
            if (_logs.Count > MAX_LOGS) _logs.RemoveAt(0);
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    static string GetPath(GameObject go)
    {
        var parts = new List<string> { go.name };
        var t = go.transform.parent;
        while (t != null) { parts.Insert(0, t.name); t = t.parent; }
        return string.Join("/", parts);
    }

    static Type FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(name) ?? asm.GetType("UnityEngine." + name);
            if (t != null && typeof(Component).IsAssignableFrom(t)) return t;
        }
        return null;
    }

    static void Reply(HttpListenerContext ctx, string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentEncoding = Encoding.UTF8;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch { }
    }

    // ── Minimal JSON serializer (zero dependencies) ───────────────────────────

    static string Ok(Dictionary<string, object> d)  { d["success"] = true; return Serialize(d); }
    static string Err(string msg)                    => $"{{\"success\":false,\"error\":{Str(msg)}}}";

    // Convenience: build a dict from alternating key/value pairs
    static Dictionary<string, object> D(params object[] pairs)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < pairs.Length; i += 2)
            d[pairs[i]?.ToString()] = pairs[i + 1];
        return d;
    }

    static string Serialize(object value)
    {
        if (value == null)                             return "null";
        if (value is bool b)                           return b ? "true" : "false";
        if (value is int  i)                           return i.ToString();
        if (value is long l)                           return l.ToString();
        if (value is float f)                          return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is double dbl)                       return dbl.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is string s)                         return Str(s);
        if (value is List<string> ls)                  return "[" + string.Join(",", ls.ConvertAll(Str)) + "]";
        if (value is List<object> lo)                  return "[" + string.Join(",", lo.ConvertAll(Serialize)) + "]";
        if (value is Dictionary<string, object> dict)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(',');
                sb.Append(Str(kv.Key)).Append(':').Append(Serialize(kv.Value));
                first = false;
            }
            return sb.Append('}').ToString();
        }
        return Str(value.ToString());
    }

    static string Str(string s)
    {
        if (s == null) return "null";
        return "\"" + s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t")
            + "\"";
    }

    // ── Minimal JSON parser (handles strings, numbers, bools, arrays, objects) ─

    // Returns the raw string value of a JSON key, or the serialized primitive
    static string ParseString(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return null;
        int idx = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (idx < 0) return null;
        idx = json.IndexOf(':', idx) + 1;
        while (idx < json.Length && json[idx] == ' ') idx++;
        if (idx >= json.Length) return null;

        if (json[idx] == '"')
        {
            // String value — respect escape sequences
            var sb = new StringBuilder();
            idx++; // skip opening quote
            while (idx < json.Length && json[idx] != '"')
            {
                if (json[idx] == '\\' && idx + 1 < json.Length)
                {
                    idx++;
                    sb.Append(json[idx] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => json[idx] });
                }
                else sb.Append(json[idx]);
                idx++;
            }
            return sb.ToString();
        }

        // Non-string primitive (number, bool, null)
        int end = idx;
        while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']')
            end++;
        return json.Substring(idx, end - idx).Trim();
    }

    // Returns the raw JSON substring of an object value for a given key
    static string ParseObject(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return "{}";
        int idx = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (idx < 0) return "{}";
        idx = json.IndexOf('{', idx);
        if (idx < 0) return "{}";
        int depth = 0, start = idx;
        bool inStr = false;
        while (idx < json.Length)
        {
            char c = json[idx];
            if (inStr) { if (c == '\\') idx++; else if (c == '"') inStr = false; }
            else
            {
                if      (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { if (--depth == 0) return json.Substring(start, idx - start + 1); }
            }
            idx++;
        }
        return "{}";
    }

    static float[] ParseFloats(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return null;
        int idx = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (idx < 0) return null;
        idx = json.IndexOf('[', idx); if (idx < 0) return null;
        int end = json.IndexOf(']', idx); if (end < 0) return null;
        var parts = json.Substring(idx + 1, end - idx - 1).Split(',');
        var result = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result[i])) return null;
        return result;
    }
}
