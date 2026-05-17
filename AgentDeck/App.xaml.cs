using AgentDeck.Core.Services;
using Microsoft.Extensions.Logging;

namespace AgentDeck;

public partial class App : Application
{
    private readonly AppInitializer _initializer;
    private readonly ILogger<App> _logger;

    public App(AppInitializer initializer, ILogger<App> logger)
    {
        InitializeComponent();
        _initializer = initializer;
        _logger = logger;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "AgentDeck" };

    protected override async void OnStart()
    {
        try
        {
            base.OnStart();
            await _initializer.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "App startup initialization failed.");
        }
    }
}
