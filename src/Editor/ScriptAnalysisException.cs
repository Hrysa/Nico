namespace Editor;

/// <summary>Reports script source assets that fail semantic attachment rules.</summary>
public sealed class ScriptAnalysisException : Exception
{
    /// <summary>Gets semantic diagnostics produced for script source assets.</summary>
    public IReadOnlyList<ScriptAssetDiagnostic> Diagnostics { get; }

    /// <summary>Creates an exception from semantic script diagnostics.</summary>
    /// <param name="diagnostics">Semantic discovery diagnostics.</param>
    public ScriptAnalysisException(IReadOnlyList<ScriptAssetDiagnostic> diagnostics)
        : base(BuildMessage(diagnostics))
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Diagnostics = diagnostics;
    }

    /// <summary>Formats semantic diagnostics for logs and progress UI.</summary>
    /// <param name="diagnostics">Semantic discovery diagnostics.</param>
    /// <returns>Combined exception message.</returns>
    private static string BuildMessage(IReadOnlyList<ScriptAssetDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return "Game script analysis failed." + Environment.NewLine +
               string.Join(Environment.NewLine,
                   diagnostics.Select(item => $"{item.SourcePath}: {item.Message}"));
    }
}
