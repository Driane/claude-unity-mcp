import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { CallToolRequestSchema, ListToolsRequestSchema } from '@modelcontextprotocol/sdk/types.js';

const UNITY_URL = process.env.CLAUDE_UNITY_URL || 'http://localhost:6400';
const DEFAULT_TIMEOUT = 15000;

async function callUnity(action, params = {}, timeout = DEFAULT_TIMEOUT) {
  try {
    const res = await fetch(`${UNITY_URL}/command`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Claude-Unity-Bridge': '1' },
      body: JSON.stringify({ action, params }),
      signal: AbortSignal.timeout(timeout),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return await res.json();
  } catch (err) {
    const hint = err.name === 'TimeoutError'
      ? `Unity did not answer within ${Math.round(timeout / 1000)}s. It may be busy (importing, compiling or building) — try again, or check the Editor.`
      : 'Is the Unity Editor open with ClaudeUnityBridge.cs in Assets/Editor/?';
    return { success: false, error: `Unity unreachable: ${err.message}. ${hint}` };
  }
}

// ── Reusable schema fragments ───────────────────────────────────────────────

const vec3 = (description) => ({ type: 'array', items: { type: 'number' }, description });
const str = (description) => ({ type: 'string', description });
const num = (description) => ({ type: 'number', description });
const bool = (description) => ({ type: 'boolean', description });

// ── Tools ───────────────────────────────────────────────────────────────────
// Each entry maps one MCP tool to one bridge action.

const SPEC = [
  // Inspection
  {
    name: 'unity_status', action: 'get_status',
    description: 'Check the Unity connection: active scene, play state, compile state, Unity version, project path.',
    properties: {},
  },
  {
    name: 'unity_get_hierarchy', action: 'get_hierarchy',
    description: 'GameObject tree of the active scene. Narrow it with root and max_depth on a large scene.',
    properties: {
      root: str('Optional: only this subtree (name or path)'),
      max_depth: num('Optional depth limit (default 100)'),
      components: bool('Include each object\'s component list (default true)'),
    },
  },
  {
    name: 'unity_find_gameobjects', action: 'find_gameobjects',
    description: 'Search the scene by partial name, component type and/or tag. Finds inactive objects too.',
    properties: {
      name: str('Case-insensitive substring of the object name'),
      component: str('Only objects carrying this component, e.g. "Light"'),
      tag: str('Only objects with this tag'),
      include_inactive: bool('Default true'),
      limit: num('Max results (default 100)'),
    },
  },
  {
    name: 'unity_get_gameobject', action: 'get_gameobject',
    description: 'Full detail on one GameObject: transform, tag, layer, children, and every component with its serialized property names and current values. Use this to discover property names for unity_set_component_property.',
    properties: {
      gameobject: str('Name or hierarchy path'),
      properties: bool('Include component properties (default true)'),
      component: str('Optional: only this component type'),
    },
    required: ['gameobject'],
  },

  // GameObject lifecycle
  {
    name: 'unity_create_gameobject', action: 'create_gameobject',
    description: 'Create a GameObject: empty or a primitive, optionally parented, placed and given components in one call.',
    properties: {
      name: str('Name of the new GameObject'),
      primitive: str('Cube, Sphere, Capsule, Cylinder, Plane or Quad'),
      parent: str('Hierarchy path of the parent'),
      position: vec3('[x, y, z] local position'),
      rotation: vec3('[x, y, z] Euler degrees'),
      scale: vec3('[x, y, z]'),
      active: bool('Start active (default true)'),
      components: { type: 'array', items: { type: 'string' }, description: 'Component types to add, e.g. ["Rigidbody", "BoxCollider"]' },
    },
    required: ['name'],
  },
  {
    name: 'unity_delete_gameobject', action: 'delete_gameobject',
    description: 'Delete a GameObject from the scene. Undoable with Ctrl+Z.',
    properties: { path: str('Name or path, e.g. "Canvas/Panel/Button"') },
    required: ['path'],
  },
  {
    name: 'unity_duplicate_gameobject', action: 'duplicate_gameobject',
    description: 'Copy a GameObject one or more times, each copy offset from the last. Keeps prefab links.',
    properties: {
      gameobject: str('Source object'),
      count: num('How many copies (default 1, max 500)'),
      offset: vec3('[x, y, z] added once per copy'),
      name: str('Base name for the copies'),
    },
    required: ['gameobject'],
  },
  {
    name: 'unity_rename_gameobject', action: 'rename_gameobject',
    description: 'Rename a GameObject.',
    properties: { gameobject: str('Name or path'), name: str('New name') },
    required: ['gameobject', 'name'],
  },
  {
    name: 'unity_set_active', action: 'set_active',
    description: 'Enable or disable a GameObject.',
    properties: { gameobject: str('Name or path'), active: bool('true = enabled') },
    required: ['gameobject', 'active'],
  },
  {
    name: 'unity_set_parent', action: 'set_parent',
    description: 'Reparent a GameObject, or move it to the scene root by omitting parent.',
    properties: {
      gameobject: str('Name or path'),
      parent: str('New parent path; omit or empty for the scene root'),
      world_position_stays: bool('Keep the world position (default true)'),
    },
    required: ['gameobject'],
  },
  {
    name: 'unity_set_transform', action: 'set_transform',
    description: 'Set position, rotation (Euler degrees) and/or scale.',
    properties: {
      gameobject: str('Name or path'),
      position: vec3('[x, y, z]'),
      rotation: vec3('[x, y, z] Euler degrees'),
      scale: vec3('[x, y, z]'),
      local: bool('Treat position/rotation as local instead of world (default false)'),
    },
    required: ['gameobject'],
  },
  {
    name: 'unity_set_tag_layer', action: 'set_tag_layer',
    description: 'Set the tag and/or layer of a GameObject. The tag and layer must already exist in Project Settings.',
    properties: {
      gameobject: str('Name or path'),
      tag: str('Existing tag name'),
      layer: str('Existing layer name, or its index as a number'),
      recursive: bool('Apply to all children too (default false)'),
    },
    required: ['gameobject'],
  },

  // Components
  {
    name: 'unity_add_component', action: 'add_component',
    description: 'Add a component and optionally set its properties in the same call, e.g. properties:{"mass":5}.',
    properties: {
      gameobject: str('Name or path'),
      component: str('Component type, e.g. "Rigidbody", "Light", or one of your scripts'),
      properties: { type: 'object', description: 'Property name to value, applied after adding' },
    },
    required: ['gameobject', 'component'],
  },
  {
    name: 'unity_remove_component', action: 'remove_component',
    description: 'Remove a component from a GameObject. Transform cannot be removed.',
    properties: { gameobject: str('Name or path'), component: str('Component type name') },
    required: ['gameobject', 'component'],
  },
  {
    name: 'unity_set_component_property', action: 'set_component_property',
    description: 'Set any serialized property on a component — built-in (Rigidbody mass, Light intensity, Camera field of view) or a field of your own script. Accepts the inspector name ("mass") or the serialized name ("m_Mass"). Object references take an asset path ("Assets/Art/hero.png") or a scene path ("Player/Model"). Colors take [r,g,b,a] in 0-1 or "#RRGGBB". On an unknown name it returns the valid ones.',
    properties: {
      gameobject: str('Name or path'),
      component: str('Component type name'),
      property: str('One property name'),
      value: { description: 'Its value: number, boolean, string, or array such as [x,y,z]' },
      properties: { type: 'object', description: 'Several properties at once, instead of property + value' },
    },
    required: ['gameobject', 'component'],
  },

  // Prefabs
  {
    name: 'unity_create_prefab', action: 'create_prefab',
    description: 'Save a scene GameObject as a prefab asset.',
    properties: {
      gameobject: str('Scene object to save'),
      path: str('Asset path, e.g. "Assets/Prefabs/Enemy.prefab"'),
      connect: bool('Turn the scene object into an instance of the new prefab (default true)'),
    },
    required: ['gameobject', 'path'],
  },
  {
    name: 'unity_instantiate_prefab', action: 'instantiate_prefab',
    description: 'Place a prefab in the scene. Pass placements:[{position,rotation,scale,name},...] to place many at once — the efficient way to lay out a level.',
    properties: {
      prefab: str('Prefab asset path'),
      parent: str('Optional parent path for every instance'),
      position: vec3('[x, y, z] for a single instance'),
      rotation: vec3('[x, y, z] Euler degrees'),
      scale: vec3('[x, y, z]'),
      name: str('Name for a single instance'),
      placements: { type: 'array', items: { type: 'object' }, description: 'Batch: one object per instance, max 500' },
    },
    required: ['prefab'],
    timeout: 60000,
  },

  // Assets
  {
    name: 'unity_write_script', action: 'write_script',
    description: 'Write or overwrite a C# script and reimport it. Unity then recompiles: call unity_compile_status before using the new type.',
    properties: {
      path: str('Asset path, e.g. "Assets/Scripts/PlayerController.cs"'),
      filename: str('Alternative to path: file name, placed under Assets/Scripts/'),
      subfolder: str('Optional subfolder when using filename'),
      content: str('Full C# source'),
    },
    required: ['content'],
    timeout: 30000,
  },
  {
    name: 'unity_create_material', action: 'create_material',
    description: 'Create or replace a material. The shader defaults to the project render pipeline\'s lit shader.',
    properties: {
      path: str('Asset path, e.g. "Assets/Materials/Stone.mat"'),
      shader: str('Shader name; defaults to URP/HDRP Lit or Standard'),
      color: { description: '[r,g,b] or [r,g,b,a] in 0-1, or "#RRGGBB"' },
      metallic: num('0 to 1'),
      smoothness: num('0 to 1'),
      texture: str('Albedo texture asset path'),
    },
    required: ['path'],
  },
  {
    name: 'unity_assign_material', action: 'assign_material',
    description: 'Assign a material to a GameObject\'s renderer(s).',
    properties: {
      gameobject: str('Name or path'),
      material: str('Material asset path'),
      slot: num('Material slot index; omit to fill every slot'),
      recursive: bool('Apply to renderers in children too (default false)'),
    },
    required: ['gameobject', 'material'],
  },
  {
    name: 'unity_create_asset', action: 'create_asset',
    description: 'Create a ScriptableObject asset, optionally filling its fields — the clean way to author game data (items, weapons, levels).',
    properties: {
      type: str('ScriptableObject class name'),
      path: str('Asset path, e.g. "Assets/Data/Sword.asset"'),
      fields: { type: 'object', description: 'Field name to value' },
    },
    required: ['type', 'path'],
  },
  {
    name: 'unity_import_file', action: 'import_file',
    description: 'Copy a file from disk into the project and import it — how a Blender export, a generated texture or an audio file enters Unity.',
    properties: {
      source: str('Absolute path on disk'),
      destination: str('Asset path, e.g. "Assets/Art/hero.png", or a folder'),
      overwrite: bool('Replace an existing file (default false)'),
    },
    required: ['source', 'destination'],
    timeout: 60000,
  },
  {
    name: 'unity_set_texture_settings', action: 'set_texture_settings',
    description: 'Change a texture\'s import settings. For crisp pixel art: filter_mode "Point", compression "Uncompressed", mip_maps false.',
    properties: {
      path: str('Texture asset path'),
      texture_type: str('Default, Sprite, NormalMap, GUI, Cursor, Cookie, Lightmap, SingleChannel'),
      sprite_mode: str('Single, Multiple or Polygon'),
      pixels_per_unit: num('Sprite pixels per unit'),
      filter_mode: str('Point, Bilinear or Trilinear'),
      compression: str('Uncompressed, Compressed, CompressedHQ or CompressedLQ'),
      wrap_mode: str('Repeat, Clamp, Mirror or MirrorOnce'),
      max_size: num('Max texture size, e.g. 2048'),
      alpha_is_transparency: bool(''),
      mip_maps: bool(''),
    },
    required: ['path'],
    timeout: 60000,
  },
  {
    name: 'unity_list_assets', action: 'list_assets',
    description: 'List project assets using AssetDatabase filter syntax, e.g. "t:Prefab" or "t:Material stone".',
    properties: {
      filter: str('Filter string; empty lists everything'),
      folder: str('Restrict to this folder, e.g. "Assets/Art"'),
      limit: num('Max results (default 100)'),
    },
  },
  {
    name: 'unity_delete_asset', action: 'delete_asset',
    description: 'Permanently delete one or more assets from the project. Not undoable with Ctrl+Z.',
    properties: {
      path: str('Asset path'),
      paths: { type: 'array', items: { type: 'string' }, description: 'Several asset paths' },
    },
  },

  // Scenes
  {
    name: 'unity_save_scene', action: 'save_scene',
    description: 'Save the open scenes, or save the active scene to a new path.',
    properties: { path: str('Optional: save as, e.g. "Assets/Scenes/Main.unity"') },
  },
  {
    name: 'unity_open_scene', action: 'open_scene',
    description: 'Open a scene. Refuses to discard unsaved work unless save or discard is set.',
    properties: {
      path: str('Scene asset path'),
      additive: bool('Load alongside the current scene (default false)'),
      save: bool('Save unsaved changes first'),
      discard: bool('Throw unsaved changes away'),
    },
    required: ['path'],
    timeout: 60000,
  },
  {
    name: 'unity_new_scene', action: 'new_scene',
    description: 'Create a new scene, optionally empty and optionally saved straight away.',
    properties: {
      empty: bool('No default camera or light (default false)'),
      path: str('Optional: save it here immediately'),
      save: bool('Save unsaved changes first'),
      discard: bool('Throw unsaved changes away'),
    },
    timeout: 60000,
  },
  {
    name: 'unity_set_lighting', action: 'set_lighting',
    description: 'Set the scene\'s ambient light, skybox and fog.',
    properties: {
      ambient_color: { description: '[r,g,b] in 0-1 or "#RRGGBB"' },
      ambient_intensity: num('Usually around 1'),
      skybox: str('Skybox material asset path'),
      fog: bool('Enable fog'),
      fog_color: { description: '[r,g,b] in 0-1' },
      fog_density: num('e.g. 0.01'),
    },
  },

  // Project and Editor
  {
    name: 'unity_install_package', action: 'install_package',
    description: 'Add a Unity package, e.g. com.unity.probuilder (level blockout), com.unity.cinemachine, com.unity.inputsystem, com.unity.2d.animation. Without a version the Package Manager resolves the newest compatible one in the background.',
    properties: { name: str('Package id'), version: str('Optional exact version') },
    required: ['name'],
    timeout: 120000,
  },
  {
    name: 'unity_list_packages', action: 'list_packages',
    description: 'List the packages this project depends on.',
    properties: {},
  },
  {
    name: 'unity_compile_status', action: 'compile_status',
    description: 'Whether Unity is compiling, plus the errors and warnings from the last compile. Call this after writing a script, before using the new type.',
    properties: { refresh: bool('Refresh the AssetDatabase first (default false)') },
    timeout: 30000,
  },
  {
    name: 'unity_execute_menu_item', action: 'execute_menu_item',
    description: 'Run any Editor menu item by its exact path, e.g. "GameObject/Align With View". The escape hatch for features without a dedicated tool. Avoid items that open a modal dialog: they block the Editor.',
    properties: { path: str('Menu path') },
    required: ['path'],
    timeout: 60000,
  },
  {
    name: 'unity_capture_screenshot', action: 'capture_screenshot',
    description: 'Render a camera to a PNG on disk and return its path, so the image can then be read and looked at. Defaults to the main camera, or pass scene_view true for the editor viewport.',
    properties: {
      path: str('Output file; defaults to <project>/ClaudeCaptures/capture-<time>.png'),
      width: num('Default 1280'),
      height: num('Default 720'),
      camera: str('GameObject path of the camera to use'),
      scene_view: bool('Capture the Scene view camera instead'),
    },
    timeout: 60000,
  },
  {
    name: 'unity_set_play_mode', action: 'set_play_mode',
    description: 'Enter or exit Play Mode.',
    properties: { play: bool('true = enter, false = stop') },
    required: ['play'],
  },
  {
    name: 'unity_step_frames', action: 'step_frames',
    description: 'Advance Play Mode by a number of frames. Essential for testing: the Editor only runs the game on its own while its window has OS focus, so a scripted test that drives Unity from outside sees a frozen game unless it steps. Stepping is also deterministic.',
    properties: { frames: num('How many frames (default 60, max 20000). 50 frames is about one second.') },
    timeout: 300000,
  },
  {
    name: 'unity_build_player', action: 'build_player',
    description: 'Build the project. Long-running: can take several minutes.',
    properties: {
      target: str('StandaloneWindows64 (default), StandaloneOSX, StandaloneLinux64, Android, iOS, WebGL'),
      path: str('Output path, e.g. "Builds/Windows/Game.exe" (relative to the project)'),
      scenes: { type: 'array', items: { type: 'string' }, description: 'Scene asset paths; defaults to the enabled scenes in Build Settings' },
      development: bool('Development build with debugging (default false)'),
    },
    required: ['path'],
    timeout: 900000,
  },
  {
    name: 'unity_get_logs', action: 'get_logs',
    description: 'Recent Unity console messages.',
    properties: {
      count: num('How many entries (default 20)'),
      errors_only: bool('Only errors, assertions and exceptions (default false)'),
    },
  },
];

const TOOLS = SPEC.map((tool) => ({
  name: tool.name,
  description: tool.description,
  inputSchema: {
    type: 'object',
    properties: tool.properties,
    ...(tool.required ? { required: tool.required } : {}),
  },
}));

const ROUTES = new Map(SPEC.map((tool) => [tool.name, tool]));

// ── Server ──────────────────────────────────────────────────────────────────

const server = new Server(
  { name: 'claude-unity', version: '2.0.0' },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: TOOLS }));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args = {} } = request.params;
  const tool = ROUTES.get(name);

  if (!tool) {
    return { content: [{ type: 'text', text: `Unknown tool: ${name}` }], isError: true };
  }

  const result = await callUnity(tool.action, args, tool.timeout || DEFAULT_TIMEOUT);
  return {
    content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
    isError: result.success === false,
  };
});

const transport = new StdioServerTransport();
await server.connect(transport);
