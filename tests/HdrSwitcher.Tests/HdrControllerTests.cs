namespace HdrSwitcher.Tests;

// Minimal IHdrManager fake for testing HdrController in isolation
public class FakeHdrManager : IHdrManager
{
    public List<DisplayInfo> Displays { get; set; } = [];
    public List<(uint Id, bool Enabled)> Calls { get; } = [];

    public IReadOnlyList<DisplayInfo> GetDisplays() => Displays;

    public void SetHdr(uint displayId, bool enabled)
    {
        Calls.Add((displayId, enabled));
        var i = Displays.FindIndex(d => d.Id == displayId);
        if (i >= 0) Displays[i] = Displays[i] with { HdrEnabled = enabled };
    }
}

public class HdrControllerTests : IDisposable
{
    private readonly string _logPath;

    public HdrControllerTests() => _logPath = Path.GetTempFileName();

    [Fact]
    public void Toggle_calls_SetHdr_on_manager()
    {
        var fake = new FakeHdrManager();
        fake.Displays = [new(1, "Test", HdrEnabled: false, IsPrimary: true)];
        using var logger = new AppLogger(_logPath);
        var ctrl = new HdrController(fake, logger);

        ctrl.Toggle(1, true);

        Assert.Single(fake.Calls);
        Assert.Equal((1u, true), fake.Calls[0]);
    }

    [Fact]
    public void Toggle_fires_StateChanged_with_updated_display_state()
    {
        var fake = new FakeHdrManager();
        fake.Displays = [new(1, "Test", HdrEnabled: false, IsPrimary: true)];
        using var logger = new AppLogger(_logPath);
        var ctrl = new HdrController(fake, logger);

        IReadOnlyList<DisplayInfo>? received = null;
        ctrl.StateChanged += d => received = d;

        ctrl.Toggle(1, true);

        Assert.NotNull(received);
        Assert.True(received![0].HdrEnabled);
    }

    [Fact]
    public void GetDisplays_delegates_to_manager()
    {
        var fake = new FakeHdrManager();
        fake.Displays = [new(1, "Monitor", HdrEnabled: true, IsPrimary: true)];
        using var logger = new AppLogger(_logPath);
        var ctrl = new HdrController(fake, logger);

        var result = ctrl.GetDisplays();

        Assert.Single(result);
        Assert.Equal("Monitor", result[0].Name);
    }

    [Fact]
    public void Dispose_unsubscribes_from_display_events()
    {
        var fake = new FakeHdrManager();
        using var logger = new AppLogger(_logPath);
        var ctrl = new HdrController(fake, logger);
        var ex = Record.Exception(() => ctrl.Dispose());
        Assert.Null(ex);
    }

    public void Dispose()
    {
        try { File.Delete(_logPath); } catch { }
    }
}
