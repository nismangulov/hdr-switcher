namespace HdrSwitcher;

public record DisplayInfo(uint Id, string Name, bool HdrEnabled, bool IsPrimary);

public interface IHdrManager
{
    IReadOnlyList<DisplayInfo> GetDisplays();
    void SetHdr(uint displayId, bool enabled);
}
