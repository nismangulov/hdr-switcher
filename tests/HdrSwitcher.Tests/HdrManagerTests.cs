namespace HdrSwitcher.Tests;

public class HdrManagerTests
{
    // Known real-world values from hardware observation:
    //   HDR ON  = 0x3  (bits: supported=1, enabled=1, wideColor=0)
    //   HDR OFF = 0x7  (bits: supported=1, enabled=1, wideColor=1 — WCG-only mode)

    // ── IsHdrSupported ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x0u, false)] // no capabilities
    [InlineData(0x1u, true)]  // supported only
    [InlineData(0x3u, true)]  // HDR ON  (real hardware)
    [InlineData(0x7u, true)]  // HDR OFF (real hardware, WCG mode)
    [InlineData(0x6u, false)] // enabled+wideColor but NOT supported — shouldn't exist, bit0 clear
    public void IsHdrSupported_reflects_bit0(uint value, bool expected)
        => Assert.Equal(expected, HdrManager.IsHdrSupported(value));

    // ── IsHdrEnabled ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x0u, false)] // nothing set
    [InlineData(0x1u, false)] // supported, but not enabled
    [InlineData(0x3u, true)]  // HDR ON  — bit1 set, bit2 clear  (real: 0b011)
    [InlineData(0x7u, false)] // HDR OFF — bit1 set, bit2 also set (WCG mode, real: 0b111)
    [InlineData(0x5u, false)] // supported + wideColor, but not enabled
    [InlineData(0x6u, false)] // enabled + wideColor, wideColor wins — not HDR
    [InlineData(0x2u, true)]  // only advancedColorEnabled (no support bit, no wideColor) — IsHdrEnabled is true
    public void IsHdrEnabled_requires_bit1_set_and_bit2_clear(uint value, bool expected)
        => Assert.Equal(expected, HdrManager.IsHdrEnabled(value));

    [Fact]
    public void IsHdrEnabled_realworld_hdr_on_value_0x3_returns_true()
        => Assert.True(HdrManager.IsHdrEnabled(0x3));

    [Fact]
    public void IsHdrEnabled_realworld_hdr_off_value_0x7_returns_false()
        => Assert.False(HdrManager.IsHdrEnabled(0x7));
}
