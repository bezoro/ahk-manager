using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Bezoro.AhkManager.Presentation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RequestContext
{
    public string Kind { get; set; } = "Legacy";
    public ulong Origin { get; set; }
    public byte[] Blob { get; set; } = [];
    public RequestContext Copy() => new() { Kind = Kind, Origin = Origin, Blob = [.. Blob] };
    public static void Validate(RequestContext c)
    {
        if (c.Blob is null || c.Blob.Length > BootContextService.BlobLimit || c.Kind == "Legacy" && (c.Origin != 0 || c.Blob.Length != 0) || c.Kind == "WindowsUnbiasedDpapiV1" && (c.Origin == 0 || c.Blob.Length == 0) || c.Kind is not ("Legacy" or "WindowsUnbiasedDpapiV1")) throw new InvalidDataException("Contexto do pedido inválido.");
    }
}
public interface IUnbiasedCounter { bool Fixture { get; } ulong Read(); }
public interface IContextProtector { bool Fixture { get; } byte[] Protect(byte[] plain); byte[] Unprotect(byte[] blob); }
public sealed class WindowsUnbiasedCounter : IUnbiasedCounter
{
    public bool Fixture => false;
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryUnbiasedInterruptTime(out ulong value);
    public ulong Read() => QueryUnbiasedInterruptTime(out var value) ? value : throw new IOException("O contador do Windows não pôde ser consultado.");
}
public sealed class WindowsContextProtector : IContextProtector
{
    public bool Fixture => false;
    [StructLayout(LayoutKind.Sequential)] struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptProtectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    public byte[] Protect(byte[] plain) => Transform(plain, true);
    public byte[] Unprotect(byte[] blob) => Transform(blob, false);
    public static bool ValidBuffer(IntPtr pointer, int length, int limit) => pointer != IntPtr.Zero && length > 0 && length <= limit;
    static byte[] Transform(byte[] bytes, bool protect)
    {
        int inputLimit = protect ? BootContextService.PlainLimit : BootContextService.BlobLimit;
        if (bytes.Length == 0 || bytes.Length > inputLimit) throw new InvalidDataException("Entrada DPAPI fora do limite.");
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned); DataBlob output = default;
        try
        {
            var input = new DataBlob { Length = bytes.Length, Data = pinned.AddrOfPinnedObject() };
            bool success = protect ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 5, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success || !ValidBuffer(output.Data, output.Length, protect ? BootContextService.BlobLimit : BootContextService.PlainLimit)) throw new IOException("Proteção DPAPI indisponível ou saída inválida.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, output.Length); return result;
        }
        finally { if (output.Data != IntPtr.Zero) _ = LocalFree(output.Data); pinned.Free(); }
    }
}
public sealed class FixtureUnbiasedCounter : IUnbiasedCounter
{
    public bool Fixture => true;
    public ulong Value { get; set; } = 1000;
    public Queue<ulong> Samples { get; } = new();
    public bool Fail { get; set; }
    public int Calls { get; private set; }
    public ulong Read() { Calls++; if (Fail) throw new IOException("Fixture: contador indisponível."); return Samples.Count > 0 ? Samples.Dequeue() : Value; }
}
public sealed class FixtureContextProtector : IContextProtector
{
    public bool Fixture => true;
    public string Machine { get; set; } = "machine-A";
    public bool FailProtect { get; set; }
    public bool FailUnprotect { get; set; }
    public Func<byte[], byte[]>? Alter { get; set; }
    public int ProtectCalls { get; private set; }
    public byte[] Protect(byte[] plain) { ProtectCalls++; if (FailProtect) throw new IOException("Fixture protect failed."); return Encoding.UTF8.GetBytes(Machine + "\0").Concat(plain).ToArray(); }
    public byte[] Unprotect(byte[] blob)
    {
        var prefix = Encoding.UTF8.GetBytes(Machine + "\0");
        if (FailUnprotect || !blob.AsSpan().StartsWith(prefix)) throw new IOException("Fixture machine/unprotect failed.");
        var plain = blob[prefix.Length..]; return Alter is null ? plain : Alter(plain);
    }
}
// A fixed, ordered binary grammar has no optional/duplicate/unknown members. Canonical re-encoding is required.
public sealed class BootContextService
{
    public const int PlainLimit = 256 * 1024, BlobLimit = 512 * 1024;
    const string Domain = "Bezoro.AhkManager.ManualLaunch.WindowsUnbiasedDpapiV1";
    readonly IUnbiasedCounter counter;
    readonly IContextProtector protector;
    public bool Fixture { get; }
    public BootContextService(bool fixture, IUnbiasedCounter counter, IContextProtector protector)
    {
        if (counter.Fixture != fixture || protector.Fixture != fixture || fixture && (counter is WindowsUnbiasedCounter || protector is WindowsContextProtector)) throw new ArgumentException("Modo do contexto deve coincidir com os adapters.");
        Fixture = fixture; this.counter = counter; this.protector = protector;
    }
    public static BootContextService Create(bool fixture) => fixture ? new(true, new FixtureUnbiasedCounter(), new FixtureContextProtector()) : new(false, new WindowsUnbiasedCounter(), new WindowsContextProtector());
    public ulong Sample() => counter.Read();
    static byte[] Payload(LaunchRequest r, AuthorityBinding b, ulong origin)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        writer.Write(Domain); writer.Write(1); writer.Write(b.Fixture ? "FAKE" : "REAL"); writer.Write(r.Nonce.ToByteArray()); writer.Write(b.LedgerId.ToByteArray());
        writer.Write(r.Sid); writer.Write(r.ScriptPath); writer.Write(r.InterpreterPath); writer.Write(origin); writer.Write(r.ManagerPid); writer.Write(r.ManagerCreated); writer.Write(r.LoginKey); writer.Write(r.SessionId); writer.Write(r.RequestedUtcTicks);
        writer.Flush(); var payload = stream.ToArray(); if (payload.Length + 36 > PlainLimit) throw new InvalidDataException("Payload excede limite."); return payload;
    }
    static byte[] Envelope(byte[] payload)
    { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); writer.Write(payload.Length); writer.Write(payload); writer.Write(SHA256.HashData(payload)); return stream.ToArray(); }
    static bool Matches(byte[] envelope, byte[] expected)
    {
        if (envelope.Length is < 36 or > PlainLimit) return false;
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(envelope);
        if (length <= 0 || length != expected.Length || length != envelope.Length - 36) return false;
        return envelope.AsSpan(4, length).SequenceEqual(expected) && CryptographicOperations.FixedTimeEquals(envelope.AsSpan(4 + length, 32), SHA256.HashData(expected)) && envelope.AsSpan().SequenceEqual(Envelope(expected));
    }
    public RequestContext Capture(LaunchRequest request, AuthorityBinding binding)
    {
        if (binding.Fixture != Fixture || request.Sid != binding.Sid) throw new InvalidDataException("Identidade/modo do contexto diverge.");
        ulong origin = Sample(); if (origin == 0) throw new IOException("Contador de origem zero: pedido recusado antes da reserva.");
        var plain = Envelope(Payload(request, binding, origin)); var blob = protector.Protect(plain);
        if (blob.Length == 0 || blob.Length > BlobLimit || !Matches(protector.Unprotect(blob), Payload(request, binding, origin))) throw new InvalidDataException("Roundtrip DPAPI não confirmou o contexto exato.");
        return new() { Kind = "WindowsUnbiasedDpapiV1", Origin = origin, Blob = blob };
    }
    public Result Verify(LaunchRequest request, AuthorityBinding binding)
    {
        try
        {
            RequestContext.Validate(request.Context);
            if (binding.Fixture != Fixture || request.Sid != binding.Sid || request.Context.Kind != "WindowsUnbiasedDpapiV1") return Result.Fail("Pedido legado sem prova capturada. Não é possível criar prova retroativa.");
            return Matches(protector.Unprotect(request.Context.Blob), Payload(request, binding, request.Context.Origin)) ? Result.Ok("Contexto protegido confirmado.") : Result.Fail("Conteúdo protegido diverge do pedido; barreira preservada.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or CryptographicException) { return Result.Fail("Prova do contexto indisponível: " + e.Message); }
    }
    public Result BeforeDispatch(LaunchRequest request, AuthorityBinding binding)
    {
        var proof = Verify(request, binding); if (!proof.Success) return proof;
        try { return Sample() >= request.Context.Origin ? Result.Ok("Contador pré-despacho não retrocedeu.") : Result.Fail("Contador retrocedeu após registrar o pedido. Nenhum despacho autorizado."); }
        catch (IOException e) { return Result.Fail(e.Message); }
    }
}
