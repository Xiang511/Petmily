namespace PawsPort.Middlewares
{
    /// <summary>
    /// AOP 日誌設定，對應 appsettings.json 的 "RequestLogging" 區段
    /// </summary>
    public class RequestLoggingOptions
    {
        public const string SectionName = "RequestLogging";

        /// <summary>
        /// 慢請求門檻（毫秒），超過時摘要行升級為 Warning
        /// </summary>
        public int SlowRequestThresholdMs { get; set; } = 1000;

        /// <summary>
        /// 單筆 body 記錄的最大字元數，超過會被截斷
        /// </summary>
        public int MaxBodyLength { get; set; } = 2048;

        /// <summary>
        /// 不記錄日誌的路徑前綴（靜態檔、文件、SignalR 等噪音）
        /// 預設為空，實際值由 appsettings.json 提供，避免陣列繫結時與預設值疊加
        /// </summary>
        public List<string> ExcludedPaths { get; set; } = new();

        public bool IsExcluded(PathString path)
        {
            foreach (var prefix in ExcludedPaths)
            {
                if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
