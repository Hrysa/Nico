using System.Globalization;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class LocalizationTests
{
    /// <summary>Verifies exact, parent, invariant, and caller fallback lookup order.</summary>
    [Fact]
    public void Catalog_Get_UsesDeterministicCultureFallback()
    {
        var catalog = new UILocalizationCatalog();
        catalog.Set(CultureInfo.InvariantCulture, "menu.save", "Save");
        catalog.Set(CultureInfo.GetCultureInfo("fr"), "menu.save", "Enregistrer");
        catalog.Set(CultureInfo.GetCultureInfo("fr-CA"), "menu.cancel", "Annuler");

        Assert.Equal("Enregistrer",
            catalog.Get(CultureInfo.GetCultureInfo("fr-CA"), "menu.save"));
        Assert.Equal("Annuler",
            catalog.Get(CultureInfo.GetCultureInfo("fr-CA"), "menu.cancel"));
        Assert.Equal("Save",
            catalog.Get(CultureInfo.GetCultureInfo("ja-JP"), "menu.save"));
        Assert.Equal("Missing",
            catalog.Get(CultureInfo.GetCultureInfo("ja-JP"), "missing", "Missing"));
    }

    /// <summary>Verifies inherited runtime culture updates localized retained text and direction.</summary>
    [Fact]
    public void LocalizedLabel_CultureChange_UpdatesTextAndDirection()
    {
        var catalog = new UILocalizationCatalog();
        catalog.Set(CultureInfo.InvariantCulture, "status.ready", "Ready");
        catalog.Set(CultureInfo.GetCultureInfo("ar"), "status.ready", "جاهز");
        var root = new Canvas { Width = 200f, Height = 40f };
        var label = new UILocalizedLabel(catalog, "status.ready");
        root.Add(label, default);
        root.Culture = CultureInfo.GetCultureInfo("en-US");
        root.BuildDrawList();
        Assert.Equal("Ready", label.Text);

        root.Culture = CultureInfo.GetCultureInfo("ar-SA");
        root.FlowDirection = UIFlowDirection.RightToLeft;
        root.BuildDrawList();

        Assert.Equal("جاهز", label.Text);
        Assert.Equal(UIFlowDirection.RightToLeft, label.FlowDirection);
    }

    /// <summary>Verifies catalog mutation can explicitly invalidate a retained localized label.</summary>
    [Fact]
    public void LocalizedLabel_RefreshLocalization_UsesUpdatedCatalogValue()
    {
        var catalog = new UILocalizationCatalog();
        catalog.Set(CultureInfo.InvariantCulture, "status", "Loading");
        var label = new UILocalizedLabel(catalog, "status", width: 100f, height: 20f);
        label.BuildDrawList();
        catalog.Set(CultureInfo.InvariantCulture, "status", "Complete");

        label.RefreshLocalization();
        label.BuildDrawList();

        Assert.Equal("Complete", label.Text);
    }
}
