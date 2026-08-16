# CtYun 优化版说明（v1.2.0）

本版本在原版基础上做了安全加固与稳定性重构，**所有改动只涉及本地行为**，登录、签名、保活协议与官方网页端的交互逻辑保持不变。

## 改动总览

| 类别 | 改动 | 说明 |
|---|---|---|
| 安全 | 验证码识别本地化 | 内置 ddddocr(common_old) 模型离线推理，验证码图片不再发送给任何第三方；远程识别仅作兜底，且可配置/禁用 |
| 安全 | OCR 请求隔离 | 远程兜底请求使用独立 HttpClient，不再携带 ctg-devicecode 等官方请求头（原版会把设备码泄露给第三方打码域名） |
| 健壮性 | WebSocket 分帧修复 | 按 EndOfMessage 累积完整消息再解析，修复原版对分帧/超 8KB 消息解析错乱的问题 |
| 健壮性 | REDQ 质询解析加固 | 公钥提取加边界校验，协议变更时给出明确报错而非越界崩溃；修复原版第二次质询复用旧公钥的状态残留缺陷 |
| 健壮性 | 杂项修复 | 云电脑列表判空；验证码 URL 时间戳取当前时间；连接收尾改用 CloseAsync；请求超时 30s |
| 稳定性 | 重连策略重构 | 去掉固定 60s 强制重连：会话存活期间只应答质询；强制重连改为兜底机制（默认 900s ±10% 抖动）；连续失败指数退避 5s→300s；多桌面错峰启动 |
| 稳定性 | 登录态缓存 | 登录结果缓存到 数据目录/sessions/，有效期内重启程序跳过登录（省去验证码识别），失效自动降级完整登录 |
| 功能 | 桌面白名单 | accounts.json 支持 desktops 配置，只保活指定云电脑；默认跳过未开机的云电脑（不再意外触发开机） |
| 功能 | selftest 自检 | `CtYun.exe selftest` 无需账号即可验证「验证码拉取 → 识别」链路 |

## 本地 OCR 说明

- 模型：`models/ddddocr.onnx`，即 [ddddocr](https://github.com/sml2h3/ddddocr) 1.6.1 的 `common_old.onnx`（与原版远程打码服务使用的方案同源），字符表由官方 `charset_manager.py` 提取生成（8210 类，含 CTC blank）。
- 预处理与解码逻辑对齐 ddddocr 官方实现：高 64 等比缩放(Lanczos) → 灰度 → /255 → CTC 解码。
- 模型查找顺序：`$CTYUN_DATA_DIR/models/ddddocr.onnx` → 程序目录 `Assets/ddddocr.onnx`（兼容旧布局 `models/ddddocr.onnx`）。
- 本地模型加载失败或推理出错时自动回退远程识别（`ocrUrl` 配置），设 `ocrUrl: ""` 可彻底禁用远程回退。

## accounts.json 新配置示例

```json
{
  "keepAliveSeconds": 900,
  "connectOfflineDesktops": false,
  "ocrUrl": "https://orc.1999111.xyz/ocr",
  "sessionCacheHours": 24,
  "accounts": [
    {
      "name": "account-a",
      "user": "你的账号",
      "password": "你的密码",
      "desktops": ["desktop-code-1"]
    }
  ]
}
```

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `keepAliveSeconds` | 900 | 强制重连兜底周期（秒，实际 ±10% 抖动）。会话存活期间不重连。设 0 为完全被动（不推荐）。旧配置写 60 依然生效，但会更容易形成固定节拍 |
| `connectOfflineDesktops` | false | 是否对未开机云电脑调用 connect（触发开机）。原版默认会触发，现默认跳过 |
| `ocrUrl` | 原远程地址 | 远程识别兜底接口。本地模型正常时不会用到；设为 `""` 禁用 |
| `sessionCacheHours` | 24 | 登录态缓存时长（小时），0 关闭缓存 |
| `accounts[].desktops` | 空 | 只保活指定云电脑，匹配 desktopCode / desktopName / desktopId（不区分大小写） |

## 使用

```bash
# 构建（需 .NET 8 SDK）
dotnet build -c Release

# 自检：验证验证码拉取与本地识别（无需账号）
dotnet run -c Release -- selftest

# 发布（模型文件会自动带出）
dotnet publish -c Release -o publish

# 正式运行：把 accounts.json 放到程序目录后
CtYun.exe
```

首次设备绑定仍需短信验证码交互输入（与原版一致）。设备码、登录态缓存保存在数据目录（程序目录，或 `CTYUN_DATA_DIR` / Docker `/app/data`）。

## 依赖许可

- [Microsoft.ML.OnnxRuntime](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime) — MIT
- [SixLabors.ImageSharp 3.1.x](https://github.com/SixLabors/ImageSharp) — Six Labors Split License（个人/小规模使用免费）
- ddddocr 模型与字符表 — 来自 [sml2h3/ddddocr](https://github.com/sml2h3/ddddocr)（MIT）

## 风险提示（不变的部分）

本工具仍是协议模拟保活，违反云电脑服务条款的可能性没有改变：封号/限制风险只能降低、无法消除。按时长计费的套餐在保活期间持续计费；`connectOfflineDesktops: true` 会触发开机计费。
