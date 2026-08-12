namespace Thaw;

internal static class Program
{
    private const string MutexName = "Thaw.InstantUnfreezer.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(
                "Thaw — Instant Unfreezer\n" +
                "Usage: Thaw.exe [options]\n" +
                "  (no args)   run in the system tray\n" +
                "  --emit-icon <path>  write the app .ico and exit\n" +
                "  --emit-png <dir>    write PNG previews of the icon and exit\n" +
                "  --elevated  skip the single-instance check (used for admin restart)\n" +
                "  --smoke     start, verify core subsystems, exit after a few seconds\n" +
                "  --test-hook [altf4|panic]  drive the hook callback with a synthetic key event\n");
            return 0;
        }

        if (TryGetArg(args, "--emit-icon", out string? iconPath))
        {
            Icons.EmitIconFile(iconPath!);
            Console.WriteLine("Icon written to " + iconPath);
            return 0;
        }

        if (TryGetArg(args, "--emit-png", out string? pngDir))
        {
            Icons.EmitPngPreview(pngDir!);
            Console.WriteLine("PNG previews written to " + pngDir);
            return 0;
        }

        bool elevatedRestart = args.Contains("--elevated");

        if (!elevatedRestart)
        {
            using var mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
                return 0; // another Thaw instance is already running
            return RunApp(args);
        }

        return RunApp(args);
    }

    private static int RunApp(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--smoke"))
        {
            using var ctx = new AppContext();
            Thread.Sleep(5000);
            Log.Info("SMOKE OK — hook+watchdog+tray initialized");
            return 0;
        }

        if (args.Contains("--test-hook"))
        {
            string which = args.Length > Array.IndexOf(args, "--test-hook") + 1
                ? args[Array.IndexOf(args, "--test-hook") + 1] ?? "altf4"
                : "altf4";
            using var ctx = new AppContext();
            Thread.Sleep(2500);
            ctx.RunHookTest(which);
            Thread.Sleep(13000); // long enough for the full unfreeze incl. explorer restart
            Log.Info("HOOK TEST DONE (" + which + ")");
            return 0;
        }

        using var app = new AppContext();
        Application.Run(app);
        return 0;
    }

    private static bool TryGetArg(string[] args, string name, out string? value)
    {
        int i = Array.IndexOf(args, name);
        if (i >= 0 && i + 1 < args.Length)
        {
            value = args[i + 1];
            return true;
        }
        value = null;
        return false;
    }
}
