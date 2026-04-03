# HDR Switcher Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A Windows 11 system tray app that toggles HDR per-monitor via Win32 APIs, with a DPI-aware vector icon and autostart support.

**Architecture:** C# .NET 8 WinForms `ApplicationContext` (no window), `NotifyIcon` for tray, Win32 `QueryDisplayConfig`/`DisplayConfigSetDeviceInfo` P/Invoke for HDR, GDI+ `GraphicsPath` for icons rendered at runtime DPI size.

**Tech Stack:** C# 12, .NET 8, WinForms, xUnit, Win32 P/Invoke (no NuGet dependencies)

---

## Project Layout

```
hdr-switcher/
  src/
    HdrSwitcher/
      HdrSwitcher.csproj
      app.manifest
      Program.cs
      TrayApplicationContext.cs
      HdrManager.cs
      IHdrManager.cs
      IconRenderer.cs
      AutostartManager.cs
  tests/
    HdrSwitcher.Tests/
      HdrSwitcher.Tests.csproj
      AutostartManagerTests.cs
      IconRendererTests.cs
  HdrSwitcher.sln
```

---

### Task 1: Scaffold the solution

**Files:**
- Create: `HdrSwitcher.sln`
- Create: `src/HdrSwitcher/HdrSwitcher.csproj`
- Create: `src/HdrSwitcher/app.manifest`
- Create: `tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj`

**Step 1: Create the solution and projects**

```bash
cd C:/Users/nail/github/hdr-switcher

dotnet new sln -n HdrSwitcher

dotnet new winforms -n HdrSwitcher -o src/HdrSwitcher --framework net8.0-windows
dotnet sln add src/HdrSwitcher/HdrSwitcher.csproj

dotnet new xunit -n HdrSwitcher.Tests -o tests/HdrSwitcher.Tests --framework net8.0-windows
dotnet sln add tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj

dotnet add tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj reference src/HdrSwitcher/HdrSwitcher.csproj
```

**Step 2: Replace `src/HdrSwitcher/HdrSwitcher.csproj` with:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <AssemblyName>HdrSwitcher</AssemblyName>
    <RootNamespace>HdrSwitcher</RootNamespace>
  </PropertyGroup>
</Project>
```

**Step 3: Create `src/HdrSwitcher/app.manifest`:**

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"
          xmlns:asmv3="urn:schemas-microsoft-com:asm.v3">
  <asmv3:application>
    <asmv3:windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/PM</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </asmv3:windowsSettings>
  </asmv3:application>
</assembly>
```

**Step 4: Delete the generated boilerplate files (not needed for a tray-only app):**

```bash
rm src/HdrSwitcher/Form1.cs
rm src/HdrSwitcher/Form1.Designer.cs
```

**Step 5: Build to verify setup**

```bash
dotnet build HdrSwitcher.sln
```

Expected: `Build succeeded. 0 Error(s)`

**Step 6: Commit**

```bash
git add .
git commit -m "chore: scaffold solution and projects"
```

---

### Task 2: AutostartManager

**Files:**
- Create: `src/HdrSwitcher/AutostartManager.cs`
- Create: `tests/HdrSwitcher.Tests/AutostartManagerTests.cs`

**Step 1: Write the failing tests**

Create `tests/HdrSwitcher.Tests/AutostartManagerTests.cs`:

```csharp
using Microsoft.Win32;
using HdrSwitcher;

namespace HdrSwitcher.Tests;

public class AutostartManagerTests : IDisposable
{
    // Use a test-only registry key so we don't pollute the real autostart
    private const string TestKeyPath = @"Software\HdrSwitcherTest\Run";
    private const string ValueName = "HdrSwitcher";

    private readonly AutostartManager _manager;

    public AutostartManagerTests()
    {
        // Clean up before each test
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
```

**Step 2: Run to verify fail**

```bash
dotnet test tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj
```

Expected: FAIL — `AutostartManager` type not found.

**Step 3: Implement `src/HdrSwitcher/AutostartManager.cs`:**

```csharp
using Microsoft.Win32;

namespace HdrSwitcher;

public class AutostartManager
{
    private readonly string _registryKeyPath;
    private const string ValueName = "HdrSwitcher";

    // Production constructor uses the real Windows autostart key
    public AutostartManager()
        : this(@"Software\Microsoft\Windows\CurrentVersion\Run") { }

    // Testable constructor accepts any key path
    public AutostartManager(string registryKeyPath)
    {
        _registryKeyPath = registryKeyPath;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registryKeyPath);
        return key?.GetValue(ValueName) is not null;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_registryKeyPath);
        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine exe path");
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
```

**Step 4: Run tests to verify pass**

```bash
dotnet test tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj
```

Expected: `Passed! - 3 test(s)`

**Step 5: Commit**

```bash
git add src/HdrSwitcher/AutostartManager.cs tests/HdrSwitcher.Tests/AutostartManagerTests.cs
git commit -m "feat: AutostartManager with registry read/write"
```

---

### Task 3: IconRenderer

**Files:**
- Create: `src/HdrSwitcher/IconRenderer.cs`
- Create: `tests/HdrSwitcher.Tests/IconRendererTests.cs`

**Step 1: Write the failing tests**

Create `tests/HdrSwitcher.Tests/IconRendererTests.cs`:

```csharp
using System.Drawing;
using HdrSwitcher;

namespace HdrSwitcher.Tests;

public class IconRendererTests
{
    [Theory]
    [InlineData(HdrState.AllOn, 16)]
    [InlineData(HdrState.AllOff, 16)]
    [InlineData(HdrState.Mixed, 16)]
    [InlineData(HdrState.AllOn, 32)]
    [InlineData(HdrState.AllOff, 32)]
    [InlineData(HdrState.Mixed, 32)]
    public void Render_ReturnsNonNullIcon(HdrState state, int size)
    {
        var icon = IconRenderer.Render(state, size);
        Assert.NotNull(icon);
    }

    [Theory]
    [InlineData(HdrState.AllOn, 16)]
    [InlineData(HdrState.AllOn, 32)]
    [InlineData(HdrState.AllOn, 48)]
    public void Render_ProducesCorrectSize(HdrState state, int size)
    {
        var icon = IconRenderer.Render(state, size);
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
    }

    [Fact]
    public void AllOn_And_AllOff_ProduceDifferentIcons()
    {
        var on = IconRenderer.Render(HdrState.AllOn, 32);
        var off = IconRenderer.Render(HdrState.AllOff, 32);
        // Convert to bitmaps and check they differ
        var bmOn = on.ToBitmap();
        var bmOff = off.ToBitmap();
        bool anyDifference = false;
        for (int x = 0; x < 32 && !anyDifference; x++)
            for (int y = 0; y < 32 && !anyDifference; y++)
                if (bmOn.GetPixel(x, y) != bmOff.GetPixel(x, y))
                    anyDifference = true;
        Assert.True(anyDifference);
    }
}
```

**Step 2: Run to verify fail**

```bash
dotnet test tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj
```

Expected: FAIL — `HdrState` and `IconRenderer` not found.

**Step 3: Implement `src/HdrSwitcher/IconRenderer.cs`:**

```csharp
using System.Drawing;
using System.Drawing.Drawing2D;

namespace HdrSwitcher;

public enum HdrState { AllOn, AllOff, Mixed }

public static class IconRenderer
{
    public static Icon Render(HdrState state, int sizePx)
    {
        using var bmp = new Bitmap(sizePx, sizePx);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float cx = sizePx / 2f;
        float cy = sizePx / 2f;
        float r = sizePx * 0.30f;   // sun body radius
        float rayLen = sizePx * 0.18f;
        float rayW = sizePx * 0.07f;
        int numRays = 8;

        Color bodyColor = state switch
        {
            HdrState.AllOn => Color.FromArgb(255, 255, 220, 0),   // bright yellow
            HdrState.AllOff => Color.FromArgb(180, 130, 130, 130), // grey
            HdrState.Mixed => Color.FromArgb(220, 200, 180, 60),   // dim yellow
        };

        using var bodyBrush = new SolidBrush(bodyColor);
        using var rayBrush = new SolidBrush(bodyColor);
        using var slashPen = new Pen(Color.FromArgb(220, 200, 50, 50), sizePx * 0.10f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Draw rays
        for (int i = 0; i < numRays; i++)
        {
            double angle = Math.PI * 2 * i / numRays;
            float rx = (float)Math.Cos(angle);
            float ry = (float)Math.Sin(angle);
            float innerR = r + sizePx * 0.04f;
            float outerR = r + sizePx * 0.04f + rayLen;

            using var path = new GraphicsPath();
            float perpX = -ry * rayW / 2;
            float perpY = rx * rayW / 2;
            path.AddPolygon(new[]
            {
                new PointF(cx + rx * innerR + perpX, cy + ry * innerR + perpY),
                new PointF(cx + rx * outerR,         cy + ry * outerR),
                new PointF(cx + rx * innerR - perpX, cy + ry * innerR - perpY),
            });
            g.FillPath(rayBrush, path);
        }

        // Draw sun body
        g.FillEllipse(bodyBrush, cx - r, cy - r, r * 2, r * 2);

        // Mixed state: draw a half-mask over the body
        if (state == HdrState.Mixed)
        {
            using var maskBrush = new SolidBrush(Color.FromArgb(140, 50, 50, 50));
            g.FillPie(maskBrush, cx - r, cy - r, r * 2, r * 2, 90, 180);
        }

        // AllOff: draw diagonal slash
        if (state == HdrState.AllOff)
        {
            float slashPad = sizePx * 0.10f;
            g.DrawLine(slashPen,
                cx + r * 0.6f + slashPad * 0.3f, cy - r * 0.6f - slashPad * 0.3f,
                cx - r * 0.6f - slashPad * 0.3f, cy + r * 0.6f + slashPad * 0.3f);
        }

        // Convert bitmap → Icon
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
```

**Step 4: Run tests to verify pass**

```bash
dotnet test tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj
```

Expected: `Passed! - 8 test(s)` (3 prior + 5 new)

**Step 5: Commit**

```bash
git add src/HdrSwitcher/IconRenderer.cs tests/HdrSwitcher.Tests/IconRendererTests.cs
git commit -m "feat: IconRenderer with GDI+ vector sun icon, 3 states"
```

---

### Task 4: HdrManager (Win32 P/Invoke)

> No unit tests here — these call real Win32 display APIs that require a physical display. Manual verification at the end.

**Files:**
- Create: `src/HdrSwitcher/IHdrManager.cs`
- Create: `src/HdrSwitcher/HdrManager.cs`

**Step 1: Create `src/HdrSwitcher/IHdrManager.cs`:**

```csharp
namespace HdrSwitcher;

public record DisplayInfo(uint Id, string Name, bool HdrEnabled, bool IsPrimary);

public interface IHdrManager
{
    IReadOnlyList<DisplayInfo> GetDisplays();
    void SetHdr(uint displayId, bool enabled);
}
```

**Step 2: Create `src/HdrSwitcher/HdrManager.cs`:**

```csharp
using System.Runtime.InteropServices;

namespace HdrSwitcher;

public class HdrManager : IHdrManager
{
    // ── Win32 constants ──────────────────────────────────────────────────────
    private const int QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 14;
    private const int DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 15;
    private const int ERROR_SUCCESS = 0;

    // ── Win32 structs ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public int scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public int infoType;
        public uint id;
        public LUID adapterId;
        // Union is 64 bytes; we skip it
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] modeInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS { public uint value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value; // bit 0 = advancedColorSupported, bit 1 = advancedColorEnabled, bit 2 = wideColorEnforced, bit 3 = advancedColorForceDisabled
        public int colorEncoding;
        public int bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value; // bit 0 = enableAdvancedColor
    }

    // ── Win32 imports ────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(int flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(int flags, ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE requestPacket);

    // ── Public API ───────────────────────────────────────────────────────────

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var paths = QueryPaths();
        var result = new List<DisplayInfo>();

        for (int i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            var colorInfo = GetAdvancedColorInfo(path.targetInfo.adapterId, path.targetInfo.id);

            bool hdrSupported = (colorInfo.value & 1) != 0;
            bool hdrEnabled = (colorInfo.value & 2) != 0;

            if (!hdrSupported) continue; // skip displays that can't do HDR

            bool isPrimary = (path.sourceInfo.statusFlags & 1) != 0; // DISPLAYCONFIG_SOURCE_IN_USE on primary

            result.Add(new DisplayInfo(
                Id: path.targetInfo.id,
                Name: $"Display {i + 1}",
                HdrEnabled: hdrEnabled,
                IsPrimary: isPrimary
            ));
        }

        return result;
    }

    public void SetHdr(uint displayId, bool enabled)
    {
        var paths = QueryPaths();
        var path = paths.FirstOrDefault(p => p.targetInfo.id == displayId);
        if (path.targetInfo.id != displayId)
            throw new InvalidOperationException($"Display {displayId} not found");

        var request = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id
            },
            value = enabled ? 1u : 0u
        };

        int result = DisplayConfigSetDeviceInfo(ref request);
        if (result != ERROR_SUCCESS)
            throw new InvalidOperationException($"SetDisplayConfig failed: {result}");
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private DISPLAYCONFIG_PATH_INFO[] QueryPaths()
    {
        int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes);
        if (err != ERROR_SUCCESS) throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {err}");

        var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
        var modes = new DISPLAYCONFIG_MODE_INFO[numModes];

        err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
        if (err != ERROR_SUCCESS) throw new InvalidOperationException($"QueryDisplayConfig failed: {err}");

        return paths[..((int)numPaths)];
    }

    private DISPLAYCONFIG_ADVANCED_COLOR_INFO GetAdvancedColorInfo(LUID adapterId, uint targetId)
    {
        var request = new DISPLAYCONFIG_ADVANCED_COLOR_INFO
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADVANCED_COLOR_INFO>(),
                adapterId = adapterId,
                id = targetId
            }
        };
        DisplayConfigGetDeviceInfo(ref request);
        return request;
    }
}
```

**Step 3: Build to verify it compiles**

```bash
dotnet build src/HdrSwitcher/HdrSwitcher.csproj
```

Expected: `Build succeeded. 0 Error(s)`

**Step 4: Commit**

```bash
git add src/HdrSwitcher/IHdrManager.cs src/HdrSwitcher/HdrManager.cs
git commit -m "feat: HdrManager with Win32 QueryDisplayConfig P/Invoke"
```

---

### Task 5: TrayApplicationContext

**Files:**
- Create: `src/HdrSwitcher/TrayApplicationContext.cs`

**Step 1: Create `src/HdrSwitcher/TrayApplicationContext.cs`:**

```csharp
using System.Drawing;

namespace HdrSwitcher;

public class TrayApplicationContext : ApplicationContext
{
    private readonly IHdrManager _hdr;
    private readonly AutostartManager _autostart;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;

    public TrayApplicationContext(IHdrManager hdr, AutostartManager autostart)
    {
        _hdr = hdr;
        _autostart = autostart;
        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu();

        _tray = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "HDR Switcher"
        };
        _tray.MouseClick += OnTrayClick;

        RefreshIcon();
    }

    // ── Icon ─────────────────────────────────────────────────────────────────

    private void RefreshIcon()
    {
        var displays = _hdr.GetDisplays();
        HdrState state = displays.Count == 0 ? HdrState.AllOff
            : displays.All(d => d.HdrEnabled) ? HdrState.AllOn
            : displays.All(d => !d.HdrEnabled) ? HdrState.AllOff
            : HdrState.Mixed;

        int sizePx = SystemInformation.SmallIconSize.Width; // DPI-aware
        var oldIcon = _tray.Icon;
        _tray.Icon = IconRenderer.Render(state, sizePx);
        oldIcon?.Dispose();

        _tray.Text = state switch
        {
            HdrState.AllOn => "HDR Switcher — All On",
            HdrState.AllOff => "HDR Switcher — All Off",
            HdrState.Mixed => "HDR Switcher — Mixed",
            _ => "HDR Switcher"
        };
    }

    // ── Tray click → toggle primary ──────────────────────────────────────────

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var displays = _hdr.GetDisplays();
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        if (primary is null) return;

        _hdr.SetHdr(primary.Id, !primary.HdrEnabled);
        RefreshIcon();
    }

    // ── Context menu ─────────────────────────────────────────────────────────

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        var displays = _hdr.GetDisplays();
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

        // Top item: HDR status for primary, clickable to toggle
        var primaryItem = new ToolStripMenuItem("HDR: Primary")
        {
            Checked = primary?.HdrEnabled ?? false,
            CheckOnClick = false
        };
        if (primary is not null)
        {
            var capturedPrimary = primary;
            primaryItem.Click += (_, _) =>
            {
                _hdr.SetHdr(capturedPrimary.Id, !capturedPrimary.HdrEnabled);
                RefreshIcon();
            };
        }
        _menu.Items.Add(primaryItem);
        _menu.Items.Add(new ToolStripSeparator());

        // Per-monitor items
        foreach (var display in displays)
        {
            var item = new ToolStripMenuItem(display.Name)
            {
                Checked = display.HdrEnabled,
                CheckOnClick = false
            };
            var captured = display;
            item.Click += (_, _) =>
            {
                _hdr.SetHdr(captured.Id, !captured.HdrEnabled);
                RefreshIcon();
            };
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());

        // Autostart
        var autostartItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = _autostart.IsEnabled(),
            CheckOnClick = false
        };
        autostartItem.Click += (_, _) =>
        {
            _autostart.SetEnabled(!_autostart.IsEnabled());
            // Rebuild immediately to reflect new check state
            RebuildMenu();
        };
        _menu.Items.Add(autostartItem);
        _menu.Items.Add(new ToolStripSeparator());

        // Exit
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _tray.Visible = false;
            Application.Exit();
        };
        _menu.Items.Add(exitItem);
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
```

**Step 2: Build to verify**

```bash
dotnet build src/HdrSwitcher/HdrSwitcher.csproj
```

Expected: `Build succeeded. 0 Error(s)`

**Step 3: Commit**

```bash
git add src/HdrSwitcher/TrayApplicationContext.cs
git commit -m "feat: TrayApplicationContext with tray icon, context menu, click handler"
```

---

### Task 6: Program.cs (entry point)

**Files:**
- Modify: `src/HdrSwitcher/Program.cs`

**Step 1: Replace the generated `Program.cs` with:**

```csharp
using HdrSwitcher;

// DPI awareness is set via app.manifest (PerMonitorV2).
// WinForms must be told to honor it at the API level too.
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var hdr = new HdrManager();
var autostart = new AutostartManager();
var context = new TrayApplicationContext(hdr, autostart);

Application.Run(context);
```

**Step 2: Build and run**

```bash
dotnet run --project src/HdrSwitcher/HdrSwitcher.csproj
```

Expected: A sun icon appears in the system tray. Right-clicking shows the menu. Left-clicking toggles primary display HDR.

**Step 3: Verify manually**
- Left-click icon → HDR toggles on primary monitor (display may briefly go dark and come back — this is normal)
- Right-click → menu shows per-monitor items with correct checked state
- Click a per-monitor item → that display's HDR toggles
- Check "Start with Windows" → reopen Task Manager > Startup apps, confirm `HdrSwitcher` appears
- Uncheck it → confirm it disappears from startup list
- Click Exit → icon disappears

**Step 4: Commit**

```bash
git add src/HdrSwitcher/Program.cs
git commit -m "feat: wire up Program.cs entry point"
```

---

### Task 7: Publish single-file exe

**Step 1: Publish**

```bash
dotnet publish src/HdrSwitcher/HdrSwitcher.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish/
```

Expected output: `publish/HdrSwitcher.exe` (~15–25 MB)

**Step 2: Run the published exe**

```bash
./publish/HdrSwitcher.exe
```

Expected: Same behavior as `dotnet run` — tray icon appears, HDR toggle works.

**Step 3: Commit**

```bash
# Do NOT commit the publish/ directory — add it to .gitignore first
echo "publish/" >> .gitignore
git add .gitignore
git commit -m "chore: ignore publish output directory"
```

---

## Running All Tests

```bash
dotnet test HdrSwitcher.sln
```

Expected: All tests pass. (HdrManager has no unit tests — it's tested manually since it requires a physical display.)

---

## Known Limitations

- Display names show as "Display 1", "Display 2" etc. (friendly name lookup via `DISPLAYCONFIG_TARGET_DEVICE_NAME` is left as a future enhancement)
- If a display doesn't support HDR it is silently omitted from the menu
- The `IsPrimary` detection uses `DISPLAYCONFIG_SOURCE_IN_USE` flag heuristic; in rare multi-GPU configs this may not be perfectly accurate
