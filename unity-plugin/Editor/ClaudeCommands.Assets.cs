using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Script, asset, material, texture and scene commands.
/// </summary>
public static partial class ClaudeUnityBridge
{
    // ── Scripts ───────────────────────────────────────────────────────────────

    static string CmdWriteScript(object p)
    {
        var content = ClaudeJson.Str(p, "content");
        if (content == null) return Err("content required");

        // Either an explicit asset path, or filename + optional subfolder.
        var assetPath = ClaudeJson.Str(p, "path");
        if (string.IsNullOrEmpty(assetPath))
        {
            var filename = ClaudeJson.Str(p, "filename");
            if (string.IsNullOrEmpty(filename)) return Err("path or filename required");
            if (!filename.EndsWith(".cs")) filename += ".cs";
            var subfolder = ClaudeJson.Str(p, "subfolder");
            assetPath = "Assets/Scripts/" +
                        (string.IsNullOrEmpty(subfolder) ? "" : subfolder.Trim('/') + "/") + filename;
        }

        assetPath = assetPath.Replace('\\', '/');
        if (!assetPath.StartsWith("Assets/")) return Err("path must start with Assets/");
        if (!assetPath.EndsWith(".cs")) return Err("path must end with .cs");

        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var fullPath = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

        bool existed = File.Exists(fullPath);
        File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

        return Ok(D("path", assetPath, "overwrote", existed, "bytes", content.Length,
                    "note", "Unity is recompiling — call compile_status before using the new type."));
    }

    // ── Materials ─────────────────────────────────────────────────────────────

    static string CmdCreateMaterial(object p)
    {
        var assetPath = ClaudeJson.Str(p, "path");
        if (assetPath == null) return Err("path required, e.g. Assets/Materials/Stone.mat");
        if (!assetPath.StartsWith("Assets/")) return Err("path must start with Assets/");
        if (!assetPath.EndsWith(".mat")) assetPath += ".mat";

        // Pick a shader that exists in this project's render pipeline.
        var requested = ClaudeJson.Str(p, "shader");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(requested)) candidates.Add(requested);
        if (GraphicsSettings.currentRenderPipeline != null)
        {
            candidates.Add("Universal Render Pipeline/Lit");
            candidates.Add("HDRP/Lit");
        }
        candidates.Add("Standard");
        candidates.Add("Sprites/Default");

        Shader shader = null;
        foreach (var name in candidates)
        {
            shader = Shader.Find(name);
            if (shader != null) break;
        }
        if (shader == null) return Err("No usable shader found", D("tried", candidates));

        var material = new Material(shader);

        var color = ClaudeJson.Floats(p, "color");
        var colorText = ClaudeJson.Str(p, "color");
        Color parsed;
        if (color != null && color.Length >= 3)
            SetMaterialColor(material, new Color(color[0], color[1], color[2], color.Length > 3 ? color[3] : 1f));
        else if (!string.IsNullOrEmpty(colorText) && ColorUtility.TryParseHtmlString(colorText, out parsed))
            SetMaterialColor(material, parsed);

        var metallic = ClaudeJson.Float(p, "metallic");
        if (metallic.HasValue && material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic.Value);

        var smoothness = ClaudeJson.Float(p, "smoothness");
        if (smoothness.HasValue)
        {
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness.Value);
            else if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness.Value);
        }

        var texturePath = ClaudeJson.Str(p, "texture");
        if (!string.IsNullOrEmpty(texturePath))
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (texture == null) return Err("Texture not found: " + texturePath);
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            else if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        }

        EnsureAssetFolder(assetPath);
        var existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        if (existing != null)
        {
            existing.shader = material.shader;
            existing.CopyPropertiesFromMaterial(material);
            EditorUtility.SetDirty(existing);
            UnityEngine.Object.DestroyImmediate(material);
        }
        else AssetDatabase.CreateAsset(material, assetPath);

        AssetDatabase.SaveAssets();
        return Ok(D("path", assetPath, "shader", shader.name, "replaced", existing != null));
    }

    static void SetMaterialColor(Material material, Color color)
    {
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
    }

    static string CmdAssignMaterial(object p)
    {
        var goPath = ClaudeJson.Str(p, "gameobject");
        var matPath = ClaudeJson.Str(p, "material");
        if (goPath == null) return Err("gameobject required");
        if (matPath == null) return Err("material required");

        var material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (material == null) return Err("Material not found: " + matPath);

        var go = ResolveGameObject(goPath);
        if (go == null) return Err("Not found: " + goPath);

        bool recursive = ClaudeJson.Bool(p, "recursive", false);
        int? slot = ClaudeJson.Int(p, "slot");

        var renderers = recursive
            ? new List<Renderer>(go.GetComponentsInChildren<Renderer>(true))
            : new List<Renderer>(go.GetComponents<Renderer>());
        if (renderers.Count == 0) return Err("No Renderer on " + GetPath(go) +
            (recursive ? " or its children" : " — pass recursive:true to search children"));

        var touched = new List<string>();
        foreach (var renderer in renderers)
        {
            Undo.RecordObject(renderer, "Claude: Assign Material");
            var materials = renderer.sharedMaterials;
            if (slot.HasValue)
            {
                if (slot.Value < 0 || slot.Value >= materials.Length)
                    return Err("slot " + slot.Value + " out of range on " + renderer.name +
                               " (has " + materials.Length + ")");
                materials[slot.Value] = material;
            }
            else for (int i = 0; i < materials.Length; i++) materials[i] = material;

            renderer.sharedMaterials = materials;
            touched.Add(GetPath(renderer.gameObject));
        }

        return Ok(D("material", matPath, "renderers", touched.Count, "objects", touched));
    }

    // ── Assets ────────────────────────────────────────────────────────────────

    static string CmdCreateAsset(object p)
    {
        var typeName = ClaudeJson.Str(p, "type");
        var assetPath = ClaudeJson.Str(p, "path");
        if (typeName == null) return Err("type required (a ScriptableObject class name)");
        if (assetPath == null) return Err("path required, e.g. Assets/Data/Sword.asset");
        if (!assetPath.StartsWith("Assets/")) return Err("path must start with Assets/");
        if (!assetPath.EndsWith(".asset")) assetPath += ".asset";

        var type = FindType(typeName, typeof(ScriptableObject));
        if (type == null) return Err("ScriptableObject type not found: " + typeName +
            " — if you just wrote the script, call compile_status first.");

        var instance = ScriptableObject.CreateInstance(type);
        if (instance == null) return Err("Could not instantiate " + typeName);

        var applied = new List<string>();
        var fields = ClaudeJson.Get(p, "fields") as Dictionary<string, object>;
        if (fields != null)
        {
            foreach (var kv in fields)
            {
                string error;
                if (!SetProperty(instance, kv.Key, kv.Value, out error))
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                    return Err("Field '" + kv.Key + "': " + error,
                               D("available", ListPropertyNames(instance)));
                }
                applied.Add(kv.Key);
            }
        }

        EnsureAssetFolder(assetPath);
        if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
            assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);

        AssetDatabase.CreateAsset(instance, assetPath);
        AssetDatabase.SaveAssets();
        return Ok(D("path", assetPath, "type", type.Name, "fieldsApplied", applied));
    }

    static string CmdImportFile(object p)
    {
        var source = ClaudeJson.Str(p, "source");
        var destination = ClaudeJson.Str(p, "destination");
        if (source == null) return Err("source required (an absolute path on disk)");
        if (destination == null) return Err("destination required, e.g. Assets/Art/hero.png");

        destination = destination.Replace('\\', '/');
        if (!destination.StartsWith("Assets/")) return Err("destination must start with Assets/");
        if (!File.Exists(source)) return Err("Source file not found: " + source);

        // A destination that is a folder keeps the source filename.
        if (destination.EndsWith("/")) destination += Path.GetFileName(source);
        else if (AssetDatabase.IsValidFolder(destination)) destination += "/" + Path.GetFileName(source);

        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var fullPath = Path.Combine(projectRoot, destination.Replace('/', Path.DirectorySeparatorChar));

        bool overwrite = ClaudeJson.Bool(p, "overwrite", false);
        if (File.Exists(fullPath) && !overwrite)
            return Err("Destination already exists: " + destination + " (pass overwrite:true to replace)");

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        File.Copy(source, fullPath, overwrite);
        AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceUpdate);

        var info = new FileInfo(fullPath);
        var importedType = AssetDatabase.GetMainAssetTypeAtPath(destination);
        var typeName = importedType == null ? "unknown" : importedType.Name;

        var result = D("path", destination, "bytes", info.Length, "assetType", typeName);

        // DefaultAsset means Unity copied the file but has no importer for it,
        // so nothing in the project can actually use its contents.
        if (typeName == "DefaultAsset")
            result["warning"] = "Unity has no importer for this format: the file is in the project " +
                                "but unusable. Models should be .fbx, .obj or .dae; .glb/.gltf need " +
                                "the com.unity.cloud.gltfast package.";

        return Ok(result);
    }

    static string CmdSetTextureSettings(object p)
    {
        var assetPath = ClaudeJson.Str(p, "path");
        if (assetPath == null) return Err("path required");

        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) return Err("Not a texture, or not imported yet: " + assetPath);

        var changed = new List<string>();

        var textureType = ClaudeJson.Str(p, "texture_type");
        if (!string.IsNullOrEmpty(textureType))
        {
            TextureImporterType parsedType;
            if (!Enum.TryParse<TextureImporterType>(textureType, true, out parsedType))
                return Err("Unknown texture_type: " + textureType + " (Default, Sprite, NormalMap, GUI, Cursor, Cookie, Lightmap, SingleChannel)");
            importer.textureType = parsedType;
            changed.Add("texture_type");
        }

        var spriteMode = ClaudeJson.Str(p, "sprite_mode");
        if (!string.IsNullOrEmpty(spriteMode))
        {
            SpriteImportMode parsedMode;
            if (!Enum.TryParse<SpriteImportMode>(spriteMode, true, out parsedMode))
                return Err("Unknown sprite_mode: " + spriteMode + " (Single, Multiple, Polygon)");
            importer.spriteImportMode = parsedMode;
            changed.Add("sprite_mode");
        }

        var ppu = ClaudeJson.Float(p, "pixels_per_unit");
        if (ppu.HasValue) { importer.spritePixelsPerUnit = ppu.Value; changed.Add("pixels_per_unit"); }

        var filterMode = ClaudeJson.Str(p, "filter_mode");
        if (!string.IsNullOrEmpty(filterMode))
        {
            FilterMode parsedFilter;
            if (!Enum.TryParse<FilterMode>(filterMode, true, out parsedFilter))
                return Err("Unknown filter_mode: " + filterMode + " (Point, Bilinear, Trilinear)");
            importer.filterMode = parsedFilter;
            changed.Add("filter_mode");
        }

        var compression = ClaudeJson.Str(p, "compression");
        if (!string.IsNullOrEmpty(compression))
        {
            TextureImporterCompression parsedCompression;
            if (!Enum.TryParse<TextureImporterCompression>(compression, true, out parsedCompression))
                return Err("Unknown compression: " + compression + " (Uncompressed, Compressed, CompressedHQ, CompressedLQ)");
            importer.textureCompression = parsedCompression;
            changed.Add("compression");
        }

        var wrapMode = ClaudeJson.Str(p, "wrap_mode");
        if (!string.IsNullOrEmpty(wrapMode))
        {
            TextureWrapMode parsedWrap;
            if (!Enum.TryParse<TextureWrapMode>(wrapMode, true, out parsedWrap))
                return Err("Unknown wrap_mode: " + wrapMode + " (Repeat, Clamp, Mirror, MirrorOnce)");
            importer.wrapMode = parsedWrap;
            changed.Add("wrap_mode");
        }

        var maxSize = ClaudeJson.Int(p, "max_size");
        if (maxSize.HasValue) { importer.maxTextureSize = maxSize.Value; changed.Add("max_size"); }

        if (ClaudeJson.Has(p, "alpha_is_transparency"))
        { importer.alphaIsTransparency = ClaudeJson.Bool(p, "alpha_is_transparency", true); changed.Add("alpha_is_transparency"); }

        if (ClaudeJson.Has(p, "mip_maps"))
        { importer.mipmapEnabled = ClaudeJson.Bool(p, "mip_maps", true); changed.Add("mip_maps"); }

        if (changed.Count == 0) return Err("No settings given");

        importer.SaveAndReimport();
        return Ok(D("path", assetPath, "changed", changed,
                    "note", "For crisp pixel art use filter_mode:Point, compression:Uncompressed, mip_maps:false."));
    }

    static string CmdListAssets(object p)
    {
        var filter = ClaudeJson.Str(p, "filter") ?? "";
        var folder = ClaudeJson.Str(p, "folder");
        int limit = ClaudeJson.Int(p, "limit") ?? 100;

        string[] guids;
        if (!string.IsNullOrEmpty(folder))
        {
            if (!AssetDatabase.IsValidFolder(folder)) return Err("Not a folder: " + folder);
            guids = AssetDatabase.FindAssets(filter, new[] { folder });
        }
        else guids = AssetDatabase.FindAssets(filter);

        var assets = new List<object>();
        for (int i = 0; i < guids.Length && assets.Count < limit; i++)
        {
            var path = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (string.IsNullOrEmpty(path)) continue;
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            assets.Add(D("path", path, "type", type == null ? "unknown" : type.Name));
        }

        return Ok(D("total", guids.Length, "returned", assets.Count, "assets", assets,
                    "note", "filter uses AssetDatabase syntax, e.g. \"t:Prefab\", \"t:Material stone\"."));
    }

    static string CmdDeleteAsset(object p)
    {
        var paths = new List<string>();
        var single = ClaudeJson.Str(p, "path");
        if (!string.IsNullOrEmpty(single)) paths.Add(single);
        var many = ClaudeJson.List(p, "paths");
        if (many != null)
            foreach (var entry in many)
            {
                var s = entry as string;
                if (!string.IsNullOrEmpty(s)) paths.Add(s);
            }
        if (paths.Count == 0) return Err("path or paths required");

        var deleted = new List<string>();
        var failed = new List<string>();
        foreach (var path in paths)
        {
            if (!path.StartsWith("Assets/")) { failed.Add(path + " (outside Assets/)"); continue; }
            if (AssetDatabase.LoadMainAssetAtPath(path) == null && !AssetDatabase.IsValidFolder(path))
            { failed.Add(path + " (not found)"); continue; }
            if (AssetDatabase.DeleteAsset(path)) deleted.Add(path);
            else failed.Add(path + " (Unity refused)");
        }

        if (deleted.Count == 0) return Err("Nothing deleted", D("failed", failed));
        return Ok(D("deleted", deleted, "failed", failed));
    }

    // ── Scenes ────────────────────────────────────────────────────────────────

    static string CmdSaveScene(object p)
    {
        var path = ClaudeJson.Str(p, "path");
        if (!string.IsNullOrEmpty(path))
        {
            if (!path.StartsWith("Assets/")) return Err("path must start with Assets/");
            if (!path.EndsWith(".unity")) path += ".unity";
            EnsureAssetFolder(path);
            var active = SceneManager.GetActiveScene();
            if (!EditorSceneManager.SaveScene(active, path)) return Err("Unity could not save to " + path);
            return Ok(D("saved", path));
        }

        var current = SceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(current.path))
            return Err("This scene has never been saved — pass a path, e.g. Assets/Scenes/Main.unity");

        if (!EditorSceneManager.SaveOpenScenes()) return Err("Unity could not save the open scenes");
        return Ok(D("saved", current.path));
    }

    static string CmdOpenScene(object p)
    {
        var path = ClaudeJson.Str(p, "path");
        if (path == null) return Err("path required, e.g. Assets/Scenes/Main.unity");
        if (AssetDatabase.LoadMainAssetAtPath(path) == null) return Err("Scene not found: " + path);

        var guard = GuardUnsavedChanges(p);
        if (guard != null) return guard;

        var additive = ClaudeJson.Bool(p, "additive", false);
        var scene = EditorSceneManager.OpenScene(path, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
        return Ok(D("scene", scene.name, "path", scene.path, "additive", additive,
                    "rootObjects", scene.rootCount));
    }

    static string CmdNewScene(object p)
    {
        var guard = GuardUnsavedChanges(p);
        if (guard != null) return guard;

        bool empty = ClaudeJson.Bool(p, "empty", false);
        var scene = EditorSceneManager.NewScene(
            empty ? NewSceneSetup.EmptyScene : NewSceneSetup.DefaultGameObjects,
            NewSceneMode.Single);

        var path = ClaudeJson.Str(p, "path");
        if (!string.IsNullOrEmpty(path))
        {
            if (!path.StartsWith("Assets/")) return Err("path must start with Assets/");
            if (!path.EndsWith(".unity")) path += ".unity";
            EnsureAssetFolder(path);
            if (!EditorSceneManager.SaveScene(scene, path)) return Err("Scene created but could not be saved to " + path);
        }

        return Ok(D("scene", scene.name, "path", scene.path, "empty", empty));
    }

    /// <summary>
    /// Opening or replacing a scene throws away unsaved work, and a modal
    /// "save changes?" dialog would hang the bridge — so require an explicit choice.
    /// </summary>
    static string GuardUnsavedChanges(object p)
    {
        var dirty = new List<string>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.isDirty) dirty.Add(string.IsNullOrEmpty(scene.path) ? scene.name + " (never saved)" : scene.path);
        }
        if (dirty.Count == 0) return null;

        if (ClaudeJson.Bool(p, "save", false))
        {
            if (!EditorSceneManager.SaveOpenScenes()) return Err("Could not save the open scenes");
            return null;
        }
        if (ClaudeJson.Bool(p, "discard", false)) return null;

        return Err("Unsaved changes in: " + string.Join(", ", dirty) +
                   " — pass save:true to keep them or discard:true to lose them.",
                   D("dirtyScenes", dirty));
    }

    static string CmdSetLighting(object p)
    {
        var changed = new List<string>();

        var ambientColor = ClaudeJson.Floats(p, "ambient_color");
        var ambientText = ClaudeJson.Str(p, "ambient_color");
        Color parsedAmbient;
        if (ambientColor != null && ambientColor.Length >= 3)
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(ambientColor[0], ambientColor[1], ambientColor[2]);
            changed.Add("ambient_color");
        }
        else if (!string.IsNullOrEmpty(ambientText) && ColorUtility.TryParseHtmlString(ambientText, out parsedAmbient))
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = parsedAmbient;
            changed.Add("ambient_color");
        }

        var ambientIntensity = ClaudeJson.Float(p, "ambient_intensity");
        if (ambientIntensity.HasValue)
        { RenderSettings.ambientIntensity = ambientIntensity.Value; changed.Add("ambient_intensity"); }

        var skyboxPath = ClaudeJson.Str(p, "skybox");
        if (!string.IsNullOrEmpty(skyboxPath))
        {
            var skybox = AssetDatabase.LoadAssetAtPath<Material>(skyboxPath);
            if (skybox == null) return Err("Skybox material not found: " + skyboxPath);
            RenderSettings.skybox = skybox;
            changed.Add("skybox");
        }

        if (ClaudeJson.Has(p, "fog"))
        { RenderSettings.fog = ClaudeJson.Bool(p, "fog", false); changed.Add("fog"); }

        var fogColor = ClaudeJson.Floats(p, "fog_color");
        if (fogColor != null && fogColor.Length >= 3)
        {
            RenderSettings.fogColor = new Color(fogColor[0], fogColor[1], fogColor[2],
                                                fogColor.Length > 3 ? fogColor[3] : 1f);
            changed.Add("fog_color");
        }

        var fogDensity = ClaudeJson.Float(p, "fog_density");
        if (fogDensity.HasValue) { RenderSettings.fogDensity = fogDensity.Value; changed.Add("fog_density"); }

        if (changed.Count == 0) return Err("No lighting settings given");

        EditorSceneManager.MarkAllScenesDirty();
        return Ok(D("changed", changed, "note", "Save the scene to keep these settings."));
    }
}
