const fs = require('node:fs');
const path = require('node:path');
const {validUpdateVersion}=require('./scripts/lib/update-versions.cjs');
const {writeOwnedFiles}=require('./scripts/owned-files.cjs');
function stage(name) { const value=process.env[name];if(!value||!path.isAbsolute(value)||!fs.statSync(value).isDirectory())throw new Error(`A staged absolute ${name} is required`);return value; }
const frontend=stage('AHK_FRONTEND_DIST');
const backend=stage('AHK_BACKEND_DIST');
const output=process.env.AHK_PACKAGE_OUT;
if(!output||!path.isAbsolute(output))throw new Error('An absolute AHK_PACKAGE_OUT is required');
const releaseVersion=process.env.AHK_RELEASE_VERSION||require('./package.json').version;
if(!validUpdateVersion(releaseVersion))throw new Error('Invalid release version');
const sourceCommit=process.env.AHK_SOURCE_COMMIT||null;
if(sourceCommit!==null&&!/^[0-9a-f]{40}$/.test(sourceCommit))throw new Error('Invalid source commit');
if(!fs.existsSync(path.join(backend,'update','AhkManager.UpdateHost.exe')))throw new Error('The standalone update host must be staged under backend/update');
const signed=!!process.env.CSC_LINK;
const publisherName=process.env.AHK_PUBLISHER_NAME||null;
if(publisherName!==null&&(!publisherName.trim()||publisherName.length>1024||/[\0\r\n]/.test(publisherName)))throw new Error('Invalid update publisher name');
module.exports={
  appId:'Bezoro.AhkManager.Electron',productName:'AHK Manager',executableName:'Bezoro.AhkManager',
  buildVersion:releaseVersion,buildNumber:'0',
  electronVersion:'45.0.0',electronDist:path.join(__dirname,'node_modules/electron/dist'),
  directories:{output,buildResources:frontend},
  asar:true,npmRebuild:false,nodeGypRebuild:false,buildDependenciesFromSource:false,
  files:[{from:frontend,to:'dist',filter:['main/**','preload/**','renderer/**','licenses/**','assets.json','build-info.json']},'!node_modules/**','!src/**','!tests/**','!scripts/**'],
  extraFiles:[{from:backend,to:'backend',filter:['**/*']}],
  extraResources:[{from:path.join(frontend,'tray.png'),to:'tray.png'}],
  extraMetadata:{version:releaseVersion,updateProtocol:1,sourceCommit,dependencies:{},devDependencies:{}},
  electronFuses:{runAsNode:false,enableNodeOptionsEnvironmentVariable:false,enableNodeCliInspectArguments:false,enableEmbeddedAsarIntegrityValidation:true,onlyLoadAppFromAsar:true},
  forceCodeSigning:signed,
  win:{target:[{target:'nsis',arch:['x64']}],icon:path.join(frontend,'app.ico'),requestedExecutionLevel:'asInvoker',signAndEditExecutable:true,verifyUpdateCodeSignature:signed||publisherName!==null,...(publisherName!==null?{signtoolOptions:{publisherName:[publisherName]}}:{})},
  nsis:{oneClick:false,perMachine:false,packElevateHelper:false,allowElevation:false,allowToChangeInstallationDirectory:true,deleteAppDataOnUninstall:false,createDesktopShortcut:false,createStartMenuShortcut:true,runAfterFinish:false,uninstallerIcon:path.join(frontend,'app.ico'),include:path.join(__dirname,'scripts/installer.nsh'),artifactName:'AHK-Manager-${version}-Setup-x64.${ext}',installerLanguages:['en_US'],language:'1033'},
  publish:{provider:'github',owner:'bezoro',repo:'ahk-manager',private:false,...(publisherName!==null?{publisherName:[publisherName]}:{})},
  afterSign:context=>writeOwnedFiles(context,output,path.join(frontend,'app.ico')),
};
