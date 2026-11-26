using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Scene, GameObject, component and prefab commands.
/// </summary>
public static partial class ClaudeUnityBridge
{
    // ── Hierarchy and lookup ──────────────────────────────────────────────────

    static string CmdHierarchy(object p)
    {
        var rootPath  = ClaudeJson.Str(p, "root");
        int maxDepth  = ClaudeJson.Int(p, "max_depth") ?? 100;
        bool withComp = ClaudeJson.Bool(p, "components", true);

        if (!string.IsNullOrEmpty(rootPath))
        {
            var root = ResolveGameObject(rootPath);
            if (root == null) return Err("Not found: " + rootPath);
            return Ok(D("hierarchy", new List<object> { BuildNode(root, "", 0, maxDepth, withComp) }));
        }

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var items = new List<object>();
        foreach (var go in scene.GetRootGameObjects())
            items.Add(BuildNode(go, "", 0, maxDepth, withComp));
        return Ok(D("scene", scene.name, "hierarchy", items));
    }

    static Dictionary<string, object> BuildNode(GameObject go, string parentPath, int depth, int maxDepth, bool withComp)
    {
        var path = parentPath.Length == 0 ? go.name : parentPath + "/" + go.name;
        var node = D("name", go.name, "path", path, "active", go.activeSelf);

        if (withComp)
        {
            var comps = new List<string>();
            foreach (var c in go.GetComponents<Component>())
                comps.Add(c == null ? "<missing script>" : c.GetType().Name);
            node["components"] = comps;
        }

        if (depth < maxDepth && go.transform.childCount > 0)
        {
            var children = new List<object>();
            foreach (Transform child in go.transform)
                children.Add(BuildNode(child.gameObject, path, depth + 1, maxDepth, withComp));
            node["children"] = children;
        }
        else if (go.transform.childCount > 0)
        {
            node["childCount"] = go.transform.childCount;
        }
        return node;
    }

    static string CmdFindGameObjects(object p)
    {
        var nameQuery = ClaudeJson.Str(p, "name");
        var compName  = ClaudeJson.Str(p, "component");
        var tag       = ClaudeJson.Str(p, "tag");
        bool inactive = ClaudeJson.Bool(p, "include_inactive", true);
        int limit     = ClaudeJson.Int(p, "limit") ?? 100;

        Type compType = null;
        if (!string.IsNullOrEmpty(compName))
        {
            compType = FindType(compName, typeof(Component));
            if (compType == null) return Err("Component type not found: " + compName);
        }

        var results = new List<object>();
        var sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
        for (int s = 0; s < sceneCount && results.Count < limit; s++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects())
                CollectMatches(root.transform, nameQuery, compType, tag, inactive, limit, results);
        }

        return Ok(D("count", results.Count, "matches", results));
    }

    static void CollectMatches(Transform t, string nameQuery, Type compType, string tag,
                               bool inactive, int limit, List<object> results)
    {
        if (results.Count >= limit) return;
        var go = t.gameObject;

        bool keep = inactive || go.activeInHierarchy;
        if (keep && !string.IsNullOrEmpty(nameQuery))
            keep = go.name.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0;
        if (keep && compType != null)
            keep = go.GetComponent(compType) != null;
        if (keep && !string.IsNullOrEmpty(tag))
            keep = go.CompareTag(tag);

        if (keep)
        {
            var comps = new List<string>();
            foreach (var c in go.GetComponents<Component>())
                comps.Add(c == null ? "<missing script>" : c.GetType().Name);
            results.Add(D("name", go.name, "path", GetPath(go), "active", go.activeSelf,
                          "tag", go.tag, "layer", LayerMask.LayerToName(go.layer),
                          "components", comps));
        }

        foreach (Transform child in t)
            CollectMatches(child, nameQuery, compType, tag, inactive, limit, results);
    }

    static string CmdGetGameObject(object p)
    {
        var path = ClaudeJson.Str(p, "gameobject");
        if (path == null) return Err("gameobject required");
        var go = ResolveGameObject(path);
        if (go == null) return Err("Not found: " + path);

        bool withProps = ClaudeJson.Bool(p, "properties", true);
        var onlyComponent = ClaudeJson.Str(p, "component");

        var t = go.transform;
        var components = new List<object>();
        foreach (var c in go.GetComponents<Component>())
        {
            if (c == null) { components.Add(D("type", "<missing script>")); continue; }
            if (!string.IsNullOrEmpty(onlyComponent) &&
                !c.GetType().Name.Equals(onlyComponent, StringComparison.OrdinalIgnoreCase)) continue;
            components.Add(DescribeComponent(c, withProps));
        }

        var children = new List<string>();
        foreach (Transform child in t) children.Add(child.name);

        return Ok(D(
            "name",          go.name,
            "path",          GetPath(go),
            "active",        go.activeSelf,
            "activeInHierarchy", go.activeInHierarchy,
            "tag",           go.tag,
            "layer",         LayerMask.LayerToName(go.layer),
            "staticFlags",   GameObjectUtility.GetStaticEditorFlags(go).ToString(),
            "isPrefabInstance", PrefabUtility.IsPartOfPrefabInstance(go),
            "parent",        t.parent == null ? null : GetPath(t.parent.gameObject),
            "position",      Vec3(t.position),
            "localPosition", Vec3(t.localPosition),
            "rotation",      Vec3(t.eulerAngles),
            "localScale",    Vec3(t.localScale),
            "children",      children,
            "components",    components
        ));
    }

    static List<object> Vec3(Vector3 v)
    {
        return new List<object> { v.x, v.y, v.z };
    }

    static Dictionary<string, object> DescribeComponent(Component c, bool withProps)
    {
        var info = D("type", c.GetType().Name, "fullType", c.GetType().FullName);
        if (!withProps) return info;

        try
        {
            var so = new SerializedObject(c);
            var it = so.GetIterator();
            var props = new List<object>();
            bool enterChildren = true;
            int guard = 0;
            while (it.NextVisible(enterChildren) && guard++ < 80)
            {
                enterChildren = false;
                if (it.name == "m_Script") continue;
                props.Add(D("name", it.name, "type", it.propertyType.ToString(), "value", ReadProperty(it)));
            }
            info["properties"] = props;
        }
        catch (Exception e)
        {
            info["propertiesError"] = e.Message;
        }
        return info;
    }

    static object ReadProperty(SerializedProperty sp)
    {
        switch (sp.propertyType)
        {
            case SerializedPropertyType.Integer:   return (sp.type == "long" || sp.type == "ulong") ? (object)sp.longValue : sp.intValue;
            case SerializedPropertyType.Boolean:   return sp.boolValue;
            case SerializedPropertyType.Float:     return (sp.type == "double") ? (object)sp.doubleValue : sp.floatValue;
            case SerializedPropertyType.String:    return sp.stringValue;
            case SerializedPropertyType.Color:     return new List<object> { sp.colorValue.r, sp.colorValue.g, sp.colorValue.b, sp.colorValue.a };
            case SerializedPropertyType.Vector2:   return new List<object> { sp.vector2Value.x, sp.vector2Value.y };
            case SerializedPropertyType.Vector3:   return Vec3(sp.vector3Value);
            case SerializedPropertyType.Vector4:   return new List<object> { sp.vector4Value.x, sp.vector4Value.y, sp.vector4Value.z, sp.vector4Value.w };
            case SerializedPropertyType.Quaternion:return new List<object> { sp.quaternionValue.eulerAngles.x, sp.quaternionValue.eulerAngles.y, sp.quaternionValue.eulerAngles.z };
            case SerializedPropertyType.Enum:
                if (sp.enumNames != null && sp.enumValueIndex >= 0 && sp.enumValueIndex < sp.enumNames.Length)
                    return sp.enumNames[sp.enumValueIndex];
                return sp.intValue;
            case SerializedPropertyType.LayerMask: return sp.intValue;
            case SerializedPropertyType.ObjectReference:
                return sp.objectReferenceValue == null
                    ? null
                    : (object)(sp.objectReferenceValue.name + " (" + sp.objectReferenceValue.GetType().Name + ")");
            case SerializedPropertyType.ArraySize:  return sp.intValue;
            default:
                if (sp.isArray) return "array[" + sp.arraySize + "]";
                return sp.propertyType.ToString();
        }
    }

    // ── GameObject lifecycle ──────────────────────────────────────────────────

    static string CmdCreateGameObject(object p)
    {
        var name = ClaudeJson.Str(p, "name");
        if (name == null) return Err("name required");

        var primitive = ClaudeJson.Str(p, "primitive");
        var parentStr = ClaudeJson.Str(p, "parent");

        GameObject go;
        if (!string.IsNullOrEmpty(primitive))
        {
            PrimitiveType pt;
            if (!Enum.TryParse<PrimitiveType>(primitive, true, out pt))
                return Err("Unknown primitive: " + primitive + " (Cube, Sphere, Capsule, Cylinder, Plane, Quad)");
            go = GameObject.CreatePrimitive(pt);
        }
        else go = new GameObject();

        go.name = name;
        Undo.RegisterCreatedObjectUndo(go, "Claude: Create " + name);

        if (!string.IsNullOrEmpty(parentStr))
        {
            var parent = ResolveGameObject(parentStr);
            if (parent == null) { Undo.DestroyObjectImmediate(go); return Err("Parent not found: " + parentStr); }
            go.transform.SetParent(parent.transform, false);
        }

        var pos = ClaudeJson.Floats(p, "position");
        var rot = ClaudeJson.Floats(p, "rotation");
        var scl = ClaudeJson.Floats(p, "scale");
        if (pos != null && pos.Length == 3) go.transform.localPosition = new Vector3(pos[0], pos[1], pos[2]);
        if (rot != null && rot.Length == 3) go.transform.localEulerAngles = new Vector3(rot[0], rot[1], rot[2]);
        if (scl != null && scl.Length == 3) go.transform.localScale = new Vector3(scl[0], scl[1], scl[2]);

        if (ClaudeJson.Has(p, "active")) go.SetActive(ClaudeJson.Bool(p, "active", true));

        var added = new List<string>();
        var components = ClaudeJson.List(p, "components");
        if (components != null)
        {
            foreach (var entry in components)
            {
                var typeName = entry as string;
                if (string.IsNullOrEmpty(typeName)) continue;
                var type = FindType(typeName, typeof(Component));
                if (type == null) return Err("Component type not found: " + typeName, D("created", GetPath(go)));
                Undo.AddComponent(go, type);
                added.Add(type.Name);
            }
        }

        Selection.activeGameObject = go;
        return Ok(D("name", go.name, "path", GetPath(go), "componentsAdded", added));
    }

    static string CmdDeleteGameObject(object p)
    {
        var path = ClaudeJson.Str(p, "path") ?? ClaudeJson.Str(p, "gameobject");
        if (path == null) return Err("path required");
        var go = ResolveGameObject(path);
        if (go == null) return Err("Not found: " + path);
        var full = GetPath(go);
        Undo.DestroyObjectImmediate(go);
        return Ok(D("deleted", full));
    }

    static string CmdDuplicateGameObject(object p)
    {
        var path = ClaudeJson.Str(p, "gameobject");
        if (path == null) return Err("gameobject required");
        var source = ResolveGameObject(path);
        if (source == null) return Err("Not found: " + path);

        int count = Math.Max(1, ClaudeJson.Int(p, "count") ?? 1);
        if (count > 500) return Err("count capped at 500 per call");
        var offset = ClaudeJson.Floats(p, "offset");
        var baseName = ClaudeJson.Str(p, "name") ?? source.name;
        var step = (offset != null && offset.Length == 3)
            ? new Vector3(offset[0], offset[1], offset[2])
            : Vector3.zero;

        var created = new List<string>();
        for (int i = 1; i <= count; i++)
        {
            GameObject copy;
            if (PrefabUtility.IsPartOfPrefabInstance(source))
            {
                var prefab = PrefabUtility.GetCorrespondingObjectFromSource(source);
                copy = prefab != null
                    ? (GameObject)PrefabUtility.InstantiatePrefab(prefab, source.transform.parent)
                    : UnityEngine.Object.Instantiate(source, source.transform.parent);
            }
            else copy = UnityEngine.Object.Instantiate(source, source.transform.parent);

            copy.name = count == 1 && ClaudeJson.Str(p, "name") != null ? baseName : baseName + " " + i;
            copy.transform.localPosition = source.transform.localPosition + step * i;
            copy.transform.localRotation = source.transform.localRotation;
            copy.transform.localScale = source.transform.localScale;
            Undo.RegisterCreatedObjectUndo(copy, "Claude: Duplicate " + source.name);
            created.Add(GetPath(copy));
        }
        return Ok(D("count", created.Count, "created", created));
    }

    static string CmdRenameGameObject(object p)
    {
        var path = ClaudeJson.Str(p, "gameobject");
        var name = ClaudeJson.Str(p, "name");
        if (path == null) return Err("gameobject required");
        if (string.IsNullOrEmpty(name)) return Err("name required");
        var go = ResolveGameObject(path);
        if (go == null) return Err("Not found: " + path);
        Undo.RecordObject(go, "Claude: Rename");
        go.name = name;
        return Ok(D("path", GetPath(go)));
    }

    static string CmdSetActive(object p)
    {
        var path = ClaudeJson.Str(p, "gameobject");
        if (path == null) return Err("gameobject required");
        if (!ClaudeJson.Has(p, "active")) return Err("active required");
        var go = ResolveGameObject(path);
        if (go == null) return Err("Not found: " + path);
        bool active = ClaudeJson.Bool(p, "active", true);
        Undo.RecordObject(go, "Claude: Set Active");
        go.SetActive(active);
        return Ok(D("path", GetPath(go), "active", go.activeSelf));
    }

    static string CmdSetParent(object p)
    {
        var path = ClaudeJson.Str(p, "gameobject");
        if (path == null) return Err("gameobject required");
        var go = ResolveGameObject(path);
        if (go == null) return Err("Not found: " + path);

        bool keepWorld = ClaudeJson.Bool(p, "world_position_stays", true);
        var parentStr = ClaudeJson.Str(p, "parent");

        Transform newParent = null;
        if (!string.IsNullOrEmpty(parentStr))
        {
            var parent = ResolveGameObject(parentStr);
            if (parent == null) return Err("Parent not found: " + parentStr);
            if (parent.transform.IsChildOf(go.transform))
                return Err("Cannot parent an object to its own descendant");
            newParent = parent.transform;
        }

        // RecordObject on the Transform captures m_Father, so Ctrl+Z restores
        // the previous parent. This works on every Unity version the bridge targets.
        Undo.RecordObject(go.transform, "Claude: Set Parent");
        go.transform.SetParent(newParent, keepWorld);
        return Ok(D("path", GetPath(go), "parent", go.transform.parent == null ? null : GetPath(go.transform.parent.gameObject)));
    }

    static string CmdSetTransform(object p)
    {
        var path = ClaudeJson.Str(p, "gameobject");
        if (path == null) return Err("gameobject required");
        var go = ResolveGameObject(path);
        if (go == null) return Err("Not found: " + path);

        Undo.RecordObject(go.transform, "Claude: Set Transform");
        bool local = ClaudeJson.Bool(p, "local", false);

        var pos = ClaudeJson.Floats(p, "position");
        var rot = ClaudeJson.Floats(p, "rotation");
        var scl = ClaudeJson.Floats(p, "scale");

        if (pos != null && pos.Length == 3)
        {
            var v = new Vector3(pos[0], pos[1], pos[2]);
            if (local) go.transform.localPosition = v; else go.transform.position = v;
        }
        if (rot != null && rot.Length == 3)
        {
            var v = new Vector3(rot[0], rot[1], rot[2]);
            if (local) go.transform.localEulerAngles = v; else go.transform.eulerAngles = v;
        }
        if (scl != null && scl.Length == 3)
            go.transform.localScale = new Vector3(scl[0], scl[1], scl[2]);

        var t = go.transform;
        return Ok(D("path", GetPath(go), "position", Vec3(t.position),
                    "rotation", Vec3(t.eulerAngles), "scale", Vec3(t.localScale)));
    }

    static string CmdSetTagLayer(object p)
    {
        var path = ClaudeJson.Str(p, "gameobject");
        if (path == null) return Err("gameobject required");
        var go = ResolveGameObject(path);
        if (go == null) return Err("Not found: " + path);

        bool recursive = ClaudeJson.Bool(p, "recursive", false);
        var tag = ClaudeJson.Str(p, "tag");
        var layerName = ClaudeJson.Str(p, "layer");
        int? layerNum = ClaudeJson.Int(p, "layer");

        int layer = -1;
        if (!string.IsNullOrEmpty(layerName) && !(ClaudeJson.Get(p, "layer") is double))
        {
            layer = LayerMask.NameToLayer(layerName);
            if (layer < 0) return Err("Layer does not exist: " + layerName +
                " (create it in Project Settings > Tags and Layers)");
        }
        else if (layerNum.HasValue) layer = layerNum.Value;

        var targets = new List<GameObject> { go };
        if (recursive)
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                if (t.gameObject != go) targets.Add(t.gameObject);

        foreach (var target in targets)
        {
            Undo.RecordObject(target, "Claude: Set Tag/Layer");
            if (!string.IsNullOrEmpty(tag))
            {
                try { target.tag = tag; }
                catch (Exception e)
                {
                    return Err("Could not set tag '" + tag + "': " + e.Message +
                               " — add it in Project Settings > Tags and Layers first.");
                }
            }
            if (layer >= 0) target.layer = layer;
        }

        return Ok(D("path", GetPath(go), "tag", go.tag, "layer", LayerMask.LayerToName(go.layer),
                    "objectsAffected", targets.Count));
    }

    // ── Components ────────────────────────────────────────────────────────────

    static string CmdAddComponent(object p)
    {
        var goPath = ClaudeJson.Str(p, "gameobject");
        var cName = ClaudeJson.Str(p, "component");
        if (goPath == null) return Err("gameobject required");
        if (cName == null) return Err("component required");

        var go = ResolveGameObject(goPath);
        if (go == null) return Err("Not found: " + goPath);
        var type = FindType(cName, typeof(Component));
        if (type == null) return Err("Component type not found: " + cName +
            " — for a script you just wrote, call compile_status first.");

        var component = Undo.AddComponent(go, type);
        if (component == null) return Err("Unity refused to add " + type.Name +
            " (already present and not allowed twice, or a missing requirement).");

        var applied = new List<string>();
        var props = ClaudeJson.Get(p, "properties") as Dictionary<string, object>;
        if (props != null)
        {
            foreach (var kv in props)
            {
                string error;
                if (!SetProperty(component, kv.Key, kv.Value, out error))
                    return Err("Component added but property '" + kv.Key + "' failed: " + error,
                               D("added", type.Name, "propertiesApplied", applied));
                applied.Add(kv.Key);
            }
        }

        return Ok(D("added", type.Name, "to", GetPath(go), "propertiesApplied", applied));
    }

    static string CmdRemoveComponent(object p)
    {
        var goPath = ClaudeJson.Str(p, "gameobject");
        var cName = ClaudeJson.Str(p, "component");
        if (goPath == null) return Err("gameobject required");
        if (cName == null) return Err("component required");

        var go = ResolveGameObject(goPath);
        if (go == null) return Err("Not found: " + goPath);

        Component target = null;
        foreach (var c in go.GetComponents<Component>())
        {
            if (c == null) continue;
            if (!c.GetType().Name.Equals(cName, StringComparison.OrdinalIgnoreCase)) continue;
            target = c;
            break;
        }
        if (target == null) return Err("Component not on object: " + cName);
        if (target is Transform) return Err("Transform cannot be removed");

        Undo.DestroyObjectImmediate(target);
        return Ok(D("removed", cName, "from", GetPath(go)));
    }

    static string CmdSetComponentProperty(object p)
    {
        var goPath = ClaudeJson.Str(p, "gameobject");
        var cName = ClaudeJson.Str(p, "component");
        if (goPath == null) return Err("gameobject required");
        if (cName == null) return Err("component required");

        var go = ResolveGameObject(goPath);
        if (go == null) return Err("Not found: " + goPath);

        Component component = null;
        var present = new List<string>();
        foreach (var c in go.GetComponents<Component>())
        {
            if (c == null) continue;
            present.Add(c.GetType().Name);
            if (component == null && c.GetType().Name.Equals(cName, StringComparison.OrdinalIgnoreCase))
                component = c;
        }
        if (component == null)
            return Err("Component not on object: " + cName, D("componentsPresent", present));

        // Either one property/value pair, or a "properties" dictionary.
        var pairs = new Dictionary<string, object>();
        var propsDict = ClaudeJson.Get(p, "properties") as Dictionary<string, object>;
        if (propsDict != null) foreach (var kv in propsDict) pairs[kv.Key] = kv.Value;
        var single = ClaudeJson.Str(p, "property");
        if (!string.IsNullOrEmpty(single)) pairs[single] = ClaudeJson.Get(p, "value");
        if (pairs.Count == 0) return Err("property + value, or properties, required");

        var applied = new List<string>();
        foreach (var kv in pairs)
        {
            string error;
            if (!SetProperty(component, kv.Key, kv.Value, out error))
                return Err("Property '" + kv.Key + "': " + error,
                           D("propertiesApplied", applied, "available", ListPropertyNames(component)));
            applied.Add(kv.Key);
        }

        return Ok(D("gameobject", GetPath(go), "component", component.GetType().Name,
                    "propertiesApplied", applied));
    }

    static List<string> ListPropertyNames(UnityEngine.Object target)
    {
        var names = new List<string>();
        try
        {
            var so = new SerializedObject(target);
            var it = so.GetIterator();
            bool enterChildren = true;
            int guard = 0;
            while (it.NextVisible(enterChildren) && guard++ < 80)
            {
                enterChildren = false;
                if (it.name == "m_Script") continue;
                names.Add(it.name + " (" + it.propertyType + ")");
            }
        }
        catch { }
        return names;
    }

    /// <summary>
    /// Set a serialized property on any Object. Accepts the serialized name
    /// ("m_Mass"), the inspector name ("Mass") or a script field name, and
    /// resolves object references from asset paths or scene paths.
    /// </summary>
    static bool SetProperty(UnityEngine.Object target, string propertyName, object value, out string error)
    {
        error = null;
        SerializedObject so;
        try { so = new SerializedObject(target); }
        catch (Exception e) { error = e.Message; return false; }

        var sp = FindProperty(so, propertyName);
        if (sp == null) { error = "no such serialized property"; return false; }

        Undo.RecordObject(target, "Claude: Set " + propertyName);
        if (!ApplyValue(sp, value, out error)) return false;

        // Unity drops a reference that does not fit the field without raising
        // anything, so confirm the write landed rather than reporting success
        // on trust.
        var wasReference = sp.propertyType == SerializedPropertyType.ObjectReference;
        var intended = wasReference ? sp.objectReferenceValue : null;

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);

        if (wasReference && intended != null)
        {
            var check = FindProperty(new SerializedObject(target), propertyName);
            if (check == null || check.objectReferenceValue == null)
            {
                error = "Unity rejected '" + intended.name + "' (" + intended.GetType().Name +
                        ") for this field — it expects a different type";
                return false;
            }
        }
        return true;
    }

    static SerializedProperty FindProperty(SerializedObject so, string name)
    {
        // Direct hit, including dotted paths such as "m_Sprites.Array.data[0]".
        var sp = so.FindProperty(name);
        if (sp != null) return sp;

        // Unity's built-in components prefix their fields with m_.
        if (!name.StartsWith("m_"))
        {
            var capitalized = char.ToUpperInvariant(name[0]) + name.Substring(1);
            sp = so.FindProperty("m_" + capitalized);
            if (sp != null) return sp;
        }

        // Fall back to a case-insensitive match on name or inspector label.
        var it = so.GetIterator();
        bool enterChildren = true;
        int guard = 0;
        while (it.NextVisible(enterChildren) && guard++ < 200)
        {
            enterChildren = false;
            if (it.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return it.Copy();
            var stripped = it.name.StartsWith("m_") ? it.name.Substring(2) : it.name;
            if (stripped.Equals(name, StringComparison.OrdinalIgnoreCase)) return it.Copy();
            if (it.displayName.Replace(" ", "").Equals(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                return it.Copy();
        }
        return null;
    }

    static bool ApplyValue(SerializedProperty sp, object value, out string error)
    {
        error = null;
        var floats = ClaudeJson.ToFloats(value);
        var text = value as string;
        var number = value is double ? (double?)(double)value : null;

        switch (sp.propertyType)
        {
            case SerializedPropertyType.Integer:
                long intTarget;
                if (number.HasValue) intTarget = (long)Math.Round(number.Value);
                else if (text == null || !long.TryParse(text, out intTarget))
                { error = "expected a number"; return false; }
                // Pick the setter that matches the real backing field: longValue on an
                // int field (or intValue on a long one) does not reliably take.
                if (sp.type == "long" || sp.type == "ulong") sp.longValue = intTarget;
                else sp.intValue = (int)intTarget;
                return true;

            case SerializedPropertyType.Boolean:
                if (value is bool) { sp.boolValue = (bool)value; return true; }
                if (number.HasValue) { sp.boolValue = Math.Abs(number.Value) > double.Epsilon; return true; }
                if (text == "true" || text == "false") { sp.boolValue = text == "true"; return true; }
                error = "expected true or false"; return false;

            case SerializedPropertyType.Float:
                double floatTarget;
                if (number.HasValue) floatTarget = number.Value;
                else if (text == null || !double.TryParse(text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out floatTarget))
                { error = "expected a number"; return false; }
                if (sp.type == "double") sp.doubleValue = floatTarget;
                else sp.floatValue = (float)floatTarget;
                return true;

            case SerializedPropertyType.String:
                sp.stringValue = text ?? (value == null ? "" : ClaudeJson.Serialize(value).Trim('"'));
                return true;

            case SerializedPropertyType.Color:
                if (floats != null && floats.Length >= 3)
                {
                    sp.colorValue = new Color(floats[0], floats[1], floats[2],
                                              floats.Length > 3 ? floats[3] : 1f);
                    return true;
                }
                Color parsedColor;
                if (text != null && ColorUtility.TryParseHtmlString(text, out parsedColor))
                { sp.colorValue = parsedColor; return true; }
                error = "expected [r,g,b] in 0-1, or \"#RRGGBB\""; return false;

            case SerializedPropertyType.Vector2:
                if (floats != null && floats.Length >= 2) { sp.vector2Value = new Vector2(floats[0], floats[1]); return true; }
                error = "expected [x,y]"; return false;

            case SerializedPropertyType.Vector3:
                if (floats != null && floats.Length >= 3) { sp.vector3Value = new Vector3(floats[0], floats[1], floats[2]); return true; }
                error = "expected [x,y,z]"; return false;

            case SerializedPropertyType.Vector4:
                if (floats != null && floats.Length >= 4) { sp.vector4Value = new Vector4(floats[0], floats[1], floats[2], floats[3]); return true; }
                error = "expected [x,y,z,w]"; return false;

            case SerializedPropertyType.Quaternion:
                if (floats != null && floats.Length == 3) { sp.quaternionValue = Quaternion.Euler(floats[0], floats[1], floats[2]); return true; }
                if (floats != null && floats.Length == 4) { sp.quaternionValue = new Quaternion(floats[0], floats[1], floats[2], floats[3]); return true; }
                error = "expected [x,y,z] Euler degrees or [x,y,z,w]"; return false;

            case SerializedPropertyType.Enum:
                // A number is the enum's stored value, not its position in the list.
                // CameraClearFlags starts at 1, so position 2 is "Depth only" while
                // the value 2 is "Solid Color"; intValue writes the real value.
                if (number.HasValue) { sp.intValue = (int)Math.Round(number.Value); return true; }
                if (text != null && sp.enumNames != null)
                {
                    // Unity spells several of these for display ("Depth only"), so
                    // compare without spaces or underscores: "SolidColor" has to
                    // match "Solid Color".
                    var wanted = text.Replace(" ", "").Replace("_", "");
                    for (int i = 0; i < sp.enumNames.Length; i++)
                    {
                        if (!sp.enumNames[i].Replace(" ", "")
                                .Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                        sp.enumValueIndex = i;
                        return true;
                    }
                    var displayNames = sp.enumDisplayNames;
                    if (displayNames != null)
                    {
                        for (int i = 0; i < displayNames.Length; i++)
                        {
                            if (!displayNames[i].Replace(" ", "")
                                    .Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                            sp.enumValueIndex = i;
                            return true;
                        }
                    }
                    error = "unknown value; valid: " + string.Join(", ", sp.enumNames);
                    return false;
                }
                error = "expected an enum name or value"; return false;

            case SerializedPropertyType.LayerMask:
                if (number.HasValue) { sp.intValue = (int)Math.Round(number.Value); return true; }
                if (text != null)
                {
                    int layer = LayerMask.NameToLayer(text);
                    if (layer < 0) { error = "unknown layer: " + text; return false; }
                    sp.intValue = 1 << layer;
                    return true;
                }
                error = "expected a layer name or mask value"; return false;

            case SerializedPropertyType.ObjectReference:
                if (value == null || text == "") { sp.objectReferenceValue = null; return true; }
                if (text == null) { error = "expected an asset path or a scene object path"; return false; }
                var expected = ExpectedReferenceType(sp);
                var resolved = ResolveObjectReference(text, expected);
                if (resolved == null)
                {
                    error = "could not resolve '" + text + "'" +
                            (expected == null ? "" : " as " + expected.Name) +
                            " — use an asset path like Assets/Art/x.png or a scene path like Player/Model";
                    return false;
                }
                sp.objectReferenceValue = resolved;
                // Unity clears the field on the spot when the type does not fit,
                // so check here rather than reporting a success that never happened.
                if (sp.objectReferenceValue == null)
                {
                    error = "Unity rejected '" + resolved.name + "' (" + resolved.GetType().Name +
                            ") — this field expects " +
                            (expected == null ? "another type" : expected.Name);
                    return false;
                }
                return true;

            case SerializedPropertyType.ArraySize:
                if (number.HasValue) { sp.intValue = (int)Math.Round(number.Value); return true; }
                error = "expected a number"; return false;

            case SerializedPropertyType.Rect:
                if (floats != null && floats.Length >= 4) { sp.rectValue = new Rect(floats[0], floats[1], floats[2], floats[3]); return true; }
                error = "expected [x,y,width,height]"; return false;

            case SerializedPropertyType.Bounds:
                if (floats != null && floats.Length >= 6)
                {
                    sp.boundsValue = new Bounds(new Vector3(floats[0], floats[1], floats[2]),
                                                new Vector3(floats[3], floats[4], floats[5]));
                    return true;
                }
                error = "expected [cx,cy,cz,sx,sy,sz]"; return false;

            default:
                // Arrays and lists: resize, then set each element.
                if (sp.isArray)
                {
                    var list = value as List<object>;
                    if (list == null) { error = "expected an array"; return false; }
                    sp.arraySize = list.Count;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var element = sp.GetArrayElementAtIndex(i);
                        if (!ApplyValue(element, list[i], out error))
                        { error = "element " + i + ": " + error; return false; }
                    }
                    return true;
                }
                error = "unsupported property type " + sp.propertyType;
                return false;
        }
    }

    /// <summary>
    /// Unity reports reference types as "PPtr&lt;$Material&gt;" — but not always:
    /// some fields report "PPtr&lt;Sprite&gt;" with no dollar. Keying on the dollar
    /// silently loses the expected type, and then any asset at the path looks
    /// acceptable. Read between the angle brackets instead.
    /// </summary>
    static Type ExpectedReferenceType(SerializedProperty sp)
    {
        var raw = sp.type;
        int open = raw.IndexOf('<');
        int close = raw.LastIndexOf('>');
        if (open < 0 || close <= open) return null;
        var name = raw.Substring(open + 1, close - open - 1).TrimStart('$');
        return string.IsNullOrEmpty(name) ? null : FindType(name);
    }

    static UnityEngine.Object ResolveObjectReference(string reference, Type expected)
    {
        if (reference.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
        {
            if (expected != null)
            {
                var typed = AssetDatabase.LoadAssetAtPath(reference, expected);
                if (typed != null) return typed;

                // What the field wants is often a sub-asset: the main asset of a
                // .png is a Texture2D while the Sprite hangs underneath it. Search
                // the sub-assets BEFORE considering the main one, and never return
                // an object of the wrong type — Unity would drop it without a word.
                foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(reference))
                    if (sub != null && expected.IsInstanceOfType(sub)) return sub;
                return null;
            }

            return AssetDatabase.LoadMainAssetAtPath(reference);
        }

        var go = ResolveGameObject(reference);
        if (go == null) return null;
        if (expected == null || expected == typeof(GameObject)) return go;
        if (typeof(Component).IsAssignableFrom(expected)) return go.GetComponent(expected);
        return go;
    }

    // ── Prefabs ───────────────────────────────────────────────────────────────

    static string CmdCreatePrefab(object p)
    {
        var goPath = ClaudeJson.Str(p, "gameobject");
        var assetPath = ClaudeJson.Str(p, "path");
        if (goPath == null) return Err("gameobject required");
        if (assetPath == null) return Err("path required, e.g. Assets/Prefabs/Enemy.prefab");
        if (!assetPath.StartsWith("Assets/")) return Err("path must start with Assets/");
        if (!assetPath.EndsWith(".prefab")) assetPath += ".prefab";

        var go = ResolveGameObject(goPath);
        if (go == null) return Err("Not found: " + goPath);

        EnsureAssetFolder(assetPath);
        bool connect = ClaudeJson.Bool(p, "connect", true);

        GameObject prefab;
        if (connect) prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, assetPath, InteractionMode.UserAction);
        else prefab = PrefabUtility.SaveAsPrefabAsset(go, assetPath);

        if (prefab == null) return Err("Unity could not save the prefab at " + assetPath);
        AssetDatabase.SaveAssets();
        return Ok(D("path", assetPath, "connected", connect));
    }

    static string CmdInstantiatePrefab(object p)
    {
        var assetPath = ClaudeJson.Str(p, "prefab");
        if (assetPath == null) return Err("prefab required, e.g. Assets/Prefabs/Enemy.prefab");

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefab == null)
        {
            // Distinguish "nothing there" from "there, but Unity cannot use it as
            // a prefab" — the usual cause is a format Unity does not import, such
            // as .glb or .gltf without a glTF package, which land as DefaultAsset.
            var existing = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            if (existing == null)
                return Err("No asset at " + assetPath);
            if (existing.Name == "DefaultAsset")
                return Err("Unity imported " + assetPath + " as an opaque file, not a model. " +
                           "Unity reads .fbx, .obj and .dae natively; .glb/.gltf need a glTF package " +
                           "(com.unity.cloud.gltfast). Re-export as FBX, or install that package.");
            return Err(assetPath + " is a " + existing.Name + ", not a prefab or model.");
        }

        Transform parent = null;
        var parentStr = ClaudeJson.Str(p, "parent");
        if (!string.IsNullOrEmpty(parentStr))
        {
            var parentGo = ResolveGameObject(parentStr);
            if (parentGo == null) return Err("Parent not found: " + parentStr);
            parent = parentGo.transform;
        }

        // Either a batch of placements, or a single one from the top-level params.
        var placements = ClaudeJson.List(p, "placements");
        if (placements == null) placements = new List<object> { p };
        if (placements.Count > 500) return Err("placements capped at 500 per call");

        var created = new List<string>();
        foreach (var entry in placements)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            if (instance == null) return Err("Unity could not instantiate " + assetPath,
                                             D("created", created));

            var name = ClaudeJson.Str(entry, "name");
            if (!string.IsNullOrEmpty(name)) instance.name = name;

            var pos = ClaudeJson.Floats(entry, "position");
            var rot = ClaudeJson.Floats(entry, "rotation");
            var scl = ClaudeJson.Floats(entry, "scale");
            if (pos != null && pos.Length == 3) instance.transform.localPosition = new Vector3(pos[0], pos[1], pos[2]);
            if (rot != null && rot.Length == 3) instance.transform.localEulerAngles = new Vector3(rot[0], rot[1], rot[2]);
            if (scl != null && scl.Length == 3) instance.transform.localScale = new Vector3(scl[0], scl[1], scl[2]);

            Undo.RegisterCreatedObjectUndo(instance, "Claude: Instantiate " + prefab.name);
            created.Add(GetPath(instance));
        }

        return Ok(D("prefab", assetPath, "count", created.Count, "created", created));
    }

    /// <summary>Create the Assets subfolders an asset path needs.</summary>
    static void EnsureAssetFolder(string assetPath)
    {
        var dir = Path.GetDirectoryName(assetPath);
        if (string.IsNullOrEmpty(dir)) return;
        dir = dir.Replace('\\', '/');
        if (AssetDatabase.IsValidFolder(dir)) return;

        var parts = dir.Split('/');
        var current = parts[0]; // "Assets"
        for (int i = 1; i < parts.Length; i++)
        {
            var next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}
