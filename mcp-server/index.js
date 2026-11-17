import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { CallToolRequestSchema, ListToolsRequestSchema } from '@modelcontextprotocol/sdk/types.js';

const UNITY_URL = 'http://localhost:6400';

async function callUnity(action, params = {}) {
  try {
    const res = await fetch(`${UNITY_URL}/command`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Claude-Unity-Bridge': '1' },
      body: JSON.stringify({ action, params }),
      signal: AbortSignal.timeout(15000),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return await res.json();
  } catch (err) {
    return { success: false, error: `Unity unreachable: ${err.message}` };
  }
}

const TOOLS = [
  {
    name: 'unity_status',
    description: 'Check if Unity Editor is connected. Returns scene name, play state, Unity version.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'unity_get_hierarchy',
    description: 'Get the full GameObject hierarchy of the active Unity scene.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'unity_create_gameobject',
    description: 'Create a new GameObject in the scene. Can create empty objects or Unity primitives.',
    inputSchema: {
      type: 'object',
      required: ['name'],
      properties: {
        name:      { type: 'string', description: 'Name of the new GameObject' },
        primitive: { type: 'string', description: 'Optional: Cube, Sphere, Capsule, Cylinder, Plane, Quad' },
        parent:    { type: 'string', description: 'Optional: hierarchy path of the parent GameObject' },
        position:  { type: 'array', items: { type: 'number' }, description: '[x, y, z]' },
      },
    },
  },
  {
    name: 'unity_delete_gameobject',
    description: 'Delete a GameObject from the scene by name or hierarchy path.',
    inputSchema: {
      type: 'object',
      required: ['path'],
      properties: {
        path: { type: 'string', description: 'Name or path (e.g. "Canvas/Panel/Button")' },
      },
    },
  },
  {
    name: 'unity_write_script',
    description: 'Write or overwrite a C# script in Assets/Scripts/. Triggers a recompile.',
    inputSchema: {
      type: 'object',
      required: ['filename', 'content'],
      properties: {
        filename:  { type: 'string', description: 'e.g. "PlayerController.cs"' },
        content:   { type: 'string', description: 'Full C# source code' },
        subfolder: { type: 'string', description: 'Optional subfolder inside Assets/Scripts/' },
      },
    },
  },
  {
    name: 'unity_add_component',
    description: 'Add a Unity component to a GameObject (e.g. Rigidbody, BoxCollider, AudioSource).',
    inputSchema: {
      type: 'object',
      required: ['gameobject', 'component'],
      properties: {
        gameobject: { type: 'string', description: 'Name or path of the target GameObject' },
        component:  { type: 'string', description: 'Component type name, e.g. "Rigidbody"' },
      },
    },
  },
  {
    name: 'unity_set_transform',
    description: 'Set position, rotation (Euler degrees), and/or scale of a GameObject.',
    inputSchema: {
      type: 'object',
      required: ['gameobject'],
      properties: {
        gameobject: { type: 'string' },
        position:   { type: 'array', items: { type: 'number' }, description: '[x, y, z]' },
        rotation:   { type: 'array', items: { type: 'number' }, description: '[x, y, z] Euler degrees' },
        scale:      { type: 'array', items: { type: 'number' }, description: '[x, y, z]' },
      },
    },
  },
  {
    name: 'unity_set_play_mode',
    description: 'Enter or exit Play Mode in the Unity Editor.',
    inputSchema: {
      type: 'object',
      required: ['play'],
      properties: {
        play: { type: 'boolean', description: 'true = enter play mode, false = stop' },
      },
    },
  },
  {
    name: 'unity_save_scene',
    description: 'Save all open scenes in the Unity Editor.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'unity_get_logs',
    description: 'Get recent Unity console logs (errors, warnings, info).',
    inputSchema: {
      type: 'object',
      properties: {
        count: { type: 'number', description: 'Number of recent entries to return (default: 20)' },
      },
    },
  },
];

const server = new Server(
  { name: 'claude-unity', version: '1.0.0' },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: TOOLS }));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args = {} } = request.params;

  const routes = {
    unity_status:           () => callUnity('get_status'),
    unity_get_hierarchy:    () => callUnity('get_hierarchy'),
    unity_create_gameobject:() => callUnity('create_gameobject', args),
    unity_delete_gameobject:() => callUnity('delete_gameobject', args),
    unity_write_script:     () => callUnity('write_script', args),
    unity_add_component:    () => callUnity('add_component', args),
    unity_set_transform:    () => callUnity('set_transform', args),
    unity_set_play_mode:    () => callUnity('set_play_mode', args),
    unity_save_scene:       () => callUnity('save_scene'),
    unity_get_logs:         () => callUnity('get_logs', args),
  };

  const handler = routes[name];
  if (!handler) {
    return { content: [{ type: 'text', text: `Unknown tool: ${name}` }], isError: true };
  }

  const result = await handler();
  return {
    content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
    isError: result.success === false,
  };
});

const transport = new StdioServerTransport();
await server.connect(transport);
