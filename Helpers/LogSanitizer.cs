using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PawsPort.Helpers
{
    /// <summary>
    /// 日誌內容的遮罩與截斷，避免個資與密鑰進入 Seq
    /// </summary>
    public static class LogSanitizer
    {
        private const string Mask = "***";

        // 欄位名稱包含下列關鍵字（不分大小寫）時，整個值會被遮罩
        private static readonly Regex SensitiveJsonField = new(
            "\"(?<key>[^\"]*(password|pwd|token|secret|authorization|apikey|email|phone|mobile|idcard|credential)[^\"]*)\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|[^,}\\]\\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            WriteIndented = false
        };

        /// <summary>
        /// 遮罩 JSON 字串中的敏感欄位，並截斷到指定長度
        /// </summary>
        public static string SanitizeJson(string? json, int maxLength)
        {
            if (string.IsNullOrEmpty(json))
            {
                return string.Empty;
            }

            var masked = SensitiveJsonField.Replace(json, "\"${key}\":\"" + Mask + "\"");
            return Truncate(masked, maxLength);
        }

        /// <summary>
        /// 將 Action 的參數序列化成已遮罩的字串，檔案與 CancellationToken 不會被序列化內容
        /// </summary>
        public static string SanitizeArguments(IDictionary<string, object?> arguments, int maxLength)
        {
            var safe = new Dictionary<string, object?>();

            foreach (var (name, value) in arguments)
            {
                safe[name] = value switch
                {
                    null => null,
                    CancellationToken => "[CancellationToken]",
                    IFormFile file => $"[File {file.FileName}, {file.Length} bytes]",
                    IFormFileCollection files => $"[Files x{files.Count}]",
                    _ => value
                };
            }

            string json;
            try
            {
                json = JsonSerializer.Serialize(safe, SerializerOptions);
            }
            catch (Exception)
            {
                return "[unserializable]";
            }

            return SanitizeJson(json, maxLength);
        }

        public static string Truncate(string value, int maxLength)
        {
            if (maxLength <= 0 || value.Length <= maxLength)
            {
                return value;
            }

            return value[..maxLength] + $"...(truncated, total {value.Length} chars)";
        }
    }
}
