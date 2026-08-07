using Engine;
using Engine.UI;
using UIShowcaseApp;

const int Width = 1280;
const int Height = 800;

if (args.Contains("--smoke", StringComparer.Ordinal))
{
    var smokeRoot = UIShowcase.Create(out var smokeOverlay);
    smokeRoot.Width = Width;
    smokeRoot.Height = Height;
    smokeRoot.Measure(new System.Numerics.Vector2(Width, Height));
    smokeRoot.Arrange(System.Numerics.Vector2.Zero, new System.Numerics.Vector2(Width, Height));
    if (!ReferenceEquals(smokeOverlay.Parent, smokeRoot))
        throw new InvalidOperationException("The showcase overlay is not mounted in the root.");
    Console.WriteLine("UI showcase composition smoke test passed.");
    return;
}

using var application = EngineHost.CreateWindow("Nico UI Component Showcase", Width, Height);
var root = UIShowcase.Create(out var overlay);
application.SetUI(
    root,
    overlay,
    inputContext: UIInputContextMode.UIExclusive,
    schedulingMode: UIHostSchedulingMode.Hybrid);
application.Run();
