namespace Translation.Bridge.Core.Models;

public class StreamlinkSettings
{
    public string StreamlinkPath { get; set; } = @"C:\Program Files\Streamlink\bin\streamlink.exe";
    public string PlayerPath { get; set; } = @"C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe";
    /// <summary>streamlink 的畫質參數（best、720p…）。</summary>
    public string Quality { get; set; } = "best";
}
