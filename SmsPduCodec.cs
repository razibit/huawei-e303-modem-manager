using System.Globalization;
using System.Text;

namespace HuaweiE303VoiceDialer;

internal sealed record DecodedSms(
    int Index,
    string Status,
    string Sender,
    DateTime Timestamp,
    string Text,
    int? ConcatenationReference,
    int PartNumber,
    int PartCount);

internal static class SmsPduCodec
{
    private static readonly char[] GsmAlphabet = BuildGsmAlphabet();
    private static readonly Dictionary<char, byte> GsmReverse = BuildGsmReverse();
    private static readonly Dictionary<byte, char> GsmExtension = new()
    {
        [0x0A] = '\f', [0x14] = '^', [0x28] = '{', [0x29] = '}', [0x2F] = '\\',
        [0x3C] = '[', [0x3D] = '~', [0x3E] = ']', [0x40] = '|', [0x65] = '€'
    };
    private static readonly Dictionary<char, byte> GsmExtensionReverse =
        GsmExtension.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static IReadOnlyList<(string Pdu, int TpduLength)> EncodeSubmit(string recipient, string text)
    {
        var normalized = recipient.Trim();
        var international = normalized.StartsWith('+');
        var digits = international ? normalized[1..] : normalized;
        if (digits.Length is < 1 or > 20 || digits.Any(ch => !char.IsAsciiDigit(ch)))
            throw new ArgumentException("Enter a phone number with digits and an optional leading +.", nameof(recipient));
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Enter a message before sending.", nameof(text));

        var gsm = TryEncodeGsm(text, out var septets);
        var parts = gsm
            ? SplitSeptets(septets, 160, 153)
            : SplitUtf16(text, 70, 67);
        var concat = parts.Count > 1;
        var reference = Random.Shared.Next(0, 256);
        var output = new List<(string Pdu, int TpduLength)>(parts.Count);

        for (var i = 0; i < parts.Count; i++)
        {
            var tpdu = new List<byte>
            {
                (byte)(0x01 | (concat ? 0x40 : 0)), // SMS-SUBMIT, optionally with UDH
                0x00,                               // message reference
                (byte)digits.Length,
                international ? (byte)0x91 : (byte)0x81
            };
            tpdu.AddRange(EncodeSemiOctets(digits));
            tpdu.Add(0x00);                         // protocol identifier
            tpdu.Add(gsm ? (byte)0x00 : (byte)0x08); // GSM 7-bit or UCS-2

            byte[] userData;
            int userDataLength;
            if (gsm)
            {
                var payload = (List<byte>)parts[i];
                userData = concat
                    ? PackGsmWithHeader(payload, (byte)reference, (byte)parts.Count, (byte)(i + 1))
                    : PackSeptets(payload);
                userDataLength = concat ? 7 + payload.Count : payload.Count;
            }
            else
            {
                var payload = (string)parts[i];
                var body = Encoding.BigEndianUnicode.GetBytes(payload);
                if (concat)
                {
                    userData = new byte[6 + body.Length];
                    userData[0] = 0x05;
                    userData[1] = 0x00;
                    userData[2] = 0x03;
                    userData[3] = (byte)reference;
                    userData[4] = (byte)parts.Count;
                    userData[5] = (byte)(i + 1);
                    Buffer.BlockCopy(body, 0, userData, 6, body.Length);
                }
                else userData = body;
                userDataLength = userData.Length;
            }

            if (userDataLength > 255)
                throw new InvalidOperationException("A message segment exceeds the modem's SMS size limit.");
            tpdu.Add((byte)userDataLength);
            tpdu.AddRange(userData);
            var pduBytes = new byte[1 + tpdu.Count];
            Buffer.BlockCopy(tpdu.ToArray(), 0, pduBytes, 1, tpdu.Count); // empty SMSC address
            output.Add((Convert.ToHexString(pduBytes), pduBytes.Length - 1));
        }

        return output;
    }

    public static bool TryDecodeDeliver(int index, string status, string hex, out DecodedSms? message)
    {
        message = null;
        try
        {
            var pdu = Convert.FromHexString(new string(hex.Where(Uri.IsHexDigit).ToArray()));
            if (pdu.Length < 2) return false;
            var cursor = 1 + pdu[0]; // SMSC length and data
            if (cursor >= pdu.Length) return false;
            var first = pdu[cursor++];
            var messageType = first & 0x03;
            var isDeliver = messageType == 0;
            var isSubmit = messageType == 1;
            if (!isDeliver && !isSubmit) return false;
            var hasHeader = (first & 0x40) != 0;
            if (isSubmit) cursor++; // TP-MR
            if (cursor + 2 > pdu.Length) return false;

            var addressLength = pdu[cursor++];
            var typeOfAddress = pdu[cursor++];
            var addressBytes = (addressLength + 1) / 2;
            if (cursor + addressBytes + 10 > pdu.Length) return false;
            var peer = DecodeAddress(pdu.AsSpan(cursor, addressBytes), addressLength, typeOfAddress);
            cursor += addressBytes;
            cursor++; // PID
            var dcs = pdu[cursor++];
            DateTime timestamp;
            if (isDeliver)
            {
                if (cursor + 7 > pdu.Length) return false;
                timestamp = DecodeTimestamp(pdu.AsSpan(cursor, 7));
                cursor += 7;
            }
            else
            {
                var validityFormat = first & 0x18;
                cursor += validityFormat switch { 0x00 => 0, 0x10 => 1, 0x08 or 0x18 => 7, _ => 0 };
                timestamp = DateTime.Now;
            }
            if (cursor >= pdu.Length) return false;
            var userDataLength = pdu[cursor++];
            var byteLength = (dcs & 0x0C) == 0x00 ? (userDataLength * 7 + 7) / 8 : userDataLength;
            if (cursor + byteLength > pdu.Length) return false;
            var userData = pdu.AsSpan(cursor, byteLength);

            var headerBytes = 0;
            int? concatReference = null;
            var partNumber = 1;
            var partCount = 1;
            if (hasHeader)
            {
                if (userData.IsEmpty) return false;
                headerBytes = userData[0] + 1;
                if (headerBytes > userData.Length) return false;
                ParseConcatenation(userData[..headerBytes], out concatReference, out partNumber, out partCount);
            }

            string body;
            if ((dcs & 0x0C) == 0x08)
            {
                var payload = userData[headerBytes..];
                body = Encoding.BigEndianUnicode.GetString(payload[..(payload.Length & ~1)]);
            }
            else if ((dcs & 0x0C) == 0x00)
            {
                var headerSeptets = hasHeader ? (headerBytes * 8 + 6) / 7 : 0;
                var textSeptets = Math.Max(0, userDataLength - headerSeptets);
                body = UnpackSeptets(userData, textSeptets, headerSeptets * 7);
            }
            else
            {
                body = Convert.ToHexString(userData[headerBytes..]);
            }

            message = new DecodedSms(index, status, isDeliver ? peer : "To " + peer, timestamp, body,
                concatReference, partNumber, partCount);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryEncodeGsm(string text, out List<byte> septets)
    {
        septets = new List<byte>(text.Length);
        foreach (var character in text)
        {
            if (GsmReverse.TryGetValue(character, out var value)) septets.Add(value);
            else if (GsmExtensionReverse.TryGetValue(character, out value))
            {
                septets.Add(0x1B);
                septets.Add(value);
            }
            else
            {
                septets.Clear();
                return false;
            }
        }
        return true;
    }

    private static List<object> SplitSeptets(List<byte> values, int singleLimit, int multipartLimit)
    {
        if (values.Count <= singleLimit) return new List<object> { values };
        var parts = new List<object>();
        for (var offset = 0; offset < values.Count;)
        {
            var take = Math.Min(multipartLimit, values.Count - offset);
            if (offset + take < values.Count && take > 0 && values[offset + take - 1] == 0x1B) take--;
            parts.Add(values.Skip(offset).Take(take).ToList());
            offset += take;
        }
        return parts;
    }

    private static List<object> SplitUtf16(string text, int singleLimit, int multipartLimit)
    {
        if (text.Length <= singleLimit) return new List<object> { text };
        var parts = new List<object>();
        for (var offset = 0; offset < text.Length;)
        {
            var take = Math.Min(multipartLimit, text.Length - offset);
            if (offset + take < text.Length && char.IsHighSurrogate(text[offset + take - 1])) take--;
            parts.Add(text.Substring(offset, take));
            offset += take;
        }
        return parts;
    }

    private static byte[] PackSeptets(IReadOnlyList<byte> septets)
    {
        var output = new byte[(septets.Count * 7 + 7) / 8];
        PackSeptetValues(output, septets, 0);
        return output;
    }

    private static byte[] PackGsmWithHeader(IReadOnlyList<byte> septets, byte reference, byte total, byte sequence)
    {
        var header = new byte[] { 0x05, 0x00, 0x03, reference, total, sequence };
        var headerSeptets = 7;
        var output = new byte[((headerSeptets + septets.Count) * 7 + 7) / 8];
        Buffer.BlockCopy(header, 0, output, 0, header.Length);
        PackSeptetValues(output, septets, headerSeptets * 7);
        return output;
    }

    private static void PackSeptetValues(byte[] output, IReadOnlyList<byte> septets, int initialBit)
    {
        for (var i = 0; i < septets.Count; i++)
        {
            var bit = initialBit + i * 7;
            var byteIndex = bit / 8;
            var shift = bit % 8;
            var value = (byte)(septets[i] & 0x7F);
            output[byteIndex] |= (byte)(value << shift);
            if (shift > 1 && byteIndex + 1 < output.Length)
                output[byteIndex + 1] |= (byte)(value >> (8 - shift));
        }
    }

    private static string UnpackSeptets(ReadOnlySpan<byte> bytes, int count, int initialBit)
    {
        var result = new StringBuilder(count);
        var escaped = false;
        for (var i = 0; i < count; i++)
        {
            var bit = initialBit + i * 7;
            var byteIndex = bit / 8;
            var shift = bit % 8;
            if (byteIndex >= bytes.Length) break;
            var value = bytes[byteIndex] >> shift;
            if (shift > 1 && byteIndex + 1 < bytes.Length)
                value |= bytes[byteIndex + 1] << (8 - shift);
            var septet = (byte)(value & 0x7F);
            if (escaped)
            {
                result.Append(GsmExtension.TryGetValue(septet, out var extension) ? extension : ' ');
                escaped = false;
            }
            else if (septet == 0x1B) escaped = true;
            else result.Append(GsmAlphabet[septet]);
        }
        if (escaped) result.Append(' ');
        return result.ToString();
    }

    private static byte[] EncodeSemiOctets(string digits)
    {
        var padded = digits.Length % 2 == 0 ? digits : digits + "F";
        var bytes = new byte[padded.Length / 2];
        for (var i = 0; i < padded.Length; i += 2)
        {
            var low = HexValue(padded[i]);
            var high = padded[i + 1] == 'F' ? 0x0F : HexValue(padded[i + 1]);
            bytes[i / 2] = (byte)(low | (high << 4));
        }
        return bytes;
    }

    private static string DecodeAddress(ReadOnlySpan<byte> semiOctets, int digits, byte type)
    {
        var builder = new StringBuilder(digits + 1);
        if ((type & 0x70) == 0x10) builder.Append('+');
        for (var i = 0; i < digits; i++)
        {
            var value = (i % 2 == 0) ? semiOctets[i / 2] & 0x0F : semiOctets[i / 2] >> 4;
            if (value <= 9) builder.Append((char)('0' + value));
        }
        return builder.ToString();
    }

    private static DateTime DecodeTimestamp(ReadOnlySpan<byte> bytes)
    {
        var year = DecodeSwappedBcd(bytes[0]);
        var month = Math.Clamp(DecodeSwappedBcd(bytes[1]), 1, 12);
        var day = Math.Clamp(DecodeSwappedBcd(bytes[2]), 1, 31);
        var hour = Math.Clamp(DecodeSwappedBcd(bytes[3]), 0, 23);
        var minute = Math.Clamp(DecodeSwappedBcd(bytes[4]), 0, 59);
        var second = Math.Clamp(DecodeSwappedBcd(bytes[5]), 0, 59);
        try { return new DateTime(year < 70 ? 2000 + year : 1900 + year, month, day, hour, minute, second, DateTimeKind.Local); }
        catch { return DateTime.Now; }
    }

    private static int DecodeSwappedBcd(byte value) => (value & 0x0F) * 10 + ((value >> 4) & 0x0F);

    private static void ParseConcatenation(ReadOnlySpan<byte> header, out int? reference, out int part, out int count)
    {
        reference = null;
        part = 1;
        count = 1;
        var cursor = 1;
        while (cursor + 1 < header.Length)
        {
            var id = header[cursor++];
            var length = header[cursor++];
            if (cursor + length > header.Length) break;
            if (id == 0x00 && length == 3)
            {
                reference = header[cursor];
                count = Math.Max(1, (int)header[cursor + 1]);
                part = Math.Max(1, (int)header[cursor + 2]);
            }
            else if (id == 0x08 && length == 4)
            {
                reference = (header[cursor] << 8) | header[cursor + 1];
                count = Math.Max(1, (int)header[cursor + 2]);
                part = Math.Max(1, (int)header[cursor + 3]);
            }
            cursor += length;
        }
    }

    private static int HexValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'A' and <= 'F' => character - 'A' + 10,
        >= 'a' and <= 'f' => character - 'a' + 10,
        _ => throw new FormatException("Invalid hexadecimal value.")
    };

    private static char[] BuildGsmAlphabet()
    {
        const string values = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ\u001BÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
        if (values.Length != 128) throw new InvalidOperationException("GSM alphabet table is incomplete.");
        return values.ToCharArray();
    }

    private static Dictionary<char, byte> BuildGsmReverse()
    {
        var result = new Dictionary<char, byte>();
        for (byte i = 0; i < GsmAlphabet.Length; i++)
            if (i != 0x1B && !result.ContainsKey(GsmAlphabet[i])) result[GsmAlphabet[i]] = i;
        return result;
    }
}
