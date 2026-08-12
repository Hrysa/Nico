using System.Numerics;
using Engine.UI;

namespace Editor;

/// <summary>Displays determinate progress for a background asset-import batch.</summary>
public sealed class AssetImportProgressDialog : Modal
{
    private readonly Label _assetLabel;
    private readonly Label _countLabel;
    private readonly ProgressBar _progressBar;

    /// <summary>Creates a centered asset-import progress dialog.</summary>
    /// <param name="width">Editor window width.</param>
    /// <param name="height">Editor window height.</param>
    /// <param name="totalCount">Number of assets scheduled for import.</param>
    /// <param name="theme">Theme supplying dialog visuals.</param>
    public AssetImportProgressDialog(
        float width,
        float height,
        int totalCount,
        UITheme? theme = null)
        : base(width, height, MathF.Min(480f, width - 48f), 190f, theme)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        var content = new Canvas();
        Dialog.AddChild(content);
        content.Add(new DialogHeader("Importing Assets",
            "The editor is ready while project assets are prepared.", resolvedTheme),
            Vector2.Zero);
        _assetLabel = new Label("Starting background import...", Dialog.Width - 40f, 28f)
        {
            Name = "AssetImportCurrentAsset",
            ForegroundColor = resolvedTheme.TextPrimary,
            PaddingLeft = 0f
        };
        content.Add(_assetLabel, new Vector2(20f, 82f));
        _progressBar = new ProgressBar(Dialog.Width - 40f, 8f, resolvedTheme)
        {
            Name = "AssetImportProgress",
            Minimum = 0f,
            Maximum = Math.Max(1, totalCount)
        };
        content.Add(_progressBar, new Vector2(20f, 122f));
        _countLabel = new Label($"0 of {totalCount}", Dialog.Width - 40f, 24f)
        {
            Name = "AssetImportCount",
            ForegroundColor = resolvedTheme.TextSecondary,
            PaddingLeft = 0f
        };
        content.Add(_countLabel, new Vector2(20f, 142f));
    }

    /// <summary>Updates the visible completed-asset progress.</summary>
    /// <param name="completedCount">Number of completed imports.</param>
    /// <param name="totalCount">Total imports in the batch.</param>
    /// <param name="projectPath">Project-relative path that most recently completed.</param>
    public void SetProgress(int completedCount, int totalCount, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        var total = Math.Max(0, totalCount);
        var completed = Math.Clamp(completedCount, 0, total);
        _progressBar.Maximum = Math.Max(1, total);
        _progressBar.Value = completed;
        _assetLabel.Text = projectPath;
        _countLabel.Text = $"{completed} of {total}";
    }
}
