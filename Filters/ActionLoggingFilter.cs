using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using PawsPort.Helpers;
using PawsPort.Middlewares;
using Serilog.Context;
using System.Diagnostics;
using System.Security.Claims;

namespace PawsPort.Filters
{
    /// <summary>
    /// Controller Action 的 AOP 日誌：
    /// - Trace：Action 進入（含已遮罩的參數）與離開（含耗時、狀態碼）
    /// - 推入 UserId 到 LogContext（此時認證已完成）
    /// - 將 Controller/Action 名稱寫入 HttpContext.Items，供摘要行與例外日誌補充資訊
    /// 例外只在此傳遞、不處理也不記錄，由 ExceptionHandlingMiddleware 統一記錄 Error，避免重複。
    /// </summary>
    public class ActionLoggingFilter : IAsyncActionFilter
    {
        public const string ControllerItemKey = "Log.Controller";
        public const string ActionItemKey = "Log.Action";
        public const string UserIdItemKey = "Log.UserId";

        private readonly ILogger<ActionLoggingFilter> _logger;
        private readonly RequestLoggingOptions _options;

        public ActionLoggingFilter(
            ILogger<ActionLoggingFilter> logger,
            IOptions<RequestLoggingOptions> options)
        {
            _logger = logger;
            _options = options.Value;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var descriptor = context.ActionDescriptor as ControllerActionDescriptor;
            var controllerName = descriptor?.ControllerName ?? "UnknownController";
            var actionName = descriptor?.ActionName ?? "UnknownAction";
            var userId = context.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            // 無論是否被 [LogIgnore]，都先記下位置資訊，讓例外與摘要行有線索
            context.HttpContext.Items[ControllerItemKey] = controllerName;
            context.HttpContext.Items[ActionItemKey] = actionName;
            context.HttpContext.Items[UserIdItemKey] = userId;

            if (IsIgnored(descriptor))
            {
                await next();
                return;
            }

            using (LogContext.PushProperty("UserId", userId))
            {
                var traceEnabled = _logger.IsEnabled(LogLevel.Trace);

                if (traceEnabled)
                {
                    _logger.LogTrace(
                        "進入 {Controller}.{Action} 參數: {Arguments}",
                        controllerName,
                        actionName,
                        LogSanitizer.SanitizeArguments(context.ActionArguments, _options.MaxBodyLength));
                }

                var stopwatch = Stopwatch.StartNew();
                var executed = await next();
                stopwatch.Stop();

                if (traceEnabled)
                {
                    // 例外尚未被 ExceptionHandlingMiddleware 處理，此時 Response.StatusCode 仍是預設的 200，
                    // 但最終一定會回 500，所以直接記 500 避免誤導
                    var statusCode = executed.Exception != null
                        ? StatusCodes.Status500InternalServerError
                        : (executed.Result as IStatusCodeActionResult)?.StatusCode
                            ?? context.HttpContext.Response.StatusCode;

                    _logger.LogTrace(
                        "離開 {Controller}.{Action} 狀態碼: {StatusCode} 耗時: {ElapsedMs}ms 例外: {HasException}",
                        controllerName,
                        actionName,
                        statusCode,
                        stopwatch.ElapsedMilliseconds,
                        executed.Exception != null);
                }
            }
        }

        private static bool IsIgnored(ControllerActionDescriptor? descriptor)
        {
            if (descriptor == null)
            {
                return false;
            }

            return descriptor.MethodInfo.IsDefined(typeof(LogIgnoreAttribute), inherit: true)
                || descriptor.ControllerTypeInfo.IsDefined(typeof(LogIgnoreAttribute), inherit: true);
        }
    }
}
