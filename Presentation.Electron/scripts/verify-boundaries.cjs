const fs = require('node:fs');
const path = require('node:path');
const { parsers } = require('prettier/plugins/typescript');

const rendererPackages = new Set([
  'react',
  'react-dom',
  '@fluentui/react-components',
  '@fluentui/react-icons',
]);

function filesIn(directory, extensions) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    if (['bin', 'obj', 'node_modules'].includes(entry.name)) return [];
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) return filesIn(file, extensions);
    return entry.isFile() && extensions.some((extension) => file.endsWith(extension)) ? [file] : [];
  });
}

function isWithin(directory, file) {
  const relative = path.relative(directory, file);
  return (
    relative === '' ||
    (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative))
  );
}

function walk(node, visit) {
  if (!node || typeof node.type !== 'string') return;
  visit(node);
  for (const value of Object.values(node)) {
    if (Array.isArray(value)) value.forEach((child) => walk(child, visit));
    else if (value && typeof value === 'object') walk(value, visit);
  }
}

function member(node, object, property) {
  return (
    node?.type === 'MemberExpression' &&
    !node.computed &&
    node.object.name === object &&
    node.property.name === property
  );
}

function verifyBoundaries(electronRoot) {
  const repositoryRoot = path.resolve(electronRoot, '..');
  const sourceRoot = path.join(electronRoot, 'src');
  const errors = [];
  const sources = new Map();
  const report = (file, node, message) =>
    errors.push(`${path.relative(repositoryRoot, file)}:${node.loc?.start.line ?? 1}: ${message}`);

  function checkImport(file, node, layer, specifier) {
    if (specifier.startsWith('.')) {
      const target = path.resolve(path.dirname(file), specifier);
      const allowed = layer === 'shared' ? ['shared'] : [layer, 'shared'];
      if (!allowed.some((directory) => isWithin(path.join(sourceRoot, directory), target))) {
        report(
          file,
          node,
          `${layer} may import only ${allowed.join(' or ')} source files: ${specifier}`,
        );
      }
      return;
    }
    const packageName = specifier
      .split('/')
      .slice(0, specifier.startsWith('@') ? 2 : 1)
      .join('/');
    if (layer === 'renderer' && rendererPackages.has(packageName)) return;
    if (layer === 'preload' && specifier === 'electron') return;
    report(
      file,
      node,
      `${layer} cannot import ${specifier}; keep platform access in main and expose a typed preload method.`,
    );
  }

  for (const layer of ['renderer', 'shared', 'preload']) {
    const directory = path.join(sourceRoot, layer);
    if (!fs.existsSync(directory)) {
      errors.push(`Missing source layer: ${path.relative(repositoryRoot, directory)}`);
      continue;
    }
    for (const file of filesIn(directory, ['.ts', '.tsx'])) {
      let ast;
      try {
        ast = parsers.typescript.parse(fs.readFileSync(file, 'utf8'), { filepath: file });
      } catch (error) {
        errors.push(`${path.relative(repositoryRoot, file)}: ${error.message}`);
        continue;
      }
      sources.set(file, ast);
      walk(ast, (node) => {
        let specifier;
        if (
          [
            'ImportDeclaration',
            'ExportNamedDeclaration',
            'ExportAllDeclaration',
            'TSImportType',
          ].includes(node.type)
        )
          specifier = node.source;
        else if (node.type === 'TSImportEqualsDeclaration')
          specifier = node.moduleReference.expression;
        else if (node.type === 'ImportExpression') {
          specifier = node.source;
          if (typeof specifier?.value !== 'string')
            report(file, node, `${layer} imports must use a literal module name.`);
        } else if (node.type === 'CallExpression' && node.callee.name === 'require') {
          report(file, node, `${layer} must use static ES imports instead of require().`);
        }
        if (typeof specifier?.value === 'string') checkImport(file, node, layer, specifier.value);
      });
    }
  }

  verifyPreload(sources, sourceRoot, report, errors);
  verifyCore(repositoryRoot, errors);
  verifyActiveProjectInputs(repositoryRoot, errors);
  return errors;
}

function verifyPreload(sources, sourceRoot, report, errors) {
  const exposures = [];
  const variables = new Map();
  for (const [file, ast] of sources) {
    if (!isWithin(path.join(sourceRoot, 'preload'), file)) continue;
    for (const statement of ast.body) {
      if (statement.type !== 'VariableDeclaration') continue;
      for (const declaration of statement.declarations) {
        if (declaration.id.type === 'Identifier')
          variables.set(`${file}:${declaration.id.name}`, declaration);
      }
    }
    walk(ast, (node) => {
      if (node.type === 'ImportDeclaration' && node.source.value === 'electron') {
        if (
          node.specifiers.some(
            (specifier) =>
              specifier.type !== 'ImportSpecifier' ||
              specifier.imported.name !== specifier.local.name ||
              !['contextBridge', 'ipcRenderer'].includes(specifier.imported.name),
          )
        ) {
          report(
            file,
            node,
            'Preload may import only the named contextBridge and ipcRenderer APIs from Electron.',
          );
        }
      }
      if (
        node.type === 'CallExpression' &&
        node.callee.type === 'MemberExpression' &&
        node.callee.object.name === 'contextBridge'
      ) {
        if (!member(node.callee, 'contextBridge', 'exposeInMainWorld'))
          report(file, node, 'Preload exposes only the ahkManager API in the main world.');
        else exposures.push({ file, node });
      }
    });
  }
  if (exposures.length !== 1)
    errors.push('Preload must have exactly one contextBridge.exposeInMainWorld call.');
  for (const { file, node } of exposures) {
    const [name, value] = node.arguments;
    if (node.arguments.length !== 2 || name?.value !== 'ahkManager')
      report(file, node, 'The only preload global is ahkManager.');
    if (
      value?.type !== 'CallExpression' ||
      !member(value.callee, 'Object', 'freeze') ||
      value.arguments.length !== 1 ||
      value.arguments[0].type !== 'Identifier'
    ) {
      report(file, node, 'Expose Object.freeze(api), with api explicitly typed as AhkManagerApi.');
      continue;
    }
    const api = variables.get(`${file}:${value.arguments[0].name}`);
    const annotation = api?.id.typeAnnotation?.typeAnnotation;
    if (
      annotation?.type !== 'TSTypeReference' ||
      annotation.typeName.name !== 'AhkManagerApi' ||
      api.init?.type !== 'ObjectExpression'
    ) {
      report(file, node, 'The exposed API must be an explicit AhkManagerApi object literal.');
      continue;
    }
    for (const property of api.init.properties) {
      if (
        property.type !== 'Property' ||
        property.computed ||
        !['ArrowFunctionExpression', 'FunctionExpression'].includes(property.value.type)
      ) {
        report(
          file,
          property,
          'Preload API members must be named methods; raw IPC objects, spreads and computed keys are forbidden.',
        );
      }
    }
  }

  const contract = sources.get(path.join(sourceRoot, 'shared', 'contracts.ts'));
  let api;
  if (contract)
    walk(contract, (node) => {
      if (node.type === 'TSInterfaceDeclaration' && node.id.name === 'AhkManagerApi') api = node;
    });
  if (
    !api ||
    api.extends.length ||
    api.body.body.some((method) => method.type !== 'TSMethodSignature' || method.computed)
  ) {
    errors.push(
      'AhkManagerApi must declare a finite set of named methods without index signatures or inherited APIs.',
    );
  }
}

function verifyCore(repositoryRoot, errors) {
  const coreRoot = path.join(repositoryRoot, 'Core');
  const projectFile = path.join(coreRoot, 'AhkManager.Core.csproj');
  if (!fs.existsSync(projectFile)) {
    errors.push('Missing independent Core/AhkManager.Core.csproj.');
    return;
  }
  const project = fs.readFileSync(projectFile, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
  if (/<ProjectReference\b/i.test(project))
    errors.push('Core must not reference host or presentation projects.');
  if (
    /<Use(?:WPF|WinUI|WindowsForms)>\s*true\s*<\//i.test(project) ||
    /<(?:PackageReference|Reference)\b[^>]*\bInclude\s*=\s*["'](?:Microsoft\.WindowsAppSDK|Microsoft\.UI|PresentationFramework|System\.Windows\.Forms)\b/i.test(
      project,
    )
  ) {
    errors.push('Core must not depend on a presentation framework.');
  }
  for (const include of project.matchAll(/<Compile\b[^>]*\bInclude\s*=\s*["']([^"']+)["']/gi)) {
    if (!isWithin(coreRoot, path.resolve(coreRoot, include[1].replaceAll('\\', path.sep))))
      errors.push(`Core may not link external presentation source: ${include[1]}`);
  }
  for (const file of filesIn(coreRoot, ['.cs'])) {
    const source = fs.readFileSync(file, 'utf8');
    if (
      /^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?(?:Microsoft\.UI|Windows\.UI\.Xaml|System\.Windows|Bezoro\.AhkManager\.(?:WinUI|Presentation))\b/m.test(
        source,
      )
    ) {
      errors.push(
        `${path.relative(repositoryRoot, file)}: Core cannot import presentation namespaces.`,
      );
    }
  }
}

function verifyActiveProjectInputs(repositoryRoot, errors) {
  const projects = new Map([
    ['Core/AhkManager.Core.csproj', []],
    ['Backend/AhkManager.Backend.csproj', ['Core/AhkManager.Core.csproj']],
    [
      'Tests.Backend/Tests.Backend.csproj',
      ['Core/AhkManager.Core.csproj', 'Backend/AhkManager.Backend.csproj'],
    ],
    ['UpdateHost/AhkManager.UpdateHost.csproj', []],
  ]);
  for (const [name, dependencies] of projects) {
    const file = path.join(repositoryRoot, name);
    if (!fs.existsSync(file)) {
      errors.push(`Missing active project: ${name}`);
      continue;
    }
    const directory = path.dirname(file);
    const project = fs.readFileSync(file, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
    for (const include of project.matchAll(
      /<(Compile|ProjectReference)\b[^>]*\bInclude\s*=\s*["']([^"']+)["']/gi,
    )) {
      const [, kind, value] = include;
      // This existing test-only property injects a baseline caption proof fixture in isolated CI.
      if (
        name === 'Tests.Backend/Tests.Backend.csproj' &&
        kind === 'Compile' &&
        value === '$(BaselineCaptionProofSource)'
      )
        continue;
      const target = path.resolve(directory, value.replaceAll('\\', path.sep));
      const allowed =
        kind === 'Compile'
          ? isWithin(directory, target)
          : dependencies.some((dependency) => target === path.join(repositoryRoot, dependency));
      if (!allowed || value.includes('$('))
        errors.push(`${name}: ${kind} crosses the active project boundary: ${value}`);
    }
  }
}

if (require.main === module) {
  const errors = verifyBoundaries(path.resolve(__dirname, '..'));
  if (errors.length) {
    console.error(
      `Architecture boundaries failed:\n${errors.map((error) => `- ${error}`).join('\n')}`,
    );
    process.exitCode = 1;
  } else {
    console.log(
      'Architecture boundaries passed: isolated renderer/shared, limited preload API and independent Core.',
    );
  }
}

module.exports = { verifyBoundaries };
