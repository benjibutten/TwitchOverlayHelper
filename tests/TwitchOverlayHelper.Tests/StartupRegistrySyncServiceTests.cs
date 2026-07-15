using TwitchOverlayHelper.Services;

namespace TwitchOverlayHelper.Tests;

public sealed class StartupRegistrySyncServiceTests
{
    [Fact]
    public void SyncWhenEnabledSetsQuotedRunEntryWithMinimizedArgument()
    {
        var store = new RecordingStore();
        var service = new StartupRegistrySyncService(new RecordingStoreFactory(store));
        const string executablePath = @"C:\Program Files\Twitch Overlay Helper\TwitchOverlayHelper.exe";

        bool result = service.Sync(startWithWindows: true, executablePath);

        Assert.True(result);
        Assert.Equal(StartupRegistrySyncService.AppRegistryName, store.SetName);
        Assert.Equal($"\"{executablePath}\" --minimized", store.SetValueText);
        Assert.False(store.DeleteCalled);
    }

    [Fact]
    public void SyncWhenDisabledDeletesRunEntryWithoutThrowingForMissingValue()
    {
        var store = new RecordingStore();
        var service = new StartupRegistrySyncService(new RecordingStoreFactory(store));

        bool result = service.Sync(startWithWindows: false, @"C:\Apps\TwitchOverlayHelper.exe");

        Assert.True(result);
        Assert.True(store.DeleteCalled);
        Assert.Equal(StartupRegistrySyncService.AppRegistryName, store.DeletedName);
        Assert.False(store.ThrowOnMissingValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SyncWithoutExecutablePathDoesNotOpenRegistry(string? executablePath)
    {
        var factory = new RecordingStoreFactory(new RecordingStore());
        var service = new StartupRegistrySyncService(factory);

        Assert.False(service.Sync(startWithWindows: true, executablePath));
        Assert.False(factory.WasOpened);
    }

    private sealed class RecordingStoreFactory(RecordingStore store) : IStartupRegistryStoreFactory
    {
        public bool WasOpened { get; private set; }

        public IStartupRegistryStore OpenCurrentUserRunKey()
        {
            WasOpened = true;
            return store;
        }
    }

    private sealed class RecordingStore : IStartupRegistryStore
    {
        public string? SetName { get; private set; }
        public string? SetValueText { get; private set; }
        public string? DeletedName { get; private set; }
        public bool DeleteCalled { get; private set; }
        public bool ThrowOnMissingValue { get; private set; }

        public void SetValue(string name, string value)
        {
            SetName = name;
            SetValueText = value;
        }

        public void DeleteValue(string name, bool throwOnMissingValue)
        {
            DeletedName = name;
            DeleteCalled = true;
            ThrowOnMissingValue = throwOnMissingValue;
        }

        public void Dispose()
        {
        }
    }
}
