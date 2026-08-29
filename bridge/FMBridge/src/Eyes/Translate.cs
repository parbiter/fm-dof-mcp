using System;

namespace FMBridge.Eyes;

/// <summary>
/// Best-effort decoding of FM26 packed translation strings.
/// Blobs arrive as base64 of a binary form whose first four bytes are a
/// little-endian GameID followed by zero padding; TranslationID can resolve
/// them through its own deserializer / constructor (forward interop only).
/// </summary>
internal static class Translate
{
    public static string TryDecode(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 16) return null;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                     (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=';
            if (!ok) return null;
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(s); } catch { return null; }
        if (bytes.Length < 8 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0 || bytes[0] > 63)
            return null;
        int gameId = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);

        try
        {
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(bytes.Length);
            for (int i = 0; i < bytes.Length; i++) arr[i] = bytes[i];
            var ms = new Il2CppSystem.IO.MemoryStream(arr);
            var br = new Il2CppSystem.IO.BinaryReader(ms);
            var tid = new SI.Translation.TranslationID(gameId, "");
            tid.Deserialize(br);
            var text = tid.TranslatedString;
            if (!string.IsNullOrEmpty(text)) return text;
        }
        catch { }

        try
        {
            var tid2 = new SI.Translation.TranslationID(gameId, "");
            var t2 = tid2.TranslatedString;
            if (!string.IsNullOrEmpty(t2)) return t2;
        }
        catch { }

        return "<tid:" + gameId + ">";
    }
}
