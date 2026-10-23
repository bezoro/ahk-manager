const fs=require('node:fs'),path=require('node:path'),esbuild=require('esbuild');
const index=process.argv.indexOf('--out'),out=process.argv[index+1];
if(index<0||!path.isAbsolute(out)||fs.existsSync(out))throw new Error('Supply a fresh absolute --out harness staging directory');fs.mkdirSync(out,{recursive:true});
(async()=>{
  for(const name of ['presentation-harness','fixture-preload'])await esbuild.build({entryPoints:[`tests/main/${name}.ts`],outfile:path.join(out,`${name}.cjs`),bundle:true,platform:'node',format:'cjs',target:'es2023',external:['electron'],logLevel:'info'});
  if(fs.existsSync('tests/renderer/scenarios.ts'))await esbuild.build({entryPoints:['tests/renderer/scenarios.ts'],outfile:path.join(out,'scenarios.js'),bundle:true,platform:'browser',format:'iife',globalName:'AhkRendererScenarios',target:'es2023',logLevel:'info'});
})().catch(error=>{console.error(error);process.exitCode=1;});
