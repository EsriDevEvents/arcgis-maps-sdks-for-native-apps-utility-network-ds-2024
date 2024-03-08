using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Maui;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UtilityNetworks;
using Color = System.Drawing.Color;
using Symbol = Esri.ArcGISRuntime.Symbology.Symbol;

namespace OfflineTraceMaui;

/// <summary>
/// For caching symbol watches
/// </summary>
public class UtilityHelper
{
    private static readonly UtilityHelper s_singleInstance = new();
    public static UtilityHelper Current
    {
        get { return s_singleInstance; }
    }

    private readonly Dictionary<string, IReadOnlyList<LegendInfo>> _legendCache = [];
    private readonly Dictionary<string, ImageSource?> _symbolCache = [];
    private readonly Symbol defaultSwatchSymbol = new SimpleMarkerSymbol(
        SimpleMarkerSymbolStyle.Square,
        Color.Blue,
        20d
    );

    public async Task<ImageSource?> GetSwatchAsync(UtilityElement element)
    {
        var symbolKey = $"{element.NetworkSource.Name}-{element.AssetGroup.Name}";
        if (_symbolCache.ContainsKey(symbolKey))
        {
            return _symbolCache[symbolKey];
        }

        IReadOnlyList<LegendInfo>? legendInfos = null;
        if (_legendCache.ContainsKey(element.NetworkSource.Name))
        {
            legendInfos = _legendCache[element.NetworkSource.Name];
        }
        else if (element.NetworkSource.FeatureTable.Layer is Layer layer)
        {
            legendInfos = await layer.GetLegendInfosAsync();
        }

        if (
            legendInfos?.FirstOrDefault(i => i.Name == element.AssetGroup.Name)
                is not LegendInfo info
            || info.Symbol is null
        )
        {
            return null;
        }

        var swatch = await info.Symbol.CreateSwatchAsync();
        var source = await swatch.ToImageSourceAsync();
        _symbolCache[symbolKey] = source;
        return source;
    }

    public async Task<ImageSource?> GetSwatchAsync(Color color)
    {
        var symbolKey = $"{color.Name}";
        if (_symbolCache.ContainsKey(symbolKey))
        {
            return _symbolCache[symbolKey];
        }

        if (defaultSwatchSymbol.Clone() is not SimpleMarkerSymbol sms)
        {
            return null;
        }
        sms.Color = color;
        var swatch = await sms.CreateSwatchAsync();
        var source = await swatch.ToImageSourceAsync();
        _symbolCache[symbolKey] = source;
        return source;
    }
}

/// <summary>
/// For caching utility element and its corresponding feature
/// </summary>
/// <param name="element"></param>
/// <param name="feature"></param>
/// <param name="position"></param>
/// <param name="swatch"></param>
public class UtilityFeature(
    UtilityElement element,
    ArcGISFeature feature,
    Point position,
    ImageSource? swatch
)
{
    public UtilityElement Element { get; set; } = element;
    public ArcGISFeature Feature { get; set; } = feature;
    public Point Position { get; set; } = position;
    public ImageSource? Swatch { get; set; } = swatch;
}

/// <summary>
/// For caching symbol color
/// </summary>
/// <param name="color"></param>
/// <param name="swatch"></param>
public class GraphicSymbol(Color color, ImageSource? swatch)
{
    public Color Color { get; set; } = color;
    public ImageSource? Swatch { get; set; } = swatch;
}

/// <summary>
/// For flattening group layer
/// </summary>
public static class EnumerableExtensions
{
    public static IEnumerable<FeatureLayer> ToFeatureLayers(this IEnumerable<Layer> layers)
    {
        foreach (var layer in layers)
        {
            if (layer is FeatureLayer featureLayer)
            {
                yield return featureLayer;
            }

            if (layer is GroupLayer groupLayer)
            {
                foreach (var childFeatureLayer in groupLayer.Layers.ToFeatureLayers())
                {
                    yield return childFeatureLayer;
                }
            }
        }
    }
}
