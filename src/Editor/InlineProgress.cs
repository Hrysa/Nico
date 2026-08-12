namespace Editor;

/// <summary>Forwards progress synchronously on the reporting thread to a thread-safe receiver.</summary>
/// <typeparam name="T">Reported value type.</typeparam>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    /// <summary>Creates a synchronous progress adapter.</summary>
    /// <param name="report">Thread-safe receiver invoked for each report.</param>
    public InlineProgress(Action<T> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _report = report;
    }

    /// <inheritdoc/>
    public void Report(T value) => _report(value);
}
