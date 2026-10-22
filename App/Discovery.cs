using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Bezoro.AhkManager;

public static class PrivateProbe
{
    public static async Task<string> Run(string code, CancellationToken cancellation = default)
    {
        var info = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new();" + code)));
        using var process = Process.Start(info) ?? throw new IOException("Não foi possível consultar o Windows.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var errors = ReadBounded(process.StandardError, timeout.Token);
        try
        {
            var output = await ReadBounded(process.StandardOutput, timeout.Token);
            await process.WaitForExitAsync(timeout.Token); await errors;
            if (process.ExitCode != 0) throw new IOException("Consulta Windows indisponível.");
            return output;
        }
        catch { if (!process.HasExited) process.Kill(); throw; }
    }
    static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        StringBuilder value = new(); char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0) { if (value.Length + count > 2_000_000) throw new IOException("Consulta excedeu limite."); value.Append(buffer, 0, count); }
        return value.ToString();
    }
}

public static class Commands
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    public static string[] Split(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32768) return [];
        var ptr = CommandLineToArgvW(command, out int count); if (ptr == IntPtr.Zero) return [];
        try { var args = new string[count]; for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size)) ?? ""; return args; }
        finally { LocalFree(ptr); }
    }
    public static bool IsInterpreter(string path) => System.IO.Path.GetFileName(path).StartsWith("AutoHotkey", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    public static string? Script(string? command)
    {
        if (command is null) return null;
        // Process command lines are literal; expanding percent text could assign the wrong script identity.
        var args = Split(command); if (args.Length == 0) return null;
        try
        {
            if (args[0].EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)) return args.Length == 1 ? Paths.Normalize(args[0]) : null;
            if (!IsInterpreter(args[0]) || !System.IO.Path.IsPathFullyQualified(args[0])) return null;
            // /script is the only interpreter switch accepted here. Other wrappers remain opaque.
            var index = args.Length > 1 && args[1].Equals("/script", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            return args.Length > index && !args[index].StartsWith('/') ? Paths.Normalize(args[index]) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
    public static string? StartupScript(string command) => command.Contains('%') ? null : Script(command);
    public static ProcessStartInfo Launch(string interpreter, string script)
    {
        var info = new ProcessStartInfo(interpreter) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(script)! };
        info.ArgumentList.Add(script); return info;
    }
}

public static class Discovery
{
    const string StartupCode = """
        $items=[Collections.Generic.List[object]]::new();$limits=[Collections.Generic.List[string]]::new()
        function Add-Command($source,$command,$elevated){if($command -match '(?i)\.ahk|autohotkey'){$items.Add(@{Source=$source;Command=$command;Elevated=[bool]$elevated})}elseif($command){$limits.Add('Comando indireto não resolvido: '+$source)}}
        foreach($folder in @([Environment]::GetFolderPath('Startup'),[Environment]::GetFolderPath('CommonStartup'))){
          try {foreach($file in Get-ChildItem -LiteralPath $folder -File -ErrorAction Stop){
            if($file.Extension -eq '.ahk'){Add-Command ('Startup: '+$file.Name) ('"'+$file.FullName+'"') $false}
            elseif($file.Extension -eq '.lnk'){try{$shell=New-Object -ComObject WScript.Shell;$link=$shell.CreateShortcut($file.FullName);Add-Command ('Atalho Startup: '+$file.Name) ('"'+$link.TargetPath+'" '+$link.Arguments) $false;[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link);[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)}catch{$limits.Add('Atalho Startup inacessível')}}
          }} catch {$limits.Add('Pasta Startup inacessível')}
        }
        foreach($base in @('HKCU:\Software\Microsoft\Windows\CurrentVersion','HKLM:\Software\Microsoft\Windows\CurrentVersion','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion')){foreach($key in @('Run','RunOnce')){try{$reg=Get-Item -LiteralPath ($base+'\'+$key) -ErrorAction Stop;foreach($name in $reg.GetValueNames()){Add-Command ('Registro '+$key+': '+$name) ([string]$reg.GetValue($name)) $false}}catch{$limits.Add('Chave Run/RunOnce ausente ou inacessível')}}}
        try {foreach($task in Get-ScheduledTask -ErrorAction Stop){foreach($action in $task.Actions){if($action.Execute){Add-Command ('Tarefa: '+$task.TaskPath+$task.TaskName) ('"'+$action.Execute+'" '+$action.Arguments) ($task.Principal.RunLevel -eq 'Highest')}}}}catch{$limits.Add('Tarefas agendadas parcialmente inacessíveis')}
        @{Items=@($items.ToArray());Limits=@($limits.ToArray())}|ConvertTo-Json -Depth 5 -Compress
        """;
    static readonly string sessionRoot = ConventionalRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    public static string DefaultRoot => sessionRoot;
    public static string ConventionalRoot(string documents, string profile)
    {
        var primary = string.IsNullOrWhiteSpace(documents) ? null : System.IO.Path.Combine(documents, "AutoHotkey");
        var fallback = System.IO.Path.Combine(profile, "Documents", "AutoHotkey");
        return primary is not null && Directory.Exists(primary) ? primary : Directory.Exists(fallback) ? fallback : primary ?? fallback;
    }
    public static List<string> Scan(string root, List<string> limits)
    {
        List<string> found = []; Stack<(string Path, int Depth)> pending = new(); pending.Push((root, 0)); int directories = 0, visitedFiles = 0;
        while (pending.TryPop(out var next))
        {
            if (++directories > 500 || visitedFiles >= 1000) { limits.Add("Descoberta atingiu limite de arquivos ou pastas."); break; }
            try
            {
                if (!Directory.Exists(next.Path)) continue;
                foreach (var file in Directory.EnumerateFiles(next.Path, "*.ahk"))
                {
                    if (++visitedFiles > 1000) { limits.Add("Descoberta atingiu limite de arquivos."); break; }
                    try { found.Add(Paths.Normalize(file)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { limits.Add("Um arquivo da descoberta está inacessível ou tem identidade recusada."); }
                }
                if (next.Depth >= 6) { limits.Add("Profundidade de descoberta limitada a seis pastas."); continue; }
                foreach (var dir in Directory.EnumerateDirectories(next.Path))
                {
                    var name = System.IO.Path.GetFileName(dir).ToLowerInvariant();
                    if (name.Contains("backup") || name is "logs" or "log" or "sessions" or "credentials" or ".git" || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push((dir, next.Depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { limits.Add("Uma pasta da descoberta está inacessível."); }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static async Task<DiscoveryResult> Find(CancellationToken token = default)
    {
        List<string> limits = []; var paths = Scan(DefaultRoot, limits); List<StartupSource> sources = [];
        try
        {
            var probe = JsonSerializer.Deserialize(await PrivateProbe.Run(StartupCode, token), AppJson.Default.StartupProbe) ?? throw new IOException();
            limits.AddRange(probe.Limits);
            foreach (var row in probe.Items)
            {
                var path = Commands.StartupScript(row.Command);
                if (path is null) limits.Add("Fonte AHK com comando não resolvido: " + row.Source);
                else { paths.Add(path); sources.Add(new(path, row.Source, row.Elevated)); }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or System.ComponentModel.Win32Exception) { limits.Add("Consulta de inicialização incompleta ou indisponível."); }
        return new(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), sources, limits.Distinct().ToList());
    }
    public static string BoundedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[Math.Min(stream.Length, 65536)]; int read = 0, chunk;
        while (read < bytes.Length && (chunk = stream.Read(bytes, read, bytes.Length - read)) > 0) read += chunk;
        // UTF-8 and UTF-16 BOMs are supported. Unsupported UTF-32 refuses rather than guessing.
        if (read >= 4 && (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff || bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)) throw new InvalidDataException("Codificação UTF-32 não suportada.");
        if (read >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) return Encoding.UTF8.GetString(bytes, 3, read - 3);
        if (read >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) return Encoding.Unicode.GetString(bytes, 2, (read - 2) / 2 * 2);
        if (read >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) return Encoding.BigEndianUnicode.GetString(bytes, 2, (read - 2) / 2 * 2);
        return Encoding.UTF8.GetString(bytes, 0, read);
    }
    public static string Infer(string path)
    {
        try
        {
            var text = BoundedText(path);
            if (Paths.Supervisor(path) && text.Contains("EnsureScriptsRunning") && text.Contains("SetTimer")) return "Inicia scripts da pasta scripts\\startup e verifica os processos a cada 60 segundos. Pode relançar filhos encerrados.";
            if (text.Contains("Unity", StringComparison.OrdinalIgnoreCase) && text.Contains("ProcessClose", StringComparison.OrdinalIgnoreCase)) return "Contém atalho para encerrar e reabrir o Unity. Confira os atalhos no editor.";
            if (text.Contains("Wheel", StringComparison.OrdinalIgnoreCase)) return "Contém atalhos ligados à roda do mouse. Pode controlar volume ou tamanho de pincel.";
            if (text.Contains("desktop", StringComparison.OrdinalIgnoreCase)) return "Contém automação relacionada à área de trabalho. Confira o comportamento no editor.";
        }
        catch (InvalidDataException) { return "Codificação UTF-32 não suportada. O cadastro foi preservado; use UTF-8 ou UTF-16 com BOM para verificar os requisitos e iniciar."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Conteúdo inacessível; descreva manualmente."; }
        return "Comportamento não identificado. Abra no editor para conferir e informe uma descrição.";
    }
    public static string DefaultInterpreter()
    {
        var candidates = new[] { System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"AutoHotkey\v2\AutoHotkey64.exe"), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"AutoHotkey\AutoHotkey.exe") };
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }
    public static Result Compatible(ScriptEntry entry)
    {
        if (!File.Exists(entry.Interpreter) || !Commands.IsInterpreter(entry.Interpreter)) return Result.Fail("Interpretador AutoHotkey ausente ou inválido.");
        try
        {
            var text = BoundedText(Paths.Normalize(entry.Path)); var info = FileVersionInfo.GetVersionInfo(entry.Interpreter); var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            var requires = text.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("#Requires", StringComparison.OrdinalIgnoreCase));
            if (requires is null) return Result.Fail("Versão não declarada com #Requires. Confira o arquivo e use o editor; início seguro recusado.");
            var parts = requires.Trim().Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[1].Equals("AutoHotkey", StringComparison.OrdinalIgnoreCase))
            {
                var declared = parts[2].TrimStart('v', 'V').TrimEnd('+'); if (declared is "1" or "2") declared += ".0";
                if (Version.TryParse(declared, out var minimum) && minimum.Major == version.Major && version >= minimum && (parts.Length == 3 || parts[3].StartsWith(';'))) return Result.Ok("Versão compatível.");
            }
            return Result.Fail("Versão do interpretador incompatível ou requisito não reconhecido.");
        }
        catch (InvalidDataException) { return Result.Fail("Codificação UTF-32 não suportada. Use UTF-8 ou UTF-16 com BOM."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException) { return Result.Fail("Não foi possível verificar arquivo e versão."); }
    }
}
