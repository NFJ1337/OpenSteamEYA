# OpenSteamEYA 项目约定（本地）

> 本文件只记录用户明确要求的协作约定，不包含任何托管注入区块（握手/路由/完整性标记由用户级配置负责，勿在此重复）。

## 触发词：裸回复「1」= 只给免安装版路径（2026-09-16 用户重新定义）
- 用户消息就是单个数字 `1`（前后无其它文字）时：**不打包安装包、不动版本号**，
  只把免安装版刷新到最新（跑 `scripts\run-portable.ps1`），然后回报它的绝对路径：
  `artifacts\publish\win-x64\SteamEyaWinUI.exe`。
- 只回报这一个可点击链接；不罗列体积/哈希/目录清单，也不自动打开或运行它。

## 触发词：裸回复「2」= 打包 + 版本号 +1 + 累积更新日志 + 发布源码（2026-09-16 用户重新定义）
- 用户消息就是单个数字 `2`（前后无其它文字）时，按顺序做完这四步：
  1. **版本号 +1**：`scripts\bump-version.ps1`（末位 0~9 递增、满 10 进位：1.6.3 → 1.6.4），
     新版本号会同时写进 csproj 与安装包信息，也作为发布正文的版本号。
  2. **打包**：`scripts\build-installer.ps1 -Version <新版本>`。
  3. **精简更新日志并发布到 GitHub release**：把最近一轮改动浓缩成 3~6 条中文说明（用户视角、不写代码细节），
     交给 `scripts\publish-release.ps1 -Version <新版本> -Commit -NotesFile <说明文件>`。
     正文格式照旧：首行 `SteamEYA v<新版本>`，说明逐条排在它下面；**新增块放在正文最上面，
     绝不覆盖以前的版本日志**（脚本已按块累积；同版本重复发布只替换自己那一块）。
  4. **发布源码到 GitHub**：`scripts\publish-source.ps1 -Version <新版本>`（打源码 zip → 提交 →
     `git push origin main` → 移动 tag「正式exe」→ 上传源码包）。
- 回报：安装包链接 + 一行确认（已替换 release 附件 / 更新日志已新增未覆盖 / 源码已推送）。
  只有失败或需要用户决策时才多解释几句。

## 版本号与打包脚本（用法备查；何时调用由用户当次明确说明）
- `scripts\bump-version.ps1`：同步 `SteamEyaWinUI.csproj`（Version/FileVersion/AssemblyVersion/InformationalVersion）、
  `scripts\build-installer.ps1` 的默认 `$Version`、`build\installer\SteamEYA.iss` 的默认 AppVersion，输出 `NewVersion=x.y.z`；`-DryRun` 只算不写。
- `scripts\build-installer.ps1 -Version <x.y.z>`：Native AOT + Inno Setup，产物在 `artifacts\`。
- `scripts\publish-release.ps1 -Version <x.y.z> [-Commit] [-Notes …|-NotesFile …]`：覆盖发布页安装包与 latest.json，
  并更新发布日志（正文为**新增累积**，不再覆盖历史版本）；`-Commit` 会先本地提交（不推送）。
- `scripts\publish-source.ps1 -Version <x.y.z>`：打源码 zip → 提交 → `git push origin main` → 移动 tag「正式exe」→ 上传源码包。
- 以上都只在用户明确要求时执行；版本号要不要 +1 也由用户当次说明决定。

## 符号留档（用户明确要求）
- 每次打包 `build-installer.ps1` 会自动调用 `scripts\archive-symbols.ps1`，把本次 Native AOT 构建的
  `SteamEyaWinUI.exe` + `SteamEyaWinUI.pdb` 归档到 `artifacts\symbols\<版本>\`（含 manifest.json：PE 时间戳与 sha256）。
- 目的：崩溃/卡死 dump 只要模块 PE 时间戳对得上，就能用同目录的 exe+pdb 还原符号。
- 还原命令（AOT 堆栈可直接看到方法名，用法示例见 manifest.json 的 usage 字段）：
  `DumpScan <dump 路径> D:\GithubProgram\OpenSteamEYA-main\artifacts\symbols\<版本> --frames 60`
  （DumpScan 工具在本次会话的 viz 目录 `tools\DumpScan`，不在仓库内）
- 归档失败只告警、不影响出包；`artifacts\symbols` 超过 2 GB 会提醒清理旧版本目录（不自动删）。

## 打包交付方式（用户明确要求）
- 打包成功后，**只回传一个东西**：安装包的**单个可点击链接**（Markdown 链接，绝对路径）。
- **不要自动打开 / 点击查看**：不要调用「在面板中打开文件」（open_in_codex 之类）的动作，也不要替用户打开窗口或资源管理器——只给链接，用户自己决定何时打开。
  - 例：`[SteamEYA-1.1.0-win-x64-setup.exe](D:\GithubProgram\OpenSteamEYA-main\artifacts\SteamEYA-1.1.0-win-x64-setup.exe)`
- **不要再罗列**目录清单、体积、文件数、哈希、发布目录明细等。
- 只有以下情况才额外说明：打包**失败**、需要用户决策（版本号、AOT 回退、是否覆盖已有产物）、或产物与用户最近一次改动不一致。

## 每轮改动后更新免安装 exe，只回报路径（用户明确要求）
- 每轮代码改动完成（编译 0 警告 0 错误、冒烟无 `crash.log`）之后，执行 `scripts\run-portable.ps1`：
  把当前代码 publish 成免安装版（Native AOT Release → `artifacts\publish\win-x64`，刷新其中的 `版本.txt`）。
- **不要自动打开它**（用户明确要求：「不用自动打开免安装版本，告诉我路径就行」）：
  脚本默认只发布，完成后把 `artifacts\publish\win-x64\SteamEyaWinUI.exe` 的绝对路径回报给用户，由用户自己决定何时运行。
- 只有本机需要立刻验证时才加 `-Launch`（会先温和结束旧实例再启动）；`-NoPublish` 只回报现有产物路径；
  `-Version <x.y.z>` 覆盖版本号（默认读 csproj）。
- 这与触发词 `1`/`2`/`3` 无关：免安装 exe 只供本机验证，**不做版本号自增、不上传、不发布**。

## 改动范围
- 不做用户未要求的重构；**既有代码路径保持原样**，新功能集中放在用户指定的模块内（例如「账号核验」模块）。
- 需要动到共享文件时，优先采用「新增文件 + 一行挂钩」的方式，并在完成后说明改了什么。
- 交付前必须：编译 0 警告 0 错误、启动冒烟无 `crash.log`、临时自检代码全部删除。

## 排查提示
- 界面文字显示成键名/英文（如 `Login_Btn_ClearAll`）：通常是语言包缺键（`Loc.T` 取不到会回落成键名）。修法：比对 `SteamEyaWinUI\Languages\*.json` 三份键集，补齐缺失键并保持三语一致。
- 打包命令：`scripts\build-installer.ps1 -Version <x.y.z>`（Native AOT + Inno Setup，产物在 `artifacts\`）。