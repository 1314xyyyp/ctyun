using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CtYun.Models
{
    public class AppConfig
    {
        [JsonPropertyName("accounts")]
        public List<AccountConfig> Accounts { get; set; } = [];

        /// <summary>
        /// 强制重连兜底周期（秒），实际会附加 ±10% 随机抖动。
        /// 会话存活期间只应答质询、不主动断开；到点后主动刷新一次会话。
        /// 默认 900；设为 0 表示完全被动（不推荐，出错后无法自愈刷新）。
        /// </summary>
        [JsonPropertyName("keepAliveSeconds")]
        public int KeepAliveSeconds { get; set; } = 900;

        /// <summary>
        /// 是否对未开机（非"运行中"）的云电脑调用 connect（会触发开机）。默认 false 跳过。
        /// </summary>
        [JsonPropertyName("connectOfflineDesktops")]
        public bool ConnectOfflineDesktops { get; set; }

        /// <summary>
        /// 远程验证码识别接口，作为本地模型不可用时的兜底。设为空字符串可彻底禁用远程识别。
        /// </summary>
        [JsonPropertyName("ocrUrl")]
        public string OcrUrl { get; set; } = "https://orc.1999111.xyz/ocr";

        /// <summary>
        /// 登录态缓存时长（小时），用于跳过登录（省去验证码识别）。0 关闭缓存，每次都完整登录。
        /// </summary>
        [JsonPropertyName("sessionCacheHours")]
        public int SessionCacheHours { get; set; } = 24;
    }

    public class AccountConfig
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("user")]
        public string User { get; set; }

        [JsonPropertyName("password")]
        public string Password { get; set; }

        [JsonPropertyName("deviceCode")]
        public string DeviceCode { get; set; }

        /// <summary>
        /// 只保活指定的云电脑，匹配 desktopCode / desktopName / desktopId（不区分大小写）。
        /// 留空或不配置表示保活全部运行中的云电脑。
        /// </summary>
        [JsonPropertyName("desktops")]
        public List<string> Desktops { get; set; }
    }
}
