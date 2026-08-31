using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Photino.Blazor;
using PSMS.App.Services;
using PSMS.Core.Abstractions;
using PSMS.Core.Storage;
using PSMS.Providers.Access;
using PSMS.Providers.Sqlite;
using PSMS.Providers.SqlServer;

namespace PSMS.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Help GTK/KDE associate the process with our icon theme name on Linux.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Environment.SetEnvironmentVariable("GDK_BACKEND",
                Environment.GetEnvironmentVariable("GDK_BACKEND") ?? "x11");
        }

        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

        appBuilder.Services.AddLogging();
        appBuilder.Services.AddMudServices();
        appBuilder.Services.AddSingleton<IConnectionStore, FileConnectionStore>();
        appBuilder.Services.AddSingleton<QueryHistoryStore>();
        appBuilder.Services.AddSingleton<SnippetStore>();
        appBuilder.Services.AddSingleton<UiLayoutStore>();
        appBuilder.Services.AddSingleton<SessionStore>();
        appBuilder.Services.AddSingleton<IDbProvider, SqlServerProvider>();
        appBuilder.Services.AddSingleton<IDbProvider, SqliteProvider>();
        appBuilder.Services.AddSingleton<IDbProvider, AccessProvider>();
        appBuilder.Services.AddSingleton<ISqlServerAdminService, SqlServerAdminService>();
        appBuilder.Services.AddSingleton<IExtendedEventsService, SqlServerExtendedEventsService>();
        appBuilder.Services.AddSingleton<IDbProviderFactory, DbProviderFactory>();
        appBuilder.Services.AddSingleton<ActiveSessionService>();
        appBuilder.Services.AddSingleton<QueryRunner>();
        appBuilder.Services.AddSingleton<EditorRegistry>();
        appBuilder.Services.AddSingleton<SchemaIntelliSenseService>();
        appBuilder.Services.AddSingleton<ContextMenuService>();
        appBuilder.Services.AddSingleton<IntelliSenseJsBridge>();
        appBuilder.Services.AddSingleton<PhotinoHost>();
        appBuilder.Services.AddSingleton<IFileDialogService, PhotinoFileDialogService>();

        appBuilder.RootComponents.Add<App>("app");

        var app = appBuilder.Build();

        var host = app.Services.GetRequiredService<PhotinoHost>();
        host.Window = app.MainBlazorWindow.Window;

        var window = app.MainBlazorWindow.Window
            .SetLogVerbosity(0)
            .SetTitle("PSMS — SQL Management Studio")
            .SetUseOsDefaultSize(false)
            .SetSize(1440, 920)
            .SetMinSize(960, 640);

        ApplyWindowIcon(window);

        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
        {
            try
            {
                app.MainBlazorWindow.Window.ShowMessage(
                    "Fatal exception",
                    error.ExceptionObject?.ToString() ?? "Unknown fatal exception.");
            }
            catch
            {
                // ignored
            }
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Console.WriteLine("PSMS: ensure WebKitGTK 4.1 is installed (see README).");
        }

        app.Run();
    }

    private static void ApplyWindowIcon(Photino.NET.PhotinoWindow window)
    {
        // Photino/GTK on Linux reliably picks up PNG/ICO next to the executable.
        foreach (var name in new[] { "appicon.png", "favicon.ico", "icon.png" })
        {
            foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "wwwroot") })
            {
                var path = Path.GetFullPath(Path.Combine(dir, name));
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    window.SetIconFile(path);
                    Console.WriteLine($"PSMS: window icon → {path}");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PSMS: SetIconFile failed for {path}: {ex.Message}");
                }
            }
        }

        // Last resort: extract embedded icon to a temp file.
        try
        {
            var asm = typeof(Program).Assembly;
            foreach (var resource in new[] { "PSMS.App.wwwroot.appicon.png", "PSMS.App.wwwroot.favicon.ico" })
            {
                using var stream = asm.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }

                var ext = resource.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? ".ico" : ".png";
                var temp = Path.Combine(Path.GetTempPath(), "psms-appicon" + ext);
                using (var fs = File.Create(temp))
                {
                    stream.CopyTo(fs);
                }

                window.SetIconFile(temp);
                Console.WriteLine($"PSMS: window icon → embedded {resource}");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PSMS: no window icon applied ({ex.Message})");
        }
    }
}
