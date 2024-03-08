using System.Collections.ObjectModel;
using System.Diagnostics;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Maui;
using Esri.ArcGISRuntime.Portal;
using Esri.ArcGISRuntime.Security;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.Tasks;
using Esri.ArcGISRuntime.Tasks.Offline;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UtilityNetworks;
using Color = System.Drawing.Color;
using Geometry = Esri.ArcGISRuntime.Geometry.Geometry;
using Map = Esri.ArcGISRuntime.Mapping.Map;

namespace OfflineTraceMaui;

public partial class MainPage : ContentPage
{
    #region Private Members

    // A webmap with named traces and map area that is configured to trace utility network features offline.
    public const string WebmapURL =
        "https://sampleserver7.arcgisonline.com/portal/home/item.html?id=8eb86267776146d694792ce55a835afc";

    // Portal login credentials that can access the webmap.
    public const string PortalURL = "https://sampleserver7.arcgisonline.com/portal/sharing/rest";
    public const string Username = "editor01";
    public const string Password = "S7#i2LWmYH75";

    // For job cancellation
    private CancellationTokenSource? _cancellationTokenSource = null;

    // For traces
    private readonly ObservableCollection<UtilityNamedTraceConfiguration> traces = [];

    // For starting points
    private const string WhereClause =
        "ASSETID in ('Dstrbtn-Pp-16071', 'Dstrbtn-Pp-15862', 'Dstrbtn-Pp-15937')";
    private readonly ObservableCollection<UtilityFeature> startingPoints = [];
    private bool _isAddingStartingPoint = false;
    private readonly GraphicsOverlay startingPointsOverlay =
        new()
        {
            Renderer = new SimpleRenderer(
                new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Cross, Color.LimeGreen, 20d)
            )
        };

    // For aggregated geometry result
    private readonly Color[] ResultColors = new Color[]
    {
        Color.Red,
        Color.Orange,
        Color.Green,
        Color.Blue,
        Color.Purple,
        Color.Pink,
        Color.Black
    };
    private GraphicSymbol? _selectedGraphicSymbol = null;
    private readonly Symbol defaultPointSymbol = new SimpleMarkerSymbol(
        SimpleMarkerSymbolStyle.Circle,
        Color.Blue,
        20d
    );
    private readonly Symbol defaultLineSymbol = new SimpleLineSymbol(
        SimpleLineSymbolStyle.Dot,
        Color.Blue,
        5d
    );
    private readonly Symbol defaultFillSymbol = new SimpleFillSymbol(
        SimpleFillSymbolStyle.ForwardDiagonal,
        Color.Blue,
        new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, Color.Blue, 2d)
    );
    private readonly GraphicsOverlay multiPointOverlay = new() { Opacity = 0.5 };
    private readonly GraphicsOverlay polylineOverlay = new() { Opacity = 0.5 };
    private readonly GraphicsOverlay polygonOverlay = new() { Opacity = 0.5 };

    #endregion Private Members

    public MainPage()
    {
        InitializeComponent();

        // For trace parameters
        Traces.ItemsSource = traces;
        StartingPoints.ItemsSource = startingPoints;

        // For trace results
        MyMapView.GraphicsOverlays ??= [];
        MyMapView.GraphicsOverlays.Add(polygonOverlay);
        MyMapView.GraphicsOverlays.Add(polylineOverlay);
        MyMapView.GraphicsOverlays.Add(multiPointOverlay);
        MyMapView.GraphicsOverlays.Add(startingPointsOverlay);

        // Start with online map
        AuthenticationManager.Current.ChallengeHandler = new ChallengeHandler(
            async (info) =>
            {
                var portalUri = new Uri(PortalURL);
                if (
                    AuthenticationManager
                        .Current
                        .FindCredential(portalUri, AuthenticationType.Token)
                    is Credential credential
                )
                {
                    return credential;
                }

                credential = await AuthenticationManager
                    .Current
                    .GenerateCredentialAsync(portalUri, Username, Password);
                AuthenticationManager.Current.AddCredential(credential);
                return credential;
            }
        );

        MyMapView.Map = new Map(new Uri(WebmapURL));
    }

    private async void OnDownloadOfflineMap(object sender, EventArgs e)
    {
        string context = "Loading offline map";
        try
        {
            IsBusy.IsVisible = true;

            var downloadDirectoryPath = Path.Combine(
                FileSystem.Current.CacheDirectory,
                "offline_map"
            );

            #region Open MMPK
            if (Directory.Exists(downloadDirectoryPath))
            {
                UpdateLoadingScreen(true, $"{context}...");
                var mmpk = await MobileMapPackage.OpenAsync(downloadDirectoryPath);
                MyMapView.Map = mmpk.Maps.ElementAtOrDefault(0);
            }
            #endregion Open MMPK

            else if (MyMapView.Map?.Item is PortalItem item && item.Type == PortalItemType.WebMap)
            {
                context = "Downloading offline map";
                UpdateLoadingScreen(true, $"{context}...");

                var task = await OfflineMapTask.CreateAsync(MyMapView.Map);

                // Prefer ahead-of-time map
                var mapAreas = await task.GetPreplannedMapAreasAsync();
                if (mapAreas.Count > 0)
                {
                    MyMapView.Map = await PreferAheadOfTimeMapAsync(
                        task,
                        mapAreas.ElementAt(0),
                        downloadDirectoryPath
                    );
                }
                else
                {
                    // Fallback to on-demand map
                    context = "Generating offline map";
                    var areaOfInterest =
                        MyMapView
                            .GetCurrentViewpoint(ViewpointType.BoundingGeometry)
                            ?.TargetGeometry
                            ?.Extent ?? item.Extent;
                    ArgumentNullException.ThrowIfNull(areaOfInterest);
                    MyMapView.Map = await FallbackOnDemandMapAsync(
                        task,
                        areaOfInterest,
                        downloadDirectoryPath
                    );
                }
            }
            Badge.IsVisible = true;

            ArgumentNullException.ThrowIfNull(MyMapView.Map);
            await SetPredefinedTracesAsync(MyMapView.Map);
        }
        catch (Exception ex)
        {
            await MainPage.ShowErrorAsync(context, ex);
        }
        finally
        {
            IsBusy.IsVisible = false;
            UpdateLoadingScreen();
        }
    }

    private async Task<Map> PreferAheadOfTimeMapAsync(
        OfflineMapTask task,
        PreplannedMapArea mapArea,
        string downloadDirectoryPath
    )
    {
        DownloadPreplannedOfflineMapJob? job = null;

        #region Job Progress
        EventHandler<EventArgs>? onProgressChanged = (s, e) =>
        {
            if (job is null)
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(
                () => LoadingIndicatorProgressBar.Progress = job.Progress / 100.0
            );
        };
        #endregion Job Progress

        try
        {
            ArgumentNullException.ThrowIfNull(task);
            ArgumentNullException.ThrowIfNull(mapArea);
            ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectoryPath);

            var parameters = await task.CreateDefaultDownloadPreplannedOfflineMapParametersAsync(
                mapArea
            );
            job = task.DownloadPreplannedOfflineMap(parameters, downloadDirectoryPath);

            #region Job cancellation

            _cancellationTokenSource = new CancellationTokenSource();
            _cancellationTokenSource.Token.Register(() => job.CancelAsync());

            #endregion  Job cancellation

            #region Job Events subscribe

            job.StatusChanged += OnJobStatusChanged;
            job.ProgressChanged += onProgressChanged;
            job.MessageAdded += OnJobMessageAdded;

            #endregion  Job Events subscribe

            var result = await job.GetResultAsync();
            return result.OfflineMap;
        }
        finally
        {
            #region Job Events unsubscribe
            if (job is not null)
            {
                job.StatusChanged -= OnJobStatusChanged;
                job.MessageAdded -= OnJobMessageAdded;
                job.ProgressChanged -= onProgressChanged;
            }
            #endregion Job Events unsubscribe
        }
    }

    private async Task<Map> FallbackOnDemandMapAsync(
        OfflineMapTask task,
        Geometry areaOfInterest,
        string downloadDirectoryPath
    )
    {
        GenerateOfflineMapJob? job = null;

        #region Job Progress

        EventHandler<EventArgs>? onProgressChanged = (s, e) =>
        {
            if (job is null)
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(
                () => LoadingIndicatorProgressBar.Progress = job.Progress / 100.0
            );
        };

        #endregion Job Progress

        try
        {
            ArgumentNullException.ThrowIfNull(task);
            ArgumentNullException.ThrowIfNull(areaOfInterest);
            ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectoryPath);

            var parameters = await task.CreateDefaultGenerateOfflineMapParametersAsync(
                areaOfInterest
            );

            job = task.GenerateOfflineMap(parameters, downloadDirectoryPath);

            #region Job cancellation

            _cancellationTokenSource = new CancellationTokenSource();
            _cancellationTokenSource.Token.Register(() => job.CancelAsync());

            #endregion  Job cancellation

            #region Job Events subscribe

            job.StatusChanged += OnJobStatusChanged;
            job.ProgressChanged += onProgressChanged;
            job.MessageAdded += OnJobMessageAdded;

            #endregion  Job Events subscribe

            var result = await job.GetResultAsync();
            return result.OfflineMap;
        }
        finally
        {
            #region Job Events unsubscribe
            if (job is not null)
            {
                job.StatusChanged -= OnJobStatusChanged;
                job.MessageAdded -= OnJobMessageAdded;
                job.ProgressChanged -= onProgressChanged;
            }
            #endregion Job Events unsubscribe
        }
    }

    private void OnJobCancel(object sender, EventArgs e)
    {
        _cancellationTokenSource?.Cancel();
    }

    private void OnJobStatusChanged(object? sender, JobStatus e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (e == JobStatus.Started || e == JobStatus.Succeeded)
            {
                StatusBorder.StrokeThickness = 20;
                StatusBorder.Stroke = Brush.Green;
            }
            else if (e == JobStatus.Canceling || e == JobStatus.Failed)
            {
                StatusBorder.StrokeThickness = 20;
                StatusBorder.Stroke = Brush.Red;
            }
            else if (e == JobStatus.Paused)
            {
                StatusBorder.StrokeThickness = 20;
                StatusBorder.Stroke = Brush.Yellow;
            }
        });
    }

    private void OnJobMessageAdded(object? sender, JobMessage e)
    {
        MainThread.BeginInvokeOnMainThread(
            () =>
                UpdateLoadingScreen(
                    true,
                    $"{e.Timestamp:yyyy_MM_dd_HH-mm-ss} ({e.Severity}) : {e.Message}",
                    true
                )
        );
    }

    #region Tracing

    private async Task AddVisualizationColorsAsync()
    {
        // For selecting color for aggregated geometry result
        foreach (var color in ResultColors)
        {
            var source = await UtilityHelper.Current.GetSwatchAsync(color);
            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += (s, e) =>
            {
                _selectedGraphicSymbol = (s as Image)?.BindingContext as GraphicSymbol;
            };
            var image = new Image()
            {
                Source = source,
                HeightRequest = 25,
                WidthRequest = 25,
                Margin = 5,
                BindingContext = new GraphicSymbol(color, source)
            };
            image.GestureRecognizers.Add(tapGesture);
            AvailableColors.Add(image);
        }
    }

    private async Task SetPredefinedTracesAsync(Map map)
    {
        ArgumentNullException.ThrowIfNull(Traces);
        ArgumentNullException.ThrowIfNull(map);

        await ResetAsync(false);
        await AddVisualizationColorsAsync();

        if (map.LoadStatus != LoadStatus.Loaded)
        {
            await map.LoadAsync();
        }

        foreach (var utilityNetwork in map.UtilityNetworks)
        {
            if (utilityNetwork.LoadStatus != LoadStatus.Loaded)
            {
                await utilityNetwork.LoadAsync();
            }

            var namedTraces = await map.GetNamedTraceConfigurationsFromUtilityNetworkAsync(
                utilityNetwork
            );
            if (!namedTraces.Any())
            {
                namedTraces = await utilityNetwork.QueryNamedTraceConfigurationsAsync(null);
            }
            foreach (var namedTrace in namedTraces)
            {
                traces.Add(namedTrace);
            }
        }

        Traces.SelectedItem = traces.ElementAtOrDefault(0);
    }

    private async Task ResetAsync(bool addStartingPoints = true)
    {
        startingPoints.Clear();
        startingPointsOverlay.Graphics.Clear();
        multiPointOverlay.Graphics.Clear();
        polylineOverlay.Graphics.Clear();
        polygonOverlay.Graphics.Clear();

        MyMapView.DismissCallout();
        if (MyMapView.Map?.AllLayers.ToFeatureLayers() is IEnumerable<FeatureLayer> featureLayers)
        {
            foreach (var layer in featureLayers)
            {
                layer.ClearSelection();
            }
        }

        FractionAlongEdgeSlider.IsVisible = false;

        TerminalPicker.ItemsSource = Enumerable.Empty<UtilityElement>().ToList();
        TerminalPicker.IsVisible = false;

        DeleteSelectedBtn.IsVisible = false;
        if (addStartingPoints)
        {
            await AddStartingPointsAsync();
        }
    }

    private async Task AddStartingPointAsync(ArcGISFeature feature, MapPoint? mapLocation)
    {
        // For adding starting point from query/identify
        var utilityNetwork =
            MyMapView.Map?.UtilityNetworks?.ElementAtOrDefault(0) as UtilityNetwork;

        ArgumentNullException.ThrowIfNull(utilityNetwork);
        ArgumentNullException.ThrowIfNull(feature);

        if (feature.LoadStatus != LoadStatus.Loaded)
        {
            await feature.LoadAsync();
        }

        var element = utilityNetwork.CreateElement(feature);
        if (mapLocation is null)
        {
            return;
        }

        if ((mapLocation.HasZ || mapLocation.HasM) && mapLocation.RemoveZAndM() is MapPoint noZAndM)
        {
            mapLocation = noZAndM;
        }

        if (
            MyMapView.SpatialReference != null
            && MyMapView.SpatialReference.Equals(mapLocation.SpatialReference) == false
            && GeometryEngine.Project(mapLocation, MyMapView.SpatialReference)
                is MapPoint projectedMapPoint
        )
        {
            mapLocation = projectedMapPoint;
        }

        var point = MyMapView.LocationToScreen(mapLocation);
        var swatch = await UtilityHelper.Current.GetSwatchAsync(element);
        var utilityFeature = new UtilityFeature(element, feature, point, swatch);
        if (utilityFeature.Element.AssetType.TerminalConfiguration?.Terminals.Count > 1)
        {
            utilityFeature.Element.Terminal = utilityFeature
                .Element
                .AssetType
                .TerminalConfiguration
                .Terminals[0];
        }

        startingPoints.Add(utilityFeature);
        startingPointsOverlay
            .Graphics
            .Add(new Graphic(mapLocation, utilityFeature.Feature.Attributes));
    }

    private async Task AddStartingPointsAsync()
    {
        var utilityNetwork =
            MyMapView.Map?.UtilityNetworks?.ElementAtOrDefault(0) as UtilityNetwork;

        ArgumentNullException.ThrowIfNull(utilityNetwork);
        if (utilityNetwork.LoadStatus != LoadStatus.Loaded)
        {
            await utilityNetwork.LoadAsync();
        }

        if (
            utilityNetwork
                .Definition
                ?.NetworkSources
                .FirstOrDefault(ns => ns.SourceUsageType == UtilityNetworkSourceUsageType.Line)
                ?.FeatureTable
            is not ArcGISFeatureTable lineTable
        )
        {
            return;
        }

        var features = await lineTable.QueryFeaturesAsync(
            new QueryParameters()
            {
                WhereClause =
                    "assetid in ('Dstrbtn-Pp-16071', 'Dstrbtn-Pp-15862', 'Dstrbtn-Pp-15937')"
            }
        );

        var featureGeometries = new List<Geometry>();
        foreach (ArcGISFeature feature in features.Cast<ArcGISFeature>())
        {
            var mapPoint =
                feature.Geometry as MapPoint
                ?? (feature.Geometry as Polyline)
                    ?.Parts
                    ?.ElementAtOrDefault(0)
                    ?.Points
                    ?.ElementAtOrDefault(0);
            await AddStartingPointAsync(feature, mapPoint);
            if (feature.Geometry is not null)
            {
                featureGeometries.Add(feature.Geometry);
            }
        }

        StartingPoints.SelectedItem = startingPoints.LastOrDefault();
        if (featureGeometries.Count != 0)
        {
            var featureExtent = GeometryEngine.CombineExtents(featureGeometries).Buffer(10);
            _ = MyMapView.SetViewpointGeometryAsync(featureExtent);
        }
    }

    private void OnAddStartingPoint(object sender, EventArgs e)
    {
        _isAddingStartingPoint = !_isAddingStartingPoint;
    }

    private async void OnGeoViewTapped(object sender, GeoViewInputEventArgs e)
    {
        if (!_isAddingStartingPoint)
        {
            return;
        }
        var utilityNetwork =
            MyMapView.Map?.UtilityNetworks?.ElementAtOrDefault(0) as UtilityNetwork;
        ArgumentNullException.ThrowIfNull(utilityNetwork);
        if (utilityNetwork.LoadStatus != LoadStatus.Loaded)
        {
            await utilityNetwork.LoadAsync();
        }
        var deviceLayer = utilityNetwork
            .Definition
            ?.NetworkSources
            ?.FirstOrDefault(ns => ns.SourceUsageType == UtilityNetworkSourceUsageType.Device)
            ?.FeatureTable
            ?.Layer;
        ArgumentNullException.ThrowIfNull(deviceLayer);

        var results = await MyMapView.IdentifyLayerAsync(deviceLayer, e.Position, 10, false);

        var featureGeometries = new List<Geometry>();
        foreach (var geoElement in results.GeoElements)
        {
            if (geoElement is ArcGISFeature feature)
            {
                await AddStartingPointAsync(feature, feature.Geometry as MapPoint ?? e.Location);
                if (feature.Geometry is not null)
                {
                    featureGeometries.Add(feature.Geometry);
                }
            }
        }

        StartingPoints.SelectedItem = startingPoints.LastOrDefault();
        if (featureGeometries.Count != 0)
        {
            var featureExtent = GeometryEngine.CombineExtents(featureGeometries).Buffer(10);
            _ = MyMapView.SetViewpointGeometryAsync(featureExtent);
        }
    }

    private void OnStartingPointSelected(object sender, SelectedItemChangedEventArgs e)
    {
        var featureLayers =
            MyMapView.Map?.OperationalLayers?.ToFeatureLayers() ?? Enumerable.Empty<FeatureLayer>();

        if (StartingPoints.SelectedItem is null)
        {
            MyMapView.DismissCallout();
            DeleteSelectedBtn.IsVisible = false;
        }

        foreach (var featureLayer in featureLayers)
        {
            featureLayer.ClearSelection();
        }

        if (
            StartingPoints.SelectedItem is not UtilityFeature utilityFeature
            || utilityFeature.Feature is not Feature feature
            || utilityFeature.Element is not UtilityElement element
        )
        {
            return;
        }

        if (feature.FeatureTable?.Layer is not FeatureLayer layer)
        {
            return;
        }

        layer.SelectFeature(feature);

        MyMapView.ShowCalloutForGeoElement(
            utilityFeature.Feature,
            utilityFeature.Position,
            new CalloutDefinition(
                $"{utilityFeature.Element.NetworkSource.Name}",
                $"{utilityFeature.Element.AssetGroup.Name}"
            )
        );

        if (utilityFeature.Feature.Geometry is MapPoint mapPoint)
        {
            _ = MyMapView.SetViewpointCenterAsync(mapPoint, MyMapView.Scale / 2);
        }
        else if (utilityFeature.Feature.Geometry is Geometry geometry)
        {
            _ = MyMapView.SetViewpointGeometryAsync(geometry);
        }

        DeleteSelectedBtn.IsVisible = true;

        FractionAlongEdgeSlider.IsVisible = utilityFeature.Feature.Geometry is Polyline;

        if (
            element.AssetType.TerminalConfiguration
                is not UtilityTerminalConfiguration configuration
            || configuration.Terminals.Count <= 1
        )
        {
            return;
        }

        TerminalPicker.BindingContext = element;
        TerminalPicker.ItemsSource = configuration.Terminals.ToList();
        TerminalPicker.IsVisible = true;
        TerminalPicker.SelectedIndex = 0;
    }

    private void OnTerminalChanged(object sender, EventArgs e)
    {
        if (
            TerminalPicker.BindingContext is not UtilityElement element
            || TerminalPicker.SelectedItem is not UtilityTerminal terminal
        )
        {
            return;
        }

        element.Terminal = terminal;
        TerminalPicker.ItemsSource = Enumerable.Empty<UtilityElement>().ToList();
        TerminalPicker.IsVisible = false;
    }

    private void OnFractionAlongEdgeChanged(object sender, ValueChangedEventArgs e)
    {
        if (
            StartingPoints?.SelectedItem is not UtilityFeature utilityFeature
            || !startingPoints.Contains(utilityFeature)
            || utilityFeature.Feature.FeatureTable is not ArcGISFeatureTable table
            || utilityFeature.Feature.GetAttributeValue(table.ObjectIdField) is not long objectId
            || utilityFeature.Element is not UtilityElement element
            || utilityFeature.Feature.Geometry is not Polyline polyline
        )
        {
            return;
        }

        if (element.FractionAlongEdge != e.NewValue)
        {
            element.FractionAlongEdge = e.NewValue;
            var graphic = startingPointsOverlay
                .Graphics
                .FirstOrDefault(
                    g =>
                        g.Attributes[table.ObjectIdField] is long graphicId && graphicId == objectId
                );

            if (graphic is not null)
            {
                graphic.Geometry = GeometryEngine.CreatePointAlong(
                    polyline,
                    GeometryEngine.Length(polyline) * element.FractionAlongEdge
                );
            }
        }
    }

    private void OnDeleteStartingPoint(object sender, EventArgs e)
    {
        if (
            StartingPoints?.SelectedItem is not UtilityFeature utilityFeature
            || !startingPoints.Contains(utilityFeature)
            || utilityFeature.Feature.FeatureTable is not ArcGISFeatureTable table
            || utilityFeature.Feature.GetAttributeValue(table.ObjectIdField) is not long objectId
        )
        {
            return;
        }

        startingPoints.Remove(utilityFeature);
        var graphic = startingPointsOverlay
            .Graphics
            .FirstOrDefault(
                g => g.Attributes[table.ObjectIdField] is long graphicId && graphicId == objectId
            );

        if (graphic is not null)
        {
            startingPointsOverlay.Graphics.Remove(graphic);
        }

        if (table.Layer is FeatureLayer layer)
        {
            layer.ClearSelection();
        }

        MyMapView?.DismissCallout();

        TerminalPicker.ItemsSource = Enumerable.Empty<UtilityElement>().ToList();
        TerminalPicker.IsVisible = false;

        DeleteSelectedBtn.IsVisible = false;
    }

    private void OnReset(object sender, EventArgs e)
    {
        _ = ResetAsync();
    }

    private async void OnRunTrace(object sender, EventArgs e)
    {
        var selectedTrace = Traces.SelectedItem as UtilityNamedTraceConfiguration;
        if (selectedTrace is null || startingPoints.Count == 0)
        {
            return;
        }

        try
        {
            var utilityNetwork =
                MyMapView.Map?.UtilityNetworks?.ElementAtOrDefault(0) as UtilityNetwork;
            ArgumentNullException.ThrowIfNull(utilityNetwork);
            UpdateLoadingScreen(true, "Running trace...");

            var parameters = new UtilityTraceParameters(
                selectedTrace,
                startingPoints.Select(s => s.Element)
            );

            var results = await utilityNetwork.TraceAsync(parameters);

            var resultExtents = new List<Envelope>();
            foreach (var result in results)
            {
                if (result is UtilityElementTraceResult elementResult)
                {
                    var featureLayers =
                        MyMapView.Map?.OperationalLayers?.ToFeatureLayers()
                        ?? Enumerable.Empty<FeatureLayer>();
                    foreach (var layer in featureLayers)
                    {
                        layer.ClearSelection();

                        var elements = elementResult
                            .Elements
                            .Where(
                                element => element.NetworkSource.FeatureTable == layer.FeatureTable
                            );
                        if (!elements.Any())
                        {
                            continue;
                        }

                        var features = await utilityNetwork.GetFeaturesForElementsAsync(elements);
                        layer.SelectFeatures(features);
                        resultExtents.Add(
                            GeometryEngine.CombineExtents(
                                (IEnumerable<Geometry>)
                                    features.Select(m => m.Geometry).OfType<Geometry>().ToList()
                            )
                        );
                    }
                }
                else if (result is UtilityGeometryTraceResult geometryResult)
                {
                    var graphicSymbol =
                        _selectedGraphicSymbol ?? new GraphicSymbol(Color.Blue, null);
                    if (geometryResult.Multipoint is not null)
                    {
                        var symbol = defaultPointSymbol;
                        if (symbol.Clone() is SimpleMarkerSymbol sms)
                        {
                            sms.Color = graphicSymbol.Color;
                            symbol = sms;
                        }
                        multiPointOverlay
                            .Graphics
                            .Add(new Graphic(geometryResult.Multipoint, symbol));
                        if (multiPointOverlay.Extent is Envelope extent)
                        {
                            resultExtents.Add(extent);
                        }
                    }
                    if (geometryResult.Polyline is not null)
                    {
                        var symbol = defaultLineSymbol;
                        if (symbol.Clone() is SimpleLineSymbol sls)
                        {
                            sls.Color = graphicSymbol.Color;
                            symbol = sls;
                        }
                        polylineOverlay.Graphics.Add(new Graphic(geometryResult.Polyline, symbol));
                        if (polylineOverlay.Extent is Envelope extent)
                        {
                            resultExtents.Add(extent);
                        }
                    }
                    if (geometryResult.Polygon is not null)
                    {
                        var symbol = defaultFillSymbol;
                        if (symbol.Clone() is SimpleFillSymbol sfs)
                        {
                            sfs.Color = graphicSymbol.Color;
                            if (sfs.Outline is SimpleLineSymbol ols)
                            {
                                ols.Color = graphicSymbol.Color;
                            }
                            symbol = sfs;
                        }
                        polygonOverlay.Graphics.Add(new Graphic(geometryResult.Polygon, symbol));
                        if (polylineOverlay.Extent is Envelope extent)
                        {
                            resultExtents.Add(extent);
                        }
                    }
                }
            }
            if (
                resultExtents.Count > 0
                && GeometryEngine.CombineExtents(resultExtents) is Envelope resultExtent
            )
            {
                _ = MyMapView.SetViewpointGeometryAsync(resultExtent);
            }
        }
        catch (Exception ex)
        {
            await MainPage.ShowErrorAsync("Error running a trace", ex);
        }
        finally
        {
            UpdateLoadingScreen();
        }
    }

    #endregion Tracing

    #region UI Helpers

    private static async Task ShowErrorAsync(string context, Exception error)
    {
        if (Application.Current?.MainPage is not null)
        {
            await Application
                .Current
                .MainPage
                .DisplayAlert(error.GetType().Name, $"{context}: {error.Message}", "OK");
        }
        Debug.WriteLine(error);
    }

    private void UpdateLoadingScreen(
        bool isLoading = false,
        string loadText = "",
        bool useBorder = false
    )
    {
        CancelButton.IsVisible = isLoading;
        LoadingIndicatorLabel.Text = loadText;
        LoadingIndicator.IsVisible = isLoading;
        StatusBorder.StrokeThickness = useBorder ? 20 : 0;
    }

    protected override void LayoutChildren(double x, double y, double width, double height)
    {
        base.LayoutChildren(x, y, width, height);
        MyMapView.ViewInsets = new Thickness(Panel.Width + 2, 0, 0, 0);
    }

    #endregion UI Helpers
}
