namespace PawsPort.Filters
{
    /// <summary>
    /// 標記在 Controller 或 Action 上，ActionLoggingFilter 會略過該方法的進入/離開記錄。
    /// 適用於參數含高度敏感資料的端點（例如登入、修改密碼）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public class LogIgnoreAttribute : Attribute
    {
    }
}
