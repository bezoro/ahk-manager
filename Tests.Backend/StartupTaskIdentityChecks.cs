using System.Collections.Immutable;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class StartupTaskIdentityChecks
{
    const string UserSid = "S-1-5-21-100-200-300-1001";
    const string OtherSid = "S-1-5-21-100-200-300-1002";
    const string Account = @"DESKTOP-FIXTURE\Alice";
    const string OtherAccount = @"DESKTOP-FIXTURE\Bob";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        CheckSidPolicy(check);
        var adapter = new Adapter();
        var startup = new UserStartupService(
            adapter,
            Path.Combine(root, "identity manager", "Bezoro.AhkManager.exe")
        );
        var specification = new StartupTaskRegistration(
            startup.Command,
            UserSid,
            true,
            true,
            true,
            true,
            UserStartupService.TaskSource
        );
        string template = WindowsStartupTaskStore.Xml(specification);
        string Xml(string principal, string trigger)
        {
            var document = XDocument.Parse(template);
            document
                .Root!.Element(Ns + "Principals")!
                .Element(Ns + "Principal")!
                .Element(Ns + "UserId")!
                .Value = principal;
            document
                .Root.Element(Ns + "Triggers")!
                .Element(Ns + "LogonTrigger")!
                .Element(Ns + "UserId")!
                .Value = trigger;
            return document.ToString(SaveOptions.DisableFormatting);
        }
        static string? Lookup(string name) =>
            name.ToUpperInvariant() switch
            {
                "DESKTOP-FIXTURE\\ALICE"
                or "ALICE@CONTOSO.EXAMPLE"
                or "MICROSOFTACCOUNT\\ALICE+TAG@EXAMPLE.COM"
                or "S-PC\\ALICE"
                or "S-ALICE@CONTOSO.EXAMPLE"
                or "S-ALICE"
                or "S-1-DOMAIN\\ALICE" => UserSid,
                "DESKTOP-FIXTURE\\BOB" => OtherSid,
                _ => throw new IdentityNotMappedException("The fixture account is not mapped."),
            };

        foreach (
            var pair in new[]
            {
                (Principal: UserSid, Trigger: UserSid),
                (Principal: UserSid, Trigger: Account),
                (Principal: Account, Trigger: UserSid),
                (Principal: Account, Trigger: Account.ToLowerInvariant()),
                (Principal: Account, Trigger: "alice@contoso.example"),
                (Principal: UserSid, Trigger: @"MicrosoftAccount\alice+tag@example.com"),
                (Principal: UserSid, Trigger: @"S-PC\Alice"),
                (Principal: @"S-PC\Alice", Trigger: UserSid),
                (Principal: "s-alice@contoso.example", Trigger: UserSid),
                (Principal: UserSid, Trigger: "S-Alice"),
                (Principal: UserSid, Trigger: @"S-1-DOMAIN\Alice"),
            }
        )
        {
            string xml = Xml(pair.Principal, pair.Trigger);
            var lookups = new List<string>();
            var parsed = WindowsStartupTaskStore.Parse(
                xml,
                name =>
                {
                    lookups.Add(name);
                    return Lookup(name);
                }
            );
            check(
                parsed with { Fingerprint = "" } == specification
                    && parsed.Fingerprint == Hash(xml)
                    && UserStartupService.OwnsTask(parsed, startup.Command, UserSid),
                "equivalent principal and logon account representations retain exact SID ownership: "
                    + pair.Principal
                    + " / "
                    + pair.Trigger
            );
            check(
                lookups.SequenceEqual(
                    new[] { pair.Principal, pair.Trigger }.Where(value => value != UserSid)
                ),
                "literal SIDs bypass lookup while each account name reaches Windows resolution unchanged"
            );
            adapter.Task = parsed;
            check(
                startup.RegistrationStatus(true, true) == ReasonCode.None
                    && (await startup.SetEnabledAsync(true, false, true)).Succeeded
                    && adapter.Writes == 0,
                "a resolved current-user administrator task is recognized without consent or rewriting"
            );
        }
        var direct = WindowsStartupTaskStore.Parse(
            Xml(UserSid, UserSid),
            _ => throw new InvalidOperationException("SID values must not require account lookup.")
        );
        check(
            direct.UserLogonOnly && direct.UserSid == UserSid,
            "literal SIDs need no account lookup"
        );
        var maximumRid = WindowsStartupTaskStore.Parse(
            Xml("S-1-5-4294967295", "S-1-5-4294967295"),
            _ =>
                throw new InvalidOperationException(
                    "A bounded literal SID needs no account lookup."
                )
        );
        check(
            maximumRid.UserLogonOnly && maximumRid.UserSid == "S-1-5-4294967295",
            "the maximum UInt32 subauthority remains a valid literal SID"
        );
        var decimalAuthority = WindowsStartupTaskStore.Parse(
            Xml("S-1-281474976710655-1", "S-1-281474976710655-1")
        );
        var hexAuthority = WindowsStartupTaskStore.Parse(
            Xml("S-1-0xFFFFFFFFFFFF-1", "S-1-0xFFFFFFFFFFFF-1")
        );
        check(
            decimalAuthority.UserLogonOnly
                && hexAuthority.UserLogonOnly
                && decimalAuthority.UserSid == hexAuthority.UserSid,
            "decimal and hexadecimal 48-bit authorities canonicalize to the same exact SID"
        );

        foreach (
            var pair in new[]
            {
                (Principal: UserSid, Trigger: OtherAccount),
                (Principal: OtherAccount, Trigger: UserSid),
                (Principal: OtherAccount, Trigger: OtherAccount),
            }
        )
        {
            var parsed = WindowsStartupTaskStore.Parse(Xml(pair.Principal, pair.Trigger), Lookup);
            check(
                !UserStartupService.OwnsTask(parsed, startup.Command, UserSid),
                "another resolved account cannot become the current user's startup task"
            );
        }
        foreach (
            string malformed in new[]
            {
                "",
                " ",
                " " + Account,
                Account + " ",
                Account + "\t",
                "S-1-5-21-invalid",
                "S-1",
                "S-1-",
                "S-1-5-21-4294967296",
                @"\Alice",
                @"DOMAIN\",
                @"DOMAIN\\Alice",
                @"DOMAIN\Alice\Other",
                new string('a', 513),
            }
        )
        {
            foreach (bool principal in new[] { true, false })
            {
                string xml = principal ? Xml(malformed, UserSid) : Xml(UserSid, malformed);
                var parsed = WindowsStartupTaskStore.Parse(
                    xml,
                    _ =>
                        throw new InvalidOperationException(
                            "Malformed identities cannot be looked up."
                        )
                );
                check(
                    !parsed.UserLogonOnly
                        && parsed.Command.Length == 0
                        && parsed.Fingerprint == Hash(xml),
                    "malformed "
                        + (principal ? "principal" : "trigger")
                        + " identity fails closed with its XML fingerprint: '"
                        + malformed.Replace("\t", @"\t")
                        + "'"
                );
            }
        }
        foreach (string unmapped in new[] { "DOMAIN/Alice", @"DOMAIN\*", "S1-5-21-invalid" })
        {
            var parsed = WindowsStartupTaskStore.Parse(Xml(UserSid, unmapped), Lookup);
            check(
                !parsed.UserLogonOnly && parsed.Command.Length == 0,
                "a malformed account name cannot establish ownership when Windows cannot map it"
            );
        }
        foreach (
            Func<Exception> failure in new Func<Exception>[]
            {
                () => new IdentityNotMappedException("Unmapped fixture."),
                () => new Win32Exception(1332),
                () => new UnauthorizedAccessException("Denied fixture."),
                () => new System.Security.SecurityException("Unavailable fixture."),
            }
        )
        {
            foreach (bool principal in new[] { true, false })
            {
                string xml = principal ? Xml(Account, UserSid) : Xml(UserSid, Account);
                var parsed = WindowsStartupTaskStore.Parse(xml, _ => throw failure());
                check(
                    !parsed.UserLogonOnly
                        && parsed.Command.Length == 0
                        && parsed.Fingerprint == Hash(xml),
                    "unresolved or denied account resolution never establishes startup ownership"
                );
            }
        }
        foreach (
            string? invalidResult in new[]
            {
                null,
                "",
                "not-a-sid",
                "S-1-5-invalid",
                "S-1-5-4294967296",
            }
        )
        {
            var parsed = WindowsStartupTaskStore.Parse(Xml(Account, UserSid), _ => invalidResult);
            check(
                !parsed.UserLogonOnly && parsed.Command.Length == 0,
                "account lookup must produce a valid SID"
            );
        }

        foreach (string field in new[] { "Source", "Arguments", "LogonType" })
        {
            var document = XDocument.Parse(Xml(UserSid, Account));
            var element =
                field == "Source"
                    ? document.Root!.Element(Ns + "RegistrationInfo")!.Element(Ns + field)!
                : field == "Arguments"
                    ? document
                        .Root!.Element(Ns + "Actions")!
                        .Element(Ns + "Exec")!
                        .Element(Ns + field)!
                : document
                    .Root!.Element(Ns + "Principals")!
                    .Element(Ns + "Principal")!
                    .Element(Ns + field)!;
            element.Value += "-foreign";
            var parsed = WindowsStartupTaskStore.Parse(document.ToString(), Lookup);
            check(
                !UserStartupService.OwnsTask(parsed, startup.Command, UserSid),
                "account normalization does not relax startup " + field + " ownership checks"
            );
        }
        var least = XDocument.Parse(Xml(Account, Account));
        least
            .Root!.Element(Ns + "Principals")!
            .Element(Ns + "Principal")!
            .Element(Ns + "RunLevel")!
            .Value = "LeastPrivilege";
        adapter.Task = WindowsStartupTaskStore.Parse(least.ToString(), Lookup);
        check(
            startup.RegistrationStatus(true, true) == ReasonCode.StartupInactive
                && adapter.Writes == 0,
            "resolved identities do not hide a task with the wrong administrator run level"
        );

        var original = WindowsStartupTaskStore.Parse(Xml(UserSid, UserSid), Lookup);
        var equivalent = WindowsStartupTaskStore.Parse(Xml(UserSid, Account), Lookup);
        check(
            original.Fingerprint != equivalent.Fingerprint
                && original with { Fingerprint = equivalent.Fingerprint } == equivalent,
            "account normalization preserves distinct raw XML identities for compare and exchange"
        );
        adapter.Task = original;
        adapter.BeforeCompare = () => adapter.Task = equivalent;
        var conflict = await startup.SetEnabledAsync(false, true, true);
        check(
            conflict.Code == ReasonCode.StartupEntryConflict
                && adapter.Task == equivalent
                && adapter.Writes == 0,
            "a concurrent equivalent account rewrite cannot bypass the observed XML fingerprint guard"
        );
    }

    static void CheckSidPolicy(Action<bool, string> check)
    {
        foreach (
            var sample in new[]
            {
                (Text: "S-1-5-0", Valid: true),
                (Text: "S-1-5-4294967295", Valid: true),
                (Text: "S-1-5-4294967296", Valid: false),
                (Text: "S-1-5-999999999999999999999999999999999", Valid: false),
                (Text: "S-1-281474976710655-1", Valid: true),
                (Text: "S-1-281474976710656-1", Valid: false),
                (Text: "S-1-0xFFFFFFFFFFFF-1", Valid: true),
                (Text: "S-1-0x1000000000000-1", Valid: false),
                (Text: "S-1-0xFFFFFFFFFFFFFFFFF-1", Valid: false),
                (Text: "S-2-5-1", Valid: false),
                (Text: "S-256-5-1", Valid: false),
                (Text: "S-1-5--1", Valid: false),
                (Text: "S-1-5-+1", Valid: false),
                (Text: "S-1-5-0x1", Valid: false),
                (Text: "S-1-5-1 ", Valid: false),
                (Text: "S-1\0-5-1", Valid: false),
                (Text: "S-1-5\0-1", Valid: false),
                (Text: "S-1-0x5\0-1", Valid: false),
                (Text: "S-1-5-1\0", Valid: false),
                (Text: "S-1-5-1-", Valid: false),
                (
                    Text: "S-1-5-" + string.Join('-', Enumerable.Repeat("4294967295", 15)),
                    Valid: true
                ),
                (Text: "S-1-5-" + string.Join('-', Enumerable.Repeat("1", 16)), Valid: false),
            }
        )
        {
            bool valid = WindowsStartupTaskStore.TryDecodeSid(sample.Text, out byte[] binary);
            check(
                valid == sample.Valid && (valid || binary.Length == 0),
                "SID numeric bounds and syntax are enforced before Windows conversion: "
                    + sample.Text.Replace("\0", "\\0", StringComparison.Ordinal)
            );
        }
        check(
            WindowsStartupTaskStore.TryDecodeSid(
                "S-1-0x010203040506-4294967295",
                out byte[] encoded
            ) && encoded.SequenceEqual(new byte[] { 1, 1, 1, 2, 3, 4, 5, 6, 255, 255, 255, 255 }),
            "binary SID encoding preserves big-endian 48-bit authority and little-endian UInt32 subauthorities"
        );
    }

    static string Hash(string xml) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));

    sealed class Adapter : IUserStartupAdapter, IUserStartupTaskAdapter
    {
        public string UserSid => StartupTaskIdentityChecks.UserSid;
        internal StartupTaskRegistration? Task;
        internal Action? BeforeCompare;
        internal int Writes;

        public string? ReadEntry() => null;

        public bool CompareExchangeEntry(string? expected, string? replacement) => false;

        public StartupTaskRegistration? ReadTask() => Task;

        public bool CompareExchangeTask(
            StartupTaskRegistration? expected,
            StartupTaskRegistration? replacement
        )
        {
            BeforeCompare?.Invoke();
            BeforeCompare = null;
            if (Task != expected)
                return false;
            Task = replacement;
            Writes++;
            return true;
        }

        public Task<StartupInspection> InspectAsync(CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(
                new StartupInspection(true, ImmutableArray<string>.Empty, [])
            );
    }
}
