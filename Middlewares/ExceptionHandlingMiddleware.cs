using PawsPort.Filters;
using System.Net;
using System.Security.Claims;

namespace PawsPort.Middlewares
{
    /// <summary>
    /// 全域例外處理：未預期例外只在這裡記錄一次 Error（含 Controller/Action/UserId 資訊），
    /// 並回傳統一的 500 JSON。
    /// </summary>
    public class ExceptionHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionHandlingMiddleware> _logger;
        private readonly IHostEnvironment _environment;

        public ExceptionHandlingMiddleware(
            RequestDelegate next,
            ILogger<ExceptionHandlingMiddleware> logger,
            IHostEnvironment environment)
        {
            _next = next;
            _logger = logger;
            _environment = environment;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                // 呼叫下一個 Middleware
                await _next(context);
            }
            catch (Exception ex)
            {
                // 由 ActionLoggingFilter 寫入；若例外發生在 MVC 之外則為 null
                var controller = context.Items[ActionLoggingFilter.ControllerItemKey] as string;
                var action = context.Items[ActionLoggingFilter.ActionItemKey] as string;
                var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                _logger.LogError(
                    ex,
                    "發生未預期的錯誤 {Method} {Path} {Controller}.{Action} UserId={UserId}",
                    context.Request.Method,
                    context.Request.Path.Value,
                    controller,
                    action,
                    userId);

                await HandleExceptionAsync(context, ex);
            }
        }

        private Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            // 回應已開始送出時無法再改寫狀態碼，直接結束
            if (context.Response.HasStarted)
            {
                return Task.CompletedTask;
            }

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            var response = new
            {
                Success = false,
                Code = "INTERNAL_SERVER_ERROR",
                Message = "伺服器內部錯誤，請稍後再試。",
                // 詳細訊息只在開發環境回傳，避免洩漏內部資訊
                Detail = _environment.IsDevelopment() ? exception.Message : null,
                CorrelationId = context.Items[RequestLoggingMiddleware.CorrelationItemKey] as string
            };

            return context.Response.WriteAsJsonAsync(response);
        }
    }
}
