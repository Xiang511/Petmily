using Microsoft.Extensions.Options;
using PawsPort.Helpers;
using Serilog.Context;
using Serilog.Events;
using System.Text;
using System.Text.RegularExpressions;

namespace PawsPort.Middlewares
{
    /// <summary>
    /// 負責三件事：
    /// 1. 產生/沿用 CorrelationId，並推入 Serilog LogContext，讓同一請求的所有日誌可串聯
    /// 2. 推入 ClientIp
    /// 3. 僅在 Development 且 Trace 等級啟用時，記錄請求/回應 body（已遮罩）
    /// 請求摘要行（Information/Warning/Error）由 UseSerilogRequestLogging 產生，這裡不重複記錄。
    /// </summary>
    public class RequestLoggingMiddleware
    {
        public const string CorrelationHeader = "X-Correlation-Id";
        public const string CorrelationItemKey = "Log.CorrelationId";

        // 只接受英數與連字號，避免用戶端塞入換行等字元偽造日誌
        private static readonly Regex SafeCorrelationId = new("^[A-Za-z0-9\\-]{1,64}$", RegexOptions.Compiled);

        private const int MaxBodyReadBytes = 64 * 1024;

        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _logger;
        private readonly RequestLoggingOptions _options;
        private readonly bool _isDevelopment;

        public RequestLoggingMiddleware(
            RequestDelegate next,
            ILogger<RequestLoggingMiddleware> logger,
            IOptions<RequestLoggingOptions> options,
            IHostEnvironment environment)
        {
            _next = next;
            _logger = logger;
            _options = options.Value;
            _isDevelopment = environment.IsDevelopment();
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var correlationId = ResolveCorrelationId(context);
            context.Items[CorrelationItemKey] = correlationId;
            context.Response.Headers[CorrelationHeader] = correlationId;

            using (LogContext.PushProperty("CorrelationId", correlationId))
            using (LogContext.PushProperty("ClientIp", context.Connection.RemoteIpAddress?.ToString()))
            {
                if (ShouldLogBody(context))
                {
                    await InvokeWithBodyLoggingAsync(context);
                }
                else
                {
                    await _next(context);
                }
            }
        }

        private static string ResolveCorrelationId(HttpContext context)
        {
            var incoming = context.Request.Headers[CorrelationHeader].FirstOrDefault();

            if (!string.IsNullOrEmpty(incoming) && SafeCorrelationId.IsMatch(incoming))
            {
                return incoming;
            }

            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// body 記錄的條件：Development + Verbose 已啟用 + 非排除路徑 + 非 WebSocket
        /// </summary>
        private bool ShouldLogBody(HttpContext context)
        {
            return _isDevelopment
                && _logger.IsEnabled(LogLevel.Trace)
                && !_options.IsExcluded(context.Request.Path)
                && !context.WebSockets.IsWebSocketRequest;
        }

        private async Task InvokeWithBodyLoggingAsync(HttpContext context)
        {
            await LogRequestBodyAsync(context);

            // 暫時以記憶體串流取代 Response.Body，結束後再複製回原串流
            var originalBody = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await _next(context);
            }
            finally
            {
                context.Response.Body = originalBody;
                buffer.Position = 0;

                if (IsJson(context.Response.ContentType) && buffer.Length > 0)
                {
                    var readLength = (int)Math.Min(buffer.Length, MaxBodyReadBytes);
                    var bytes = new byte[readLength];
                    var read = await buffer.ReadAsync(bytes.AsMemory(0, readLength));
                    var text = Encoding.UTF8.GetString(bytes, 0, read);

                    _logger.Log(
                        LogLevel.Trace,
                        "回應 Body {StatusCode}: {Body}",
                        context.Response.StatusCode,
                        LogSanitizer.SanitizeJson(text, _options.MaxBodyLength));
                }

                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody);
            }
        }

        private async Task LogRequestBodyAsync(HttpContext context)
        {
            var request = context.Request;

            if (!IsJson(request.ContentType)
                || request.ContentLength is null or 0
                || request.ContentLength > MaxBodyReadBytes)
            {
                return;
            }

            request.EnableBuffering();

            using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync();
            request.Body.Position = 0;

            _logger.Log(
                LogLevel.Trace,
                "請求 Body {Method} {Path}: {Body}",
                request.Method,
                request.Path.Value,
                LogSanitizer.SanitizeJson(text, _options.MaxBodyLength));
        }

        private static bool IsJson(string? contentType)
        {
            return contentType != null
                && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        }
    }
}
