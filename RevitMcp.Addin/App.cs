#nullable enable
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI.Events;
using RevitMcp.Addin.Bridge;
using RevitMcp.Addin.Status;
using RevitMcp.Addin.UI;
using RevitMcp.Core.Handlers;

namespace RevitMcp.Addin;

/// <summary>
/// Revit external application entry point.
/// Wires up the MCP bridge pipeline on startup, creates the ribbon UI,
/// and tears everything down on shutdown.
/// </summary>
internal class App : IExternalApplication
{
    private PushButton? _statusButton;
    private ConnectionStatus _lastIconState = ConnectionStatus.Error;
    private DateTime _lastIconCheck = DateTime.MinValue;

    /// <summary>
    /// Static reference to the pipe server so external commands can restart it.
    /// </summary>
    public static PipeServer? PipeServer { get; private set; }

    /// <summary>
    /// Static reference to the request channel so external commands can clear pending requests.
    /// </summary>
    public static RequestChannel? Channel { get; private set; }

    /// <summary>
    /// Minimum interval between icon-state checks in the Idling handler.
    /// </summary>
    private static readonly TimeSpan IconCheckInterval = TimeSpan.FromSeconds(2);

    public Result OnStartup(UIControlledApplication app)
    {
        // 1. Build the handler registry with all known command handlers.
        var registry = new HandlerRegistry(new ICommandHandler[]
        {
            new GetElementsHandler(),
            new GetElementParametersHandler(),
            new GetProjectInfoHandler(),
            new GetElementByIdHandler(),
            new AnalyzeModelStatisticsHandler(),
            new GetCurrentViewInfoHandler(),
            new ExportRoomDataHandler(),
            new SetParameterHandler(),
            new GetSelectedElementsHandler(),
            new OpenViewHandler(),
            new CreatePlanViewHandler(),
            new CreateElevationViewHandler(),
            new CreateSectionViewHandler(),
            new CreateScheduleViewHandler(),
            new CreateSheetHandler(),
            new AddViewToSheetHandler(),
            new DeleteElementsHandler(),
            new GetParameterValueHandler(),
            new CreateWallHandler(),
            new CreateTextNoteHandler(),
            new InsertFamilyInstanceByPointHandler(),
            new CreateRailingHandler(),
            new InsertGroupHandler(),
            new CreateDimensionHandler(),
            new CreateFloorHandler(),
            new CreateDetailLineHandler(),
            new GetSheetViewsHandler(),
            new GetElementsInViewHandler(),
            new FindElementsByParameterHandler(),
            new BatchSetParametersHandler(),
            new GetViewsOnSheetHandler(),
            new MoveElementsHandler(),
            new FindElementsByNameHandler(),
            new GetSheetViewMappingHandler(),
            new GetWarningsHandler(),
            new DiagnosticHandler(),
            new IsFamilyDocumentHandler(),
            new GetFamilyInfoHandler(),
            new ListFamilyElementsHandler(),
            new GetReferencePlanesHandler(),
            new GetParametersHandler()
        });

        // 2. Create the channel that bridges the pipe thread → Revit main thread.
        var channel = new RequestChannel();
        Channel = channel;

        // 3. Create the external-event handler that drains the channel on Revit's main thread.
        var executor = new ExternalEventExecutor(channel, registry);
        var externalEvent = ExternalEvent.Create(executor);

        // 4. Start the named-pipe server on a background thread.
        PipeServer = new PipeServer(channel, externalEvent);
        PipeServer.Start();

        // 5. Create the ribbon panel and all buttons.
        CreateRibbonUI(app);

        // 6. Subscribe to Idling for periodic icon updates.
        app.Idling += OnIdling;

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication app)
    {
        app.Idling -= OnIdling;

        PipeServer?.Dispose();
        PipeServer = null;
        Channel = null;

        return Result.Succeeded;
    }

    /// <summary>
    /// Creates the "Revit MCP" ribbon panel with all push buttons.
    /// </summary>
    private void CreateRibbonUI(UIControlledApplication app)
    {
        var panel = GetOrCreatePanel(app, "Revit MCP");
        var assemblyPath = Assembly.GetExecutingAssembly().Location;

        // MCP Status button
        var statusData = new PushButtonData(
            "btnMcpStatus",
            "MCP\nStatus",
            assemblyPath,
            typeof(McpStatusCommand).FullName)
        {
            ToolTip = "View the status of the MCP server connection",
            LargeImage = ConvertToImageSource(Properties.Resources.Red_32),
            Image = ConvertToImageSource(Properties.Resources.Red_16)
        };
        _statusButton = panel.AddItem(statusData) as PushButton;

        // The three actions sit in a stack beside the status button. Stacked rows render the
        // 16px Image, never LargeImage, and the label goes on one line - a "\n" in the text
        // would be drawn literally rather than wrapping.
        var restartData = new PushButtonData(
            "btnRestartConnection",
            "Restart Connection",
            assemblyPath,
            typeof(RestartConnectionCommand).FullName)
        {
            ToolTip = "Restart the MCP pipe server. Use this if tool calls are timing out or the connection seems stuck.",
            LargeImage = ConvertToImageSource(Properties.Resources.Restart_32),
            Image = ConvertToImageSource(Properties.Resources.Restart_16)
        };

        var killData = new PushButtonData(
            "btnKillServer",
            "Kill Server",
            assemblyPath,
            typeof(KillMcpServerCommand).FullName)
        {
            ToolTip = "Terminate the MCP server process. Use this if the server is unresponsive. Claude Desktop will start a new server automatically.",
            LargeImage = ConvertToImageSource(Properties.Resources.Kill_32),
            Image = ConvertToImageSource(Properties.Resources.Kill_16)
        };

        var claudeData = new PushButtonData(
            "btnOpenClaude",
            "Open Claude",
            assemblyPath,
            typeof(OpenClaudeCommand).FullName)
        {
            ToolTip = "Launch Claude Desktop or bring it to the foreground",
            LargeImage = ConvertToImageSource(Properties.Resources.Claude_32),
            Image = ConvertToImageSource(Properties.Resources.Claude_16)
        };

        panel.AddStackedItems(restartData, killData, claudeData);
    }

    /// <summary>
    /// Name of the shared TheatreDNA ribbon tab.
    /// <para>
    /// This must stay identical to <c>RibbonRegistrar.TabName</c> in the other TheatreDNA
    /// toolkits, so every TheatreDNA add-in lands on one tab rather than each creating
    /// its own. Revit matches tabs by this string exactly, including case.
    /// </para>
    /// </summary>
    private const string TabName = "TheatreDNA";

    /// <summary>
    /// Gets an existing panel on the TheatreDNA tab, or creates it.
    /// </summary>
    /// <remarks>
    /// The previous implementation searched <see cref="UIControlledApplication.GetRibbonPanels()"/>,
    /// which only enumerates the Add-Ins tab, and then created the panel on an "ArchSmarter"
    /// tab inherited from upstream - so the lookup could never match what it created.
    /// </remarks>
    private static RibbonPanel GetOrCreatePanel(UIControlledApplication app, string panelName)
    {
        // GetRibbonPanels(tabName) throws when the tab does not exist yet, which is the
        // normal case whenever this is the first TheatreDNA add-in to load in a session.
        try
        {
            foreach (var panel in app.GetRibbonPanels(TabName))
            {
                if (panel.Name == panelName)
                    return panel;
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Tab absent. CreateRibbonPanel below creates the tab and the panel together.
        }

        return app.CreateRibbonPanel(TabName, panelName);
    }

    /// <summary>
    /// Idling event handler that checks the MCP status and swaps the ribbon icon
    /// if the state has changed. Throttled to check at most every 2 seconds.
    /// </summary>
    private void OnIdling(object? sender, IdlingEventArgs e)
    {
        var now = DateTime.Now;
        if (now - _lastIconCheck < IconCheckInterval)
            return;

        _lastIconCheck = now;

        var currentStatus = McpStatusTracker.Instance.OverallStatus;
        if (currentStatus == _lastIconState || _statusButton is null)
            return;

        _lastIconState = currentStatus;

        var (large, small) = currentStatus switch
        {
            ConnectionStatus.Connected => (Properties.Resources.Green_32, Properties.Resources.Green_16),
            ConnectionStatus.Waiting => (Properties.Resources.Yellow_32, Properties.Resources.Yellow_16),
            _ => (Properties.Resources.Red_32, Properties.Resources.Red_16)
        };

        _statusButton.LargeImage = ConvertToImageSource(large);
        _statusButton.Image = ConvertToImageSource(small);
    }

    /// <summary>
    /// Converts a byte array (embedded resource PNG) to a WPF BitmapImage.
    /// </summary>
    private static BitmapImage ConvertToImageSource(byte[] imageData)
    {
        using var stream = new MemoryStream(imageData);
        stream.Position = 0;
        var bmi = new BitmapImage();
        bmi.BeginInit();
        bmi.StreamSource = stream;
        bmi.CacheOption = BitmapCacheOption.OnLoad;
        bmi.EndInit();
        return bmi;
    }
}
