import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import test, { type TestContext } from 'node:test';

const requireTool = createRequire(path.resolve('package.json'));
const { verifyBoundaries } = requireTool('./scripts/verify-boundaries.cjs') as {
  verifyBoundaries(root: string): string[];
};

function fixture(t: TestContext) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-boundaries-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const write = (name: string, source: string) => {
    const target = path.join(root, name);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, source);
  };
  for (const name of [
    'Core/AhkManager.Core.csproj',
    'Backend/AhkManager.Backend.csproj',
    'Tests.Backend/Tests.Backend.csproj',
    'UpdateHost/AhkManager.UpdateHost.csproj',
    'AgentCli/ahk-manager.csproj',
    'Tests.AgentCli/Tests.AgentCli.csproj',
  ])
    write(name, fs.readFileSync(path.resolve('..', name), 'utf8'));
  write('Presentation.Electron/src/renderer/index.ts', 'export {};');
  write(
    'Presentation.Electron/src/shared/contracts.ts',
    'export interface AhkManagerApi { status(): Promise<void>; }',
  );
  write(
    'Presentation.Electron/src/preload/index.ts',
    `import { contextBridge, ipcRenderer } from 'electron';
import type { AhkManagerApi } from '../shared/contracts.js';
const api: AhkManagerApi = { status: () => ipcRenderer.invoke('status') };
contextBridge.exposeInMainWorld('ahkManager', Object.freeze(api));`,
  );
  const check = () => verifyBoundaries(path.join(root, 'Presentation.Electron'));
  const append = (name: string, input: string) =>
    write(
      name,
      fs
        .readFileSync(path.join(root, name), 'utf8')
        .replace('</Project>', `<ItemGroup>${input}</ItemGroup>\n</Project>`),
    );
  return { root, check, append, write };
}

test('architecture checks cover the current CLI projects and their exact generated/source links', (t) => {
  assert.deepEqual(fixture(t).check(), []);
});

test('a missing CLI project cannot disappear from the architecture check', (t) => {
  const f = fixture(t);
  fs.unlinkSync(path.join(f.root, 'AgentCli/ahk-manager.csproj'));
  assert.deepEqual(f.check(), ['Missing active project: AgentCli/ahk-manager.csproj']);
});

test('CLI cannot acquire a host dependency outside its declared project boundary', (t) => {
  const f = fixture(t);
  f.append(
    'AgentCli/ahk-manager.csproj',
    '<ProjectReference Include="../Backend/AhkManager.Backend.csproj" />',
  );
  assert.deepEqual(f.check(), [
    'AgentCli/ahk-manager.csproj: ProjectReference crosses the active project boundary: ../Backend/AhkManager.Backend.csproj',
  ]);
});

test('CLI tests cannot link arbitrary host source or unrecognized MSBuild properties', (t) => {
  const f = fixture(t);
  f.append('Tests.AgentCli/Tests.AgentCli.csproj', '<Compile Include="../Backend/Program.cs" />');
  f.append('AgentCli/ahk-manager.csproj', '<Compile Include="$(OtherSource)AgentCatalog.g.cs" />');
  const errors = f.check();
  assert.equal(errors.length, 2);
  assert.ok(
    errors.some(
      (error) => error.includes('Tests.AgentCli/') && error.includes('../Backend/Program.cs'),
    ),
  );
  assert.ok(
    errors.some((error) => error.includes('AgentCli/') && error.includes('$(OtherSource)')),
  );
});

test('Core remains independent of host references when new CLI projects are checked', (t) => {
  const f = fixture(t);
  f.append(
    'Core/AhkManager.Core.csproj',
    '<ProjectReference Include="../AgentCli/ahk-manager.csproj" />',
  );
  assert.ok(
    f.check().some((error) => error === 'Core must not reference host or presentation projects.'),
  );
});
