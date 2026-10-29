using Tests.Backend.Fixtures;
using System.Diagnostics;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Principal;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.WinUI.Tests.Integration;

internal static class OwnedAhkSuite
{
    const string Interpreter = "C:/Program Files/AutoHotkey/v2/AutoHotkey64.exe";
    const string InterpreterHash = "2eab99848ebc07f8ab0cc448bf69af8c700b2fc7fcd1ff8794df743dcc58da51";
    public static async Task RunAsync(string root)
    {
        var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);Console.WriteLine("PASS "+name);}
        Check(File.Exists(Interpreter)&&Hash(Interpreter)==InterpreterHash,"official-interpreter-identity");
        string directory=Path.Combine(root,"AHK fixture espaços 漢字");Directory.CreateDirectory(directory);
        string script=Path.Combine(directory,"owned harmless.ahk");string veto=Path.Combine(directory,"veto harmless.ahk");
        await File.WriteAllTextAsync(script,"#Requires AutoHotkey v2.0\n#SingleInstance Off\n#NoTrayIcon\nPersistent\nSetTimer(() => 0, 1000)\n");
        await File.WriteAllTextAsync(veto,"""
            #Requires AutoHotkey v2.0
            #SingleInstance Off
            #NoTrayIcon
            Persistent
            OnExit((*) => FileExist(A_ScriptDir "\allow-exit") ? 0 : 1)
            SetTimer(() => 0, 1000)
            """);
        using var owner=new FixtureOwner(directory,Interpreter,[script,veto]);
        var observation=new FixtureProcessObservation(directory,[script,veto],Interpreter);
        await using var manager=new ProcessManager(owner,documentedControlsProven:true,observationSource:observation);
        ProcessIdentity? active=null,vetoActive=null;
        try
        {
            var started=await manager.StartAsync(Interpreter,script,false);Check(started.Result.Succeeded&&started.Process is not null,"launch-observed-confirmed: "+started.Result.Outcome+" / "+started.Result.Code+" / "+started.Result.Detail);active=started.Process!.Identity;
            Check(active.ScriptPath==Path.GetFullPath(script)&&active.InterpreterHash==InterpreterHash&&active.ScriptHash==Hash(script),"held-path-and-fingerprint-proof");
            Check(AhkWindowControl.FindWindow(active)!=0,"hidden-ahk-window-proof");
            var duplicate=await manager.StartAsync(Interpreter,script,false);Check(!duplicate.Result.Succeeded,"duplicate-refused");
            var suspend=await manager.ControlAsync(active,ScriptOperation.ToggleSuspend);Check(suspend.Succeeded,"suspend-observed-confirmed");
            var resumed=await manager.ControlAsync(active,ScriptOperation.ToggleSuspend);Check(resumed.Succeeded,"resume-hotkeys-observed-confirmed");
            var pause=await manager.ControlAsync(active,ScriptOperation.TogglePause);Check(!pause.Succeeded&&pause.Code is ReasonCode.CapabilityUnverified or ReasonCode.StateNotObservable,"pause-unobservable-refused");
            using(var held=new NativeProcessIdentity(active.Pid))
            {
                var request=AhkWindowControl.Request(held,active,ScriptOperation.TogglePause);
                Check(request.Outcome==Outcome.Pending&&request.Code==ReasonCode.StateNotObservable,"documented-pause-toggle-dispatched-state-unknown");
                await Task.Delay(100);
                var deliberateSecond=AhkWindowControl.Request(held,active,ScriptOperation.TogglePause);
                Check(deliberateSecond.Outcome==Outcome.Pending&&deliberateSecond.Code==ReasonCode.StateNotObservable,"second-deliberate-toggle-no-state-claim");
            }
            var wrong=await manager.ControlAsync(active with {CreationFileTime=active.CreationFileTime+1},ScriptOperation.Stop);Check(!wrong.Succeeded,"wrong-creation-identity-refused");
            var stop=await manager.ControlAsync(active,ScriptOperation.Stop);Check(stop.Succeeded&&Exited(active),"graceful-wm-close-exit-observed");active=null;
            var again=await manager.StartAsync(Interpreter,script,false);Check(again.Result.Succeeded&&again.Process is not null,"start-after-confirmed-exit");active=again.Process!.Identity;
            var restart=await manager.ControlAsync(active,ScriptOperation.Stop);
            // ManagerService owns full restart sequencing; here prove the OS exit mechanism.
            Check(restart.Succeeded&&Exited(active),"restart-old-target-exit-confirmed");active=null;
            var vetoed=await manager.StartAsync(Interpreter,veto,false);Check(vetoed.Result.Succeeded&&vetoed.Process is not null,"veto-fixture-started");vetoActive=vetoed.Process!.Identity;
            var vetoResult=await manager.ControlAsync(vetoActive,ScriptOperation.Stop);Check(!vetoResult.Succeeded&&!Exited(vetoActive),"onexit-veto-is-not-success");
            await File.WriteAllTextAsync(Path.Combine(directory,"allow-exit"),"owned fixture cleanup");
            var final=await manager.ControlAsync(vetoActive,ScriptOperation.Stop);Check(final.Succeeded&&Exited(vetoActive),"veto-release-graceful-cleanup");vetoActive=null;
            await ServiceLifecycleAsync();
        }
        finally
        {
            // Never terminate a process. Release our fixture veto, request documented graceful exit,
            // and retain a failure if Windows does not confirm it.
            await File.WriteAllTextAsync(Path.Combine(directory,"allow-exit"),"owned fixture cleanup");
            foreach(var identity in new[]{active,vetoActive}.OfType<ProcessIdentity>())if(!Exited(identity))
            {
                var cleanup=await manager.ControlAsync(identity,ScriptOperation.Stop);
                if(!cleanup.Succeeded)File.AppendAllText(Path.Combine(root,"cleanup-pending.txt"),identity.Pid+" "+cleanup.Code+Environment.NewLine);
            }
            File.WriteAllLines(Path.Combine(root,"checks.txt"),checks);
        }
        async Task ServiceLifecycleAsync()
        {
            using var serviceOwner=new FixtureOwner(directory,Interpreter,[script,veto]);
            var processes=new ProcessManager(serviceOwner,documentedControlsProven:true,observationSource:observation);
            var startup=new NoStartup();
            var service=new ManagerService(Path.Combine(root,"service-data"),serviceOwner,processes,new UserStartupService(startup,Environment.ProcessPath!));
            ProcessIdentity? identity=null;
            try
            {
                await service.LoadAsync();await service.SaveSettingsAsync(service.Snapshot.Settings with {InterpreterPath=Interpreter});
                var added=await service.AddFilesAsync([script]);Check(added.Added.Length==1,"real-service-catalog-added-without-launch");Guid scriptId=added.Added[0];
                SelectionContext Selection()=>new(scriptId,new ScriptQuery(),service.Snapshot.Revision);
                var selection=Selection();var plan=await service.PrepareAsync(selection,ScriptOperation.Start);
                Check(plan.Allowed,"real-service-start-plan");var started=await service.PerformAsync(plan,Selection(),true);Check(started.Succeeded,"real-service-start-confirmed");
                identity=service.Snapshot.Scripts.Single().Process!.Identity;
                var old=identity;plan=await service.PrepareAsync(Selection(),ScriptOperation.Restart);
                Check(plan.Allowed,"real-service-restart-plan");var restarted=await service.PerformAsync(plan,Selection(),true);Check(restarted.Succeeded&&Exited(old),"real-service-restart-old-exit-before-replacement");
                identity=service.Snapshot.Scripts.Single().Process!.Identity;Check(identity.Pid!=old.Pid||identity.CreationFileTime!=old.CreationFileTime,"real-service-restart-one-new-identity");
                var observed=await processes.ObserveAsync([Interpreter]);Check(observed.Processes.Count(p=>ScriptPaths.Equal(p.Identity.ScriptPath,script))==1,"real-service-restart-one-instance");
                using var instance=new SingleInstanceService();int applicationExits=0;
                var lifecycle=new AppLifecycle(service,instance,Microsoft.UI.Xaml.Application.Current,()=>applicationExits++,_=>false);
                await lifecycle.StartAsync([]);await Task.Delay(150);
                lifecycle.Window!.Closed+=(_,_)=>File.AppendAllText(Path.Combine(root,"startup-stages.txt"),"manager lifecycle Closed\n");
                await lifecycle.ExitAsync();
                File.AppendAllText(Path.Combine(root,"startup-stages.txt"),"manager ExitAsync returned\n");
                Check(applicationExits==1&&!Exited(identity),"native-manager-exit-leaves-owned-script-alive");
                Check(startup.Writes==0,"actual-fixture-never-writes-startup");
            }
            finally
            {
                await service.DisposeAsync();
                if(identity is not null&&!Exited(identity))
                {
                    using var cleanupOwner=new FixtureOwner(directory,Interpreter,[script,veto]);await using var cleanup=new ProcessManager(cleanupOwner,true,observationSource:observation);
                    var result=await cleanup.ControlAsync(identity,ScriptOperation.Stop);Check(result.Succeeded&&Exited(identity),"service-fixture-graceful-cleanup");
                }
            }
        }
    }
    static bool Exited(ProcessIdentity identity)
    {
        try {using var process=Process.GetProcessById(identity.Pid);return process.HasExited||process.StartTime.ToFileTimeUtc()!=identity.CreationFileTime;}catch(ArgumentException){return true;}
    }
    static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    sealed class FixtureOwner : IEffectOwner
    {
        readonly string root,interpreter;readonly Dictionary<string,string> scripts;readonly SemaphoreSlim mutex=new(1,1);bool disposed;
        public bool Held {get;private set;}
        public bool Trusted=>Held&&!disposed;
        public string Sid {get;}=WindowsIdentity.GetCurrent().User!.Value;
        public string LoginKey {get;}="fixture-"+Guid.NewGuid().ToString("N");
        public FixtureOwner(string root,string interpreter,IEnumerable<string> scripts)
        {
            this.root=Path.GetFullPath(root)+Path.DirectorySeparatorChar;this.interpreter=Path.GetFullPath(interpreter);
            this.scripts=scripts.ToDictionary(Path.GetFullPath,Hash,StringComparer.OrdinalIgnoreCase);
            Held=mutex.Wait(0);
        }
        public bool Allows(string scriptPath,string interpreterPath)
        {
            string path=Path.GetFullPath(scriptPath);
            return Trusted&&path.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&scripts.TryGetValue(path,out var hash)&&Hash(path)==hash&&Path.GetFullPath(interpreterPath).Equals(interpreter,StringComparison.OrdinalIgnoreCase)&&Hash(interpreter)==InterpreterHash;
        }
        public void Dispose(){if(disposed)return;disposed=true;if(Held){mutex.Release();Held=false;}mutex.Dispose();}
    }
    sealed class NoStartup : IUserStartupAdapter
    {
        public int Writes;
        public string? ReadEntry()=>null;
        public bool CompareExchangeEntry(string? expected,string? replacement){Writes++;throw new InvalidOperationException("Real fixture must never register startup");}
        public Task<StartupInspection> InspectAsync(CancellationToken ct)=>Task.FromResult(new StartupInspection(true,ImmutableArray<string>.Empty,ImmutableArray<Issue>.Empty));
    }
}
