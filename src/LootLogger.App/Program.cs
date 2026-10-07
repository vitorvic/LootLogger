using Velopack;

namespace LootLogger.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: lets the installer and updater hook into the app.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
