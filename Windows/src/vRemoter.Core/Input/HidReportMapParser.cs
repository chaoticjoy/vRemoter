using System.Numerics;

namespace vRemoter.Core.Input;

public readonly record struct ParsedHidUsage(ushort UsagePage, ushort Usage);

/// <summary>
/// Pure C# bounded HID Report Descriptor (0x2A4B Report Map) parser.
/// Ported from 10RemoteHub's hid_reports.py to decode both Variable (bitfield)
/// and Array (1-based index) input reports on Keyboard (0x07), Button (0x09),
/// Consumer (0x0C), and Generic Desktop (0x01) pages without requiring hid.dll.
/// </summary>
public sealed class HidReportMapParser
{
    private sealed record HidField(
        int BitOffset,
        int BitSize,
        int ReportCount,
        ushort UsagePage,
        ushort[] Usages,
        int LogicalMinimum,
        bool IsVariable);

    private readonly Dictionary<byte, List<HidField>> _fieldsByReportId = [];
    private readonly Dictionary<byte, int> _bitLengthsByReportId = [];

    public bool HasReportIds { get; private set; }
    public IReadOnlyCollection<byte> ReportIds => _fieldsByReportId.Keys;

    public HidReportMapParser(ReadOnlySpan<byte> descriptor)
    {
        ParseDescriptor(descriptor);
    }

    public ParsedHidUsage[] DecodeActiveUsages(byte reportId, ReadOnlySpan<byte> payload)
    {
        byte effectiveId = HasReportIds ? reportId : (byte)0;
        ReadOnlySpan<byte> data = payload;

        // If caller passed a packet that still starts with reportId while payload matches
        // the expected byte length + 1, strip the leading reportId byte.
        if (HasReportIds &&
            _bitLengthsByReportId.TryGetValue(effectiveId, out int expectedBits) &&
            data.Length > 1 &&
            data[0] == effectiveId &&
            (data.Length - 1) * 8 >= expectedBits &&
            data.Length * 8 > expectedBits)
        {
            data = data[1..];
        }

        if (!_fieldsByReportId.TryGetValue(effectiveId, out var fields) || fields.Count == 0)
        {
            return [];
        }

        var active = new List<ParsedHidUsage>(4);
        foreach (var field in fields)
        {
            for (int i = 0; i < field.ReportCount; i++)
            {
                int bitPos = field.BitOffset + (i * field.BitSize);
                uint raw = ExtractBits(data, bitPos, field.BitSize);
                if (field.UsagePage == 0x07 && !field.IsVariable && raw is > 0 and < 4)
                {
                    // Keyboard rollover error
                    return [];
                }
                if (raw == 0 && (!field.IsVariable && field.LogicalMinimum > 0 || field.IsVariable))
                {
                    continue;
                }

                int index = field.IsVariable ? i : (int)raw - field.LogicalMinimum;
                ushort usage;
                if (index >= 0 && index < field.Usages.Length)
                {
                    usage = field.Usages[index];
                }
                else if (!field.IsVariable && field.Usages.Length == 0 && raw <= ushort.MaxValue)
                {
                    usage = (ushort)raw;
                }
                else
                {
                    continue;
                }

                if (usage == 0 || (field.UsagePage == 0x07 && usage < 4))
                {
                    continue;
                }

                var pair = new ParsedHidUsage(field.UsagePage, usage);
                if (!active.Contains(pair))
                {
                    active.Add(pair);
                }
            }
        }

        return [.. active];
    }

    private void ParseDescriptor(ReadOnlySpan<byte> descriptor)
    {
        ushort page = 0;
        int size = 0;
        int count = 0;
        byte reportId = 0;
        int logicalMin = 0;

        var usages = new List<ushort>();
        int? usageMin = null;
        int? usageMax = null;
        var stack = new Stack<(ushort Page, int Size, int Count, byte ReportId, int LogicalMin)>();

        int i = 0;
        while (i < descriptor.Length)
        {
            byte prefix = descriptor[i++];
            if (prefix == 0xFE)
            {
                if (i + 2 > descriptor.Length) break;
                int longLen = descriptor[i];
                i += 2 + longLen;
                continue;
            }

            int len = (prefix & 0x03) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
            if (i + len > descriptor.Length) break;

            uint uval = 0;
            int sval = 0;
            if (len == 1)
            {
                uval = descriptor[i];
                sval = (sbyte)descriptor[i];
            }
            else if (len == 2)
            {
                uval = (uint)(descriptor[i] | (descriptor[i + 1] << 8));
                sval = (short)uval;
            }
            else if (len == 4)
            {
                uval = (uint)(descriptor[i] | (descriptor[i + 1] << 8) | (descriptor[i + 2] << 16) | (descriptor[i + 3] << 24));
                sval = (int)uval;
            }
            i += len;

            int kind = (prefix >> 2) & 0x03;
            int tag = prefix >> 4;

            if (kind == 1) // Global
            {
                switch (tag)
                {
                    case 0: page = (ushort)uval; break;
                    case 1: logicalMin = sval; break;
                    case 7: size = (int)uval; break;
                    case 8: reportId = (byte)uval; HasReportIds = true; break;
                    case 9: count = (int)uval; break;
                    case 10: stack.Push((page, size, count, reportId, logicalMin)); break;
                    case 11:
                        if (stack.Count > 0) (page, size, count, reportId, logicalMin) = stack.Pop();
                        break;
                }
            }
            else if (kind == 2) // Local
            {
                switch (tag)
                {
                    case 0:
                        if (uval <= ushort.MaxValue) usages.Add((ushort)uval);
                        break;
                    case 1: usageMin = (int)uval; break;
                    case 2: usageMax = (int)uval; break;
                }
            }
            else if (kind == 0) // Main
            {
                if (tag == 8 && size is > 0 and <= 32 && count is > 0 and <= 512) // Input
                {
                    bool isConstant = (uval & 0x01) != 0;
                    bool isVariable = (uval & 0x02) != 0;
                    if (usages.Count == 0 && usageMin is { } lo && usageMax is { } hi && hi >= lo && (hi - lo) < 1024)
                    {
                        for (int u = lo; u <= hi; u++)
                        {
                            if (u <= ushort.MaxValue) usages.Add((ushort)u);
                        }
                    }

                    int offset = _bitLengthsByReportId.GetValueOrDefault(reportId, 0);
                    if (!isConstant && page is 0x01 or 0x07 or 0x09 or 0x0C)
                    {
                        if (!_fieldsByReportId.TryGetValue(reportId, out var list))
                        {
                            list = [];
                            _fieldsByReportId[reportId] = list;
                        }
                        list.Add(new HidField(offset, size, count, page, [.. usages], logicalMin, isVariable));
                    }
                    _bitLengthsByReportId[reportId] = offset + (size * count);
                }
                usages.Clear();
                usageMin = null;
                usageMax = null;
            }
        }
    }

    private static uint ExtractBits(ReadOnlySpan<byte> data, int bitOffset, int bitSize)
    {
        if (bitSize <= 0 || bitOffset < 0) return 0;
        uint result = 0;
        for (int b = 0; b < bitSize; b++)
        {
            int pos = bitOffset + b;
            int byteIdx = pos >> 3;
            if (byteIdx >= data.Length) break;
            int bitIdx = pos & 7;
            if (((data[byteIdx] >> bitIdx) & 1) != 0)
            {
                result |= 1u << b;
            }
        }
        return result;
    }
}

