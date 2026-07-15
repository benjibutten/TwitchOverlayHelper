using Microsoft.Win32;

namespace TwitchOverlayHelper.Services;

internal interface IStartupRegistryStore : IDisposable
{
    void SetValue(string name, string value);

    void DeleteValue(string name, bool throwOnMissingValue);
}

internal interface IStartupRegistryStoreFactory
{
    IStartupRegistryStore? OpenCurrentUserRunKey();
}

internal sealed class StartupRegistrySyncService
{
    internal const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    internal const string AppRegistryName = "TwitchOverlayHelper";

    private readonly IStartupRegistryStoreFactory _storeFactory;

    internal StartupRegistrySyncService(IStartupRegistryStoreFactory? storeFactory = null)
    {
        _storeFactory = storeFactory ?? new RegistryStartupStoreFactory();
    }

    internal static string BuildStartupCommand(string exePath) => $"\"{exePath}\" --minimized";

    internal bool Sync(bool startWithWindows, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        using IStartupRegistryStore? store = _storeFactory.OpenCurrentUserRunKey();
        if (store is null)
            return false;

        if (startWithWindows)
            store.SetValue(AppRegistryName, BuildStartupCommand(exePath));
        else
            store.DeleteValue(AppRegistryName, throwOnMissingValue: false);

        return true;
    }

    private sealed class RegistryStartupStoreFactory : IStartupRegistryStoreFactory
    {
        public IStartupRegistryStore? OpenCurrentUserRunKey()
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
            return key is null ? null : new RegistryStartupStore(key);
        }
    }

    private sealed class RegistryStartupStore(RegistryKey key) : IStartupRegistryStore
    {
        public void SetValue(string name, string value) => key.SetValue(name, value, RegistryValueKind.String);

        public void DeleteValue(string name, bool throwOnMissingValue) => key.DeleteValue(name, throwOnMissingValue);

        public void Dispose() => key.Dispose();
    }
}
