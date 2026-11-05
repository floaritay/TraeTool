# TraeTool

Trae（trae.cn）多账号效率工具 —— WPF + WebView2，`net8.0-windows`，**数据全本地**。

多账号每日签到、数据看板、用量统计，以及 Trae 桌面客户端登录态一键切换。与 ByteDance / Trae 官方无关，请遵守 Trae 服务条款，风险自负。

## 功能

| 页面 | 能力 |
|---|---|
| **仪表盘**（默认首页） | 四项统计卡、连签天数、可翻月日历、近 14 天趋势图、账号切换、快捷签到 |
| **账号管理** | WebView2 扫码/验证码登录、分组筛选、参与签到开关、Token 查看复制、从客户端导入凭据、刷新状态（含学生认证徽标） |
| **每日签到** | 一键批量签到、每日定时自动签到（迟到补跑、防重复）、签到记录筛选、运行日志 |
| **客户端切换** | 桌面客户端登录态 vault 备份/回写，软件内切换后打开客户端即另一账号；建档向导、档案校验 |
| **用量统计** | 按会话的积分/tokens 消耗、缓存命中率、模型分布、临期套餐提醒 |
| **设置** | 主题（浅/深色）、开机自启、托盘、备份导入导出、设备标识重置、检查更新、日志查看、EULA |

其他：单实例运行、系统托盘常驻、exe 图标、自动更新（GitHub Releases / version.json 双源）。

## 构建与运行

```powershell
git clone https://github.com/floaritay/traetool.git
cd traetool
dotnet build TraeTool.sln -c Release
.\TraeTool\bin\Release\net8.0-windows\TraeTool.exe
```

依赖：.NET 8 SDK、Windows 10/11（WebView2 运行时随系统）。

启动参数：`--page=dashboard|accounts|checkin|switch|usage|settings` 指定默认页；`--wizard-preview` 向导演示；`--selftest-update=<feed>` / `--selftest-import` 内部自检。

## 数据位置

全部运行数据落在本机，**不含任何上传**：

```
%APPDATA%\TraeTool\
├── config.json        # 配置（Token/Session/DeviceId 经 DPAPI 当前用户加密）
├── data\checkin.db    # SQLite 签到记录与用量缓存
├── logs\              # traetool-yyyyMMdd.log
├── vault\             # 客户端登录态档案（按账号 Id 键控）
└── switch\            # 切换状态与回滚快照
%LOCALAPPDATA%\TraeTool\WebView\account_<id>\   # 每账号独立 WebView2 目录
```

请勿将 `config.json`、`vault\` 等目录内容提交到公开仓库。

## 实现要点

- **登录**：WebView2 打开 `www.trae.cn/dashboard#usage`，轮询 `localStorage['Cloud-IDE-Token']`（JWT）+ Cookie `X-Cloudide-Session`；JWT 失效经 `POST /cloudide/api/v3/common/GetUserToken` 静默换新
- **签到**：`POST /trae/api/v2/ug/checkin_credits/claim`，头部 `Cloud-IDE-JWT` + `X-User-Region: cn` + `x-device-id`
- **风控**：`x-device-id` 为 16 位数字且每账号独立（服务端按设备号每日去重）；9074 → 换设备号重试一次
- **客户端切换**：固定 15 项载体白名单备份/回写 `%APPDATA%\Trae CN`；恢复前删 `code.lock` 与 stale WAL/SHM；成功判定轮询凭据键而非时间戳；切换成功后回写 live `storage.json`（refresh 轮换防 401）
- **配置**：新增密文字段必须同时登记 `ConfigStore.SecretFields`（漏一侧会"重启就失效"）
- **主题**：`Themes/Light|Dark.xaml` 双字典同键集，应用主题时先移除旧字典再追加（WPF 合并字典越靠后优先级越高）

## 仓库结构

```
TraeTool.sln
TraeTool/                 # 主工程（Views / ViewModels / Services / Themes / Resources）
tools/                    # 开发辅助脚本（图标生成、窗口截图、结构勘察等）
docs/UI设计需求.txt        # 信息架构与模块设计说明
docs/prototype/           # 布局与交互基线原型（双击 index.html 离线演示）
```

## 参考与致谢

实现思路调研自以下开源项目，**代码均为按公开行为自行编写，未复制其源文件**：

- [star620/TraeTools](https://github.com/star620/TraeTools)（GPL-3.0）
- [smart-open/TraeWorkAssistant](https://github.com/smart-open/TraeWorkAssistant)（MIT，署名 朱天伟）

界面设计基于本仓库 `docs/` 内的设计说明与原型完成。

## 许可证

[MIT](LICENSE)
