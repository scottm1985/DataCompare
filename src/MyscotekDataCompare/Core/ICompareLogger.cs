namespace MyscotekDataCompare.Core
{
    /// <summary>
    /// Receives every log line the compare engine and the Core services produce. Called synchronously on
    /// the thread running <see cref="CompareEngine.Compare"/>; a UI implementation must marshal to its own
    /// thread.
    /// </summary>
    public interface ICompareLogger { void Log(LogLevel level, string message); }
}
