using System.Buffers.Binary;
using System.Text.Json;

namespace Porchlight.Core;

internal enum Kind : byte { Data, Credit, Close, Resize, Focus, AutomaticInput = 6 }
internal readonly record struct Frame(uint Channel, Kind Kind, byte[] Bytes);

internal static class Wire
{
    internal const int MaxFrame = 16 << 20, Window = 256 << 10;
    public static byte[] Request(int id, string method, object args) =>
        JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = args });

    public static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    public static JsonElement Result(byte[] bytes, int id, string method) => Result(Parse(bytes), id, method);

    public static JsonElement Result(JsonElement msg, int id, string method)
    {
        if (msg.GetProperty("jsonrpc").GetString() != "2.0" || msg.GetProperty("id").GetInt32() != id)
            throw new InvalidDataException("Unexpected RPC response.");
        if (msg.TryGetProperty("error", out _)) throw new CoreException($"Core refused {method}. Check compatibility and reconnect for a fresh list.");
        return msg.GetProperty("result");
    }

    public static async Task<byte[]> LineAsync(Stream reader, CancellationToken ct)
    {
        using var line = new MemoryStream();
        byte[] one = new byte[1];
        while (await reader.ReadAsync(one, ct).ConfigureAwait(false) != 0)
        {
            if (one[0] == '\n') return line.ToArray();
            if (line.Length >= 65536) throw new InvalidDataException("Oversized hello.");
            line.WriteByte(one[0]);
        }
        throw new EndOfStreamException();
    }

    public static async Task<Frame> ReadAsync(Stream reader, CancellationToken ct)
    {
        byte[] header = new byte[9];
        await reader.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(5));
        if (length > MaxFrame) throw new InvalidDataException("Oversized frame.");
        byte[] bytes = new byte[length];
        await reader.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(header), (Kind)header[4], bytes);
    }

    public static async Task WriteAsync(Stream writer, Frame frame, CancellationToken ct)
    {
        byte[] header = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(header, frame.Channel);
        header[4] = (byte)frame.Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5), (uint)frame.Bytes.Length);
        await writer.WriteAsync(header, ct).ConfigureAwait(false);
        if (frame.Bytes.Length != 0) await writer.WriteAsync(frame.Bytes, ct).ConfigureAwait(false);
    }

    public static Frame Credit(Frame consumed)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)consumed.Bytes.Length);
        return new(consumed.Channel, Kind.Credit, bytes);
    }

    public static uint Channel(JsonElement reply) => reply.GetProperty("channel").GetUInt32() is > 0 and var ch
        ? ch : throw new InvalidDataException("Reserved channel.");
    public static bool Flag(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    public static bool Contains(JsonElement obj, string key, string value) => obj.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.Array && v.EnumerateArray().Any(e => e.GetString() == value);
}

// Only messages authored by the client, never raw RPC errors, bytes or JSON fragments.
public sealed class CoreException(string message) : Exception(message);
