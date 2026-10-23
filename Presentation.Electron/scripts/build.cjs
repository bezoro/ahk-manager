const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');
const crypto = require('node:crypto');
const esbuild = require('esbuild');
const output = process.argv[process.argv.indexOf('--out') + 1];
if (!process.argv.includes('--out') || !path.isAbsolute(output)) throw new Error('Supply an absolute --out staging directory');
if (fs.existsSync(output)) throw new Error('Use a fresh output directory; previous build evidence must remain intact');
fs.mkdirSync(output, { recursive: true });
const renderer = path.join(output, 'renderer');
fs.mkdirSync(renderer);
const common = { bundle: true, sourcemap: false, logLevel: 'info', target: 'es2023', legalComments: 'external' };
(async () => {
  await esbuild.build({ ...common, entryPoints: ['src/main/index.ts'], outfile: path.join(output, 'main/index.cjs'), platform: 'node', format: 'cjs', external: ['electron'] });
  await esbuild.build({ ...common, entryPoints: ['src/preload/index.ts'], outfile: path.join(output, 'preload/index.cjs'), platform: 'node', format: 'cjs', external: ['electron'] });
  await esbuild.build({ ...common, entryPoints: ['src/renderer/main.tsx'], outfile: path.join(renderer, 'app.js'), platform: 'browser', format: 'iife', minify: true, define: { 'process.env.NODE_ENV': '"production"' } });
  fs.copyFileSync('src/renderer/index.html', path.join(renderer, 'index.html'));
  if (!fs.existsSync(path.join(renderer, 'app.css'))) fs.copyFileSync('src/renderer/styles.css', path.join(renderer, 'app.css'));
  const assets = fs.readdirSync(renderer).filter(name => /\.(html|js|css|svg|png|woff2)$/.test(name));
  fs.writeFileSync(path.join(output, 'assets.json'), JSON.stringify(assets));
  const size = 256, pixels = Buffer.alloc(size * (size * 4 + 1));
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const offset = y * (size * 4 + 1) + 1 + x * 4;
    const px=x*64/size,py=y*64/size;
    const inside = px >= 3 && px < 61 && py >= 3 && py < 61 && !((px < 10 || px >= 54) && (py < 10 || py >= 54));
    const glyph = inside && ((px >= 16 && px < 23 && py >= 15 && py < 49) || (px >= 41 && px < 48 && py >= 15 && py < 49) || (px >= 23 && px < 41 && py >= 28 && py < 35));
    pixels.set(glyph ? [255, 255, 255, 255] : inside ? [22, 115, 72, 255] : [0, 0, 0, 0], offset);
  }
  const crc = data => { let v = 0xffffffff; for (const b of data) { v ^= b; for (let i = 0; i < 8; i++) v = (v >>> 1) ^ ((v & 1) ? 0xedb88320 : 0); } return (v ^ 0xffffffff) >>> 0; };
  const chunk = (type, data) => { const body = Buffer.concat([Buffer.from(type), data]), result = Buffer.alloc(data.length + 12); result.writeUInt32BE(data.length); body.copy(result, 4); result.writeUInt32BE(crc(body), result.length - 4); return result; };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(size); ihdr.writeUInt32BE(size, 4); ihdr[8] = 8; ihdr[9] = 6;
  const png = Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]),chunk('IHDR',ihdr),chunk('IDAT',zlib.deflateSync(pixels)),chunk('IEND',Buffer.alloc(0))]);
  fs.writeFileSync(path.join(output, 'tray.png'), png);
  const ico = Buffer.alloc(22); ico.writeUInt16LE(1,2); ico.writeUInt16LE(1,4); ico[6]=size===256?0:size;ico[7]=size===256?0:size;ico.writeUInt16LE(1,10);ico.writeUInt16LE(32,12);ico.writeUInt32LE(png.length,14);ico.writeUInt32LE(22,18);
  fs.writeFileSync(path.join(output, 'app.ico'), Buffer.concat([ico,png]));
  fs.writeFileSync(path.join(output,'build-info.json'),JSON.stringify({versions:process.versions,build:'production',sourceMaps:false,rendererAssets:assets},null,2));
  const lock=JSON.parse(fs.readFileSync('package-lock.json','utf8'));
  const inventory=[];
  for(const [location,entry] of Object.entries(lock.packages)){
    if(!location||!location.startsWith('node_modules/'))continue;
    const directory=path.resolve(location),manifest=path.join(directory,'package.json');if(!fs.existsSync(manifest))continue;
    const pkg=JSON.parse(fs.readFileSync(manifest,'utf8'));
    const key=crypto.createHash('sha256').update(location).digest('hex').slice(0,16);
    const licenses=[];
    for(const name of fs.readdirSync(directory).filter(name=>/^(licen[sc]e|copying|notice)(\.|$)/i.test(name))){
      const source=path.join(directory,name),stat=fs.statSync(source);if(!stat.isFile()||stat.size>2*1024*1024)continue;
      const target=path.join('licenses',key,name);fs.mkdirSync(path.dirname(path.join(output,target)),{recursive:true});fs.copyFileSync(source,path.join(output,target));licenses.push(target.replaceAll('\\','/'));
    }
    inventory.push({location,name:pkg.name,version:pkg.version,license:pkg.license??null,integrity:entry.integrity??null,dev:!!entry.dev,licenseFiles:licenses});
  }
  fs.mkdirSync(path.join(output,'licenses'),{recursive:true});
  fs.writeFileSync(path.join(output,'licenses/dependency-inventory.json'),JSON.stringify({packageLockSha256:crypto.createHash('sha256').update(fs.readFileSync('package-lock.json')).digest('hex'),packages:inventory},null,2));
})().catch(error => { console.error(error); process.exitCode = 1; });
