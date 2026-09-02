namespace LotteryLab.Api.Security;

public static class Permissions
{
    public const string ImportsWrite = "imports.write";
    public const string HistoryRead = "history.read";
    public const string HistoryWrite = "history.write";
    public const string AnalysisUse = "analysis.use";
    public const string PredictionsRead = "predictions.read";
    public const string PredictionsWrite = "predictions.write";
    public const string DashboardRead = "dashboard.read";
    public const string UsersManage = "users.manage";

    public static readonly string[] All = [ImportsWrite, HistoryRead, HistoryWrite, AnalysisUse,
        PredictionsRead, PredictionsWrite, DashboardRead, UsersManage];
}
