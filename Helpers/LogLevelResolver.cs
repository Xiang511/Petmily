using Serilog.Events;

namespace PawsPort.Helpers
{
    /// <summary>
    /// 集中管理「什麼情況使用什麼日誌等級」的判斷規則。
    /// 其他程式碼不應自行判斷等級，一律呼叫此類別。
    ///
    /// 等級語意：
    ///   Verbose (Trace) : 方法進入/離開（僅 Development）
    ///   Information     : 一切正常
    ///   Warning         : 業務邏輯失敗但系統正常運作（4xx、慢請求）
    ///   Error           : 系統錯誤（5xx、未處理例外）
    ///   Fatal           : 應用程式無法繼續，由 Program.cs 負責，不經過此類別
    /// </summary>
    public static class LogLevelResolver
    {
        public static LogEventLevel Resolve(
            int statusCode,
            Exception? exception,
            double elapsedMs,
            int slowThresholdMs)
        {
            // 系統錯誤：有未處理例外，或是 5xx
            if (exception != null || statusCode >= 500)
            {
                return LogEventLevel.Error;
            }

            // 業務失敗：4xx，系統本身運作正常
            if (statusCode >= 400)
            {
                return LogEventLevel.Warning;
            }

            // 慢請求：回應成功但耗時過長
            if (elapsedMs > slowThresholdMs)
            {
                return LogEventLevel.Warning;
            }

            return LogEventLevel.Information;

            // TODO: 導入 Result pattern 後，在此新增 errorKind 參數：
            //   Validation / NotFound / Conflict / Unauthorized -> Warning
            //   Unexpected / Failure                            -> Error
            // 其他呼叫端不需要修改。
        }
    }
}
