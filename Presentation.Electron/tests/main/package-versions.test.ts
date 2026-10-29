import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const { validUpdateVersion } = require('../../scripts/lib/update-versions.cjs') as {
  validUpdateVersion(value: unknown): boolean;
};

test('packaging accepts bounded test identities and rejects ambiguous versions before building', () => {
  for (const version of ['1.2.0', '1.3.0-test.18.1.2', '2147483647.0.0-test.1.1.1'])
    assert.equal(validUpdateVersion(version), true);
  for (const version of [
    '1.3.0-test.018.1.2',
    '1.3.0-test.18.0.2',
    '1.3.0-test.18.1.0',
    '1.3.0-test.18.1',
    '1.3.0-beta.1',
    '1.3.0-test.18.1.2+extra',
    '2147483648.0.0',
    '1.3.0-test.2147483648.1.2',
    '1.3.0-test.18.1.2\n',
    '1.3.0-test.18.1.2/../other',
  ])
    assert.equal(validUpdateVersion(version), false, version);
});

test('the production Windows resource editor preserves full test FileVersion with numeric ProductVersion', async () => {
  const { Platform } = require(path.resolve('node_modules/app-builder-lib')) as {
    Platform: { WINDOWS: unknown };
  };
  const { NtExecutable, NtExecutableResource, Resource } = require(
    path.resolve('node_modules/resedit'),
  ) as typeof import('resedit');
  const { AppInfo } = require(path.resolve('node_modules/app-builder-lib/out/appInfo.js')) as {
    AppInfo: new (info: Record<string, unknown>) => Record<string, unknown>;
  };
  const { WinPackager } = require(
    path.resolve('node_modules/app-builder-lib/out/winPackager.js'),
  ) as {
    WinPackager: { prototype: { signAndEditResources(...args: unknown[]): Promise<void> } };
  };
  const { getAppUpdatePublishConfiguration } = require(
    path.resolve('node_modules/app-builder-lib/out/publish/PublishManager.js'),
  ) as {
    getAppUpdatePublishConfiguration(...args: unknown[]): Promise<Record<string, unknown>>;
  };
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-test-package-version-'));
  const variables = [
    'AHK_FRONTEND_DIST',
    'AHK_BACKEND_DIST',
    'AHK_PACKAGE_OUT',
    'AHK_RELEASE_VERSION',
    'AHK_SOURCE_COMMIT',
    'BUILD_NUMBER',
    'AHK_PUBLISHER_NAME',
    'CSC_LINK',
  ];
  const previous = new Map(variables.map((name) => [name, process.env[name]]));
  try {
    const version = '1.3.0-test.18.1.2';
    const backend = path.join(root, 'backend');
    fs.mkdirSync(path.join(backend, 'update'), { recursive: true });
    fs.writeFileSync(
      path.join(backend, 'update', 'AhkManager.UpdateHost.exe'),
      'inert staged fixture',
    );
    process.env.AHK_FRONTEND_DIST = root;
    process.env.AHK_BACKEND_DIST = backend;
    process.env.AHK_PACKAGE_OUT = path.join(root, 'output');
    process.env.AHK_RELEASE_VERSION = version;
    process.env.AHK_SOURCE_COMMIT = 'a'.repeat(40);
    process.env.BUILD_NUMBER = '999';
    process.env.AHK_PUBLISHER_NAME = 'AHK Manager Test Publisher';
    delete process.env.CSC_LINK;
    const config = require(path.resolve('electron-builder.cjs')) as Record<string, unknown>;
    const appInfo = new AppInfo({
      config,
      metadata: { name: 'ahk-manager', version, author: 'Bezoro' },
    });
    assert.equal(config.forceCodeSigning, false);
    const appUpdate = await getAppUpdatePublishConfiguration(
      {
        appInfo,
        config,
        info: { appInfo, config },
        platform: Platform.WINDOWS,
        platformSpecificBuildOptions: config.win,
        expandMacro: (value: string) => value,
        get signingManager() {
          throw new Error(
            'An unsigned preview must not require signing credentials to preserve the publisher policy.',
          );
        },
      },
      null,
      1,
      false,
    );
    assert.deepEqual(appUpdate.publisherName, ['AHK Manager Test Publisher']);
    const image = path.join(root, 'Manager.exe');
    const initialImage = NtExecutable.createEmpty(false);
    const initialResources = NtExecutableResource.from(initialImage);
    Resource.VersionInfo.create({
      lang: 0x0409,
      fixedInfo: {},
      strings: [
        {
          lang: 0x0409,
          codepage: 1200,
          values: { FileVersion: '45.0.0', ProductVersion: '45.0.0.0' },
        },
      ],
    }).outputToResourceEntries(initialResources.entries);
    initialResources.outputResource(initialImage);
    fs.writeFileSync(image, Buffer.from(initialImage.generate()));
    await WinPackager.prototype.signAndEditResources.call(
      {
        appInfo,
        config,
        platformSpecificBuildOptions: {},
        getIconPath: async () => null,
        signIf: async () => true,
      },
      image,
      1,
      root,
      'Bezoro.AhkManager',
      'asInvoker',
    );
    const resources = NtExecutableResource.from(NtExecutable.from(fs.readFileSync(image)));
    const info = Resource.VersionInfo.fromEntries(resources.entries)[0]!;
    const strings = info.getStringValues({ lang: 0x0409, codepage: 1200 });
    assert.equal(strings.FileVersion, version);
    assert.equal(strings.ProductVersion, '1.3.0.0');
  } finally {
    for (const [name, value] of previous) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
    fs.rmSync(root, { recursive: true, force: true });
  }
});
