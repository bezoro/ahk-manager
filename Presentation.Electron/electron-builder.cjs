const fs = require('node:fs');
const path = require('node:path');
const {writeOwnedFiles}=require('./scripts/owned-files.cjs');
function stage(name) { const value=process.env[name];if(!value||!path.isAbsolute(value)||!fs.statSync(value).isDirectory())throw new Error(`A staged absolute ${name} is required`);return value; }
const frontend=stage('AHK_FRONTEND_DIST');
const backend=stage('AHK_BACKEND_DIST');
const output=process.env.AHK_PACKAGE_OUT;
if(!output||!path.isAbsolute(output))throw new Error('An absolute AHK_PACKAGE_OUT is required');
module.exports={
  appId:'Bezoro.AhkManager.Electron',productName:'AHK Manager',executableName:'Bezoro.AhkManager',
  electronVersion:'45.0.0',electronDist:path.join(__dirname,'node_modules/electron/dist'),
  directories:{output,buildResources:frontend},
  asar:true,npmRebuild:false,nodeGypRebuild:false,buildDependenciesFromSource:false,
  files:[{from:frontend,to:'dist',filter:['main/**','preload/**','renderer/**','licenses/**','assets.json','build-info.json']},'!node_modules/**','!src/**','!tests/**','!scripts/**'],
  extraFiles:[{from:backend,to:'backend',filter:['**/*']}],
  extraResources:[{from:path.join(frontend,'tray.png'),to:'tray.png'}],
  extraMetadata:{dependencies:{},devDependencies:{}},
  electronFuses:{runAsNode:false,enableNodeOptionsEnvironmentVariable:false,enableNodeCliInspectArguments:false,enableEmbeddedAsarIntegrityValidation:true,onlyLoadAppFromAsar:true},
  win:{target:[{target:'nsis',arch:['x64']}],icon:path.join(frontend,'app.ico'),requestedExecutionLevel:'asInvoker',signAndEditExecutable:true,verifyUpdateCodeSignature:false},
  nsis:{oneClick:false,perMachine:false,packElevateHelper:false,allowElevation:false,allowToChangeInstallationDirectory:true,deleteAppDataOnUninstall:false,createDesktopShortcut:false,createStartMenuShortcut:true,runAfterFinish:false,uninstallerIcon:path.join(frontend,'app.ico'),include:path.join(__dirname,'scripts/installer.nsh'),artifactName:'AHK-Manager-${version}-Setup-x64.${ext}',installerLanguages:['pt_BR'],language:'1046'},
  publish:null,
  afterSign:context=>writeOwnedFiles(context,output,path.join(frontend,'app.ico')),
};
