namespace AssetBeeDrone.Tray;

internal static class Program
{
    private const string MutexName = "Global\\AssetBee.Drone.Tray";

    [STAThread]
    private static void Main()
    {
        using Mutex mutex = new(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
