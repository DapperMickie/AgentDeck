using AgentDeck.Core.Services;

namespace AgentDeck;

public partial class App : Application
{
    private readonly AppInitializer _initializer;

    public App(AppInitializer initializer)
    {
        InitializeComponent();
        _initializer = initializer;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "AgentDeck" };

    protected override async void OnStart()
    {
        base.OnStart();
        await _initializer.InitializeAsync();
    }
}
