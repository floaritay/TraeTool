# Services/Switch — 客户端登录态切换

模块：

| 模块 | 职责 |
|---|---|
| `CarrierDefs` | Trae 桌面客户端（`%APPDATA%\Trae CN`）的 15 项登录态载体白名单 + exe/进程名探测 |
| `VaultStore` | 按账号 `Id`/`Uid` 字节级备份/恢复载体到 `%APPDATA%\TraeTool\vault\`，SHA-256 清单 |
| `Switcher` | 冷切换编排：杀进程 → 快照回滚区 → 写回 vault → 拉起 → 守护判定 |
| `ClientControl` | 优雅关闭（8~12s）→ 强杀 → 仍存活则中止 |
| `AuthProbe` | 轮询 `storage.json` 凭据键（`iCubeAuthInfo://usertag` + `icube.cloudide`）判定是否进入目标账号 |

入口在「客户端切换」页（建档/切换至此/校验/删除）。账号模型（`Models/Account.cs`）刻意不含客户端路径字段，切换子系统不得向其添加。
