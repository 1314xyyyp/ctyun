# CtYun 云电脑保活（本地优化版）

天翼云电脑保活工具优化版：基于 [leleji/CtYun](https://github.com/leleji/CtYun)（MIT）修改，登录与保活协议逻辑不变，安全性与稳定性增强。**仅供个人保活自己的云电脑使用。**

## 本版改进（相对原版）

- **验证码识别本地化**：内置 ddddocr(common_old) 模型离线推理，验证码图片不外发；远程识别仅作可配置兜底，且请求隔离（不再向第三方泄露设备码）
- **协议健壮性**：WebSocket 按 `EndOfMessage` 分帧重组；REDQ 质询解析加边界校验；修复长会话公钥状态残留缺陷
- **重连策略**：取消固定 60s 强制重连，改为被动保活 + 900s(±10%) 兜底刷新；连续失败指数退避；多桌面错峰
- **登录态缓存**：默认 24h 内重启免登录、免验证码
- **桌面白名单**：只保活指定云电脑；默认跳过未开机电脑（不再意外触发开机）
- **selftest 自检**：`CtYun.exe selftest` 无需账号即可验证识别链路

详细说明与完整配置项见 [优化说明.md](优化说明.md)。

## 快速开始（Windows 10+，免安装）

1. 从本仓库 **Releases**（或 Actions 构建产物）下载 `CtYun-win10-x64.zip` 并解压
2. 在程序目录创建 `accounts.json`：

```json
{
  "accounts": [
    {
      "name": "main",
      "user": "你的账号",
      "password": "你的密码",
      "desktops": ["要保活的云电脑编号"]
    }
  ]
}
```

3. 运行 `CtYun.exe`（建议先用 `CtYun.exe selftest` 验证网络与识别链路）
4. 首次绑定设备需按提示输入短信验证码

## 从源码构建

需 .NET 8 SDK：

```bash
dotnet publish CtYun/CtYun.csproj -c Release -r win-x64 --self-contained true -o publish
```

## 致谢与许可

- 原版实现：[leleji/CtYun](https://github.com/leleji/CtYun)（MIT）
- OCR 模型：[sml2h3/ddddocr](https://github.com/sml2h3/ddddocr)（MIT）
- 推理引擎：[Microsoft.ML.OnnxRuntime](https://github.com/microsoft/onnxruntime)（MIT）
- 图像解码：[SixLabors.ImageSharp 3.1.x](https://github.com/SixLabors/ImageSharp)（Six Labors Split License，个人使用免费）

## 风险提示

本工具为协议模拟保活，存在违反云电脑服务条款的可能（账号限制/封号风险只能降低、无法消除）；按时长计费的套餐在保活期间持续计费。
