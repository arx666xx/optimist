using System.IO;
using System.Text;
using System.Text.Json;
using NetOptimizer.Models;

namespace NetOptimizer.Services;

public static class ExportService
{
    public static void ToCsv(IEnumerable<ConnectionInfo> items, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Протокол;Процесс;Описание;PID;Локальный;Удалённый;Хост;Страна;Подпись;Приём(Б/с);Отдача(Б/с);Состояние");

        foreach (var c in items)
        {
            sb.Append(Q(c.Protocol)).Append(';')
              .Append(Q(c.ProcessName)).Append(';')
              .Append(Q(c.Description)).Append(';')
              .Append(c.Pid).Append(';')
              .Append(Q(c.LocalEndpoint)).Append(';')
              .Append(Q(c.RemoteEndpoint)).Append(';')
              .Append(Q(c.RemoteHost)).Append(';')
              .Append(Q(c.Country)).Append(';')
              .Append(Q(c.Signature)).Append(';')
              .Append((long)c.DownloadRate).Append(';')
              .Append((long)c.UploadRate).Append(';')
              .Append(Q(c.State))
              .AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM so Excel reads Cyrillic
    }

    public static void ToJson(IEnumerable<ConnectionInfo> items, string path)
    {
        var data = items.Select(c => new
        {
            c.Protocol,
            c.ProcessName,
            c.Description,
            c.Pid,
            Local = c.LocalEndpoint,
            Remote = c.RemoteEndpoint,
            c.RemoteHost,
            c.Country,
            c.Signature,
            DownloadBytesPerSec = (long)c.DownloadRate,
            UploadBytesPerSec = (long)c.UploadRate,
            c.State,
            c.Suspicious,
            c.ProcessPath
        });

        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static string Q(string? s)
    {
        s ??= "";
        if (s.Contains(';') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
