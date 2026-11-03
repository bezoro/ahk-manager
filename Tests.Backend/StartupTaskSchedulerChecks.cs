using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

// Explicit opt-in only. Tasks are disabled, use an inert fixture image, and stay
// inside one freshly created GUID folder. The production startup task is never read.
internal static class StartupTaskSchedulerChecks
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static void Run(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The Task Scheduler round trip requires Windows."
            );
        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query | TokenAccessLevels.Duplicate
        );
        string sid =
            identity.User?.Value ?? throw new InvalidOperationException("User SID unavailable.");
        string account = identity.Name;
        bool highest = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        string host = Path.Combine(root, "Bezoro.AhkManager.exe");
        File.WriteAllText(host, "Inert startup identity fixture. This is not an executable.");
        string command = ScriptPaths.QuoteArgument(host) + " --login --background";
        var specification = new StartupTaskRegistration(
            command,
            sid,
            false,
            highest,
            true,
            true,
            UserStartupService.TaskSource
        );
        var checks = new List<string>();
        void Check(bool pass, string name)
        {
            checks.Add((pass ? "PASS " : "FAIL ") + name);
            File.WriteAllLines(Path.Combine(root, "checks.txt"), checks);
            if (!pass)
                throw new InvalidOperationException(name);
        }
        using (var fixture = new TaskFixture(sid, specification))
        {
            File.WriteAllText(Path.Combine(root, "task-folder.txt"), fixture.Path);
            int index = 0;
            foreach (
                var pair in new[]
                {
                    (Principal: sid, Trigger: sid),
                    (Principal: sid, Trigger: account),
                    (Principal: account, Trigger: sid),
                    (Principal: account, Trigger: account),
                }
            )
            {
                var xml = XDocument.Parse(WindowsStartupTaskStore.Xml(specification));
                xml.Root!.Element(Ns + "Principals")!
                    .Element(Ns + "Principal")!
                    .Element(Ns + "UserId")!
                    .Value = pair.Principal;
                xml
                    .Root.Element(Ns + "Triggers")!
                    .Element(Ns + "LogonTrigger")!
                    .Element(Ns + "UserId")!
                    .Value = pair.Trigger;
                xml.Root.Element(Ns + "RegistrationInfo")!
                    .AddFirst(new XElement(Ns + "Description", fixture.Marker));
                string registered = fixture.Register(xml.ToString(SaveOptions.DisableFormatting));
                File.WriteAllText(
                    Path.Combine(root, $"registered-{index++:D2}.xml"),
                    registered,
                    Encoding.Unicode
                );
                var parsed = WindowsStartupTaskStore.Parse(registered);
                Check(
                    parsed with { Fingerprint = "" } == specification
                        && parsed.Fingerprint == Hash(registered)
                        && UserStartupService.OwnsTask(parsed, command, sid),
                    "registered SID/account principal and trigger resolve to the exact current-user task"
                );
                Check(
                    !fixture.Enabled,
                    "the temporary task remains disabled and cannot run at login"
                );
                fixture.RemoveTask();
                Check(!fixture.TaskPresent(), "only the exact observed temporary task was removed");
            }
            CheckWorkingDirectories(root, fixture, specification, Check);
        }
        Check(true, "the exact empty temporary Task Scheduler folder was removed");
        Console.WriteLine("STARTUP TASK SCHEDULER checks=" + checks.Count);
    }

    static string Hash(string xml) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));

    static void CheckWorkingDirectories(
        string root,
        TaskFixture fixture,
        StartupTaskRegistration specification,
        Action<bool, string> check
    )
    {
        string directory = Path.GetDirectoryName(
            ScriptPaths.SplitCommand(specification.Command)[0]
        )!;
        foreach (
            var variant in new[]
            {
                (Name: "case", Directory: directory.ToUpperInvariant()),
                (Name: "trailing", Directory: directory + Path.DirectorySeparatorChar),
            }
        )
        {
            var document = XDocument.Parse(fixture.Definition(specification));
            document
                .Root!.Element(Ns + "Actions")!
                .Element(Ns + "Exec")!
                .Element(Ns + "WorkingDirectory")!
                .Value = variant.Directory;
            string registered = fixture.Register(document.ToString(SaveOptions.DisableFormatting));
            File.WriteAllText(
                Path.Combine(root, $"cwd-{variant.Name}.xml"),
                registered,
                Encoding.Unicode
            );
            var observed = fixture.Observe();
            check(
                observed.Registration with { Fingerprint = "" } == specification
                    && observed.Registration.WorkingDirectoryMatches
                    && UserStartupService.OwnsTask(
                        observed.Registration,
                        specification.Command,
                        specification.UserSid
                    )
                    && !fixture.Enabled,
                "native Scheduler preserves owned disabled task identity for an equivalent working directory"
            );
            fixture.RemoveTask();
            check(
                !fixture.TaskPresent(),
                "equivalent-directory fixture cleanup removes only its observed task"
            );
        }

        string previousDirectory = Path.Combine(root, "previous App");
        Directory.CreateDirectory(previousDirectory);
        foreach (
            var variant in new[]
            {
                (Name: "missing", Directory: (string?)null),
                (Name: "stale", Directory: (string?)previousDirectory),
            }
        )
        {
            var document = XDocument.Parse(fixture.Definition(specification));
            var working = document
                .Root!.Element(Ns + "Actions")!
                .Element(Ns + "Exec")!
                .Element(Ns + "WorkingDirectory")!;
            if (variant.Directory is null)
                working.Remove();
            else
                working.Value = variant.Directory;
            string registered = fixture.Register(document.ToString(SaveOptions.DisableFormatting));
            File.WriteAllText(
                Path.Combine(root, $"cwd-{variant.Name}-original.xml"),
                registered,
                Encoding.Unicode
            );
            var original = fixture.Observe();
            string nativeDirectory =
                XDocument
                    .Parse(original.Xml)
                    .Root!.Element(Ns + "Actions")!
                    .Element(Ns + "Exec")!
                    .Element(Ns + "WorkingDirectory")
                    ?.Value
                ?? "";
            check(
                original.Registration.NonstandardWorkingDirectory == nativeDirectory
                    && !original.Registration.WorkingDirectoryMatches
                    && UserStartupService.OwnsTask(
                        original.Registration,
                        specification.Command,
                        specification.UserSid
                    )
                    && !fixture.Enabled,
                "native missing or stale working directory remains a provably owned disabled task"
            );
            check(
                fixture.CompareExchange(original.Registration, specification),
                "native directory repair replaces only the exact observed XML fingerprint"
            );
            var repaired = fixture.Observe();
            File.WriteAllText(
                Path.Combine(root, $"cwd-{variant.Name}-repaired.xml"),
                repaired.Xml,
                Encoding.Unicode
            );
            check(
                repaired.Registration with { Fingerprint = "" } == specification
                    && repaired.Owner == original.Owner
                    && repaired.Registration.WorkingDirectoryMatches
                    && !fixture.Enabled,
                "native directory repair preserves owner source account privileges and disabled state"
            );
            check(
                fixture.CompareExchange(repaired.Registration, original.Registration),
                "native rollback recreates the captured original nonstandard directory definition"
            );
            var restored = fixture.Observe();
            File.WriteAllText(
                Path.Combine(root, $"cwd-{variant.Name}-restored.xml"),
                restored.Xml,
                Encoding.Unicode
            );
            check(
                restored.Registration with
                {
                    Fingerprint = original.Registration.Fingerprint,
                } == original.Registration
                    && restored.Owner == original.Owner
                    && !fixture.Enabled,
                "native rollback retains the original directory and task identity without claiming a canonical repair"
            );
            File.WriteAllLines(
                Path.Combine(root, $"cwd-{variant.Name}-owners.txt"),
                [
                    "Original: " + original.Owner,
                    "Repaired: " + repaired.Owner,
                    "Restored: " + restored.Owner,
                ]
            );
            fixture.RemoveTask();
            check(
                !fixture.TaskPresent(),
                "nonstandard-directory fixture cleanup removes only its observed task"
            );
        }

        fixture.Register(fixture.Definition(specification));
        var before = fixture.Observe();
        fixture.RemoveTask();
        var concurrentDefinition = XDocument.Parse(fixture.Definition(specification));
        concurrentDefinition
            .Root!.Element(Ns + "RegistrationInfo")!
            .Add(new XElement(Ns + "Documentation", "Controlled concurrent XML revision"));
        fixture.Register(concurrentDefinition.ToString(SaveOptions.DisableFormatting));
        var concurrent = fixture.Observe();
        File.WriteAllText(
            Path.Combine(root, "cwd-concurrent.xml"),
            concurrent.Xml,
            Encoding.Unicode
        );
        int writes = fixture.Writes;
        check(
            concurrent.Registration.Fingerprint != before.Registration.Fingerprint
                && concurrent.Registration with
                {
                    Fingerprint = before.Registration.Fingerprint,
                } == before.Registration
                && !fixture.CompareExchange(before.Registration, specification)
                && fixture.Writes == writes
                && fixture.Observe().Xml == concurrent.Xml
                && !fixture.Enabled,
            "native compare exchange preserves concurrent XML even when its projected task identity is unchanged"
        );
        fixture.RemoveTask();
        check(
            !fixture.TaskPresent(),
            "concurrent-definition fixture cleanup removes only the acknowledged replacement"
        );
    }

    sealed record TaskObservation(string Xml, StartupTaskRegistration Registration, string Owner);

    sealed class TaskFixture : IDisposable
    {
        const string TaskName = "IdentityRoundTrip";
        readonly string sid;
        readonly StartupTaskRegistration specification;
        readonly string name =
            "Bezoro.AhkManager.Tests.StartupIdentity." + Guid.NewGuid().ToString("N");
        object? service;
        object? root;
        object? folder;
        string? observedHash;
        bool created;
        internal string Path => "\\" + name;
        internal string Marker => "AHK Manager disabled identity fixture " + name;
        internal bool Enabled { get; private set; }
        internal int Writes { get; private set; }

        internal TaskFixture(string sid, StartupTaskRegistration specification)
        {
            this.sid = sid;
            this.specification = specification;
            try
            {
                service = Activator.CreateInstance(
                    Type.GetTypeFromProgID("Schedule.Service")
                        ?? throw new InvalidOperationException("Task Scheduler unavailable.")
                );
                ((dynamic)service!).Connect();
                root = ((dynamic)service).GetFolder("\\");
                folder = ((dynamic)root).CreateFolder(name, null);
                created = true;
                if ((string)((dynamic)folder).Path != Path)
                    throw new InvalidOperationException("The temporary task folder path changed.");
            }
            catch
            {
                Release(folder);
                Release(root);
                Release(service);
                throw;
            }
        }

        internal string Register(string xml)
        {
            if (!Owns(xml))
                throw new InvalidOperationException(
                    "Only this fixture's disabled task may be registered."
                );
            if (observedHash is not null || TaskPresent())
                throw new InvalidOperationException(
                    "The temporary task must be absent before creation."
                );
            object? task = null;
            try
            {
                // TASK_CREATE only. A task supplied concurrently is never overwritten.
                task = ((dynamic)folder!).RegisterTask(TaskName, xml, 2, sid, null, 3, null);
                Writes++;
                string persisted = (string)((dynamic)task).Xml;
                if ((string)((dynamic)task).Path != Path + "\\" + TaskName || !Owns(persisted))
                    throw new InvalidOperationException(
                        "Temporary task creation did not establish ownership."
                    );
                observedHash = Hash(persisted);
                Enabled = (bool)((dynamic)task).Enabled;
                return persisted;
            }
            finally
            {
                Release(task);
            }
        }

        internal string Definition(StartupTaskRegistration registration)
        {
            var document = XDocument.Parse(WindowsStartupTaskStore.Xml(registration));
            document
                .Root!.Element(Ns + "RegistrationInfo")!
                .AddFirst(new XElement(Ns + "Description", Marker));
            return document.ToString(SaveOptions.DisableFormatting);
        }

        internal TaskObservation Observe()
        {
            object? task = null;
            try
            {
                task = ((dynamic)folder!).GetTask(TaskName);
                string xml = (string)((dynamic)task).Xml;
                if ((string)((dynamic)task).Path != Path + "\\" + TaskName || !Owns(xml))
                    throw new InvalidOperationException(
                        "The observed task is not this disabled fixture."
                    );
                Enabled = (bool)((dynamic)task).Enabled;
                string owner =
                    new RawSecurityDescriptor((string)((dynamic)task).GetSecurityDescriptor(1))
                        .Owner
                        ?.Value
                    ?? throw new InvalidOperationException("Temporary task owner unavailable.");
                return new(xml, WindowsStartupTaskStore.Parse(xml), owner);
            }
            finally
            {
                Release(task);
            }
        }

        internal bool CompareExchange(
            StartupTaskRegistration expected,
            StartupTaskRegistration replacement
        )
        {
            if (Observe().Registration != expected)
                return false;
            RemoveTask();
            Register(Definition(replacement));
            return true;
        }

        internal bool TaskPresent()
        {
            object? task = null;
            try
            {
                task = ((dynamic)folder!).GetTask(TaskName);
                return true;
            }
            catch (Exception error) when (WindowsStartupTaskStore.Missing(error))
            {
                return false;
            }
            finally
            {
                Release(task);
            }
        }

        bool Owns(string xml)
        {
            var document = XDocument.Parse(xml);
            // Cleanup is independent of the parser under test. TASK_CREATE's
            // acknowledgement, unique nonce, exact path and observed XML hash
            // bind this disabled definition even if the parser assertion fails.
            return document
                    .Root?.Element(Ns + "RegistrationInfo")
                    ?.Element(Ns + "Description")
                    ?.Value == Marker
                && document.Root.Element(Ns + "RegistrationInfo")?.Element(Ns + "Source")?.Value
                    == specification.Source
                && document.Root.Element(Ns + "Settings")?.Element(Ns + "Enabled")?.Value
                    is "false"
                        or "0";
        }

        internal void RemoveTask()
        {
            object? task = null;
            try
            {
                try
                {
                    task = ((dynamic)folder!).GetTask(TaskName);
                }
                catch (Exception error) when (WindowsStartupTaskStore.Missing(error))
                {
                    observedHash = null;
                    return;
                }
                string xml = (string)((dynamic)task).Xml;
                if (
                    observedHash is null
                    || Hash(xml) != observedHash
                    || !Owns(xml)
                    || (string)((dynamic)task).Path != Path + "\\" + TaskName
                )
                    throw new InvalidOperationException(
                        "The temporary task changed. It was preserved at " + Path + "\\" + TaskName
                    );
                Release(task);
                task = null;
                ((dynamic)folder!).DeleteTask(TaskName, 0);
                Writes++;
                observedHash = null;
            }
            finally
            {
                Release(task);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!created || folder is null)
                    return;
                RemoveTask();
                object? tasks = null;
                object? folders = null;
                try
                {
                    tasks = ((dynamic)folder).GetTasks(1);
                    folders = ((dynamic)folder).GetFolders(0);
                    if (
                        (int)((dynamic)tasks).Count != 0
                        || (int)((dynamic)folders).Count != 0
                        || (string)((dynamic)folder).Path != Path
                    )
                        throw new InvalidOperationException(
                            "The temporary folder gained foreign contents. It was preserved at "
                                + Path
                        );
                }
                finally
                {
                    Release(folders);
                    Release(tasks);
                }
                ((dynamic)root!).DeleteFolder(name, 0);
                created = false;
            }
            finally
            {
                Release(folder);
                Release(root);
                Release(service);
            }
        }

        static void Release(object? value)
        {
            if (value is not null && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
    }
}
