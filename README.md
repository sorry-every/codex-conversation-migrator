# Codex 对话迁移工具

一款面向 Windows 的 Codex 本地对话迁移工具。它可以读取当前电脑的会话列表，按会话选择导出，并在另一台电脑恢复完整对话历史、分页历史链、标题、置顶状态以及对话中引用的附件和文档。

## 功能

- 图形界面读取 `.codex` 中的会话列表。
- 按会话 ID 去重，置顶会话显示在最前面。
- 更新时间取 JSONL 历史文件中最后一条实际事件的时间。
- 勾选一个、多个或全部会话导出。
- 导出一个会话的全部分页历史段，修复 `missing source rollout` 类型的恢复问题。
- 可选打包工作目录、附件、文档和生成文件，并在恢复后自动改写旧电脑路径。
- 恢复时显示实时阶段状态，恢复大迁移包时窗口保持响应。
- 恢复前备份目标端的索引和状态数据库。
- 使用 Codex 官方 App Server 重建左侧列表并同步原始标题。
- 对迁移包中的会话和关联文件执行 SHA-256 校验。

## 下载使用

建议直接下载 [Windows x64 发布包](dist/Codex会话选择迁移工具-v3.4-Windows.zip)，解压后运行：

`Codex会话选择迁移工具-v3.4.exe`

旧电脑：

1. 启动工具并勾选需要迁移的会话。
2. 点击“导出勾选的会话…”。
3. 选择是否同时打包关联文件。
4. 将生成的 `.codexpack.zip` 复制到新电脑。

新电脑：

1. 安装并登录 Codex，然后完全退出 Codex。
2. 启动工具，点击“从迁移包恢复…”。
3. 选择迁移包并等待进度窗口完成。
4. 重新启动 Codex，检查左侧会话列表。

恢复期间可以看到当前阶段，例如“恢复关联文件”“恢复会话历史链”“更新会话索引”和“调用 Codex App Server 重建列表和标题”。

## 安全与隐私

- 工具不会导出 `auth.json`、账号令牌、设备 ID、插件或全局设置。
- 关联文件模式会排除 `.git`、`node_modules`、缓存和常见密钥文件。
- 迁移包可能包含完整对话和私人文档，只应通过可信渠道传输。
- 恢复前会在目标端 `.codex\migration-backups` 创建备份。
- 只恢复可信来源生成的迁移包。

## 限制

- 当前支持 Windows 到 Windows，目标电脑需要安装 Codex。
- Codex 没有公开稳定的本地迁移格式，未来版本变化可能需要更新工具。
- 已删除、仅存云端或无法读取的附件不能从本地恢复。
- 工具不会迁移登录状态；目标电脑需要单独登录 Codex。

## 从源码构建

构建环境需要 Windows、.NET Framework 4.x 编译器和仓库中的 `third-party/sqlite3.dll`。在 PowerShell 中运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

生成文件位于 `dist\Codex会话选择迁移工具-v3.4.exe`。构建脚本使用 Windows 自带的 `csc.exe`，不需要 Python 或 Node.js。

## 版本

当前发布版本：**v3.4**。完整变更记录见 [docs/版本说明.txt](docs/版本说明.txt)，操作步骤见 [docs/使用说明.txt](docs/使用说明.txt)。

## 许可证

本项目使用 MIT License，详见 [LICENSE](LICENSE)。仓库中的 SQLite 动态库来自 SQLite 项目，详见 [third-party/THIRD-PARTY-NOTICES.md](third-party/THIRD-PARTY-NOTICES.md)。
