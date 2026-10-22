using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bezoro.AhkManager.Presentation;

public sealed class NormalBackupFile
{
    public string Source { get; set; } = "";
    public string Copy { get; set; } = "";
    public bool Exists { get; set; }
    public string SHA256 { get; set; } = "";
}
public sealed class NormalBackup
{
    public string ManifestHash { get; set; } = "";
    public string PreparedShortcutSHA256 { get; set; } = "";
    public List<NormalBackupFile> Files { get; set; } = [];
}

// No task, elevation or generic process effect is exposed by this adapter.
public sealed class NormalMigrationEffects(IProcessAccess processes) : INormalMigrationEffects
{
    public const string ExpectedSid = "S-1-5-21-1314942500-26635142-908828824-1001";
    public const string ScriptRoot = @"C:\Users\Bezor\Documents\AutoHotkey";
    public const string ProfileRoot = @"C:\Users\Bezor\AppData\Local\Bezoro\AhkManager";
    public const string StartupRoot = @"C:\Users\Bezor\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
    public const string ManagerPath = @"V:\Repos\Bezoro.AhkManager\publish\Bezoro.AhkManager.exe";
    public const string Interpreter = @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe";
    public bool Fixture => false;
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, int information, IntPtr buffer, int length, out int returned);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct FileIdentity { public uint Attributes; public long Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileIdentity identity);
    public static void SafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path[2..].Contains(':') || path.Contains('"') || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Caminho local exato necessário.");
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Link/redirecionamento recusado.");
    }
    public static void ValidateScope(NormalCutoverManifest m)
    {
        if (m.ExpectedSid != ExpectedSid || !Paths.Equal(m.Root, ScriptRoot) || !Paths.Equal(m.ProfileDirectory, ProfileRoot) || !Paths.Equal(m.LegacyShortcut, Path.Combine(StartupRoot, "AutoHotkey Startup Scripts.lnk")) || !Paths.Equal(m.NormalShortcut, Path.Combine(StartupRoot, "Bezoro AHK Manager.lnk")) || !Paths.Equal(m.ManagerExecutable, ManagerPath) || !m.BackupDirectory.StartsWith(ProfileRoot + @"\normal-cutover-backups\", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(m.BackupDirectory).Length < 20) throw new InvalidDataException("Escopo normal diverge do escopo autorizado.");
    }
    static void Context(NormalCutoverManifest m)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query); using var current = Process.GetCurrentProcess();
        if (identity.User?.Value != ExpectedSid || current.SessionId != m.ExpectedSession || !Environment.UserInteractive || !Paths.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"C:\Users\Bezor\AppData\Local")) throw new InvalidDataException("Usuário/sessão interativa normal necessários.");
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (!GetTokenInformation(identity.AccessToken, 25, buffer, 256, out var length) || length < IntPtr.Size + 4 || length > 256) throw new InvalidDataException("Integridade indisponível.");
            var sid = Marshal.ReadIntPtr(buffer); long offset = sid.ToInt64() - buffer.ToInt64();
            if (offset < 0 || offset > length - 12) throw new InvalidDataException("SID de integridade inválido.");
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count <= 0 || offset + 8 + count * 4 > length) throw new InvalidDataException("SID de integridade truncado.");
            int level = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            if (level < 0x2000 || level >= 0x3000) throw new InvalidDataException("Execução em integridade normal necessária.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
        foreach (var candidate in Process.GetProcesses())
        {
            using (candidate)
            {
                string name; try { name = candidate.ProcessName; } catch (InvalidOperationException) { continue; }
                if (name.Equals("Bezoro.AhkManager", StringComparison.OrdinalIgnoreCase) && candidate.Id != current.Id) throw new InvalidDataException("Feche Manager voluntariamente antes do cutover.");
                string? path = null; try { path = candidate.MainModule?.FileName; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                if (Regex.IsMatch(name, "^(Playnite.*|eldenring|dota2|LeagueClient.*|League of Legends|Factorio|Minecraft.*|Roblox.*|Fortnite.*|Overwatch.*|Diablo.*|Warframe|Hades.*|Terraria|Stardew Valley|Cyberpunk2077|PathOfExile.*|Baldur.*|bg3.*|GTA5.*|RDR2|cs2|r5apex|VALORANT.*|RocketLeague|witcher3|MonsterHunter.*|Hollow.*|NoMansSky|Satisfactory|Palworld.*|RustClient|DayZ.*|Ryujinx|RPCS3|DuckStation|Cemu|RetroArch|PCSX2|yuzu|MelonDS)$", RegexOptions.IgnoreCase) || path is not null && Regex.IsMatch(path, @"\\(steamapps\\common|XboxGames|Playnite|Games)\\", RegexOptions.IgnoreCase) && !path.EndsWith(@"\wallpaper_engine\wallpaper32.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Jogo/Playnite observado; adiar cutover.");
                bool visible; try { visible = candidate.MainWindowHandle != IntPtr.Zero && candidate.MainWindowTitle.Length > 0; } catch (InvalidOperationException) { throw new InvalidDataException("Visibilidade de aplicativo não confirmada."); }
                if (visible && !Regex.IsMatch(name, "^(Bezoro\\.AhkManager|Codex|ApplicationFrameHost|ChatGPT|chrome|msedge|firefox|brave|NVIDIA Overlay|PieMenu|SystemSettings|TextInputHost|explorer|Code|rider64|devenv|powershell|pwsh|WindowsTerminal|cmd|Taskmgr|SearchHost|StartMenuExperienceHost|Notepad|notepad\\+\\+|Spotify|Discord|Slack|EpicGamesLauncher|steam)$", RegexOptions.IgnoreCase) && !KnownSystemShell(name, path, Environment.GetFolderPath(Environment.SpecialFolder.Windows))) throw new InvalidDataException("Aplicativo visível desconhecido; revisar antes do cutover.");
            }
        }
    }
    public static bool KnownSystemShell(string name, string? path, string windows) => name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(windows) && Paths.Equal(path, Path.Combine(windows, @"SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe"));
    public static Result CheckContext(NormalCutoverManifest m)
    {
        try { ValidateScope(m); Context(m); return Result.Ok("Contexto normal conferido."); }
        catch (Exception ex) when (ex is InvalidDataException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return Result.Fail(ex.Message); }
    }
    public static string FileHash(string path)
    {
        SafePath(path); using var stream = File.OpenRead(path);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var identity) || identity.Links != 1) throw new InvalidDataException("Identidade de arquivo incerta ou vários links.");
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public static void GuardProfileFiles(string profile)
    {
        SafePath(profile);
        foreach (var name in new[] { "catalog.json", "catalog.json.bak", "migration-journal.json", "migration-journal.json.bak", "launch-requests.json", "launch-requests.json.bak", "launch-authority.anchor.json", "launch-authority.anchor.json.bak" })
        {
            var path = Path.Combine(profile, name); SafePath(path);
            if (File.Exists(path)) _ = FileHash(path);
        }
        foreach (var name in new[] { "catalog.json.tmp", "migration-journal.json.tmp", "launch-requests.json.tmp", "launch-authority.anchor.json.tmp" })
        {
            var path = Path.Combine(profile, name); SafePath(path);
            if (File.Exists(path) || Directory.Exists(path)) throw new InvalidDataException("Destino temporário existente; preservar evidência e recusar escrita.");
        }
    }
    static string ArchivedShortcut(NormalCutoverManifest m) => Path.Combine(m.BackupDirectory, "legacy-archived.lnk");
    static string ArchivedSupervisor(NormalCutoverManifest m) => Path.Combine(m.BackupDirectory, "supervisor-retired.bytes");
    static string PreparedShortcut(NormalCutoverManifest m) => Path.Combine(m.BackupDirectory, "prepared-manager.lnk");
    static string BackupPath(NormalCutoverManifest m) => Path.Combine(m.BackupDirectory, "backup.json");
    static bool Done(MigrationJournal? j, string step) => j?.AppliedSteps.Contains(step) == true || j?.PendingStep == step;
    static void RequireHash(string path, string hash) { if (!File.Exists(path) || FileHash(path) != hash) throw new InvalidDataException("Arquivo ausente/alterado: " + path); }
    public Result Check(NormalCutoverManifest m, MigrationJournal? j)
    {
        try
        {
            ValidateScope(m); Context(m);
            foreach (var p in new[] { m.Root, m.ProfileDirectory, m.BackupDirectory, m.LegacyShortcut, m.NormalShortcut, m.ManagerExecutable }) SafePath(p);
            GuardProfileFiles(m.ProfileDirectory);
            RequireHash(m.ManagerExecutable, m.ManagerSHA256);
            for (int i = 0; i < 4; i++)
            {
                var path = Path.Combine(m.Root, NormalCutoverCoordinator.RelativePaths[i]);
                if (i == 0 && !File.Exists(path) && Done(j, "SupervisorFileArchived")) RequireHash(ArchivedSupervisor(m), m.Choices[i].ExpectedSHA256);
                else { if (i == 0 && j?.AppliedSteps.Contains("SupervisorFileArchived") == true) throw new InvalidDataException("Supervisor reapareceu no caminho ativo."); RequireHash(path, m.Choices[i].ExpectedSHA256); }
            }
            if (File.Exists(m.LegacyShortcut)) { if (j?.AppliedSteps.Contains("LegacyShortcutArchived") == true) throw new InvalidDataException("Atalho legado reapareceu."); RequireHash(m.LegacyShortcut, m.LegacyShortcutSHA256); ShortcutIO.VerifyLegacy(m); }
            else if (Done(j, "LegacyShortcutArchived")) RequireHash(ArchivedShortcut(m), m.LegacyShortcutSHA256);
            else throw new InvalidDataException("Atalho legado ausente sem intent durável.");
            if (File.Exists(m.NormalShortcut))
            {
                if (!Done(j, "NormalLoginPrepared")) throw new InvalidDataException("Atalho Manager já existe sem intent.");
                var backup = ReadBackup(m); RequireHash(m.NormalShortcut, backup.PreparedShortcutSHA256); ShortcutIO.VerifyNormal(m.NormalShortcut, m);
            }
            else if (j?.AppliedSteps.Contains("NormalLoginPrepared") == true) throw new InvalidDataException("Atalho Manager concluído foi removido.");
            foreach (var shortcut in Directory.EnumerateFiles(StartupRoot, "*.lnk"))
            {
                if (Paths.Equal(shortcut, m.LegacyShortcut) || Paths.Equal(shortcut, m.NormalShortcut)) continue;
                var properties = ShortcutIO.Read(shortcut);
                if (Paths.Equal(properties.Target, m.ManagerExecutable) || properties.Arguments.Contains(Path.Combine(m.Root, "startup.ahk"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Fonte Startup duplicada.");
            }
            return Result.Ok("Escopo, contexto e arquivos conferidos.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception) { return Result.Fail(ex.Message); }
    }
    public Task<Snapshot> Observe(CancellationToken token) => processes.Snapshot(token);
    static IEnumerable<string> BackupSources(NormalCutoverManifest m)
    {
        yield return Path.Combine(m.Root, "startup.ahk"); yield return m.LegacyShortcut; yield return m.NormalShortcut;
        foreach (var name in new[] { "catalog.json", "catalog.json.bak", "migration-journal.json", "migration-journal.json.bak", "launch-requests.json", "launch-requests.json.bak", "launch-authority.anchor.json", "launch-authority.anchor.json.bak" }) yield return Path.Combine(m.ProfileDirectory, name);
    }
    public string Backup(NormalCutoverManifest m)
    {
        if (Directory.Exists(m.BackupDirectory))
        {
            // Crash before the first journal write: reuse only this complete backup whose sources still match.
            var digest = FileHash(BackupPath(m));
            var checkedBackup = VerifyBackup(m, new() { ManifestHash = NormalCutoverCoordinator.Hash(m), BackupDigest = digest });
            if (!checkedBackup.Success) throw new IOException(checkedBackup.Message);
            foreach (var row in ReadBackup(m).Files) { if (row.Exists) RequireHash(row.Source, row.SHA256); else if (File.Exists(row.Source)) throw new IOException("Fonte backup anteriormente ausente apareceu."); }
            return digest;
        }
        SafePath(m.BackupDirectory); Directory.CreateDirectory(m.BackupDirectory);
        var backup = new NormalBackup { ManifestHash = NormalCutoverCoordinator.Hash(m) }; int i = 0;
        foreach (var source in BackupSources(m))
        {
            SafePath(source); var copy = Path.Combine(m.BackupDirectory, "original-" + i++ + ".bytes"); var exists = File.Exists(source);
            var row = new NormalBackupFile { Source = source, Copy = copy, Exists = exists };
            if (exists)
            {
                row.SHA256 = FileHash(source);
                using (var input = File.OpenRead(source)) using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
                RequireHash(copy, row.SHA256); RequireHash(source, row.SHA256);
            }
            backup.Files.Add(row);
        }
        ShortcutIO.CreateNormal(PreparedShortcut(m), m); ShortcutIO.VerifyNormal(PreparedShortcut(m), m); backup.PreparedShortcutSHA256 = FileHash(PreparedShortcut(m));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(backup, MigrationJson.Default.NormalBackup);
        using (var output = new FileStream(BackupPath(m), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { output.Write(bytes); output.Flush(true); }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    static NormalBackup ReadBackup(NormalCutoverManifest m)
    {
        SafePath(BackupPath(m)); if (new FileInfo(BackupPath(m)).Length > 64000) throw new InvalidDataException("Backup excede limite.");
        return JsonSerializer.Deserialize(File.ReadAllBytes(BackupPath(m)), MigrationJson.Default.NormalBackup) ?? throw new InvalidDataException("Backup vazio.");
    }
    public Result VerifyBackup(NormalCutoverManifest m, MigrationJournal j)
    {
        try
        {
            RequireHash(BackupPath(m), j.BackupDigest); var backup = ReadBackup(m);
            if (backup.ManifestHash != j.ManifestHash || !backup.Files.Select(f => f.Source).SequenceEqual(BackupSources(m))) throw new InvalidDataException("Backup de outra transação/escopo.");
            for (int i = 0; i < backup.Files.Count; i++)
            {
                var f = backup.Files[i]; if (f.Copy != Path.Combine(m.BackupDirectory, "original-" + i + ".bytes")) throw new InvalidDataException("Destino backup divergente.");
                if (f.Exists) RequireHash(f.Copy, f.SHA256); else if (File.Exists(f.Copy)) throw new InvalidDataException("Ausência backup divergente.");
            }
            RequireHash(PreparedShortcut(m), backup.PreparedShortcutSHA256); ShortcutIO.VerifyNormal(PreparedShortcut(m), m); return Result.Ok("Backup durável conferido.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Runtime.InteropServices.COMException) { return Result.Fail(ex.Message); }
    }
    static void Archive(string source, string destination, string hash)
    {
        SafePath(destination);
        if (!File.Exists(source)) { RequireHash(destination, hash); return; }
        if (File.Exists(destination)) throw new IOException("Destino arquivo já existe; preservar ambos.");
        RequireHash(source, hash); File.Move(source, destination); RequireHash(destination, hash);
    }
    public async Task<Result> Apply(string step, NormalCutoverManifest m, MigrationJournal j, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (step == "LegacyShortcutArchived") Archive(m.LegacyShortcut, ArchivedShortcut(m), m.LegacyShortcutSHA256);
            else if (step == "SupervisorStopped")
            {
                var snapshot = await Observe(token); if (snapshot.Unknown) return Result.Fail("Ausência do supervisor não verificável.");
                var active = snapshot.For(Path.Combine(m.Root, "startup.ahk"));
                if (active.Count > 0)
                {
                    if (active.Count != 1 || j.Supervisor is null || !ProcessIdentity.SameCore(active[0], j.Supervisor)) return Result.Fail("Supervisor mudou; nenhuma parada.");
                    var result = await processes.Stop(j.Supervisor, token); if (!result.Success) return result;
                }
                snapshot = await Observe(token); if (snapshot.Unknown || snapshot.For(Path.Combine(m.Root, "startup.ahk")).Count != 0) return Result.Fail("Saída do supervisor não confirmada.");
            }
            else if (step == "SupervisorFileArchived") Archive(Path.Combine(m.Root, "startup.ahk"), ArchivedSupervisor(m), m.Choices[0].ExpectedSHA256);
            else if (step == "NormalLoginPrepared")
            {
                var backup = ReadBackup(m); RequireHash(PreparedShortcut(m), backup.PreparedShortcutSHA256);
                if (!File.Exists(m.NormalShortcut)) File.Copy(PreparedShortcut(m), m.NormalShortcut, false);
                RequireHash(m.NormalShortcut, backup.PreparedShortcutSHA256); ShortcutIO.VerifyNormal(m.NormalShortcut, m);
            }
            else if (step == "ChildrenAdopted")
            {
                foreach (var child in j.Children) { var result = await processes.Prove(child, token); if (!result.Success) return result; }
            }
            else return Result.Fail("Etapa fora do escopo normal.");
            return Result.Ok("Etapa verificada: " + step);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Runtime.InteropServices.COMException) { return Result.Fail(ex.Message); }
    }
}

public sealed record ShortcutProperties(string Target, string Arguments, string WorkingDirectory, int ShowCommand, short HotKey, uint Flags);
public static class ShortcutIO
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr item); void SetIDList(IntPtr item);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count); void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count); void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey); void SetHotkey(short hotkey); void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index); void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved); void Resolve(IntPtr window, uint flags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    [ComImport, Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IShellLinkDataList
    { void AddDataBlock(IntPtr data); void CopyDataBlock(uint signature, out IntPtr data); void RemoveDataBlock(uint signature); void GetFlags(out uint flags); void SetFlags(uint flags); }
    public static ShortcutProperties Read(string path)
    {
        NormalMigrationEffects.SafePath(path); var native = new ShellLink();
        try
        {
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)native).Load(path, 0); var link = (IShellLink)native;
            var target = new StringBuilder(32768); var arguments = new StringBuilder(32768); var directory = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4); link.GetArguments(arguments, arguments.Capacity); link.GetWorkingDirectory(directory, directory.Capacity); link.GetShowCmd(out var show); link.GetHotkey(out var hotkey); ((IShellLinkDataList)native).GetFlags(out var flags);
            return new(target.ToString(), arguments.ToString(), directory.ToString(), show, hotkey, flags);
        }
        finally { Marshal.FinalReleaseComObject(native); }
    }
    static void Verify(string path, string target, string arguments, string directory)
    {
        var p = Read(path); if (!Paths.Equal(p.Target, target) || p.Arguments != arguments || !Paths.Equal(p.WorkingDirectory, directory) || p.ShowCommand != 1 || p.HotKey != 0 || (p.Flags & 0x2000) != 0) throw new InvalidDataException("Propriedades do atalho divergem ou pedem elevação.");
    }
    public static void VerifyLegacy(NormalCutoverManifest m) => Verify(m.LegacyShortcut, NormalMigrationEffects.Interpreter, "\"" + Path.Combine(m.Root, "startup.ahk") + "\"", m.Root);
    public static void VerifyNormal(string path, NormalCutoverManifest m) => Verify(path, m.ManagerExecutable, m.ShortcutArguments, Path.GetDirectoryName(m.ManagerExecutable)!);
    public static void CreateNormal(string path, NormalCutoverManifest m)
    {
        NormalMigrationEffects.SafePath(path); if (File.Exists(path)) throw new IOException("Atalho preparado já existe."); var native = new ShellLink();
        try
        {
            var link = (IShellLink)native; link.SetPath(m.ManagerExecutable); link.SetArguments(m.ShortcutArguments); link.SetWorkingDirectory(Path.GetDirectoryName(m.ManagerExecutable)!); link.SetShowCmd(1); link.SetHotkey(0); link.SetIconLocation(m.ManagerExecutable, 0); link.SetDescription("Bezoro AHK Manager");
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)native).Save(path, true);
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); file.Flush(true);
        }
        finally { Marshal.FinalReleaseComObject(native); }
    }
}
