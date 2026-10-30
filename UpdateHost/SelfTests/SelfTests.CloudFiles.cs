using System.Runtime.InteropServices;
using System.Text;
using Bezoro.AhkManager.Core.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static async Task CheckCloudFiles(string root, Action<bool, string> check)
    {
        if (!CloudFilesNative.Available(out string unavailable))
        {
            Console.WriteLine("SKIP native Cloud Files integration: " + unavailable);
            return;
        }

        string syncRoot = Path.Combine(root, "cloud-files-" + Guid.NewGuid().ToString("N"));
        SnapshotSecurity.CreatePrivateDirectory(syncRoot);
        WindowsStoragePath.RequireNtfs(syncRoot);
        byte[] nonce = Encoding.UTF8.GetBytes("AHK-Manager-test:" + Guid.NewGuid().ToString("D"));
        var expected = new Dictionary<string, Native.FileStamp>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = CloudFilesNative.Identity(syncRoot),
        };
        using var retainedRoot = new DirectoryLease([syncRoot]);
        int registration = CloudFilesNative.Register(syncRoot, nonce);
        if (CloudFilesNative.Unsupported(registration))
        {
            Console.WriteLine(
                "SKIP native Cloud Files integration: registration unsupported, HRESULT=0x"
                    + registration.ToString("X8")
            );
            return;
        }
        CloudFilesNative.RequireSuccess(registration, "register the isolated Cloud Files root");
        try
        {
            CloudFilesNative.VerifyRegistration(syncRoot, nonce, expected[""]);
            var layout = CreatePortableFixture(Path.Combine(syncRoot, "Manager"), "1.2.0");
            var ownedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                layout.Root,
                layout.Marker,
                Path.Combine(layout.Root, PortableLayout.Descriptor),
                layout.Data,
                layout.Updates,
            };
            foreach (var file in PortableFixtureDescriptor("1.2.0").Files)
                for (
                    string? path = Path.Combine(layout.Root, file.Path.Replace('/', '\\'));
                    path is not null && PortableLayout.SameOrChild(path, layout.Root);
                    path = Path.GetDirectoryName(path)
                )
                    ownedPaths.Add(path);
            foreach (string path in ownedPaths)
                expected.Add(Path.GetRelativePath(syncRoot, path), CloudFilesNative.Identity(path));

            // Ordinary local bytes are converted without dehydration. No provider
            // connection, network access or hydration callback is involved.
            foreach (
                string path in expected
                    .Keys.Where(path => path.Length > 0)
                    .OrderByDescending(path => path.Length)
            )
                CloudFilesNative.Convert(Path.Combine(syncRoot, path), dehydrate: false);

            check(
                CloudFilesNative.IsCloud(layout.Application)
                    && CloudFilesNative.IsCloud(layout.Data)
                    && CloudFilesNative.IsCloud(layout.Marker),
                "CFAPI creates actual Cloud-tagged application, data and regular file entries"
            );
            WindowsStoragePath.VerifyEntry(layout.Marker);
            WindowsStoragePath.RequireNtfs(layout.Root);
            WindowsStoragePath.RequireNtfs(layout.Data);
            using (var namespaces = new DirectoryLease([layout.Root, layout.Data]))
            using (var inventory = new PortableInventory(layout.Root))
            {
                check(
                    inventory.Files.Count == PortablePayload.RequiredFiles.Length,
                    "native inventory verifies actual hydrated Cloud files through retained handles"
                );
                string executable = Path.Combine(layout.Application, PortableLayout.Executable);
                using var file = FileLease.Open(executable, 4096);
                check(
                    file.Read(4096)
                        .SequenceEqual(PortableBytes("1.2.0", "App/" + PortableLayout.Executable)),
                    "a no-follow retained Cloud metadata handle permits normal verified data reads"
                );
                bool renameBlocked = false;
                try
                {
                    File.Move(executable, executable + ".moved");
                }
                catch (IOException failure) when ((failure.HResult & 0xFFFF) == 32)
                {
                    renameBlocked = true;
                }
                check(
                    renameBlocked,
                    "a hydrated Cloud file remains protected against namespace swaps"
                );
            }

            string backup = Path.Combine(root, "cloud-snapshot-" + Guid.NewGuid().ToString("N"));
            var snapshot = await InstallationSnapshot
                .Copy(layout.Application, backup)
                .ConfigureAwait(false);
            InstallationSnapshot.Verify(backup, snapshot);
            check(
                snapshot.Files.Length > 0,
                "native recovery snapshots preserve the verified bytes of Cloud-tagged files"
            );

            string offline = Path.Combine(syncRoot, "offline.txt");
            File.WriteAllText(offline, "Disposable offline Cloud Files test content.");
            expected.Add("offline.txt", CloudFilesNative.Identity(offline));
            CloudFilesNative.Convert(offline, dehydrate: true);
            check(
                CloudFilesNative.IsCloud(offline),
                "CFAPI creates an actual dehydrated Cloud file"
            );
            StorageRefused(
                () => WindowsStoragePath.VerifyEntry(offline),
                StoragePathFailure.CloudFileUnavailable,
                check,
                "unavailable Cloud files are refused by metadata before any data recall"
            );
            StorageRefused(
                () =>
                {
                    using var file = FileLease.Open(offline, 4096);
                },
                StoragePathFailure.CloudFileUnavailable,
                check,
                "native file leases refuse an actual offline placeholder without a provider"
            );

            string link = Path.Combine(syncRoot, "redirected.txt");
            bool linkCreated = false;
            try
            {
                File.CreateSymbolicLink(link, layout.Marker);
                linkCreated = true;
            }
            catch (IOException failure) when ((failure.HResult & 0xFFFF) == 1314)
            {
                Console.WriteLine(
                    "SKIP native Cloud symlink probe: ERROR_PRIVILEGE_NOT_HELD (1314)"
                );
            }
            if (linkCreated)
            {
                try
                {
                    StorageRefused(
                        () => WindowsStoragePath.VerifyEntry(link),
                        StoragePathFailure.RedirectedPath,
                        check,
                        "Cloud support still refuses an actual symbolic link"
                    );
                }
                finally
                {
                    File.Delete(link);
                }
            }
        }
        catch (Exception failure)
        {
            // If subsequent cleanup must preserve unfamiliar state, retain the
            // original test failure in the evidence as well as that refusal.
            Console.Error.WriteLine("Native Cloud Files fixture failed: " + failure);
            throw;
        }
        finally
        {
            // Unregistration can delete dehydrated children. Never apply it to
            // an unfamiliar registration, replaced entry, or unexpected file.
            CloudFilesNative.VerifyRegistration(syncRoot, nonce, expected[""]);
            CloudFilesNative.VerifyTree(syncRoot, expected);
            CloudFilesNative.RequireSuccess(
                CloudFilesNative.Unregister(syncRoot),
                "unregister the exact owned Cloud Files fixture"
            );
            check(
                true,
                "the isolated Cloud Files registration is removed after exact ownership checks"
            );
        }
    }

    static class CloudFilesNative
    {
        internal static bool Available(out string reason)
        {
            if (
                !NativeLibrary.TryLoad(
                    "CldApi.dll",
                    typeof(SelfTests).Assembly,
                    DllImportSearchPath.System32,
                    out nint library
                )
            )
            {
                reason = "System32/CldApi.dll is unavailable";
                return false;
            }
            try
            {
                foreach (
                    string export in new[]
                    {
                        "CfGetPlatformInfo",
                        "CfRegisterSyncRoot",
                        "CfConvertToPlaceholder",
                        "CfGetSyncRootInfoByHandle",
                        "CfUnregisterSyncRoot",
                    }
                )
                    if (!NativeLibrary.TryGetExport(library, export, out _))
                    {
                        reason = "the platform does not export " + export;
                        return false;
                    }
                int result = GetPlatformInfo(out var platform);
                if (Unsupported(result))
                {
                    reason = "platform unsupported, HRESULT=0x" + result.ToString("X8");
                    return false;
                }
                RequireSuccess(result, "query the Cloud Files platform");
                if (platform.BuildNumber == 0)
                    throw new IOException("The Cloud Files platform returned no build identity.");
                reason = "";
                return true;
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }

        internal static bool Unsupported(int result) =>
            result
                is unchecked((int)0x80070001)
                    or unchecked((int)0x80070032)
                    or unchecked((int)0x80070078)
                    or unchecked((int)0x8007017B)
                    or unchecked((int)0x801F0007);

        internal static void RequireSuccess(int result, string operation)
        {
            if (result < 0)
                throw new IOException(
                    "Could not " + operation + "; HRESULT=0x" + result.ToString("X8"),
                    result
                );
        }

        internal static int Register(string root, byte[] nonce)
        {
            var pin = GCHandle.Alloc(nonce, GCHandleType.Pinned);
            try
            {
                var registration = new Registration
                {
                    StructSize = (uint)Marshal.SizeOf<Registration>(),
                    ProviderName = "AHK Manager isolated native tests",
                    ProviderVersion = "1.0",
                    SyncRootIdentity = pin.AddrOfPinnedObject(),
                    SyncRootIdentityLength = (uint)nonce.Length,
                    ProviderId = Guid.NewGuid(),
                };
                var policies = new Policies
                {
                    StructSize = (uint)Marshal.SizeOf<Policies>(),
                    HydrationPrimary = 2,
                    PopulationPrimary = 3,
                };
                return RegisterSyncRoot(root, ref registration, ref policies, 6);
            }
            finally
            {
                pin.Free();
            }
        }

        internal static void Convert(string path, bool dehydrate)
        {
            using var handle = Open(path, 0xC0000000, dehydrate ? 0u : 7u, 0, 3, 0x02200000, 0);
            if (handle.IsInvalid)
                throw new IOException(
                    "A Cloud Files fixture entry could not be opened.",
                    Marshal.GetHRForLastWin32Error()
                );
            byte[] identity = Guid.NewGuid().ToByteArray();
            var pin = GCHandle.Alloc(identity, GCHandleType.Pinned);
            try
            {
                RequireSuccess(
                    ConvertToPlaceholder(
                        handle,
                        pin.AddrOfPinnedObject(),
                        (uint)identity.Length,
                        dehydrate ? 3u : 1u,
                        0,
                        0
                    ),
                    "convert an owned fixture entry to a placeholder"
                );
            }
            finally
            {
                pin.Free();
            }
        }

        internal static Native.FileStamp Identity(string path)
        {
            using var handle = WindowsStoragePath.OpenVerifiedMetadata(path);
            if (!Native.GetFileInformationByHandle(handle, out var identity))
                throw new IOException("A Cloud Files fixture identity could not be verified.");
            return identity;
        }

        static bool SameIdentity(Native.FileStamp expected, Native.FileStamp actual) =>
            expected.Volume == actual.Volume
            && expected.IndexHigh == actual.IndexHigh
            && expected.IndexLow == actual.IndexLow
            && (expected.Attributes & 0x10) == (actual.Attributes & 0x10);

        internal static bool IsCloud(string path)
        {
            // The unavailable-file probe must inspect its tag without asking
            // the production availability policy to accept that entry.
            using var handle = Open(path, 0x80, 7, 0, 3, 0x02200000, 0);
            if (handle.IsInvalid || !GetAttributeTag(handle, 9, out var information, 8))
                throw new IOException("Cloud Files fixture tags could not be inspected.");
            return (information.Attributes & 0x400) != 0
                && WindowsStoragePath.IsCloudTag(information.Tag);
        }

        internal static void VerifyRegistration(
            string root,
            byte[] nonce,
            Native.FileStamp identity
        )
        {
            SnapshotSecurity.VerifyPrivate(root, true, true);
            using var handle = WindowsStoragePath.OpenVerifiedMetadata(root);
            if (
                !Native.GetFileInformationByHandle(handle, out var actual)
                || !SameIdentity(identity, actual)
            )
                throw new IOException(
                    "The isolated Cloud Files root changed; cleanup was refused."
                );
            byte[] information = new byte[4096];
            RequireSuccess(
                GetSyncRootInfo(
                    handle,
                    1,
                    information,
                    (uint)information.Length,
                    out uint returned
                ),
                "authenticate the Cloud Files registration"
            );
            // SDK CF_SYNC_ROOT_STANDARD_INFO puts the counted opaque identity
            // after its fixed provider name/version arrays, at byte offset1056.
            if (
                returned < 1056
                || returned > information.Length
                || BitConverter.ToUInt32(information, 1052) != nonce.Length
                || returned < 1056 + nonce.Length
                || !information.AsSpan(1056, nonce.Length).SequenceEqual(nonce)
            )
                throw new IOException(
                    "The Cloud Files registration nonce changed; cleanup was refused."
                );
        }

        internal static void VerifyTree(string root, Dictionary<string, Native.FileStamp> expected)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            int count = 1;
            while (pending.Count > 0)
                foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    string relative = Path.GetRelativePath(root, path);
                    if (!expected.TryGetValue(relative, out var identity))
                        throw new IOException(
                            "Unexpected Cloud Files fixture contents were preserved."
                        );
                    // Metadata inspection also works for the deliberately
                    // dehydrated file, whose data must never be opened here.
                    using var handle = Open(path, 0x80, 7, 0, 3, 0x02200000, 0);
                    if (
                        handle.IsInvalid
                        || !Native.GetFileInformationByHandle(handle, out var actual)
                        || !SameIdentity(identity, actual)
                        || !GetAttributeTag(handle, 9, out var tag, 8)
                        || (tag.Attributes & 0x40) != 0
                        || (
                            (tag.Attributes & 0x400) != 0 && !WindowsStoragePath.IsCloudTag(tag.Tag)
                        )
                    )
                        throw new IOException(
                            "A replaced Cloud Files fixture entry was preserved."
                        );
                    count++;
                    if ((actual.Attributes & 0x10) != 0)
                        pending.Push(path);
                }
            if (count != expected.Count)
                throw new IOException(
                    "Cloud Files fixture contents disappeared; cleanup was refused."
                );
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Platform
        {
            internal uint BuildNumber,
                RevisionNumber,
                IntegrationNumber;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Registration
        {
            internal uint StructSize;

            [MarshalAs(UnmanagedType.LPWStr)]
            internal string ProviderName;

            [MarshalAs(UnmanagedType.LPWStr)]
            internal string ProviderVersion;
            internal nint SyncRootIdentity;
            internal uint SyncRootIdentityLength;
            internal nint FileIdentity;
            internal uint FileIdentityLength;
            internal Guid ProviderId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Policies
        {
            internal uint StructSize;
            internal ushort HydrationPrimary,
                HydrationModifier,
                PopulationPrimary,
                PopulationModifier;
            internal uint InSync,
                HardLink,
                PlaceholderManagement;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct AttributeTag
        {
            internal uint Attributes,
                Tag;
        }

        [DllImport("CldApi.dll", EntryPoint = "CfGetPlatformInfo")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int GetPlatformInfo(out Platform platform);

        [DllImport("CldApi.dll", EntryPoint = "CfRegisterSyncRoot", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int RegisterSyncRoot(
            string path,
            ref Registration registration,
            ref Policies policies,
            uint flags
        );

        [DllImport("CldApi.dll", EntryPoint = "CfConvertToPlaceholder")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int ConvertToPlaceholder(
            SafeFileHandle handle,
            nint identity,
            uint length,
            uint flags,
            nint usn,
            nint overlapped
        );

        [DllImport("CldApi.dll", EntryPoint = "CfGetSyncRootInfoByHandle")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int GetSyncRootInfo(
            SafeFileHandle handle,
            int informationClass,
            [Out] byte[] buffer,
            uint length,
            out uint returned
        );

        [DllImport("CldApi.dll", EntryPoint = "CfUnregisterSyncRoot", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int Unregister(string root);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            CharSet = CharSet.Unicode,
            SetLastError = true
        )]
        static extern SafeFileHandle Open(
            string name,
            uint access,
            uint sharing,
            nint security,
            uint disposition,
            uint flags,
            nint template
        );

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFileInformationByHandleEx",
            SetLastError = true
        )]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetAttributeTag(
            SafeFileHandle handle,
            int kind,
            out AttributeTag information,
            uint length
        );
    }
}
