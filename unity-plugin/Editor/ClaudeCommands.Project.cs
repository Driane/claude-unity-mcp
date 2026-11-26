using UnityEngine;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.Build.Reporting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Project-level commands: packages, menu items, screenshots, builds, play mode.
/// </summary>
public static partial class ClaudeUnityBridge
{
    // ── Packages ──────────────────────────────────────────────────────────────

    static string ManifestPath()
    {
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        return Path.Combine(projectRoot, "Packages", "manifest.json");
    }

    static string CmdListPackages(object p)
    {
        var manifestPath = ManifestPath();
        if (!File.Exists(manifestPath)) return Err("No Packages/manifest.json in this project");

        var manifest = ClaudeJson.ParseObject(File.ReadAllText(manifestPath));
        var dependencies = ClaudeJson.Get(manifest, "dependencies") as Dictionary<string, object>;
        if (dependencies == null) return Ok(D("packages", new List<object>()));

        var packages = new List<object>();
        foreach (var kv in dependencies)
            packages.Add(D("name", kv.Key, "version", kv.Value == null ? "" : kv.Value.ToString()));

        return Ok(D("count", packages.Count, "packages", packages));
    }

    static string CmdInstallPackage(object p)
    {
        var name = ClaudeJson.Str(p, "name");
        if (string.IsNullOrEmpty(name)) return Err("name required, e.g. com.unity.probuilder");
        var version = ClaudeJson.Str(p, "version");

        // Without a version, let the Package Manager resolve the newest compatible
        // release. That runs in the background, so report it as queued.
        if (string.IsNullOrEmpty(version))
        {
            try
            {
                Client.Add(name);
                return Ok(D("name", name, "status", "queued",
                            "note", "The Package Manager is resolving the latest compatible version. " +
                                    "Call list_packages in a few seconds to confirm."));
            }
            catch (Exception e) { return Err("Package Manager refused the request: " + e.Message); }
        }

        // With a version, edit the manifest in place. Surgical text edits keep the
        // file's formatting intact, unlike reserialising it.
        var manifestPath = ManifestPath();
        if (!File.Exists(manifestPath)) return Err("No Packages/manifest.json in this project");

        var text = File.ReadAllText(manifestPath);
        var manifest = ClaudeJson.ParseObject(text);
        var dependencies = ClaudeJson.Get(manifest, "dependencies") as Dictionary<string, object>;
        if (dependencies == null) return Err("manifest.json has no dependencies block");

        string updated;
        bool alreadyPresent = dependencies.ContainsKey(name);

        if (alreadyPresent)
        {
            var needle = ClaudeJson.Quote(name) + ":";
            int keyIndex = text.IndexOf(needle, StringComparison.Ordinal);
            if (keyIndex < 0) return Err("Could not locate " + name + " in manifest.json");
            int valueStart = text.IndexOf('"', keyIndex + needle.Length);
            int valueEnd = valueStart < 0 ? -1 : text.IndexOf('"', valueStart + 1);
            if (valueStart < 0 || valueEnd < 0) return Err("Malformed version entry for " + name);
            updated = text.Substring(0, valueStart + 1) + version + text.Substring(valueEnd);
        }
        else
        {
            int depIndex = text.IndexOf("\"dependencies\"", StringComparison.Ordinal);
            if (depIndex < 0) return Err("manifest.json has no dependencies block");
            int brace = text.IndexOf('{', depIndex);
            if (brace < 0) return Err("Malformed dependencies block");
            var entry = "\n    " + ClaudeJson.Quote(name) + ": " + ClaudeJson.Quote(version);
            // A comma is only needed when the block already holds an entry.
            int firstNonSpace = brace + 1;
            while (firstNonSpace < text.Length && char.IsWhiteSpace(text[firstNonSpace])) firstNonSpace++;
            if (firstNonSpace < text.Length && text[firstNonSpace] != '}') entry += ",";
            updated = text.Substring(0, brace + 1) + entry + text.Substring(brace + 1);
        }

        File.WriteAllText(manifestPath, updated, new UTF8Encoding(false));
        try { Client.Resolve(); }
        catch (Exception e)
        {
            return Ok(D("name", name, "version", version, "manifestUpdated", true,
                        "warning", "manifest.json was updated but Client.Resolve failed: " + e.Message));
        }

        return Ok(D("name", name, "version", version, "replaced", alreadyPresent,
                    "note", "Unity is importing the package; it may recompile. Call compile_status next."));
    }

    // ── Editor menu ───────────────────────────────────────────────────────────

    static string CmdExecuteMenuItem(object p)
    {
        var path = ClaudeJson.Str(p, "path");
        if (string.IsNullOrEmpty(path)) return Err("path required, e.g. \"Window/General/Console\"");

        bool executed = EditorApplication.ExecuteMenuItem(path);
        if (!executed) return Err("Unity did not execute the menu item: " + path +
            " — check the exact label, or the item may be disabled right now.");

        return Ok(D("executed", path,
                    "note", "Menu items that open a modal dialog will block the Editor until it is closed."));
    }

    // ── Screenshot ────────────────────────────────────────────────────────────

    static string CmdCaptureScreenshot(object p)
    {
        int width = ClaudeJson.Int(p, "width") ?? 1280;
        int height = ClaudeJson.Int(p, "height") ?? 720;
        if (width < 16 || height < 16 || width > 4096 || height > 4096)
            return Err("width and height must be between 16 and 4096");

        // Choose the camera: an explicit one, the scene view, or the main camera.
        Camera camera = null;
        var cameraPath = ClaudeJson.Str(p, "camera");
        if (!string.IsNullOrEmpty(cameraPath))
        {
            var go = ResolveGameObject(cameraPath);
            if (go == null) return Err("Not found: " + cameraPath);
            camera = go.GetComponent<Camera>();
            if (camera == null) return Err("No Camera component on " + cameraPath);
        }
        else if (ClaudeJson.Bool(p, "scene_view", false))
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null) return Err("No Scene view is open");
            camera = sceneView.camera;
        }
        else
        {
            camera = Camera.main;
            if (camera == null)
            {
                // Resources.FindObjectsOfTypeAll finds inactive objects too and is
                // available on every Unity version the bridge targets.
                foreach (var candidate in Resources.FindObjectsOfTypeAll<Camera>())
                {
                    if (candidate == null || !candidate.gameObject.scene.IsValid()) continue;
                    camera = candidate;
                    break;
                }
            }
            if (camera == null)
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                    return Err("No camera in the scene and no Scene view open — add a Camera, or pass scene_view:true");
                camera = sceneView.camera;
            }
        }

        // Default to a folder beside Assets, so the image is not imported as an asset.
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var path = ClaudeJson.Str(p, "path");
        if (string.IsNullOrEmpty(path))
        {
            var dir = Path.Combine(projectRoot, "ClaudeCaptures");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
        }
        else if (!Path.IsPathRooted(path)) path = Path.Combine(projectRoot, path);

        RenderTexture renderTexture = null;
        Texture2D image = null;
        var previousTarget = camera.targetTexture;
        var previousActive = RenderTexture.active;

        try
        {
            renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32,
                                              RenderTextureReadWrite.sRGB);
            camera.targetTexture = renderTexture;
            camera.Render();

            RenderTexture.active = renderTexture;
            image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, image.EncodeToPNG());
        }
        catch (Exception e)
        {
            return Err("Capture failed: " + e.Message);
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            if (image != null) UnityEngine.Object.DestroyImmediate(image);
            if (renderTexture != null)
            {
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        return Ok(D("path", path, "width", width, "height", height,
                    "camera", camera.name, "isPlaying", EditorApplication.isPlaying));
    }

    // ── Play mode ─────────────────────────────────────────────────────────────

    static string CmdSetPlayMode(object p)
    {
        if (!ClaudeJson.Has(p, "play")) return Err("play required (true or false)");
        bool play = ClaudeJson.Bool(p, "play", false);

        if (play && EditorApplication.isCompiling)
            return Err("Unity is still compiling — call compile_status until it is idle.");
        if (play && EditorApplication.isPlaying) return Ok(D("isPlaying", true, "note", "Already playing."));

        EditorApplication.isPlaying = play;
        return Ok(D("requested", play, "isPlaying", EditorApplication.isPlaying,
                    "note", "The switch takes a moment; call get_status or get_logs to see the result."));
    }

    static string CmdStepFrames(object p)
    {
        if (!EditorApplication.isPlaying)
            return Err("Not in Play Mode — call set_play_mode first");

        int frames = ClaudeJson.Int(p, "frames") ?? 60;
        if (frames < 1 || frames > 20000) return Err("frames must be between 1 and 20000");

        var depart = Time.frameCount;
        for (int i = 0; i < frames; i++) EditorApplication.Step();

        return Ok(D(
            "framesRequested", frames,
            "frameCount",      Time.frameCount,
            "framesAdvanced",  Time.frameCount - depart,
            "time",            Time.time,
            "note", "Play Mode only advances on its own while the Editor window has " +
                    "OS focus. Stepping makes a test deterministic and independent of that."
        ));
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    static string CmdBuildPlayer(object p)
    {
        if (EditorApplication.isPlaying) return Err("Exit Play Mode before building");
        if (EditorApplication.isCompiling) return Err("Unity is still compiling — wait for compile_status to be idle");

        var targetName = ClaudeJson.Str(p, "target") ?? "StandaloneWindows64";
        BuildTarget target;
        if (!Enum.TryParse<BuildTarget>(targetName, true, out target))
            return Err("Unknown build target: " + targetName +
                       " (StandaloneWindows64, StandaloneOSX, StandaloneLinux64, Android, iOS, WebGL)");

        var outputPath = ClaudeJson.Str(p, "path");
        if (string.IsNullOrEmpty(outputPath))
            return Err("path required, e.g. Builds/Windows/Game.exe");

        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        if (!Path.IsPathRooted(outputPath)) outputPath = Path.Combine(projectRoot, outputPath);

        // Scenes: an explicit list, or everything enabled in Build Settings.
        var scenes = new List<string>();
        var requested = ClaudeJson.List(p, "scenes");
        if (requested != null)
        {
            foreach (var entry in requested)
            {
                var scenePath = entry as string;
                if (string.IsNullOrEmpty(scenePath)) continue;
                if (AssetDatabase.LoadMainAssetAtPath(scenePath) == null)
                    return Err("Scene not found: " + scenePath);
                scenes.Add(scenePath);
            }
        }
        else
        {
            foreach (var scene in EditorBuildSettings.scenes)
                if (scene.enabled) scenes.Add(scene.path);
        }

        if (scenes.Count == 0)
            return Err("No scenes to build — pass scenes:[...] or enable scenes in File > Build Settings");

        var options = BuildOptions.None;
        if (ClaudeJson.Bool(p, "development", false))
            options |= BuildOptions.Development | BuildOptions.AllowDebugging;

        var buildOptions = new BuildPlayerOptions
        {
            scenes = scenes.ToArray(),
            locationPathName = outputPath,
            target = target,
            options = options
        };

        BuildReport report;
        try { report = BuildPipeline.BuildPlayer(buildOptions); }
        catch (Exception e) { return Err("Build threw: " + e.Message); }

        if (report == null) return Err("Unity returned no build report");

        var summary = report.summary;
        var result = D(
            "result",      summary.result.ToString(),
            "target",      target.ToString(),
            "outputPath",  summary.outputPath,
            "sizeBytes",   (long)summary.totalSize,
            "durationSec", Math.Round(summary.totalTime.TotalSeconds, 1),
            "errors",      summary.totalErrors,
            "warnings",    summary.totalWarnings,
            "scenes",      scenes
        );

        if (summary.result != BuildResult.Succeeded)
            return Err("Build " + summary.result + " with " + summary.totalErrors +
                       " error(s) — call get_logs for details", result);

        return Ok(result);
    }
}
