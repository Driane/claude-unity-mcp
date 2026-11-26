// Quick check that the Unity bridge is reachable and answering.
//   node smoke.mjs
// Run it with the Unity Editor open and the Editor/ folder installed in the project.

const URL = process.env.CLAUDE_UNITY_URL || 'http://localhost:6400';

async function call(action, params = {}) {
  const res = await fetch(`${URL}/command`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Claude-Unity-Bridge': '1' },
    body: JSON.stringify({ action, params }),
    signal: AbortSignal.timeout(15000),
  });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

try {
  const status = await call('get_status');
  if (!status.success) throw new Error(status.error);

  console.log(`Bridge ${status.bridgeVersion} on Unity ${status.unityVersion}`);
  console.log(`Scene:   ${status.scene || '(unsaved)'}${status.sceneDirty ? ' *modified*' : ''}`);
  console.log(`Objects: ${status.rootObjects} at the scene root`);
  console.log(`State:   ${status.isPlaying ? 'playing' : 'edit mode'}${status.isCompiling ? ', compiling' : ''}`);
  console.log(`Project: ${status.projectPath}`);

  const compile = await call('compile_status');
  console.log(`Compile: ${compile.errorCount} error(s), ${(compile.warnings || []).length} warning(s)`);

  const packages = await call('list_packages');
  console.log(`Packages: ${packages.count}`);

  console.log('\nOK — the bridge is answering.');
} catch (err) {
  console.error(`FAILED: ${err.message}`);
  console.error('\nCheck that:');
  console.error('  1. the Unity Editor is open on your project');
  console.error('  2. unity-plugin/Editor/ is copied into Assets/Editor/');
  console.error('  3. the console shows "[ClaudeUnity] Bridge listening on http://localhost:6400/"');
  process.exit(1);
}
