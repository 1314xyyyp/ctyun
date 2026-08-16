using System.Text.Json.Serialization;

namespace CtYun.Models
{
    /// <summary>
    /// 登录态缓存（保存在 数据目录/sessions/账号.json），
    /// 用于重启程序时跳过登录流程，减少验证码识别次数。
    /// </summary>
    public class SessionCache
    {
        [JsonPropertyName("savedAt")]
        public long SavedAtUnixSeconds { get; set; }

        [JsonPropertyName("deviceCode")]
        public string DeviceCode { get; set; }

        [JsonPropertyName("loginInfo")]
        public LoginInfo LoginInfo { get; set; }
    }
}
