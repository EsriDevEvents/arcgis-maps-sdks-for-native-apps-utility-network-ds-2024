using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.ArcGISServices;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Mapping.Labeling;
using Esri.ArcGISRuntime.Security;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using Esri.ArcGISRuntime.UtilityNetworks;

namespace ValidateTopology;

public partial class MainWindow : Window
{
    #region Private Members

    // A webmap with utility network.
    public const string WebmapURL =
        "https://sampleserver7.arcgisonline.com/portal/home/item.html?id=6e3fc6db3d0b4e6589eb4097eb3e5b9b";

    // Portal login credentials that can access the webmap.
    public const string PortalURL = "https://sampleserver7.arcgisonline.com/portal/sharing/rest";
    public const string Username = "editor01";
    public const string Password = "S7#i2LWmYH75";

    private readonly Viewpoint InitialViewpoint =
        new(
            new Envelope(
                -9815489.0660101417,
                5128463.4221229386,
                -9814625.2768726498,
                5128968.4911854975,
                SpatialReferences.WebMercator
            )
        );

    private readonly Viewpoint EditViewpoint =
        new(
            new Envelope(
                -9814998.7137129605,
                5128589.8043012805,
                -9814997.2282638419,
                5128590.5477995109,
                SpatialReferences.WebMercator
            )
        );

    // For editing
    private const string LineTableName = "Electric Distribution Line";
    private const string DeviceTableName = "Electric Distribution Device";
    private ArcGISFeature? _featureToEdit;

    // To impact trace
    private const string DeviceStatusField = "devicestatus";
    private readonly LabelDefinition DeviceLabelDefinition =
        new(
            new SimpleLabelExpression($"[{DeviceStatusField}]"),
            new TextSymbol
            {
                Color = Color.Blue,
                HaloColor = Color.White,
                HaloWidth = 2,
                Size = 12
            }
        )
        {
            UseCodedValues = true
        };

    // To better visualize dirty area
    private const string NominalVoltageField = "nominalvoltage";
    private readonly LabelDefinition LineLabelDefinition =
        new(
            new SimpleLabelExpression($"[{NominalVoltageField}]"),
            new TextSymbol
            {
                Color = Color.Red,
                HaloColor = Color.White,
                HaloWidth = 2,
                Size = 12
            }
        )
        {
            UseCodedValues = true
        };

    // For tracing
    private const string AssetGroupName = "Circuit Breaker";
    private const string AssetTypeName = "Three Phase";
    private const string GlobalId = "{1CAF7740-0BF4-4113-8DB2-654E18800028}";
    private const string DomainNetworkName = "ElectricDistribution";
    private const string TierName = "Medium Voltage Radial";
    private UtilityTraceParameters? _traceParameters;

    #endregion Private Members

    public MainWindow()
    {
        InitializeComponent();

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

        MyMapView.Map = new Map(new Uri(WebmapURL))
        {
            InitialViewpoint = InitialViewpoint,
            LoadSettings = new LoadSettings()
            {
                // Load in persistent session mode (workaround for server caching issue)
                // https://support.esri.com/en-us/bug/asynchronous-validate-request-for-utility-network-servi-bug-000160443
                FeatureServiceSessionType = FeatureServiceSessionType.Persistent
            }
        };

        _ = InitializeAsync();
    }

    private async Task GetNetworkStateAsync(UtilityNetwork utilityNetwork)
    {
        ArgumentNullException.ThrowIfNull(utilityNetwork);

        Status.Text = "Getting utility network state...";
        var state = await utilityNetwork.GetStateAsync();

        ValidateBtn.IsEnabled = state.HasDirtyAreas;
        TraceBtn.IsEnabled = state.IsNetworkTopologyEnabled;

        var sb = new StringBuilder(
            "Utility Network State:\n"
                + $"\tHas Dirty Areas: {state.HasDirtyAreas}\n"
                + $"\tIs Network Topology Enabled: {state.IsNetworkTopologyEnabled}\n"
        );

        if (state.HasDirtyAreas)
        {
            sb.AppendLine("Click 'Validate' before Trace.");
        }
        else
        {
            sb.AppendLine("Tap on a feature to edit.\n");
        }

        Status.Text = sb.ToString();
    }

    private async void OnValidate(object sender, RoutedEventArgs e)
    {
        try
        {
            IsBusy.Visibility = Visibility.Visible;
            Status.Text = "Validating network topology...";

            var utilityNetwork = MyMapView.Map?.UtilityNetworks.ElementAtOrDefault(0);
            ArgumentNullException.ThrowIfNull(utilityNetwork);

            // Validate using the current extent
            var extent = MyMapView
                ?.GetCurrentViewpoint(ViewpointType.BoundingGeometry)
                ?.TargetGeometry
                ?.Extent;
            ArgumentNullException.ThrowIfNull(extent);

            var job = utilityNetwork.ValidateNetworkTopology(extent);
            var result = await job.GetResultAsync();

            Status.Text = $"Has Dirty Areas: {result.HasDirtyAreas}\n";
            ValidateBtn.IsEnabled = result.HasDirtyAreas;

            #region Query Dirty Area Table

            if (result.HasErrors)
            {
                var table = utilityNetwork.DirtyAreaTable;
                ArgumentNullException.ThrowIfNull(table);

                var query = new QueryParameters() { WhereClause = "ERRORMESSAGE IS NOT NULL" };
                var features = await table.QueryFeaturesAsync(query);

                foreach (ArcGISFeature f in features.Cast<ArcGISFeature>())
                {
                    await f.LoadAsync();
                    if (f.GetAttributeValue("ERRORMESSAGE") is string errorMessage)
                    {
                        Debug.WriteLine(errorMessage);
                    }
                }
            }

            #endregion Query Dirty Area Table

            ArgumentNullException.ThrowIfNull(utilityNetwork.Definition);
            if (utilityNetwork.Definition.Capabilities.SupportsNetworkState)
            {
                await GetNetworkStateAsync(utilityNetwork);
            }
        }
        catch (Exception ex)
        {
            Status.Text = "Validate network topology failed.";
            MessageBox.Show(ex.Message, ex.GetType().Name, MessageBoxButton.OK);
        }
        finally
        {
            IsBusy.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnApplyEdits(object sender, RoutedEventArgs e)
    {
        try
        {
            var utilityNetwork = MyMapView.Map?.UtilityNetworks.ElementAtOrDefault(0);
            ArgumentNullException.ThrowIfNull(utilityNetwork);

            var fieldName = FieldName.Text?.Trim();
            if (
                string.IsNullOrWhiteSpace(fieldName)
                || _featureToEdit is null
                || !_featureToEdit.Attributes.ContainsKey(fieldName)
                || Choices.SelectedItem is not CodedValue codedValue
                || _featureToEdit.FeatureTable is null
            )
            {
                return;
            }

            var serviceGeodatabase = utilityNetwork.ServiceGeodatabase;
            ArgumentNullException.ThrowIfNull(serviceGeodatabase);

            IsBusy.IsEnabled = false;
            Status.Text = "Updating feature...";

            _featureToEdit.Attributes[fieldName] = codedValue.Code;
            await _featureToEdit.FeatureTable.UpdateFeatureAsync(_featureToEdit);

            Status.Text = "Applying edits...";
            await serviceGeodatabase.ApplyEditsAsync();

            ArgumentNullException.ThrowIfNull(utilityNetwork.Definition);
            if (utilityNetwork.Definition.Capabilities.SupportsNetworkState == true)
            {
                await GetNetworkStateAsync(utilityNetwork);
            }
        }
        catch (Exception ex)
        {
            Status.Text = "Apply edits failed.";
            MessageBox.Show(ex.Message, ex.GetType().Name, MessageBoxButton.OK);
        }
        finally
        {
            IsBusy.Visibility = Visibility.Collapsed;
            AttributePicker.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnTrace(object sender, RoutedEventArgs e)
    {
        var utilityNetwork = MyMapView.Map?.UtilityNetworks.ElementAtOrDefault(0);
        try
        {
            IsBusy.Visibility = Visibility.Visible;
            Status.Text = $"Running a downstream trace...";

            ArgumentNullException.ThrowIfNull(utilityNetwork);
            var featureLayers =
                MyMapView.Map?.OperationalLayers.OfType<FeatureLayer>()
                ?? Enumerable.Empty<FeatureLayer>();
            if (!featureLayers.Any())
            {
                return;
            }

            // Clear previous selection from the layers.
            foreach (var layer in featureLayers)
            {
                layer.ClearSelection();
            }

            ArgumentNullException.ThrowIfNull(_traceParameters);
            var traceResult = await utilityNetwork.TraceAsync(_traceParameters);
            var elementTraceResult =
                traceResult.FirstOrDefault(r => r is UtilityElementTraceResult)
                as UtilityElementTraceResult;

            ArgumentNullException.ThrowIfNull(elementTraceResult);
            var elementsFound = elementTraceResult.Elements.Count;
            Status.Text = $"Trace completed: {elementsFound} elements found";

            var resultExtents = new List<Envelope>();
            foreach (var layer in featureLayers)
            {
                var elements = elementTraceResult
                    .Elements
                    .Where(element => element.NetworkSource.FeatureTable == layer.FeatureTable);
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
            Status.Text = "Trace failed";
            MessageBox.Show(ex.Message, ex.GetType().Name);
        }
        finally
        {
            IsBusy.Visibility = Visibility.Collapsed;
        }
    }

    #region Helper Methods

    private async Task<UtilityNetwork> SwitchMapLayersAsync(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var utilityNetwork = map.UtilityNetworks.ElementAtOrDefault(0);
        ArgumentNullException.ThrowIfNull(utilityNetwork);
        await utilityNetwork.LoadAsync();

        ArgumentNullException.ThrowIfNull(utilityNetwork.DirtyAreaTable);
        map.OperationalLayers.Insert(
            0,
            new FeatureLayer(utilityNetwork.DirtyAreaTable)
            {
                Renderer = Renderer.FromJson(File.ReadAllText("dirtyAreaLayer.JSON"))
            }
        );

        var sgdb = utilityNetwork.ServiceGeodatabase;
        ArgumentNullException.ThrowIfNull(sgdb);

        // Restrict editing and tracing on a random branch
        var parameters = new ServiceVersionParameters
        {
            Name = $"ValidateNetworkTopology_{Guid.NewGuid()}",
            Access = VersionAccess.Private,
            Description = "Validate network topology with ArcGIS Runtime"
        };

        var info = await sgdb.CreateVersionAsync(parameters);
        await sgdb.SwitchVersionAsync(info.Name);

        // Visualize attribute editing using labels
        foreach (var layer in map.OperationalLayers.OfType<FeatureLayer>())
        {
            if (layer.Name == DeviceTableName)
            {
                layer.LabelDefinitions.Add(DeviceLabelDefinition);
                layer.LabelsEnabled = true;
            }
            else if (layer.Name == LineTableName)
            {
                layer.LabelDefinitions.Add(LineLabelDefinition);
                layer.LabelsEnabled = true;
            }
        }

        return utilityNetwork;
    }

    private async Task<UtilityTraceParameters> GetDefaultTraceParametersAsync(
        UtilityNetwork utilityNetwork
    )
    {
        ArgumentNullException.ThrowIfNull(utilityNetwork.Definition);
        var networkSource = utilityNetwork.Definition.GetNetworkSource(DeviceTableName);
        ArgumentNullException.ThrowIfNull(networkSource);
        var assetGroup = networkSource.GetAssetGroup(AssetGroupName);
        ArgumentNullException.ThrowIfNull(assetGroup);
        var assetType = assetGroup.GetAssetType(AssetTypeName);
        ArgumentNullException.ThrowIfNull(assetType);

        var globalId = Guid.Parse(GlobalId);
        var startingLocation = utilityNetwork.CreateElement(assetType, globalId);
        startingLocation.Terminal = startingLocation
            .AssetType
            .TerminalConfiguration
            ?.Terminals
            .FirstOrDefault(terminal => terminal.Name == "Load");

        // Display starting location as graphic
        var features = await utilityNetwork.GetFeaturesForElementsAsync(new[] { startingLocation });
        var feature = features.ElementAtOrDefault(0);
        ArgumentNullException.ThrowIfNull(feature);

        await feature.LoadAsync();
        var graphic = new Graphic(feature.Geometry)
        {
            Symbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Cross, Color.Green, 25d)
        };
        var overlay = new GraphicsOverlay();
        overlay.Graphics.Add(graphic);

        ArgumentNullException.ThrowIfNull(MyMapView.GraphicsOverlays);
        MyMapView.GraphicsOverlays.Add(overlay);

        // Trace with a configuration that stops traversability on an open device
        var domainNetwork = utilityNetwork.Definition.GetDomainNetwork(DomainNetworkName);
        ArgumentNullException.ThrowIfNull(domainNetwork);
        var sourceTier = domainNetwork.GetTier(TierName);
        ArgumentNullException.ThrowIfNull(sourceTier);

        return new UtilityTraceParameters(UtilityTraceType.Downstream, new[] { startingLocation })
        {
            TraceConfiguration = sourceTier.GetDefaultTraceConfiguration()
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            IsBusy.Visibility = Visibility.Visible;
            Status.Text = "Loading a webmap...";

            var map = MyMapView.Map;
            ArgumentNullException.ThrowIfNull(map);
            await map.LoadAsync();

            // Load and switch utility network version
            Status.Text = "Loading the utility network...";
            var utilityNetwork = await SwitchMapLayersAsync(map);

            // Trace with a subnetwork controller as default starting location
            _traceParameters = await GetDefaultTraceParametersAsync(utilityNetwork);
            ArgumentNullException.ThrowIfNull(utilityNetwork.Definition);

            // Enable buttons with UtilityNetworkCapabilities
            ValidateBtn.IsEnabled = utilityNetwork
                .Definition
                .Capabilities
                .SupportsValidateNetworkTopology;
            TraceBtn.IsEnabled = utilityNetwork.Definition.Capabilities.SupportsTrace;

            if (utilityNetwork.Definition.Capabilities.SupportsNetworkState)
            {
                await GetNetworkStateAsync(utilityNetwork);
            }
        }
        catch (Exception ex)
        {
            Status.Text = "Initialization failed.";
            MessageBox.Show(ex.Message, ex.GetType().Name);
        }
        finally
        {
            IsBusy.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnGeoViewTapped(object sender, GeoViewInputEventArgs e)
    {
        try
        {
            // AttributePicker is visible during edit
            if (AttributePicker.Visibility == Visibility.Visible)
            {
                return;
            }

            IsBusy.Visibility = Visibility.Visible;
            Status.Text = "Identifying a feature to edit...";

            var layerResults = await MyMapView.IdentifyLayersAsync(e.Position, 5, false);
            if (
                layerResults
                    .FirstOrDefault(
                        l =>
                            (
                                l.LayerContent.Name == DeviceTableName
                                || l.LayerContent.Name == LineTableName
                            )
                    )
                    ?.GeoElements
                    .ElementAtOrDefault(0)
                is not ArcGISFeature feature
            )
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(feature.FeatureTable);
            var updateFieldName =
                feature.FeatureTable.TableName == DeviceTableName
                    ? DeviceStatusField
                    : NominalVoltageField;
            var field = feature.FeatureTable.GetField(updateFieldName);
            var codedValues = (field?.Domain as CodedValueDomain)?.CodedValues;
            if (field is null || codedValues is null || codedValues.Count == 0)
            {
                return;
            }

            if (feature.LoadStatus != LoadStatus.Loaded)
            {
                await feature.LoadAsync();
            }

            _featureToEdit = feature;

            ArgumentNullException.ThrowIfNull(MyMapView?.Map);
            MyMapView
                .Map
                .OperationalLayers
                .OfType<FeatureLayer>()
                .ToList()
                .ForEach(layer => layer.ClearSelection());

            if (_featureToEdit.FeatureTable.Layer is FeatureLayer featureLayer)
            {
                featureLayer.SelectFeature(_featureToEdit);
            }

            Choices.ItemsSource = codedValues;
            var actualValue = Convert.ToInt32(_featureToEdit.Attributes[field.Name]);
            Choices.SelectedItem = codedValues.Single(
                c => Convert.ToInt32(c.Code).Equals(actualValue)
            );

            FieldName.Text = field.Name;
            Status.Text = $"Select a new '{field.Alias ?? field.Name}'";
            AttributePicker.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Status.Text = "Identifying feature to edit failed.";
            MessageBox.Show(ex.Message, ex.GetType().Name);
        }
        finally
        {
            IsBusy.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOptionsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (MyMapView is not null)
        {
            _ = MyMapView.SetViewpointAsync(EditViewpoint);
        }
    }

    #endregion Helper Methods
}
