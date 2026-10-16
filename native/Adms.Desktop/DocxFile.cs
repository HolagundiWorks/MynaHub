using System.Text;

namespace Adms.Desktop;

/// <summary>
/// Reads a template stored as a Word document (.docx). The letter text lives in the document package.
/// </summary>
internal static class DocxFile
{
    public static bool TryOpen(string payload, string css, out string html, out string style)
    {
        html = "";
        style = "";
        var trimmed = payload.Trim();
        if (!string.Equals(css, "docx", StringComparison.Ordinal) && !trimmed.StartsWith("UEsDB", StringComparison.Ordinal))
            return false;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            return false;
        }
        var files = Unzip(bytes);
        if (files == null)
            return false;
        if (files.TryGetValue("word/adms-source.html", out var source))
            html = Encoding.UTF8.GetString(source);
        else if (files.TryGetValue("word/document.xml", out var document))
            html = "<p>" + System.Net.WebUtility.HtmlEncode(Plain(Encoding.UTF8.GetString(document))) + "</p>";
        else
            return false;
        if (files.TryGetValue("word/adms-page.txt", out var page))
            style = Encoding.UTF8.GetString(page);
        return true;
    }

    private static Dictionary<string, byte[]>? Unzip(byte[] bytes)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var i = 0;
        while (i + 30 <= bytes.Length && bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04)
        {
            var method = BitConverter.ToUInt16(bytes, i + 8);
            var size = BitConverter.ToInt32(bytes, i + 18);
            var nameLen = BitConverter.ToUInt16(bytes, i + 26);
            var extraLen = BitConverter.ToUInt16(bytes, i + 28);
            var nameAt = i + 30;
            var dataAt = nameAt + nameLen + extraLen;
            if (size < 0 || nameAt + nameLen > bytes.Length || dataAt + size > bytes.Length || method != 0)
                return null;
            var name = Encoding.UTF8.GetString(bytes, nameAt, nameLen);
            var data = new byte[size];
            Buffer.BlockCopy(bytes, dataAt, data, 0, size);
            files[name] = data;
            i = dataAt + size;
        }
        return files.ContainsKey("word/document.xml") ? files : null;
    }

    private static string Plain(string xml)
    {
        var builder = new StringBuilder();
        var i = 0;
        while (i < xml.Length)
        {
            if (xml.AsSpan(i).StartsWith("<w:t"))
            {
                var gt = xml.IndexOf('>', i);
                var end = gt < 0 ? -1 : xml.IndexOf("</w:t>", gt, StringComparison.Ordinal);
                if (gt >= 0 && end > gt)
                    builder.Append(xml.AsSpan(gt + 1, end - gt - 1));
                i = end < 0 ? xml.Length : end + 6;
                continue;
            }
            i++;
        }
        return builder.ToString();
    }
}
