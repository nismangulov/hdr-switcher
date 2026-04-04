using Microsoft.Win32;
using HdrSwitcher;

namespace HdrSwitcher.Tests;

public class AutostartManagerTests : IDisposable
{
    private const string TestKeyPath = @"Software\HdrSwitcherTest\Run";
    private readonly AutostartManager _manager;

    public AutostartManagerTests()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\HdrSwitcherTest", throwOnMissingSubKey: false);
        _manager = new AutostartManager(TestKeyPath);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\HdrSwitcherTest", throwOnMissingSubKey: false);
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenValueAbsent()
    {
        Assert.False(_manager.IsEnabled());
    }

    [Fact]
    public void SetEnabled_True_WritesRegistryValue()
    {
        _manager.SetEnabled(true);
        Assert.True(_manager.IsEnabled());
    }

    [Fact]
    public void SetEnabled_False_RemovesRegistryValue()
    {
        _manager.SetEnabled(true);
        _manager.SetEnabled(false);
        Assert.False(_manager.IsEnabled());
    }
}
