using System.ComponentModel;
using System.Security;
using System.Text;
using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckAdmissionDiagnostics(Action<bool, string> check)
    {
        const string sensitive =
            "private fixture path C:\\Users\\fixture\\profile\nforged diagnostic";
        (Exception Exception, string Code)[] cases =
        [
            (new OperationCanceledException(sensitive), "cancelled"),
            (new InvalidDataException(sensitive), "invalid-data"),
            (new UnauthorizedAccessException(sensitive), "access-denied"),
            (new FileNotFoundException(sensitive, sensitive), "file-not-found"),
            (new DirectoryNotFoundException(sensitive), "directory-not-found"),
            (new ArgumentException(sensitive), "invalid-argument"),
            (new InvalidOperationException(sensitive), "invalid-state"),
            (new JsonException(sensitive), "invalid-json"),
            (new SecurityException(sensitive), "security-error"),
            (new TimeoutException(sensitive), "timeout"),
            (new Win32Exception(87, sensitive), "native-error"),
            (new IOException(sensitive), "io-error"),
            (
                new StoragePathException(StoragePathFailure.UnsupportedFileSystem),
                "unsupported-filesystem"
            ),
            (new StoragePathException(StoragePathFailure.RedirectedPath), "redirected-path"),
            (
                new StoragePathException(StoragePathFailure.CloudFileUnavailable),
                "cloud-file-unavailable"
            ),
        ];
        foreach (UpdatePhase phase in Enum.GetValues<UpdatePhase>())
        {
            bool valid = true;
            foreach (var item in cases)
            {
                string line = AdmissionDiagnostics.Encode(phase, item.Exception);
                using var document = JsonDocument.Parse(line[AdmissionDiagnostics.Prefix.Length..]);
                var root = document.RootElement;
                valid &=
                    line.StartsWith(AdmissionDiagnostics.Prefix, StringComparison.Ordinal)
                    && line.Length < 512
                    && Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(line)) == line
                    && !line.Contains('\n')
                    && !line.Contains('\r')
                    && !line.Contains("fixture", StringComparison.Ordinal)
                    && root.EnumerateObject()
                        .Select(property => property.Name)
                        .SequenceEqual(["schemaVersion", "phase", "code"])
                    && root.GetProperty("schemaVersion").GetInt32() == 1
                    && root.GetProperty("phase").GetString() is { Length: > 0 } name
                    && name.All(c => char.IsAsciiLetterLower(c) || c == '-')
                    && root.GetProperty("code").GetString()
                        == (
                            phase == UpdatePhase.PortableInitialization
                                ? item.Code switch
                                {
                                    "file-not-found" or "directory-not-found" =>
                                        "incomplete-package",
                                    "invalid-data" or "invalid-json" => "invalid-package",
                                    _ => item.Code,
                                }
                                : item.Code
                        );
            }
            check(valid, "bounded path-free refusal diagnostics cover " + phase);
        }
        foreach (
            var item in new (Exception Failure, string Code)[]
            {
                (new DirectoryNotFoundException(sensitive), "directory-not-found"),
                (new Win32Exception(3, sensitive), "directory-not-found"),
                (new UnauthorizedAccessException(sensitive), "access-denied"),
                (
                    new StoragePathException(StoragePathFailure.UnsupportedFileSystem),
                    "unsupported-filesystem"
                ),
                (
                    new StoragePathException(StoragePathFailure.CloudFileUnavailable),
                    "cloud-file-unavailable"
                ),
            }
        )
        {
            var failure = new PortableDataInitializationException(item.Failure);
            using var diagnostic = JsonDocument.Parse(
                AdmissionDiagnostics.Encode(
                    UpdatePhase.PortableDataInitialization,
                    failure.Failure
                )[AdmissionDiagnostics.Prefix.Length..]
            );
            check(
                ReferenceEquals(failure.Failure, item.Failure)
                    && ReferenceEquals(failure.InnerException, item.Failure)
                    && diagnostic.RootElement.GetProperty("phase").GetString()
                        == "portable-data-initialization"
                    && diagnostic.RootElement.GetProperty("code").GetString() == item.Code,
                "portable data failures retain their reason without implying an incomplete package"
            );
            using var updateDiagnostic = JsonDocument.Parse(
                AdmissionDiagnostics.Encode(UpdatePhase.Arguments, failure)[
                    AdmissionDiagnostics.Prefix.Length..
                ]
            );
            check(
                updateDiagnostic.RootElement.GetProperty("phase").GetString() == "arguments"
                    && updateDiagnostic.RootElement.GetProperty("code").GetString() == item.Code,
                "a data location refusal during an update preserves its original reason and phase"
            );
        }
        foreach (
            var item in new[]
            {
                (2, "file-not-found"),
                (3, "directory-not-found"),
                (5, "access-denied"),
                (32, "sharing-violation"),
                (33, "lock-violation"),
            }
        )
        {
            foreach (int error in new[] { item.Item1, unchecked((int)0x80070000) | item.Item1 })
            {
                foreach (
                    Exception exception in new Exception[]
                    {
                        new Win32Exception(error, sensitive),
                        new IOException(sensitive, error),
                    }
                )
                {
                    using var document = JsonDocument.Parse(
                        AdmissionDiagnostics.Encode(UpdatePhase.HostLease, exception)[
                            AdmissionDiagnostics.Prefix.Length..
                        ]
                    );
                    check(
                        document.RootElement.GetProperty("code").GetString() == item.Item2,
                        "native and HRESULT file failures retain their fixed safe reason"
                    );
                }
            }
        }
        using var output = new StringWriter();
        TextWriter original = Console.Error;
        try
        {
            Console.SetError(output);
            AdmissionDiagnostics.Write(UpdatePhase.PlanRead, new JsonException(sensitive));
        }
        finally
        {
            Console.SetError(original);
        }
        string expected = AdmissionDiagnostics.Encode(UpdatePhase.PlanRead, new JsonException());
        check(
            output.ToString() == expected + output.NewLine,
            "refusal diagnostics emit exactly one complete stderr line"
        );
        foreach (
            Exception failure in new Exception[]
            {
                new IOException("closed diagnostic pipe"),
                new UnauthorizedAccessException("unavailable diagnostic sink"),
                new ObjectDisposedException("diagnostic sink"),
            }
        )
        {
            using var unavailable = new UnavailableDiagnosticSink(failure);
            try
            {
                Console.SetError(unavailable);
                AdmissionDiagnostics.Write(UpdatePhase.Apply, new IOException(sensitive));
            }
            finally
            {
                Console.SetError(original);
            }
            check(true, "a lost diagnostic sink cannot change the update result");
        }
        bool refused = false;
        try
        {
            AdmissionDiagnostics.Encode((UpdatePhase)(-1), new IOException(sensitive));
        }
        catch (ArgumentOutOfRangeException)
        {
            refused = true;
        }
        check(refused, "unknown diagnostic phases cannot enter the stderr protocol");
        refused = false;
        try
        {
            AdmissionDiagnostics.Encode(UpdatePhase.Arguments, new Exception(sensitive));
        }
        catch (ArgumentException)
        {
            refused = true;
        }
        check(refused, "unknown exception types cannot become arbitrary diagnostic text");
    }

    sealed class UnavailableDiagnosticSink(Exception failure) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw failure;
    }
}
