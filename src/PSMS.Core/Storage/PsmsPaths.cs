using System.Runtime.InteropServices;

namespace PSMS.Core.Storage;

public static class PsmsPaths
{
    public static string GetAppDataRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "PSMS");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "psms");
    }
}
