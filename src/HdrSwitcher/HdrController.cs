using Microsoft.Win32;

namespace HdrSwitcher;

/// <summary>
/// Wraps IHdrManager and owns all HDR state coordination:
/// display event subscription, own-change suppression, and state-change notifications.
/// </summary>
public class HdrController : IDisposable
{
    private readonly IHdrManager _hdr;
    private readonly AppLogger   _logger;

    // Set before every SetHdr call so OnDisplaySettingsChanged can suppress
    // the resulting event (which is our own change, not an external one).
    // Both Toggle and the event handler run on the UI thread — plain bool is safe.
    private bool _ownedDisplayChange;

    // Last known computed state — used to skip no-op events (e.g. wake-from-sleep
    // fires DisplaySettingsChanged even when HDR state was preserved).
    private HdrState _lastState = (HdrState)(-1);

    /// <summary>
    /// Fired when HDR state changes — either from a Toggle call or an external change.
    /// Runs on the UI thread.
    /// </summary>
    public event Action<IReadOnlyList<DisplayInfo>>? StateChanged;

    public HdrController(IHdrManager hdr, AppLogger logger)
    {
        _hdr    = hdr;
        _logger = logger;
        _logger.LogHdrStatus("startup", _hdr.GetDisplays());
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => _hdr.GetDisplays();

    /// <summary>
    /// Toggles HDR on the specified display. Fires StateChanged with the new state.
    /// Safe to call from the UI thread only.
    /// </summary>
    public void Toggle(uint displayId, bool enabled)
    {
        _ownedDisplayChange = true;
        try
        {
            _hdr.SetHdr(displayId, enabled);
        }
        catch
        {
            // Clear the flag so the next external DisplaySettingsChanged is not silently swallowed
            _ownedDisplayChange = false;
            throw;
        }
        var displays = _hdr.GetDisplays();
        _lastState = ComputeHdrState(displays);
        StateChanged?.Invoke(displays);
        _logger.LogHdrStatus("tray toggle", displays);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Suppress events triggered by our own Toggle call — already handled above
        if (_ownedDisplayChange) { _ownedDisplayChange = false; return; }

        try
        {
            var displays = _hdr.GetDisplays();
            var newState = ComputeHdrState(displays);

            // Skip no-op events (e.g. wake-from-sleep when HDR state was preserved)
            if (newState == _lastState) return;
            _lastState = newState;

            StateChanged?.Invoke(displays);
            _logger.LogHdrStatus("external change", displays);
        }
        catch (Exception ex) { _logger.LogScanError("DisplaySettingsChanged", ex); }
    }

    internal static HdrState ComputeHdrState(IReadOnlyList<DisplayInfo> displays) =>
        displays.Count == 0             ? HdrState.AllOff
        : displays.All(d => d.HdrEnabled)  ? HdrState.AllOn
        : displays.All(d => !d.HdrEnabled) ? HdrState.AllOff
        : HdrState.Mixed;

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
