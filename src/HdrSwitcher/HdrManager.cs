using System.Runtime.InteropServices;

namespace HdrSwitcher;

public class HdrManager : IHdrManager
{
    private const int QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    private const int DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;
    private const int ERROR_SUCCESS = 0;

    // value bits: 0=advancedColorSupported, 1=advancedColorEnabled, 2=wideColorEnforced
    // True HDR = bit1 set AND bit2 clear (bit2 is set when display is in WCG-only mode)

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
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] modeInfo;
    }

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
        public uint value;
        public int colorEncoding;
        public int bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
    }

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

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var paths = QueryPaths();
        var result = new List<DisplayInfo>();

        for (int i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            var colorInfo = GetAdvancedColorInfo(path.targetInfo.adapterId, path.targetInfo.id);

            bool hdrSupported = (colorInfo.value & 1) != 0;
            bool hdrEnabled = (colorInfo.value & 2) != 0 && (colorInfo.value & 4) == 0;

            if (!hdrSupported) continue;

            bool isPrimary = (path.sourceInfo.statusFlags & 1) != 0;

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
        int idx = Array.FindIndex(paths, p => p.targetInfo.id == displayId);
        if (idx < 0)
            throw new InvalidOperationException($"Display {displayId} not found");
        var path = paths[idx];

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
        int err = DisplayConfigGetDeviceInfo(ref request);
        if (err != ERROR_SUCCESS)
            throw new InvalidOperationException($"DisplayConfigGetDeviceInfo failed: {err}");
        return request;
    }
}
