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
        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

        appBuilder.Services.AddLogging();
        appBuilder.Services.AddMudServices();
        appBuilder.Services.AddSingleton<IConnectionStore, FileConnectionStore>();
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

        app.MainBlazorWindow.Window
            .SetTitle("PSMS — SQL Management Studio")
            .SetUseOsDefaultSize(false)
            .SetSize(1440, 920)
            .SetMinSize(960, 640);

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
}
