using Google.Protobuf;
using Minicloud.Common.Services;

namespace Minicloud.Telemetry.Otlp;

/// <summary>
/// T12 OTLP protobuf codec. Parses and emits ExportLogsServiceRequest/Response using the
/// official OTLP field numbers (opentelemetry-proto logs/v1, common/v1, resource/v1,
/// collector/logs/v1) via Google.Protobuf primitives, without the heavyweight
/// OpenTelemetry.Proto/grpc dependency surface. Only application/x-protobuf is supported.
/// </summary>
public sealed class OtlpDecodeException(string message) : Exception(message);

public enum OtlpValueKind
{
    Empty,
    String,
    Bool,
    Int,
    Double,
    Array,
    KvList,
    Bytes,
}

internal static class Pb
{
    internal static int Str(int field, string value) =>
        CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeStringSize(value);

    internal static int Bts(int field, ByteString value) =>
        CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeBytesSize(value);

    internal static int Boo(int field, bool value) =>
        CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeBoolSize(value);

    internal static int I64(int field, long value) =>
        CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeInt64Size(value);

    internal static int Dbl(int field, double value) =>
        CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeDoubleSize(value);

    internal static int Enm(int field, int value) =>
        CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeEnumSize(value);

    internal static int U32(int field, uint value) =>
        CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeUInt32Size(value);

    internal static int F64(int field) => CodedOutputStream.ComputeTagSize(field) + 8;

    internal static int F32(int field) => CodedOutputStream.ComputeTagSize(field) + 4;

    internal static void Str(CodedOutputStream output, int field, string value)
    {
        output.WriteTag(field, WireFormat.WireType.LengthDelimited);
        output.WriteString(value);
    }

    internal static void Bts(CodedOutputStream output, int field, ByteString value)
    {
        output.WriteTag(field, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(value);
    }

    internal static void Boo(CodedOutputStream output, int field, bool value)
    {
        output.WriteTag(field, WireFormat.WireType.Varint);
        output.WriteBool(value);
    }

    internal static void I64(CodedOutputStream output, int field, long value)
    {
        output.WriteTag(field, WireFormat.WireType.Varint);
        output.WriteInt64(value);
    }

    internal static void Dbl(CodedOutputStream output, int field, double value)
    {
        output.WriteTag(field, WireFormat.WireType.Fixed64);
        output.WriteDouble(value);
    }

    internal static void Enm(CodedOutputStream output, int field, int value)
    {
        output.WriteTag(field, WireFormat.WireType.Varint);
        output.WriteEnum(value);
    }

    internal static void U32(CodedOutputStream output, int field, uint value)
    {
        output.WriteTag(field, WireFormat.WireType.Varint);
        output.WriteUInt32(value);
    }

    internal static void F64(CodedOutputStream output, int field, ulong value)
    {
        output.WriteTag(field, WireFormat.WireType.Fixed64);
        output.WriteFixed64(value);
    }

    internal static void F32(CodedOutputStream output, int field, uint value)
    {
        output.WriteTag(field, WireFormat.WireType.Fixed32);
        output.WriteFixed32(value);
    }
}

public sealed class OtlpAnyValue
{
    public OtlpValueKind Kind { get; set; }
    public string StringValue { get; set; } = string.Empty;
    public bool BoolValue { get; set; }
    public long IntValue { get; set; }
    public double DoubleValue { get; set; }
    public List<OtlpAnyValue> ArrayValues { get; set; } = [];
    public List<OtlpKeyValue> KvValues { get; set; } = [];
    public byte[] BytesValue { get; set; } = [];

    public int CalculateSize()
    {
        return Kind switch
        {
            OtlpValueKind.String => Pb.Str(1, StringValue),
            OtlpValueKind.Bool => Pb.Boo(2, BoolValue),
            OtlpValueKind.Int => Pb.I64(3, IntValue),
            OtlpValueKind.Double => Pb.Dbl(4, DoubleValue),
            OtlpValueKind.Array => OtlpProtobuf.MessageSize(5, ArraySize()),
            OtlpValueKind.KvList => OtlpProtobuf.MessageSize(6, KvListSize()),
            OtlpValueKind.Bytes => Pb.Bts(7, ByteString.CopyFrom(BytesValue)),
            _ => 0,
        };
    }

    public void WriteTo(CodedOutputStream output)
    {
        switch (Kind)
        {
            case OtlpValueKind.String:
                Pb.Str(output, 1, StringValue);
                break;
            case OtlpValueKind.Bool:
                Pb.Boo(output, 2, BoolValue);
                break;
            case OtlpValueKind.Int:
                Pb.I64(output, 3, IntValue);
                break;
            case OtlpValueKind.Double:
                Pb.Dbl(output, 4, DoubleValue);
                break;
            case OtlpValueKind.Array:
                OtlpProtobuf.WriteMessage(output, 5, ArraySize(), o =>
                {
                    foreach (var item in ArrayValues)
                    {
                        OtlpProtobuf.WriteMessage(o, 1, item.CalculateSize(), item.WriteTo);
                    }
                });
                break;
            case OtlpValueKind.KvList:
                OtlpProtobuf.WriteMessage(output, 6, KvListSize(), o =>
                {
                    foreach (var item in KvValues)
                    {
                        OtlpProtobuf.WriteMessage(o, 1, item.CalculateSize(), item.WriteTo);
                    }
                });
                break;
            case OtlpValueKind.Bytes:
                Pb.Bts(output, 7, ByteString.CopyFrom(BytesValue));
                break;
        }
    }

    private int ArraySize()
    {
        var size = 0;
        foreach (var item in ArrayValues)
        {
            size += OtlpProtobuf.MessageSize(1, item.CalculateSize());
        }

        return size;
    }

    private int KvListSize()
    {
        var size = 0;
        foreach (var item in KvValues)
        {
            size += OtlpProtobuf.MessageSize(1, item.CalculateSize());
        }

        return size;
    }
}

public sealed class OtlpKeyValue
{
    public string Key { get; set; } = string.Empty;
    public OtlpAnyValue Value { get; set; } = new();

    public int CalculateSize() =>
        Pb.Str(1, Key) + OtlpProtobuf.MessageSize(2, Value.CalculateSize());

    public void WriteTo(CodedOutputStream output)
    {
        Pb.Str(output, 1, Key);
        OtlpProtobuf.WriteMessage(output, 2, Value.CalculateSize(), Value.WriteTo);
    }
}

public sealed class OtlpLogRecord
{
    public ulong TimeUnixNano { get; set; }
    public int SeverityNumber { get; set; }
    public string SeverityText { get; set; } = string.Empty;
    public OtlpAnyValue? Body { get; set; }
    public List<OtlpKeyValue> Attributes { get; set; } = [];
    public uint DroppedAttributesCount { get; set; }
    public uint Flags { get; set; }
    public byte[] TraceId { get; set; } = [];
    public byte[] SpanId { get; set; } = [];
    public ulong ObservedTimeUnixNano { get; set; }
    public string EventName { get; set; } = string.Empty;

    public int CalculateSize()
    {
        var size = 0;
        if (TimeUnixNano != 0)
        {
            size += Pb.F64(1);
        }

        if (SeverityNumber != 0)
        {
            size += Pb.Enm(2, SeverityNumber);
        }

        if (SeverityText.Length != 0)
        {
            size += Pb.Str(3, SeverityText);
        }

        if (Body is not null)
        {
            size += OtlpProtobuf.MessageSize(5, Body.CalculateSize());
        }

        foreach (var attribute in Attributes)
        {
            size += OtlpProtobuf.MessageSize(6, attribute.CalculateSize());
        }

        if (DroppedAttributesCount != 0)
        {
            size += Pb.U32(7, DroppedAttributesCount);
        }

        if (Flags != 0)
        {
            size += Pb.F32(8);
        }

        if (TraceId.Length != 0)
        {
            size += Pb.Bts(9, ByteString.CopyFrom(TraceId));
        }

        if (SpanId.Length != 0)
        {
            size += Pb.Bts(10, ByteString.CopyFrom(SpanId));
        }

        if (ObservedTimeUnixNano != 0)
        {
            size += Pb.F64(11);
        }

        if (EventName.Length != 0)
        {
            size += Pb.Str(12, EventName);
        }

        return size;
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (TimeUnixNano != 0)
        {
            Pb.F64(output, 1, TimeUnixNano);
        }

        if (SeverityNumber != 0)
        {
            Pb.Enm(output, 2, SeverityNumber);
        }

        if (SeverityText.Length != 0)
        {
            Pb.Str(output, 3, SeverityText);
        }

        if (Body is not null)
        {
            OtlpProtobuf.WriteMessage(output, 5, Body.CalculateSize(), Body.WriteTo);
        }

        foreach (var attribute in Attributes)
        {
            OtlpProtobuf.WriteMessage(output, 6, attribute.CalculateSize(), attribute.WriteTo);
        }

        if (DroppedAttributesCount != 0)
        {
            Pb.U32(output, 7, DroppedAttributesCount);
        }

        if (Flags != 0)
        {
            Pb.F32(output, 8, Flags);
        }

        if (TraceId.Length != 0)
        {
            Pb.Bts(output, 9, ByteString.CopyFrom(TraceId));
        }

        if (SpanId.Length != 0)
        {
            Pb.Bts(output, 10, ByteString.CopyFrom(SpanId));
        }

        if (ObservedTimeUnixNano != 0)
        {
            Pb.F64(output, 11, ObservedTimeUnixNano);
        }

        if (EventName.Length != 0)
        {
            Pb.Str(output, 12, EventName);
        }
    }
}

public sealed class OtlpScope
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<OtlpKeyValue> Attributes { get; set; } = [];
    public uint DroppedAttributesCount { get; set; }

    public int CalculateSize()
    {
        var size = 0;
        if (Name.Length != 0)
        {
            size += Pb.Str(1, Name);
        }

        if (Version.Length != 0)
        {
            size += Pb.Str(2, Version);
        }

        foreach (var attribute in Attributes)
        {
            size += OtlpProtobuf.MessageSize(3, attribute.CalculateSize());
        }

        if (DroppedAttributesCount != 0)
        {
            size += Pb.U32(4, DroppedAttributesCount);
        }

        return size;
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (Name.Length != 0)
        {
            Pb.Str(output, 1, Name);
        }

        if (Version.Length != 0)
        {
            Pb.Str(output, 2, Version);
        }

        foreach (var attribute in Attributes)
        {
            OtlpProtobuf.WriteMessage(output, 3, attribute.CalculateSize(), attribute.WriteTo);
        }

        if (DroppedAttributesCount != 0)
        {
            Pb.U32(output, 4, DroppedAttributesCount);
        }
    }
}

public sealed class OtlpScopeLogs
{
    public OtlpScope? Scope { get; set; }
    public List<OtlpLogRecord> LogRecords { get; set; } = [];
    public string SchemaUrl { get; set; } = string.Empty;

    public int CalculateSize()
    {
        var size = 0;
        if (Scope is not null)
        {
            size += OtlpProtobuf.MessageSize(1, Scope.CalculateSize());
        }

        foreach (var record in LogRecords)
        {
            size += OtlpProtobuf.MessageSize(2, record.CalculateSize());
        }

        if (SchemaUrl.Length != 0)
        {
            size += Pb.Str(3, SchemaUrl);
        }

        return size;
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (Scope is not null)
        {
            OtlpProtobuf.WriteMessage(output, 1, Scope.CalculateSize(), Scope.WriteTo);
        }

        foreach (var record in LogRecords)
        {
            OtlpProtobuf.WriteMessage(output, 2, record.CalculateSize(), record.WriteTo);
        }

        if (SchemaUrl.Length != 0)
        {
            Pb.Str(output, 3, SchemaUrl);
        }
    }
}

public sealed class OtlpResource
{
    public List<OtlpKeyValue> Attributes { get; set; } = [];
    public uint DroppedAttributesCount { get; set; }

    public int CalculateSize()
    {
        var size = 0;
        foreach (var attribute in Attributes)
        {
            size += OtlpProtobuf.MessageSize(1, attribute.CalculateSize());
        }

        if (DroppedAttributesCount != 0)
        {
            size += Pb.U32(2, DroppedAttributesCount);
        }

        return size;
    }

    public void WriteTo(CodedOutputStream output)
    {
        foreach (var attribute in Attributes)
        {
            OtlpProtobuf.WriteMessage(output, 1, attribute.CalculateSize(), attribute.WriteTo);
        }

        if (DroppedAttributesCount != 0)
        {
            Pb.U32(output, 2, DroppedAttributesCount);
        }
    }
}

public sealed class OtlpResourceLogs
{
    public OtlpResource? Resource { get; set; }
    public List<OtlpScopeLogs> ScopeLogs { get; set; } = [];
    public string SchemaUrl { get; set; } = string.Empty;

    public int CalculateSize()
    {
        var size = 0;
        if (Resource is not null)
        {
            size += OtlpProtobuf.MessageSize(1, Resource.CalculateSize());
        }

        foreach (var scope in ScopeLogs)
        {
            size += OtlpProtobuf.MessageSize(2, scope.CalculateSize());
        }

        if (SchemaUrl.Length != 0)
        {
            size += Pb.Str(3, SchemaUrl);
        }

        return size;
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (Resource is not null)
        {
            OtlpProtobuf.WriteMessage(output, 1, Resource.CalculateSize(), Resource.WriteTo);
        }

        foreach (var scope in ScopeLogs)
        {
            OtlpProtobuf.WriteMessage(output, 2, scope.CalculateSize(), scope.WriteTo);
        }

        if (SchemaUrl.Length != 0)
        {
            Pb.Str(output, 3, SchemaUrl);
        }
    }
}

public sealed class OtlpExportRequest
{
    public List<OtlpResourceLogs> ResourceLogs { get; set; } = [];

    public int RecordCount()
    {
        var count = 0;
        foreach (var resource in ResourceLogs)
        {
            foreach (var scope in resource.ScopeLogs)
            {
                count += scope.LogRecords.Count;
            }
        }

        return count;
    }

    public int CalculateSize()
    {
        var size = 0;
        foreach (var resource in ResourceLogs)
        {
            size += OtlpProtobuf.MessageSize(1, resource.CalculateSize());
        }

        return size;
    }

    public byte[] ToByteArray()
    {
        var bytes = new byte[CalculateSize()];
        var output = new CodedOutputStream(bytes);
        foreach (var resource in ResourceLogs)
        {
            OtlpProtobuf.WriteMessage(output, 1, resource.CalculateSize(), resource.WriteTo);
        }

        output.CheckNoSpaceLeft();
        return bytes;
    }
}

public static class OtlpProtobuf
{
    public const string ProtobufContentType = "application/x-protobuf";
    public const int MaxErrorMessageLength = 512;

    internal static int MessageSize(int fieldNumber, int size) =>
        CodedOutputStream.ComputeTagSize(fieldNumber) + VarintSize((uint)size) + size;

    private static int VarintSize(uint value)
    {
        if (value < 128)
        {
            return 1;
        }

        if (value < 16384)
        {
            return 2;
        }

        if (value < 2097152)
        {
            return 3;
        }

        return value < 268435456 ? 4 : 5;
    }

    internal static void WriteMessage(CodedOutputStream output, int fieldNumber, int size, Action<CodedOutputStream> write)
    {
        output.WriteTag(fieldNumber, WireFormat.WireType.LengthDelimited);
        output.WriteLength(size);
        write(output);
    }

    public static OtlpExportRequest ParseExportRequest(byte[] bytes)
    {
        try
        {
            var request = new OtlpExportRequest();
            var input = new CodedInputStream(bytes);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == 1 &&
                    WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    request.ResourceLogs.Add(ParseResourceLogs(input.ReadBytes().ToByteArray()));
                }
                else
                {
                    input.SkipLastField();
                }
            }

            return request;
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new OtlpDecodeException($"Request body is not a valid ExportLogsServiceRequest: {ex.Message}");
        }
    }

    public static (long RejectedLogRecords, string ErrorMessage) ParseExportResponse(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return (0, string.Empty);
        }

        try
        {
            long rejected = 0;
            var error = string.Empty;
            var input = new CodedInputStream(bytes);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == 1 &&
                    WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    var partial = input.ReadBytes().ToByteArray();
                    var inner = new CodedInputStream(partial);
                    uint innerTag;
                    while ((innerTag = inner.ReadTag()) != 0)
                    {
                        var field = WireFormat.GetTagFieldNumber(innerTag);
                        if (field == 1 && WireFormat.GetTagWireType(innerTag) == WireFormat.WireType.Varint)
                        {
                            rejected = inner.ReadInt64();
                        }
                        else if (field == 2 && WireFormat.GetTagWireType(innerTag) == WireFormat.WireType.LengthDelimited)
                        {
                            error = inner.ReadString();
                        }
                        else
                        {
                            inner.SkipLastField();
                        }
                    }
                }
                else
                {
                    input.SkipLastField();
                }
            }

            return (rejected, error);
        }
        catch (InvalidProtocolBufferException)
        {
            return (0, string.Empty);
        }
    }

    public static byte[] EncodeExportResponse(long rejectedLogRecords, string errorMessage)
    {
        if (rejectedLogRecords == 0 && string.IsNullOrEmpty(errorMessage))
        {
            return [];
        }

        var bounded = BoundError(errorMessage);
        var partialSize = Pb.I64(1, rejectedLogRecords);
        if (bounded.Length != 0)
        {
            partialSize += Pb.Str(2, bounded);
        }

        var bytes = new byte[MessageSize(1, partialSize)];
        var output = new CodedOutputStream(bytes);
        WriteMessage(output, 1, partialSize, o =>
        {
            Pb.I64(o, 1, rejectedLogRecords);
            if (bounded.Length != 0)
            {
                Pb.Str(o, 2, bounded);
            }
        });
        output.CheckNoSpaceLeft();
        return bytes;
    }

    public static string BoundError(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        return message.Length <= MaxErrorMessageLength
            ? message
            : message[..MaxErrorMessageLength];
    }

    private static OtlpResourceLogs ParseResourceLogs(byte[] bytes)
    {
        var resource = new OtlpResourceLogs();
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            if (wireType != WireFormat.WireType.LengthDelimited)
            {
                input.SkipLastField();
                continue;
            }

            if (field == 1)
            {
                resource.Resource = ParseResource(input.ReadBytes().ToByteArray());
            }
            else if (field == 2)
            {
                resource.ScopeLogs.Add(ParseScopeLogs(input.ReadBytes().ToByteArray()));
            }
            else if (field == 3)
            {
                resource.SchemaUrl = input.ReadString();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return resource;
    }

    private static OtlpResource ParseResource(byte[] bytes)
    {
        var resource = new OtlpResource();
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            if (field == 1 && wireType == WireFormat.WireType.LengthDelimited)
            {
                resource.Attributes.Add(ParseKeyValue(input.ReadBytes().ToByteArray(), 0));
            }
            else if (field == 2 && wireType == WireFormat.WireType.Varint)
            {
                resource.DroppedAttributesCount = input.ReadUInt32();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return resource;
    }

    private static OtlpScopeLogs ParseScopeLogs(byte[] bytes)
    {
        var scopeLogs = new OtlpScopeLogs();
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            if (wireType != WireFormat.WireType.LengthDelimited)
            {
                input.SkipLastField();
                continue;
            }

            if (field == 1)
            {
                scopeLogs.Scope = ParseScope(input.ReadBytes().ToByteArray());
            }
            else if (field == 2)
            {
                scopeLogs.LogRecords.Add(ParseLogRecord(input.ReadBytes().ToByteArray()));
            }
            else if (field == 3)
            {
                scopeLogs.SchemaUrl = input.ReadString();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return scopeLogs;
    }

    private static OtlpScope ParseScope(byte[] bytes)
    {
        var scope = new OtlpScope();
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            if (field == 1 && wireType == WireFormat.WireType.LengthDelimited)
            {
                scope.Name = input.ReadString();
            }
            else if (field == 2 && wireType == WireFormat.WireType.LengthDelimited)
            {
                scope.Version = input.ReadString();
            }
            else if (field == 3 && wireType == WireFormat.WireType.LengthDelimited)
            {
                scope.Attributes.Add(ParseKeyValue(input.ReadBytes().ToByteArray(), 0));
            }
            else if (field == 4 && wireType == WireFormat.WireType.Varint)
            {
                scope.DroppedAttributesCount = input.ReadUInt32();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return scope;
    }

    private static OtlpLogRecord ParseLogRecord(byte[] bytes)
    {
        var record = new OtlpLogRecord();
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            switch (field)
            {
                case 1 when wireType == WireFormat.WireType.Fixed64:
                    record.TimeUnixNano = input.ReadFixed64();
                    break;
                case 2 when wireType == WireFormat.WireType.Varint:
                    record.SeverityNumber = input.ReadEnum();
                    break;
                case 3 when wireType == WireFormat.WireType.LengthDelimited:
                    record.SeverityText = input.ReadString();
                    break;
                case 5 when wireType == WireFormat.WireType.LengthDelimited:
                    record.Body = ParseAnyValue(input.ReadBytes().ToByteArray(), 0);
                    break;
                case 6 when wireType == WireFormat.WireType.LengthDelimited:
                    record.Attributes.Add(ParseKeyValue(input.ReadBytes().ToByteArray(), 0));
                    break;
                case 7 when wireType == WireFormat.WireType.Varint:
                    record.DroppedAttributesCount = input.ReadUInt32();
                    break;
                case 8 when wireType == WireFormat.WireType.Fixed32:
                    record.Flags = input.ReadFixed32();
                    break;
                case 9 when wireType == WireFormat.WireType.LengthDelimited:
                    record.TraceId = input.ReadBytes().ToByteArray();
                    break;
                case 10 when wireType == WireFormat.WireType.LengthDelimited:
                    record.SpanId = input.ReadBytes().ToByteArray();
                    break;
                case 11 when wireType == WireFormat.WireType.Fixed64:
                    record.ObservedTimeUnixNano = input.ReadFixed64();
                    break;
                case 12 when wireType == WireFormat.WireType.LengthDelimited:
                    record.EventName = input.ReadString();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        return record;
    }

    private static OtlpKeyValue ParseKeyValue(byte[] bytes, int depth)
    {
        var pair = new OtlpKeyValue();
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            if (field == 1 && wireType == WireFormat.WireType.LengthDelimited)
            {
                pair.Key = input.ReadString();
            }
            else if (field == 2 && wireType == WireFormat.WireType.LengthDelimited)
            {
                pair.Value = ParseAnyValue(input.ReadBytes().ToByteArray(), depth);
            }
            else
            {
                input.SkipLastField();
            }
        }

        return pair;
    }

    private static OtlpAnyValue ParseAnyValue(byte[] bytes, int depth)
    {
        if (depth > TelemetryLimits.MaxAttributeNestingDepth)
        {
            throw new OtlpDecodeException($"Attribute value nesting exceeds depth {TelemetryLimits.MaxAttributeNestingDepth}.");
        }

        var value = new OtlpAnyValue();
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            switch (field)
            {
                case 1 when wireType == WireFormat.WireType.LengthDelimited:
                    value.Kind = OtlpValueKind.String;
                    value.StringValue = input.ReadString();
                    break;
                case 2 when wireType == WireFormat.WireType.Varint:
                    value.Kind = OtlpValueKind.Bool;
                    value.BoolValue = input.ReadBool();
                    break;
                case 3 when wireType == WireFormat.WireType.Varint:
                    value.Kind = OtlpValueKind.Int;
                    value.IntValue = input.ReadInt64();
                    break;
                case 4 when wireType == WireFormat.WireType.Fixed64:
                    value.Kind = OtlpValueKind.Double;
                    value.DoubleValue = input.ReadDouble();
                    break;
                case 5 when wireType == WireFormat.WireType.LengthDelimited:
                    value.Kind = OtlpValueKind.Array;
                    value.ArrayValues = ParseArrayValue(input.ReadBytes().ToByteArray(), depth + 1);
                    break;
                case 6 when wireType == WireFormat.WireType.LengthDelimited:
                    value.Kind = OtlpValueKind.KvList;
                    value.KvValues = ParseKvList(input.ReadBytes().ToByteArray(), depth + 1);
                    break;
                case 7 when wireType == WireFormat.WireType.LengthDelimited:
                    value.Kind = OtlpValueKind.Bytes;
                    value.BytesValue = input.ReadBytes().ToByteArray();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        return value;
    }

    private static List<OtlpAnyValue> ParseArrayValue(byte[] bytes, int depth)
    {
        if (depth > TelemetryLimits.MaxAttributeNestingDepth)
        {
            throw new OtlpDecodeException($"Attribute value nesting exceeds depth {TelemetryLimits.MaxAttributeNestingDepth}.");
        }

        var values = new List<OtlpAnyValue>();
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 1 &&
                WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                values.Add(ParseAnyValue(input.ReadBytes().ToByteArray(), depth));
            }
            else
            {
                input.SkipLastField();
            }
        }

        return values;
    }

    private static List<OtlpKeyValue> ParseKvList(byte[] bytes, int depth)
    {
        if (depth > TelemetryLimits.MaxAttributeNestingDepth)
        {
            throw new OtlpDecodeException($"Attribute value nesting exceeds depth {TelemetryLimits.MaxAttributeNestingDepth}.");
        }

        var values = new List<OtlpKeyValue>();
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 1 &&
                WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                values.Add(ParseKeyValue(input.ReadBytes().ToByteArray(), depth));
            }
            else
            {
                input.SkipLastField();
            }
        }

        return values;
    }
}
