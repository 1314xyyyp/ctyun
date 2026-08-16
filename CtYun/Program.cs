using CtYun;
using CtYun.Models;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// selftest 模式：不登录，仅验证"验证码拉取 → 识别"链路是否可用
if (args.Any(a => string.Equals(a, "selftest", StringComparison.OrdinalIgnoreCase)))
{
    await RunSelfTestAsync();
    return;
}

using var globalCts = new CancellationTokenSource();

Utility.WriteLine(ConsoleColor.Green, $"版本：v {Assembly.GetEntryAssembly()?.GetName().Version}");

var runtimeConfig = LoadRuntimeConfig();
if (runtimeConfig.Config.Accounts.Count == 0)
{
    Utility.WriteLine(ConsoleColor.Red, "未读取到账号配置。请配置 accounts.json，或设置 APP_USER/APP_PASSWORD，或使用交互输入模式。");
    return;
}

CtYunApi.RemoteOcrUrl = runtimeConfig.Config.OcrUrl;

Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    globalCts.Cancel();
};

var sessionTasks = runtimeConfig.Config.Accounts.Select(account => RunAccountAsync(account, runtimeConfig, globalCts.Token));

try
{
    await Task.WhenAll(sessionTasks);
}
catch (OperationCanceledException)
{
    Utility.WriteLine(ConsoleColor.Yellow, "程序已停止。");
}

async Task RunAccountAsync(AccountConfig account, RuntimeConfig runtimeConfig, CancellationToken ct)
{
    var label = AccountLabel(account);
    var api = new CtYunApi(account.DeviceCode);

    List<Desktop> desktopList = null;

    // 1) 尝试登录态缓存，避免每次启动都走"验证码识别 + 登录"
    var cachedLogin = TryLoadSessionCache(account, runtimeConfig);
    if (cachedLogin != null)
    {
        api.LoginInfo = cachedLogin;
        desktopList = await api.GetLlientListAsync();
        if (desktopList != null)
        {
            Utility.WriteLine(ConsoleColor.Green, $"[{label}] 登录态缓存有效，跳过登录。");
        }
        else
        {
            api.LoginInfo = null;
            TryDeleteSessionCache(account, runtimeConfig);
            Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 登录态缓存已失效，走完整登录。");
        }
    }

    // 2) 完整登录
    if (api.LoginInfo == null)
    {
        Utility.WriteLine(ConsoleColor.Cyan, $"[{label}] 开始登录。");
        if (!await PerformLoginSequence(api, account, runtimeConfig, ct))
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}] 登录失败，跳过该账号。");
            return;
        }
        SaveSessionCache(account, runtimeConfig, api.LoginInfo);
    }

    // 3) 云电脑列表 + 过滤
    desktopList ??= await api.GetLlientListAsync();
    if (desktopList == null || desktopList.Count == 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 未获取到云电脑。");
        return;
    }

    var activeDesktops = new List<Desktop>();
    foreach (var desktop in desktopList)
    {
        if (!IsDesktopSelected(desktop, account.Desktops))
        {
            Utility.WriteLine(ConsoleColor.DarkGray, $"[{label}][{desktop.DesktopCode}] 不在保活名单（desktops 配置）内，跳过。");
            continue;
        }

        if (desktop.UseStatusText != "运行中")
        {
            if (!runtimeConfig.Config.ConnectOfflineDesktops)
            {
                Utility.WriteLine(ConsoleColor.DarkGray, $"[{label}][{desktop.DesktopCode}] 状态为 {desktop.UseStatusText}，跳过（如需自动开机请配置 connectOfflineDesktops: true）。");
                continue;
            }
            Utility.WriteLine(ConsoleColor.Yellow, $"[{label}][{desktop.DesktopCode}] 状态 {desktop.UseStatusText}，尝试 connect 触发开机...");
        }

        var connectResult = await api.ConnectAsync(desktop.DesktopId);
        if (connectResult.Success && connectResult.Data?.DesktopInfo != null)
        {
            desktop.DesktopInfo = connectResult.Data.DesktopInfo;
            activeDesktops.Add(desktop);
        }
        else
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}] Connect Error: [{desktop.DesktopId}] {connectResult.Msg}");
        }
    }

    if (activeDesktops.Count == 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 没有可保活的云电脑。");
        return;
    }

    var keepAliveSeconds = runtimeConfig.Config.KeepAliveSeconds;
    if (keepAliveSeconds <= 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 保活任务启动：完全被动模式（仅应答质询，无强制重连兜底）。");
    }
    else
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 保活任务启动：被动保活 + 每 ~{keepAliveSeconds}s(±10%) 强制重连兜底。");
    }

    var keepAliveTasks = activeDesktops.Select(d => KeepAliveWorker(api, account, d, keepAliveSeconds, ct));
    await Task.WhenAll(keepAliveTasks);
}

async Task<bool> PerformLoginSequence(CtYunApi api, AccountConfig account, RuntimeConfig runtimeConfig, CancellationToken ct)
{
    if (!await api.LoginAsync(account.User, account.Password))
    {
        return false;
    }

    if (api.LoginInfo.BondedDevice)
    {
        return true;
    }

    var label = AccountLabel(account);
    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 当前设备未绑定，正在发送短信验证码。");
    if (!await api.GetSmsCodeAsync(account.User))
    {
        return false;
    }

    var verificationCode = ReadVerificationCode(account);
    if (string.IsNullOrWhiteSpace(verificationCode))
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 未获取到短信验证码。");
        return false;
    }

    return await api.BindingDeviceAsync(verificationCode.Trim());
}

string ReadVerificationCode(AccountConfig account)
{
    var label = AccountLabel(account);
    if (!CanReadFromConsole())
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 当前账号需要短信验证码，请使用 -it 交互模式重新运行并输入验证码。");
        return "";
    }

    Console.Write($"[{label}] 短信验证码: ");
    return Console.ReadLine();
}

static bool IsDesktopSelected(Desktop desktop, List<string> whitelist)
{
    if (whitelist == null || whitelist.Count == 0)
    {
        return true;
    }

    return whitelist.Any(w => !string.IsNullOrWhiteSpace(w)
        && (string.Equals(w.Trim(), desktop.DesktopCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w.Trim(), desktop.DesktopName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w.Trim(), desktop.DesktopId, StringComparison.OrdinalIgnoreCase)));
}

async Task KeepAliveWorker(CtYunApi api, AccountConfig account, Desktop desktop, int forceResetSeconds, CancellationToken globalToken)
{
    var label = AccountLabel(account);
    var initialPayload = Convert.FromBase64String("UkVEUQIAAAACAAAAGgAAAAAAAAABAAEAAAABAAAAEgAAAAkAAAAECAAA");
    var uri = new Uri($"wss://{desktop.DesktopInfo.ClinkLvsOutHost}/clinkProxy/{desktop.DesktopId}/MAIN");

    // 多桌面错峰启动，避免所有连接同相位建立/重置
    await Task.Delay(Random.Shared.Next(0, 4000), globalToken);

    int consecutiveFailures = 0;

    while (!globalToken.IsCancellationRequested)
    {
        // 强制重连兜底周期附加 ±10% 随机抖动；<=0 表示完全被动
        double resetSeconds = forceResetSeconds <= 0 ? -1 : forceResetSeconds * (0.9 + Random.Shared.NextDouble() * 0.2);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
        if (resetSeconds > 0)
        {
            sessionCts.CancelAfter(TimeSpan.FromSeconds(resetSeconds));
        }

        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", "https://pc.ctyun.cn");
        client.Options.AddSubProtocol("binary");
        client.Options.KeepAliveInterval = TimeSpan.FromSeconds(30); // 协议层 ping，防中间设备掐掉空闲连接

        bool sessionEstablished = false;
        try
        {
            Utility.WriteLine(ConsoleColor.Cyan, $"[{label}][{desktop.DesktopCode}] === 建立保活连接 ===");

            // 握手单独限时，避免整个周期耗在连接上
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(uri, connectCts.Token);

            var hostParts = desktop.DesktopInfo.ClinkLvsOutHost.Split(':', 2);
            var connectMessage = new ConnecMessage
            {
                type = 1,
                ssl = 1,
                host = hostParts[0],
                port = hostParts.Length > 1 ? hostParts[1] : "443",
                ca = desktop.DesktopInfo.CaCert,
                cert = desktop.DesktopInfo.ClientCert,
                key = desktop.DesktopInfo.ClientKey,
                servername = desktop.DesktopInfo.Host + ":" + desktop.DesktopInfo.Port,
                oqs = 0
            };

            var msgBytes = JsonSerializer.SerializeToUtf8Bytes(connectMessage, AppJsonSerializerContext.Default.ConnecMessage);
            await client.SendAsync(msgBytes, WebSocketMessageType.Text, true, sessionCts.Token);

            await Task.Delay(500, sessionCts.Token);
            await client.SendAsync(initialPayload, WebSocketMessageType.Binary, true, sessionCts.Token);

            Utility.WriteLine(ConsoleColor.Green, $"[{label}][{desktop.DesktopCode}] 连接已就绪，被动保活中" + (resetSeconds > 0 ? $"（{(int)resetSeconds}s 后强制刷新兜底）" : "（无强制重连）") + "...");
            sessionEstablished = true;
            consecutiveFailures = 0;

            try
            {
                await ReceiveLoop(api, client, account, desktop, sessionCts.Token);
                Utility.WriteLine(ConsoleColor.Yellow, $"[{label}][{desktop.DesktopCode}] 连接被关闭，准备重连...");
            }
            catch (OperationCanceledException) when (!globalToken.IsCancellationRequested)
            {
                Utility.WriteLine(ConsoleColor.Yellow, $"[{label}][{desktop.DesktopCode}] 强制重连周期到，主动刷新会话...");
            }
        }
        catch (OperationCanceledException) when (globalToken.IsCancellationRequested)
        {
            break;
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely
                                            || ex.Message.Contains("closed the WebSocket connection", StringComparison.Ordinal))
        {
            // 服务端回收连接（不打关闭握手直接断开），属于正常现象，重连即可
            Utility.WriteLine(ConsoleColor.DarkYellow, $"[{label}][{desktop.DesktopCode}] 连接被服务端回收（正常现象），自动重连...");
        }
        catch (Exception ex)
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktop.DesktopCode}] 异常: {ex.Message}");
        }
        finally
        {
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reset", CancellationToken.None);
                }
                catch
                {
                    // 服务器侧可能已断开，忽略关闭异常
                }
            }
        }

        if (globalToken.IsCancellationRequested)
        {
            break;
        }

        if (sessionEstablished)
        {
            // 会话曾成功建立：短暂停顿后重连即可
            await Task.Delay(Random.Shared.Next(1000, 4000), globalToken);
        }
        else
        {
            // 连续失败：指数退避 5s → 300s，附加随机抖动
            consecutiveFailures++;
            var backoffSeconds = Math.Min(300, 5 * Math.Pow(2, consecutiveFailures - 1)) + Random.Shared.NextDouble() * 3;
            Utility.WriteLine(ConsoleColor.Yellow, $"[{label}][{desktop.DesktopCode}] 连接失败 {consecutiveFailures} 次，{backoffSeconds:F0} 秒后重试...");
            await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), globalToken);
        }
    }

    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}][{desktop.DesktopCode}] 保活任务已停止。");
}

async Task ReceiveLoop(CtYunApi api, ClientWebSocket ws, AccountConfig account, Desktop desktop, CancellationToken ct)
{
    var buffer = new byte[8192];
    var encryptor = new Encryption();
    var label = AccountLabel(account);

    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        // 一条 WebSocket 消息可能分多帧到达，必须按 EndOfMessage 累积完整消息再解析
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            message.Write(buffer, 0, result.Count);

            if (message.Length > 4 * 1024 * 1024)
            {
                Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktop.DesktopCode}] 单条消息超过 4MB，丢弃并重连。");
                return;
            }
        } while (!result.EndOfMessage);

        if (message.Length == 0)
        {
            continue;
        }

        var data = message.ToArray();

        // REDQ 保活质询（魔数 0x52 0x45 0x44 0x51）
        if (data.Length >= 4 && data[0] == 0x52 && data[1] == 0x45 && data[2] == 0x44 && data[3] == 0x51)
        {
            try
            {
                var response = encryptor.Execute(data);
                await ws.SendAsync(response, WebSocketMessageType.Binary, true, ct);
                Utility.WriteLine(ConsoleColor.DarkGreen, $"[{label}][{desktop.DesktopCode}] -> 保活校验应答成功");
            }
            catch (Exception ex)
            {
                Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktop.DesktopCode}] 保活校验应答失败：{ex.Message}，断开重连。");
                return;
            }
            continue;
        }

        try
        {
            var infos = SendInfo.FromBuffer(data);
            foreach (var info in infos)
            {
                if (info.Type == 103)
                {
                    var payload = Encoding.UTF8.GetBytes("{\"type\":1,\"userName\":\"" + api.LoginInfo.UserName + "\",\"userInfo\":\"\",\"userId\":" + api.LoginInfo.UserId + "}");
                    var byUserName = new SendInfo { Type = 118, Data = payload }.ToBuffer(true);
                    await ws.SendAsync(byUserName, WebSocketMessageType.Binary, true, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Utility.WriteLine(ConsoleColor.DarkYellow, $"[{label}][{desktop.DesktopCode}] 消息解析失败: {ex.Message}");
        }
    }
}

async Task RunSelfTestAsync()
{
    Utility.WriteLine(ConsoleColor.Cyan, "selftest：验证「验证码拉取 → 识别」链路（无需账号，不登录）。");
    var api = new CtYunApi("web_" + GenerateRandomString(32));

    var img = await api.DownloadLoginCaptchaForSelfTest("13800000000");
    if (img == null || img.Length == 0)
    {
        Utility.WriteLine(ConsoleColor.Red, "验证码图片拉取失败：请检查本机网络能否访问 desk.ctyun.cn:8810。");
        return;
    }

    var samplePath = Path.Combine(AppContext.BaseDirectory, "captcha-sample.png");
    await File.WriteAllBytesAsync(samplePath, img);
    Utility.WriteLine(ConsoleColor.Green, $"已获取验证码图片（{img.Length} 字节），保存到 {samplePath}，开始识别...");

    var code = await OcrService.RecognizeAsync(img, CtYunApi.RemoteOcrUrl);
    if (string.IsNullOrEmpty(code))
    {
        Utility.WriteLine(ConsoleColor.Red, "识别失败：若为远程模式请检查 ocrUrl 是否可达。");
        return;
    }

    var engine = OcrService.LocalAvailable ? "本地模型" : "远程接口";
    Utility.WriteLine(ConsoleColor.Green, $"selftest 通过：识别引擎={engine}，识别结果={code}（请打开 captcha-sample.png 肉眼核对是否一致）。");
}

// —— 登录态缓存（C2）——

string SessionCachePath(AccountConfig account, string dataDir)
    => Path.Combine(dataDir, "sessions", SafeName(account.Name ?? account.User) + ".json");

LoginInfo TryLoadSessionCache(AccountConfig account, RuntimeConfig runtimeConfig)
{
    if (runtimeConfig.Config.SessionCacheHours <= 0)
    {
        return null;
    }

    var path = SessionCachePath(account, runtimeConfig.DataDir);
    if (!File.Exists(path))
    {
        return null;
    }

    try
    {
        var cache = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonSerializerContext.Default.SessionCache);
        if (cache?.LoginInfo == null
            || string.IsNullOrEmpty(cache.LoginInfo.SecretKey)
            || !string.Equals(cache.DeviceCode, account.DeviceCode, StringComparison.Ordinal))
        {
            return null;
        }

        var ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - cache.SavedAtUnixSeconds;
        if (ageSeconds < 0 || ageSeconds > runtimeConfig.Config.SessionCacheHours * 3600L)
        {
            return null;
        }

        return cache.LoginInfo;
    }
    catch
    {
        return null;
    }
}

void SaveSessionCache(AccountConfig account, RuntimeConfig runtimeConfig, LoginInfo loginInfo)
{
    if (runtimeConfig.Config.SessionCacheHours <= 0)
    {
        return;
    }

    try
    {
        Directory.CreateDirectory(Path.Combine(runtimeConfig.DataDir, "sessions"));
        var cache = new SessionCache
        {
            SavedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DeviceCode = account.DeviceCode,
            LoginInfo = loginInfo
        };
        File.WriteAllText(SessionCachePath(account, runtimeConfig.DataDir),
            JsonSerializer.Serialize(cache, AppJsonSerializerContext.Default.SessionCache));
    }
    catch (Exception ex)
    {
        Utility.WriteLine(ConsoleColor.DarkYellow, $"[{AccountLabel(account)}] 保存登录态缓存失败：{ex.Message}");
    }
}

void TryDeleteSessionCache(AccountConfig account, RuntimeConfig runtimeConfig)
{
    try
    {
        File.Delete(SessionCachePath(account, runtimeConfig.DataDir));
    }
    catch
    {
        // 删除失败不影响主流程
    }
}

// —— 配置加载 ——

RuntimeConfig LoadRuntimeConfig()
{
    var dataDir = GetDataDir();
    Directory.CreateDirectory(dataDir);

    var config = LoadAccountsFromFile(dataDir) ?? LoadAccountsFromEnvironment();
    if (config == null || config.Accounts.Count == 0)
    {
        config = LoadAccountsFromConsole(dataDir);
    }

    foreach (var account in config.Accounts)
    {
        account.Name = FirstNotEmpty(account.Name, account.User);
        account.DeviceCode = ResolveDeviceCode(account, dataDir);
    }

    return new RuntimeConfig(config, dataDir);
}

AppConfig LoadAccountsFromEnvironment()
{
    var user = Environment.GetEnvironmentVariable("APP_USER");
    var password = Environment.GetEnvironmentVariable("APP_PASSWORD");
    if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
    {
        return null;
    }

    return new AppConfig
    {
        Accounts =
        [
            new AccountConfig
            {
                Name = Environment.GetEnvironmentVariable("APP_NAME"),
                User = user,
                Password = password,
                DeviceCode = Environment.GetEnvironmentVariable("DEVICECODE")
            }
        ]
    };
}

AppConfig LoadAccountsFromFile(string dataDir)
{
    var configPath = Environment.GetEnvironmentVariable("CTYUN_CONFIG");
    if (string.IsNullOrWhiteSpace(configPath))
    {
        configPath = Path.Combine(dataDir, "accounts.json");
    }

    if (!File.Exists(configPath))
    {
        return null;
    }

    try
    {
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppConfig);
        Utility.WriteLine(ConsoleColor.Green, $"已读取配置文件：{configPath}");
        return config;
    }
    catch (Exception ex)
    {
        Utility.WriteLine(ConsoleColor.Red, $"读取配置文件失败：{ex.Message}");
        return null;
    }
}

AppConfig LoadAccountsFromConsole(string dataDir)
{
    if (!CanReadFromConsole())
    {
        return new AppConfig();
    }

    var accounts = new List<AccountConfig>();
    while (true)
    {
        Console.Write("账号: ");
        var user = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(user))
        {
            break;
        }

        Console.Write("密码: ");
        var password = ReadPassword();
        accounts.Add(new AccountConfig { Name = user, User = user, Password = password });

        Console.Write("继续添加账号? (y/N): ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }
    }

    if (accounts.Count > 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"交互输入模式已读取 {accounts.Count} 个账号。设备码会保存到 {Path.Combine(dataDir, "devices")}。");
    }

    return new AppConfig { Accounts = accounts };
}

string ResolveDeviceCode(AccountConfig account, string dataDir)
{
    if (!string.IsNullOrWhiteSpace(account.DeviceCode))
    {
        return account.DeviceCode.Trim();
    }

    var devicesDir = Path.Combine(dataDir, "devices");
    Directory.CreateDirectory(devicesDir);
    var deviceCodePath = Path.Combine(devicesDir, SafeName(account.Name ?? account.User) + ".txt");
    if (!File.Exists(deviceCodePath))
    {
        File.WriteAllText(deviceCodePath, "web_" + GenerateRandomString(32));
    }

    return File.ReadAllText(deviceCodePath).Trim();
}

string GetDataDir()
{
    var dataDir = Environment.GetEnvironmentVariable("CTYUN_DATA_DIR");
    if (!string.IsNullOrWhiteSpace(dataDir))
    {
        return dataDir;
    }

    return IsRunningInContainer() ? "/app/data" : AppContext.BaseDirectory;
}

static string GenerateRandomString(int length)
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    return new string(Enumerable.Repeat(chars, length).Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray());
}

static string ReadPassword()
{
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return sb.ToString();
        }

        if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
        {
            sb.Remove(sb.Length - 1, 1);
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
            Console.Write("*");
        }
    }
}

static string AccountLabel(AccountConfig account) => account.Name ?? account.User;

static string SafeName(string value)
{
    var source = string.IsNullOrWhiteSpace(value) ? "default" : value;
    var builder = new StringBuilder(source.Length);
    foreach (var ch in source)
    {
        builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
    }
    return builder.ToString();
}

static string FirstNotEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

static bool CanReadFromConsole() => !Console.IsInputRedirected && !Console.IsOutputRedirected;

static bool IsRunningInContainer() => File.Exists("/.dockerenv");

record RuntimeConfig(
    AppConfig Config,
    string DataDir);
