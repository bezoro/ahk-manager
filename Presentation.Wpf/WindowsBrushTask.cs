using System.IO;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
namespace Bezoro.AhkManager.Presentation;

public interface IBrushRegisterRefusalWitness { bool ProvesRegisterRefusal(BrushJournal journal); }

public sealed class WindowsBrushTask : IBrushTask, IBrushRegisterRefusalWitness, IBrushTaskIdleWitness
{
    readonly Func<Result>? effectGate;
    public WindowsBrushTask() { }
    internal WindowsBrushTask(Func<Result> effectGate) { this.effectGate = effectGate; }
    public const string CapturedXmlSHA256 = "C106B38EB4FCDA4BA633A6BF3A8A1274B6C9D2562BEBDFE7BDC5E4A2E4688D2B";
    public const string CapturedSecurity = "O:BAG:S-1-5-21-1314942500-26635142-908828824-1001D:(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;ID;FA;;;BA)(A;;FR;;;S-1-5-21-1314942500-26635142-908828824-1001)";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    const string BaselineBase64 = "PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iVVRGLTE2Ij8+DQo8VGFzayB2ZXJzaW9uPSIxLjMiIHhtbG5zPSJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dpbmRvd3MvMjAwNC8wMi9taXQvdGFzayI+DQogIDxSZWdpc3RyYXRpb25JbmZvPg0KICAgIDxEZXNjcmlwdGlvbj5SdW4gdGhlIEF1dG9Ib3RrZXkgdm9sdW1lLXRvLWJydXNoLXNpemUgc2NyaXB0IGVsZXZhdGVkIGF0IHVzZXIgbG9nb24uPC9EZXNjcmlwdGlvbj4NCiAgICA8VVJJPlxBdXRvSG90a2V5XFZvbHVtZSB0byBCcnVzaCBTaXplPC9VUkk+DQogIDwvUmVnaXN0cmF0aW9uSW5mbz4NCiAgPFByaW5jaXBhbHM+DQogICAgPFByaW5jaXBhbCBpZD0iQXV0aG9yIj4NCiAgICAgIDxVc2VySWQ+Uy0xLTUtMjEtMTMxNDk0MjUwMC0yNjYzNTE0Mi05MDg4Mjg4MjQtMTAwMTwvVXNlcklkPg0KICAgICAgPExvZ29uVHlwZT5JbnRlcmFjdGl2ZVRva2VuPC9Mb2dvblR5cGU+DQogICAgICA8UnVuTGV2ZWw+SGlnaGVzdEF2YWlsYWJsZTwvUnVuTGV2ZWw+DQogICAgPC9QcmluY2lwYWw+DQogIDwvUHJpbmNpcGFscz4NCiAgPFNldHRpbmdzPg0KICAgIDxEaXNhbGxvd1N0YXJ0SWZPbkJhdHRlcmllcz5mYWxzZTwvRGlzYWxsb3dTdGFydElmT25CYXR0ZXJpZXM+DQogICAgPFN0b3BJZkdvaW5nT25CYXR0ZXJpZXM+ZmFsc2U8L1N0b3BJZkdvaW5nT25CYXR0ZXJpZXM+DQogICAgPEV4ZWN1dGlvblRpbWVMaW1pdD5QVDBTPC9FeGVjdXRpb25UaW1lTGltaXQ+DQogICAgPE11bHRpcGxlSW5zdGFuY2VzUG9saWN5Pklnbm9yZU5ldzwvTXVsdGlwbGVJbnN0YW5jZXNQb2xpY3k+DQogICAgPElkbGVTZXR0aW5ncz4NCiAgICAgIDxEdXJhdGlvbj5QVDEwTTwvRHVyYXRpb24+DQogICAgICA8V2FpdFRpbWVvdXQ+UFQxSDwvV2FpdFRpbWVvdXQ+DQogICAgICA8U3RvcE9uSWRsZUVuZD50cnVlPC9TdG9wT25JZGxlRW5kPg0KICAgICAgPFJlc3RhcnRPbklkbGU+ZmFsc2U8L1Jlc3RhcnRPbklkbGU+DQogICAgPC9JZGxlU2V0dGluZ3M+DQogICAgPFVzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lPnRydWU8L1VzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lPg0KICA8L1NldHRpbmdzPg0KICA8VHJpZ2dlcnM+DQogICAgPExvZ29uVHJpZ2dlcj4NCiAgICAgIDxVc2VySWQ+QkVaT1JPXEJlem9yPC9Vc2VySWQ+DQogICAgPC9Mb2dvblRyaWdnZXI+DQogIDwvVHJpZ2dlcnM+DQogIDxBY3Rpb25zIENvbnRleHQ9IkF1dGhvciI+DQogICAgPEV4ZWM+DQogICAgICA8Q29tbWFuZD5DOlxQcm9ncmFtIEZpbGVzXEF1dG9Ib3RrZXlcdjJcQXV0b0hvdGtleTY0LmV4ZTwvQ29tbWFuZD4NCiAgICAgIDxBcmd1bWVudHM+IkM6XFVzZXJzXEJlem9yXERvY3VtZW50c1xBdXRvSG90a2V5XHNjcmlwdHNcZWxldmF0ZWRcYWhrX3ZvbHVtZS10by1icnVzaC1zaXplLmFoayI8L0FyZ3VtZW50cz4NCiAgICAgIDxXb3JraW5nRGlyZWN0b3J5PkM6XFVzZXJzXEJlem9yXERvY3VtZW50c1xBdXRvSG90a2V5XHNjcmlwdHNcZWxldmF0ZWQ8L1dvcmtpbmdEaXJlY3Rvcnk+DQogICAgPC9FeGVjPg0KICA8L0FjdGlvbnM+DQo8L1Rhc2s+";
    public static string BaselineXml => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(BaselineBase64));
    public static XDocument Parse(string xml)
    {
        if (xml.Length > 32768) throw new InvalidDataException("Definicao da tarefa excede limite.");
        using var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32768 });
        return XDocument.Load(r);
    }
    public static string Canonical(string xml)
    {
        var d = Parse(xml); var trigger = d.Root?.Element(Ns + "Triggers")?.Elements().SingleOrDefault();
        if (trigger?.Name != Ns + "LogonTrigger") throw new InvalidDataException("Gatilho de login exato obrigatorio.");
        if (trigger.Element(Ns + "Enabled") is { } flag)
        {
            // Scheduler indentation before Enabled is redundant only when its
            // following indentation remains. Prepared inline XML has no such node.
            if (flag.PreviousNode is XText preceding && string.IsNullOrWhiteSpace(preceding.Value)
                && flag.NextNode is XText following && string.IsNullOrWhiteSpace(following.Value)) preceding.Remove();
            flag.Remove();
        }
        d.Declaration = null; return d.ToString(SaveOptions.DisableFormatting);
    }
    // Exact pre-Register C7 witness keeps the historical canonical behavior.
    public static string LegacyRegisterRefusalCanonical(string xml)
    {
        var d = Parse(xml); var trigger = d.Root?.Element(Ns + "Triggers")?.Elements().SingleOrDefault();
        if (trigger?.Name != Ns + "LogonTrigger") throw new InvalidDataException("Gatilho de login exato obrigatorio.");
        trigger.Element(Ns + "Enabled")?.Remove();
        d.Declaration = null; return d.ToString(SaveOptions.DisableFormatting);
    }
    public static BrushProbe Validate(string xml, string security, bool enabled)
    {
        try
        {
            var d = Parse(xml); var trigger = d.Root!.Element(Ns + "Triggers")!.Elements().Single();
            bool logon = trigger.Element(Ns + "Enabled") is not { } flag || XmlConvert.ToBoolean(flag.Value);
            bool demand = d.Root.Element(Ns + "Settings")?.Element(Ns + "AllowStartOnDemand") is not { } allow || XmlConvert.ToBoolean(allow.Value);
            string canonical = Canonical(xml);
            bool known = canonical == Canonical(BaselineXml) && security == CapturedSecurity;
            return new(known, enabled, demand, known ? logon : null, canonical, security, known ? enabled && demand ? "Tarefa Brush verificada." : "Tarefa desabilitada ou inicio manual bloqueado." : "Definicao ou seguranca da tarefa mudou; efeitos bloqueados.");
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or XmlException or FormatException or NullReferenceException) { return BrushProbe.Unknown("Definicao da tarefa nao confirmada."); }
    }
    static (BrushService Service, BrushFolder Folder, BrushRegistered Task) Open()
    {
        var native = new BrushScheduler(); var service = (BrushService)native;
        try { service.Connect(null, null, null, null); var folder = service.GetFolder(@"\AutoHotkey"); try { return (service, folder, folder.GetTask("Volume to Brush Size")); } catch { Marshal.ReleaseComObject(folder); throw; } }
        catch { Marshal.ReleaseComObject(service); throw; }
    }
    static void Release((BrushService Service, BrushFolder Folder, BrushRegistered Task) a) { Marshal.ReleaseComObject(a.Task); Marshal.ReleaseComObject(a.Folder); Marshal.ReleaseComObject(a.Service); }
    public BrushProbe Probe()
    {
        try { var a = Open(); try { return Validate(a.Task.Xml(), a.Task.Security(7), a.Task.Enabled()); } finally { Release(a); } }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or IOException) { return BrushProbe.Unknown("Consulta normal da tarefa nao confirmada. " + e.Message); }
    }
    public bool Fixture => false;
    public BrushTaskIdle ReadIdle()
    {
        try { var a = Open(); try { return ReadIdleWitness(() => a.Task.State(), () => Validate(a.Task.Xml(), a.Task.Security(7), a.Task.Enabled())); } finally { Release(a); } }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or IOException) { return new(BrushProbe.Unknown("Estado da tarefa indisponivel."), null); }
    }
    // Calls the typed TASK_STATE getter in its existing SDK slot, then fresh fields.
    public static BrushTaskIdle ReadIdleWitness(Func<int> state, Func<BrushProbe> probe) { int observed = state(); return new(probe(), observed); }
    public Result Run(int session)
    {
        if (effectGate is null || session <= 0 || !BrushTaskHelper.Elevated()) return Result.Fail("Helper elevado na sessao verificada obrigatorio.");
        var a = Open(); try
        {
            var p = Validate(a.Task.Xml(), a.Task.Security(7), a.Task.Enabled()); if (!p.Ready) return Result.Fail(p.Notice);
            var gate = effectGate(); if (!gate.Success) return gate;
            var running = a.Task.RunEx(null, 0x4, session, null); if (running is not null) Marshal.ReleaseComObject(running);
            return Result.Ok("RunEx aceito; execucao ainda nao confirmada.");
        }
        finally { Release(a); }
    }
    static string Digest(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    public static bool MatchRegisterRefusal(BrushJournal journal, RegisteredXml registered, string expanded)
    {
        try
        {
            var before = Validate(registered.Xml, registered.Security, registered.Enabled);
            return before.Ready && before.LogonEnabled == true && journal.AllowedLogon && before.Definition == journal.Definition && before.Security == journal.Security
                && Digest(registered.Xml) == "C106B38EB4FCDA4BA633A6BF3A8A1274B6C9D2562BEBDFE7BDC5E4A2E4688D2B"
                && Digest(expanded) == "44286BAD1F18787199493962C03D73EDB5BB8A3E10C75FFFF549E1A4DB0E2CDE"
                && Digest(LegacyRegisterRefusalCanonical(registered.Xml)) == "25924742497F27E8A38B277B51778B728F762DCE51264E9500D914A44035CE80"
                && Digest(LegacyRegisterRefusalCanonical(expanded)) == "098315CB958F04BC2199D8E92546940A99C832E5146E37DE7D2F9BA9ED873339"
                && LegacyRegisterRefusalCanonical(WithLogonEnabled(expanded, false)) != before.Definition;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or XmlException or FormatException or ArgumentException) { return false; }
    }
    public bool ProvesRegisterRefusal(BrushJournal journal)
    {
        try
        {
            var a = Open(); try
            {
                var definition = a.Task.Definition(); try { return MatchRegisterRefusal(journal, new(a.Task.Xml(), a.Task.Security(7), a.Task.Enabled()), definition.Xml()); }
                finally { Marshal.ReleaseComObject(definition); }
            }
            finally { Release(a); }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException) { return false; }
    }
    public sealed record RegisteredXml(string Xml, string Security, bool Enabled);
    public sealed record XmlRegistration(string Path, string Xml, int Flags, string? User, string? Password, int Logon, string? Sddl);
    public static string WithLogonEnabled(string registeredXml, bool enabled)
    {
        var xml = Parse(registeredXml); var trigger = xml.Root!.Element(Ns + "Triggers")!.Elements().Single();
        var flag = trigger.Element(Ns + "Enabled");
        if (flag is null) trigger.Element(Ns + "UserId")!.AddBeforeSelf(new XElement(Ns + "Enabled", XmlConvert.ToString(enabled)));
        else flag.Value = XmlConvert.ToString(enabled);
        return xml.ToString(SaveOptions.DisableFormatting);
    }
    static BrushProbe Probe(RegisteredXml value) => Validate(value.Xml, value.Security, value.Enabled);
    static bool SameTask(BrushProbe value, BrushProbe before) => value.Ready && value.Definition == before.Definition && value.Security == before.Security && value.LogonEnabled == before.LogonEnabled;
    // Local sequence shared by the COM adapter and isolated registration fixtures.
    public static Result UpdateRegisteredLogon(bool enabled, Func<RegisteredXml> read, Func<Result> effectGate, Func<XmlRegistration, RegisteredXml> register)
    {
        try
        {
            var captured = read(); var before = Probe(captured); if (!before.Ready) return Result.Fail(before.Notice);
            string changed = WithLogonEnabled(captured.Xml, enabled);
            if (Canonical(changed) != before.Definition) return Result.Fail("Alteracao nao limitada ao gatilho.");
            if (!SameTask(Probe(read()), before)) return Result.Fail("Tarefa mudou durante a preparacao.");
            var gate = effectGate(); if (!gate.Success) return gate;
            if (!SameTask(Probe(read()), before)) return Result.Fail("Tarefa mudou antes da registracao.");
            var after = Probe(register(new("Volume to Brush Size", changed, 0x34, null, null, 3, null)));
            return after.Ready && after.Definition == before.Definition && after.Security == before.Security && after.LogonEnabled == enabled
                ? Result.Ok("Somente gatilho de login alterado e verificado.") : Result.Fail("Preservacao da tarefa nao confirmada; nao repetir.");
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { return Result.Fail("Registracao Brush nao confirmada; nao repetir."); }
    }
    public Result SetLogon(bool enabled)
    {
        if (effectGate is null || !BrushTaskHelper.Elevated()) return Result.Fail("Alteracao requer helper elevado autenticado.");
        var a = Open(); try
        {
            return UpdateRegisteredLogon(enabled, () => new(a.Task.Xml(), a.Task.Security(7), a.Task.Enabled()), effectGate, input =>
            {
                var updated = a.Folder.RegisterXml(input.Path, input.Xml, input.Flags, input.User, input.Password, input.Logon, input.Sddl);
                try { return new(updated.Xml(), updated.Security(7), updated.Enabled()); }
                finally { Marshal.ReleaseComObject(updated); }
            });
        }
        finally { Release(a); }
    }

}

// Static vtable declarations follow the installed Microsoft taskschd.h ABI.
// Unused slots are deliberately unavailable as task effects; no dynamic dispatch.
[ComImport, Guid("0f87369f-a4e5-4cfc-bd3e-73e6154572dd"), ClassInterface(ClassInterfaceType.None)] internal class BrushScheduler { }
[ComImport, Guid("2faba4c7-4da9-4013-9697-20cc3fd40f85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface BrushService
{
    void Dispatch0(); void Dispatch1(); void Dispatch2(); void Dispatch3();
    BrushFolder GetFolder([MarshalAs(UnmanagedType.BStr)] string path);
    void Gap1(); void Gap2();
    void Connect([MarshalAs(UnmanagedType.Struct)] object? server, [MarshalAs(UnmanagedType.Struct)] object? user, [MarshalAs(UnmanagedType.Struct)] object? domain, [MarshalAs(UnmanagedType.Struct)] object? password);
}
[ComImport, Guid("8cfac062-a080-4c15-9a88-aa7c2af80dfc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface BrushFolder
{
    void Dispatch0(); void Dispatch1(); void Dispatch2(); void Dispatch3();
    void Gap0(); void Gap1(); void Gap2(); void Gap3(); void Gap4(); void Gap5();
    BrushRegistered GetTask([MarshalAs(UnmanagedType.BStr)] string path);
    void Gap7(); void Gap8();
    BrushRegistered RegisterXml([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.BStr)] string xml, int flags, [MarshalAs(UnmanagedType.Struct)] object? user, [MarshalAs(UnmanagedType.Struct)] object? password, int logon, [MarshalAs(UnmanagedType.Struct)] object? sddl);
    BrushRegistered Register([MarshalAs(UnmanagedType.BStr)] string path, BrushDefinition definition, int flags, [MarshalAs(UnmanagedType.Struct)] object? user, [MarshalAs(UnmanagedType.Struct)] object? password, int logon, [MarshalAs(UnmanagedType.Struct)] object? sddl);
}
[ComImport, Guid("9c86f320-dee3-4dd1-b972-a303f26b061e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface BrushRegistered
{
    void Dispatch0(); void Dispatch1(); void Dispatch2(); void Dispatch3();
    void Gap0(); void Gap1(); int State();
    [return: MarshalAs(UnmanagedType.VariantBool)] bool Enabled();
    void Gap4(); void Gap5();
    [return: MarshalAs(UnmanagedType.Interface)] object? RunEx([MarshalAs(UnmanagedType.Struct)] object? parameters, int flags, int session, [MarshalAs(UnmanagedType.BStr)] string? user);
    void Gap7(); void Gap8(); void Gap9(); void Gap10(); void Gap11();
    BrushDefinition Definition();
    [return: MarshalAs(UnmanagedType.BStr)] string Xml();
    [return: MarshalAs(UnmanagedType.BStr)] string Security(int information);
}
[ComImport, Guid("f5bc8fc5-536d-4f77-b852-fbc1356fdeb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface BrushDefinition
{
    void Dispatch0(); void Dispatch1(); void Dispatch2(); void Dispatch3();
    void Gap0(); void Gap1(); void Gap2(); void Gap3(); void Gap4(); void Gap5(); void Gap6(); void Gap7(); void Gap8(); void Gap9(); void Gap10(); void Gap11();
    [return: MarshalAs(UnmanagedType.BStr)] string Xml();
    void SetXml([MarshalAs(UnmanagedType.BStr)] string xml);
}
