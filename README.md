# Global Protector WiFi

轻量、免安装的 Windows 分流助手，让 GlobalProtect 远程访问与日常上网并行。

![界面预览](docs/screenshot.png)

## 下载与使用

在 [Releases](https://github.com/ooLemon0-0/Global_Protector_WIFI_User/releases) 下载 `GlobalProtectorWiFi.exe` 或便携 ZIP。源码 ZIP 供开发者构建，不能替代 EXE。

1. **先断开 GlobalProtect**，打开助手。
2. 主界面无需填写参数，默认分流目标为 **`10.213.134.31`**。需要调整时在 **设置** 中修改目标地址、DNS 和自动打开 VPN 的选项；已有配置继续保留。
3. 点击 **启动**，允许 Windows 管理员权限提示。
4. 看到“等待 VPN”后，正常登录 VPN。断网期间，助手仍在本机继续工作。
5. 等待“已连接”，之后照常使用自己的远程桌面或其他软件。助手不负责连接远程桌面。可最小化到系统托盘，**保留助手运行**。
6. 断开 VPN 后会自动撤销临时修改，并继续等待下次连接。正常重连会重新检测与应用分流，无需重启助手。
7. 要结束待命，点击 **停止**，或在托盘中选择 **退出并恢复**。

“自动打开 GlobalProtect”默认选中，只负责打开已安装的 GlobalProtect；也可以手动打开客户端。设置和日志入口位于窗口底部。

## 快速登录与账号密码

助手不保存或自动填写 VPN 密码，也不绕过 MFA。GlobalProtect 官方支持由管理员配置 **Save User Credentials**（保存用户名与密码）或 **SSO**；是否可用取决于公司门户策略。若管理员已允许，正常登录一次后可由官方客户端记忆凭据，助手下次打开客户端即可继续使用。参见 [官方凭据保存设置](https://docs.paloaltonetworks.com/ngfw/help/10-1/globalprotect/network-globalprotect-portals/globalprotect-portals-agent-configuration-tab/globalprotect-portals-agent-authentication-tab)。

GlobalProtect 需由公司提供并预先安装。工具不会处理账号、密码或 MFA。EXE 尚未使用商业证书签名；校验发布页的 SHA-256，不需要关闭防病毒软件。

## 工作原理

- 自动识别 GlobalProtect 的 PANGP 虚拟网卡，以及连接前的物理 Wi-Fi / 以太网默认路由。
- 为默认或自定义的每个远程 IP 添加临时 `/32` 主机路由，经 VPN 访问。默认目标在 `src/Program.cs` 的 `Program.DefaultTarget` 中配置；清空输入后点击开启也会使用默认目标。
- 只移除 GlobalProtect 的 IPv4 默认路由，保留原物理网络默认路由。**其他已由 VPN 下发的内网路由保留**。
- 从 VPN 下发且具有隧道路由的 DNS 中，探测可解析公网域名的服务器，临时用于 VPN 网卡。也可指定其中一个 DNS。
- 保留 VPN DNS 的隧道路由。它并非“只让 mstsc.exe 走 VPN”：访问指定 IP 的所有端口，以及 DNS，仍经过 VPN。
- 记录快照，再执行临时修改。每约 15 秒检查实际路由、公网连接和 DNS；连续失败会恢复。不会反复与 VPN 重新下发的默认路由争抢。

工具不修改 GlobalProtect 安装文件、服务端策略、Wi-Fi DNS 或 FlClash 设置，不安装驱动、后台服务或开机启动项。界面是原生 WinForms，PowerShell 工作程序嵌入在单个 EXE 内，运行时提取至本机数据目录。

## 兼容范围与边界

- Windows 10 1903+ / Windows 11，.NET Framework 4.8+，Windows PowerShell 5.1。现代 Windows 通常已内置这些组件。主构建为 AnyCPU；目前在 Windows x64 进行构建和界面检查，x86/ARM64 尚未实机验证。
- 需要当前账号能够以管理员身份运行。公司设备策略可能禁止修改路由或 DNS，此时应联系管理员。
- 初版支持一个 PANGP 网卡和 IPv4 默认路由。多个 PANGP 网卡、IPv6 隧道默认路由、半默认路由会停止操作，不盲目修改。
- 本地逻辑根据一套已成功实测的 GlobalProtect 配置改编，**不能保证所有公司的 VPN 策略都兼容**。公开版的自动 DNS 选择、多目标及 GUI 仍需要更多环境实测。
- VPN 强制 DNS 策略仍可能影响公司内部域名。需要完整的企业内外 DNS 分流时，应由 VPN 管理员配置 Split Tunnel / Split DNS；不能仅靠本工具解决所有环境。
- 远程桌面高延迟、UDP 被阻止、带宽或服务器负载问题不会被分流助手自动修复。
- 初版公网健康检查使用 `1.1.1.1:443` 与 `example.com`。若网络阻止这些检测目标，可能无法通过验证。

## 恢复与日志

本机数据位于 `%LOCALAPPDATA%\GlobalProtectorWiFi\`：

- `events.jsonl`：状态、检测与错误记录。
- `state.json`：恢复快照。
- `settings.json`：本机保存的目标地址及 DNS 选项。

异常关闭后，再次开启助手会识别未完成的快照。先断开 VPN，再点击 **停止**。不要删除数据目录后再尝试恢复，也不要同时运行旧版脚本和 EXE。

日志不自动上传。提交 Issue 前自行检查并脱敏 IP、网关、DNS 与本机路径。仓库与 Release 不包含个人配置或运行记录。

## 从源码构建

在 Windows PowerShell 中运行：

```powershell
.\build.ps1 -Test
```

输出为 `dist/GlobalProtectorWiFi.exe`。使用 Windows 自带 C# 编译器，不需要 NuGet、Node.js、Python 或额外的 .NET SDK。`src/Engine.ps1` 作为资源嵌入。

测试使用模拟网卡、路由和 DNS，验证修改范围、事务回退、重复 DNS、输入校验与异常状态；**不会对测试机器执行真实网络修改**。界面可用 `GlobalProtectorWiFi.exe --demo` 只读预览。

GitHub Actions 会在推送时构建和测试；推送 `v*` 标签时构建带校验和的 EXE、便携 ZIP、源码 ZIP，并创建 Release。GitHub 自带的 Source code 下载也可使用。

## English

A small portable Windows companion for GlobalProtect. It keeps the physical internet default route, adds selected IPv4 target routes through the VPN, and chooses a working VPN-provided DNS server. It retains other VPN-provided routes, needs administrator rights to change networking, and must remain running for restoration. It is not an official Palo Alto Networks product and cannot override every corporate policy. No credentials or telemetry are collected.

## License

MIT. This independent project is not affiliated with or endorsed by Palo Alto Networks.
