const fs = require('node:fs');
const path = require('node:path');
const {writeOwnedFiles}=require('./scripts/owned-files.cjs');
function stage(name) { const value=process.env[name];if(!value||!path.isAbsolute(value)||!fs.statSync(value).isDirectory())throw new Error(`A staged absolute ${name} is required`);return value; }
const frontend=stage('AHK_FRONTEND_DIST');
const backend=stage('AHK_BACKEND_DIST');
const output=process.env.AHK_PACKAGE_OUT;
if(!output||!path.isAbsolute(output))throw new Error('An absolute AHK_PACKAGE_OUT is required');
const releaseVersion=process.env.AHK_RELEASE_VERSION||require('./package.json').version;
if(!/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(releaseVersion))throw new Error('Invalid release version');
const sourceCommit=process.env.AHK_SOURCE_COMMIT||null;
if(sourceCommit!==null&&!/^[0-9a-f]{40}$/.test(sourceCommit))throw new Error('Invalid source commit');
if(!fs.existsSync(path.join(backend,'update','AhkManager.UpdateHost.exe')))throw new Error('The standalone update host must be staged under backend/update');
const signed=!!process.env.CSC_LINK;
module.exports={
  appId:'Bezoro.AhkManager.Electron',productName:'AHK Manager',executableName:'Bezoro.AhkManager',
  electronVersion:'45.0.0',electronDist:path.join(__dirname,'node_modules/electron/dist'),
  directories:{output,buildResources:frontend},
  asar:true,npmRebuild:false,nodeGypRebuild:false,buildDependenciesFromSource:false,
  files:[{from:frontend,to:'dist',filter:['main/**','preload/**','renderer/**','licenses/**','assets.json','build-info.json']},'!node_modules/**','!src/**','!tests/**','!scripts/**'],
  extraFiles:[{from:backend,to:'backend',filter:['**/*']}],
  extraResources:[{from:path.join(frontend,'tray.png'),to:'tray.png'}],
  extraMetadata:{version:releaseVersion,updateProtocol:1,sourceCommit,dependencies:{},devDependencies:{}},
  electronFuses:{runAsNode:false,enableNodeOptionsEnvironmentVariable:false,enableNodeCliInspectArguments:false,enableEmbeddedAsarIntegrityValidation:true,onlyLoadAppFromAsar:true},
  forceCodeSigning:signed,
  win:{target:[{target:'nsis',arch:['x64']}],icon:path.join(frontend,'app.ico'),requestedExecutionLevel:'asInvoker',signAndEditExecutable:true,verifyUpdateCodeSignature:signed,...(signed&&process.env.AHK_PUBLISHER_NAME?{publisherName:process.env.AHK_PUBLISHER_NAME}:{})},
  nsis:{oneClick:false,perMachine:false,packElevateHelper:false,allowElevation:false,allowToChangeInstallationDirectory:true,deleteAppDataOnUninstall:false,createDesktopShortcut:false,createStartMenuShortcut:true,runAfterFinish:false,uninstallerIcon:path.join(frontend,'app.ico'),include:path.join(__dirname,'scripts/installer.nsh'),artifactName:'AHK-Manager-${version}-Setup-x64.${ext}',installerLanguages:['pt_BR'],language:'1046'},
  publish:{provider:'github',owner:'bezoro',repo:'ahk-manager',private:false},
  afterSign:context=>writeOwnedFiles(context,output,path.join(frontend,'app.ico')),
};
