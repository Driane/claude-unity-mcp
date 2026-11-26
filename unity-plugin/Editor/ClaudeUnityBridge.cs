using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

/// <summary>
/// HTTP bridge that lets the Claude MCP server drive this Editor.
///
/// Claude Code --stdio--> mcp-server (Node) --HTTP localhost:6400--> this bridge
///
/// Requests are accepted on a background thread and queued; every command runs
/// on Unity's main thread from EditorApplication.update, because the Unity API
/// is not thread-safe. Scene changes go through Undo, so Ctrl+Z reverts them.
///
/// Commands live in the ClaudeCommands.*.cs partial files.
/// </summary>
[InitializeOnLoad]
public static partial class ClaudeUnityBridge
{
    private static HttpListener _listener;
    private static Thread _listenerThread;
    private static readonly ConcurrentQueue<KeyValuePair<HttpListenerContext, string>> _queue
        = new ConcurrentQueue<KeyValuePair<HttpListenerContext, string>>();

    private static readonly List<string> _logs = new List<string>();
    private static readonly List<Dictionary<string, object>> _compileMessages
        = new List<Dictionary<string, object>>();

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
        CompilationPipeline.compilationStarted += OnCompilationStarted;
        CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
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
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = reader.ReadToEnd();
                _queue.Enqueue(new KeyValuePair<HttpListenerContext, string>(ctx, body));
            }
            catch { /* listener stopped */ }
        }
    }

    // Runs on the Unity main thread every frame
    static void DrainQueue()
    {
        KeyValuePair<HttpListenerContext, string> item;
        while (_queue.TryDequeue(out item))
        {
            string response;
            try { response = Dispatch(item.Value); }
            catch (Exception e) { response = Err(e.GetType().Name + ": " + e.Message); }
            Reply(item.Key, response);
        }
    }

    static void Shutdown()
    {
        EditorApplication.update -= DrainQueue;
        Application.logMessageReceived -= CaptureLog;
        CompilationPipeline.compilationStarted -= OnCompilationStarted;
        CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
        try { if (_listener != null) { _listener.Stop(); _listener.Close(); } } catch { }
    }

    // ── Command dispatcher ────────────────────────────────────────────────────

    static string Dispatch(string body)
    {
        var request = ClaudeJson.ParseObject(body);
        var action = ClaudeJson.Str(request, "action");
        var p = ClaudeJson.Get(request, "params");

        switch (action)
        {
            // Scene and GameObjects
            case "get_status":             return CmdStatus();
            case "get_hierarchy":          return CmdHierarchy(p);
            case "find_gameobjects":       return CmdFindGameObjects(p);
            case "get_gameobject":         return CmdGetGameObject(p);
            case "create_gameobject":      return CmdCreateGameObject(p);
            case "delete_gameobject":      return CmdDeleteGameObject(p);
            case "duplicate_gameobject":   return CmdDuplicateGameObject(p);
            case "rename_gameobject":      return CmdRenameGameObject(p);
            case "set_active":             return CmdSetActive(p);
            case "set_parent":             return CmdSetParent(p);
            case "set_transform":          return CmdSetTransform(p);
            case "set_tag_layer":          return CmdSetTagLayer(p);

            // Components
            case "add_component":          return CmdAddComponent(p);
            case "remove_component":       return CmdRemoveComponent(p);
            case "set_component_property": return CmdSetComponentProperty(p);

            // Prefabs
            case "create_prefab":          return CmdCreatePrefab(p);
            case "instantiate_prefab":     return CmdInstantiatePrefab(p);

            // Assets
            case "write_script":           return CmdWriteScript(p);
            case "create_material":        return CmdCreateMaterial(p);
            case "assign_material":        return CmdAssignMaterial(p);
            case "create_asset":           return CmdCreateAsset(p);
            case "import_file":            return CmdImportFile(p);
            case "set_texture_settings":   return CmdSetTextureSettings(p);
            case "list_assets":            return CmdListAssets(p);
            case "delete_asset":           return CmdDeleteAsset(p);

            // Scenes
            case "save_scene":             return CmdSaveScene(p);
            case "open_scene":             return CmdOpenScene(p);
            case "new_scene":              return CmdNewScene(p);
            case "set_lighting":           return CmdSetLighting(p);

            // Project and Editor
            case "install_package":        return CmdInstallPackage(p);
            case "list_packages":          return CmdListPackages(p);
            case "compile_status":         return CmdCompileStatus(p);
            case "execute_menu_item":      return CmdExecuteMenuItem(p);
            case "capture_screenshot":     return CmdCaptureScreenshot(p);
            case "build_player":           return CmdBuildPlayer(p);
            case "set_play_mode":          return CmdSetPlayMode(p);
            case "get_logs":               return CmdGetLogs(p);

            default:                       return Err("Unknown action: " + action);
        }
    }

    // ── Status, logs, compilation ─────────────────────────────────────────────

    static string CmdStatus()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        return Ok(D(
            "scene",         scene.name,
            "scenePath",     scene.path,
            "sceneDirty",    scene.isDirty,
            "rootObjects",   scene.rootCount,
            "isPlaying",     EditorApplication.isPlaying,
            "isCompiling",   EditorApplication.isCompiling,
            "isUpdating",    EditorApplication.isUpdating,
            "unityVersion",  Application.unityVersion,
            "projectPath",   Directory.GetParent(Application.dataPath).FullName,
            "bridgeVersion", "2.0.0"
        ));
    }

    static void CaptureLog(string msg, string stack, LogType type)
    {
        lock (_logs)
        {
            _logs.Add("[" + type + "] " + msg);
            if (_logs.Count > MAX_LOGS) _logs.RemoveAt(0);
        }
    }

    static string CmdGetLogs(object p)
    {
        int count = ClaudeJson.Int(p, "count") ?? 20;
        var onlyErrors = ClaudeJson.Bool(p, "errors_only", false);
        lock (_logs)
        {
            var source = new List<string>();
            foreach (var line in _logs)
            {
                if (onlyErrors && !line.StartsWith("[Error") && !line.StartsWith("[Exception")
                    && !line.StartsWith("[Assert")) continue;
                source.Add(line);
            }
            int start = Math.Max(0, source.Count - count);
            return Ok(D("logs", source.GetRange(start, source.Count - start), "total", source.Count));
        }
    }

    static void OnCompilationStarted(object context)
    {
        lock (_compileMessages) _compileMessages.Clear();
    }

    static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
    {
        lock (_compileMessages)
        {
            foreach (var m in messages)
            {
                if (m.type != CompilerMessageType.Error && m.type != CompilerMessageType.Warning) continue;
                _compileMessages.Add(D(
                    "type",     m.type.ToString(),
                    "message",  m.message,
                    "file",     m.file,
                    "line",     m.line,
                    "assembly", Path.GetFileName(assemblyPath)
                ));
            }
        }
    }

    static string CmdCompileStatus(object p)
    {
        if (ClaudeJson.Bool(p, "refresh", false)) AssetDatabase.Refresh();

        lock (_compileMessages)
        {
            var errors = new List<object>();
            var warnings = new List<object>();
            foreach (var m in _compileMessages)
            {
                if ((string)m["type"] == "Error") errors.Add(m);
                else warnings.Add(m);
            }
            return Ok(D(
                "isCompiling", EditorApplication.isCompiling,
                "isUpdating",  EditorApplication.isUpdating,
                "errorCount",  errors.Count,
                "errors",      errors,
                "warnings",    warnings,
                "note",        EditorApplication.isCompiling
                    ? "Still compiling — call again in a moment for the final result."
                    : "Compilation idle. Messages are from the last compile in this session."
            ));
        }
    }

    // ── Shared utilities ──────────────────────────────────────────────────────

    /// <summary>
    /// Find a GameObject by hierarchy path or name, in any loaded scene,
    /// including inactive objects — which GameObject.Find cannot do.
    /// </summary>
    static GameObject ResolveGameObject(string pathOrName)
    {
        if (string.IsNullOrEmpty(pathOrName)) return null;

        var parts = pathOrName.Split('/');
        var sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;

        // 1. Exact path from a scene root, walking children by name.
        for (int s = 0; s < sceneCount; s++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name != parts[0]) continue;
                var current = root.transform;
                bool matched = true;
                for (int i = 1; i < parts.Length && matched; i++)
                {
                    matched = false;
                    foreach (Transform child in current)
                    {
                        if (child.name != parts[i]) continue;
                        current = child;
                        matched = true;
                        break;
                    }
                }
                if (matched) return current.gameObject;
            }
        }

        // 2. Active-object path lookup (handles paths we may have missed).
        var found = GameObject.Find(pathOrName);
        if (found != null) return found;

        // 3. Name search anywhere in the hierarchy, inactive objects included.
        var leaf = parts[parts.Length - 1];
        for (int s = 0; s < sceneCount; s++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                var hit = SearchByName(root.transform, leaf);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    static GameObject SearchByName(Transform t, string name)
    {
        if (t.name == name) return t.gameObject;
        foreach (Transform child in t)
        {
            var hit = SearchByName(child, name);
            if (hit != null) return hit;
        }
        return null;
    }

    static string GetPath(GameObject go)
    {
        var parts = new List<string> { go.name };
        var t = go.transform.parent;
        while (t != null) { parts.Insert(0, t.name); t = t.parent; }
        return string.Join("/", parts);
    }

    private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

    /// <summary>
    /// Resolve a type from a short name ("Rigidbody"), a namespaced name
    /// ("UnityEngine.UI.Image") or a user script name. Optionally require a base type.
    /// </summary>
    static Type FindType(string name, Type mustInherit = null)
    {
        if (string.IsNullOrEmpty(name)) return null;

        var cacheKey = name + "|" + (mustInherit == null ? "" : mustInherit.FullName);
        Type cached;
        if (_typeCache.TryGetValue(cacheKey, out cached)) return cached;

        var candidates = new[]
        {
            name,
            "UnityEngine." + name,
            "UnityEngine.UI." + name,
            "UnityEditor." + name,
            "TMPro." + name
        };

        Type result = null;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var candidate in candidates)
        {
            foreach (var asm in assemblies)
            {
                Type t;
                try { t = asm.GetType(candidate); } catch { continue; }
                if (t == null) continue;
                if (mustInherit != null && !mustInherit.IsAssignableFrom(t)) continue;
                result = t;
                break;
            }
            if (result != null) break;
        }

        // Last resort: match on the short type name across every loaded assembly.
        if (result == null)
        {
            foreach (var asm in assemblies)
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t.Name != name) continue;
                    if (mustInherit != null && !mustInherit.IsAssignableFrom(t)) continue;
                    result = t;
                    break;
                }
                if (result != null) break;
            }
        }

        _typeCache[cacheKey] = result;
        return result;
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

    // ── Response helpers ──────────────────────────────────────────────────────

    static string Ok(Dictionary<string, object> d)
    {
        d["success"] = true;
        return ClaudeJson.Serialize(d);
    }

    static string Err(string msg)
    {
        return "{\"success\":false,\"error\":" + ClaudeJson.Quote(msg) + "}";
    }

    /// <summary>Error with extra context — e.g. the list of valid property names.</summary>
    static string Err(string msg, Dictionary<string, object> extra)
    {
        extra["success"] = false;
        extra["error"] = msg;
        return ClaudeJson.Serialize(extra);
    }

    /// <summary>Build a dictionary from alternating key/value pairs.</summary>
    static Dictionary<string, object> D(params object[] pairs)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < pairs.Length; i += 2)
            d[pairs[i] == null ? "null" : pairs[i].ToString()] = pairs[i + 1];
        return d;
    }
}
