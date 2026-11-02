namespace Bezoro.AhkManager.UpdateHost;

internal sealed class PortableInitializationContextException(
    string stage,
    string resource,
    Exception failure
) : IOException("Portable initialization could not complete.", failure)
{
    internal string Stage { get; } = stage;
    internal string Resource { get; } = resource;
    internal Exception Failure { get; } = failure;
}

// This supplements the legacy refusal with finite context before any diagnostic path is trusted.
internal static class PortableInitializationDiagnostics
{
    internal const string Prefix = "AHK_PORTABLE_INITIALIZATION:";
    static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "resolve-layout",
        "retain-root",
        "retain-data",
        "open-marker",
        "verify-marker",
        "verify-helper",
        "prepare-app",
        "verify-root-members",
        "open-inventory",
        "verify-version",
        "protect-root",
        "protect-app",
        "protect-directory",
        "protect-file",
        "protect-descriptor",
        "protect-marker",
        "verify-root-access",
        "verify-app-access",
        "prepare-data",
        "create-data",
        "verify-data-access",
    };
    static readonly HashSet<string> Resources = new(StringComparer.Ordinal)
    {
        "root",
        "application",
        "data",
        "marker",
        "descriptor",
        "helper",
        "payload-directory",
        "payload-file",
    };

    internal static T Step<T>(string stage, string resource, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (PortableDataInitializationException data)
        {
            throw new PortableDataInitializationException(
                data.Failure is PortableInitializationContextException
                    ? data.Failure
                    : new PortableInitializationContextException(stage, "data", data.Failure)
            );
        }
        catch (Exception failure)
            when (UpdateProtocol.Known(failure)
                && failure is not PortableInitializationContextException
            )
        {
            throw new PortableInitializationContextException(stage, resource, failure);
        }
    }

    internal static void Step(string stage, string resource, Action action) =>
        Step(
            stage,
            resource,
            () =>
            {
                action();
                return 0;
            }
        );

    internal static Exception Original(Exception failure)
    {
        for (int depth = 0; depth < 16; depth++)
        {
            if (failure is PortableInitializationContextException context)
                failure = context.Failure;
            else if (failure is PortableDataInitializationException data)
                failure = data.Failure;
            else
                break;
        }
        return failure;
    }

    static (string? UserSid, bool? Administrator) Subject()
    {
        if (!OperatingSystem.IsWindows())
            return (null, null);
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;
            if (
                sid is null
                || sid.Length > 184
                || !System.Text.RegularExpressions.Regex.IsMatch(sid, @"\AS-\d+(?:-\d+){1,15}\z")
            )
                return (null, null);
            bool administrator = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
                System.Security.Principal.WindowsBuiltInRole.Administrator
            );
            return (sid, administrator);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    internal static string? Encode(UpdatePhase phase, Exception failure)
    {
        string? phaseName = phase switch
        {
            UpdatePhase.PortableInitialization => "portable-initialization",
            UpdatePhase.PortableDataInitialization => "portable-data-initialization",
            _ => null,
        };
        if (phaseName is null)
            return null;
        for (int depth = 0; depth < 16; depth++)
        {
            if (failure is PortableDataInitializationException data)
            {
                failure = data.Failure;
                continue;
            }
            if (failure is not PortableInitializationContextException context)
                return null;
            if (!Stages.Contains(context.Stage) || !Resources.Contains(context.Resource))
                return null;
            var original = Original(context);
            var subject = Subject();
            string nativeError =
                original is System.ComponentModel.Win32Exception native
                && native.NativeErrorCode is >= 0 and <= 65535
                    ? native.NativeErrorCode.ToString(
                        System.Globalization.CultureInfo.InvariantCulture
                    )
                    : "null";
            return Prefix
                + "{\"schemaVersion\":1,\"phase\":\""
                + phaseName
                + "\",\"stage\":\""
                + context.Stage
                + "\",\"resource\":\""
                + context.Resource
                + "\",\"hresult\":\"0x"
                + unchecked((uint)original.HResult).ToString("X8")
                + "\",\"nativeError\":"
                + nativeError
                + ",\"userSid\":"
                + (subject.UserSid is null ? "null" : "\"" + subject.UserSid + "\"")
                + ",\"administrator\":"
                + (
                    subject.Administrator is null ? "null"
                    : subject.Administrator.Value ? "true"
                    : "false"
                )
                + "}";
        }
        return null;
    }

    internal static void Write(UpdatePhase phase, Exception failure)
    {
        try
        {
            if (Encode(phase, failure) is { } line)
                Console.Error.WriteLine(line);
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        { }
    }
}
