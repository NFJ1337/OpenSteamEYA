# OpenSteamEYA 项目约定（本地）

> 本文件只记录用户明确要求的协作约定，不包含任何托管注入区块（握手/路由/完整性标记由用户级配置负责，勿在此重复）。

## 触发词：裸回复「1」= 打包（用户明确要求）
- 每轮调整结束后，**检查用户这条消息是否就是单个数字 `1`**（前后无其它文字、无标点）。
- 是 `1` → **先把版本号 +1**（见下节），再执行打包（`scripts\build-installer.ps1 -Version <新版本>`），完成后按下面的交付方式只回一个链接。
- **其它任何内容都不算打包指令**，包括 `1.`、「第1个」、「选1」、带任何附加文字的 `1`、以及我之前给出的编号选项里的 `1`——一律不触发打包。
## 版本号自增规则（用户明确要求）
- **只有**当用户那条消息就是单个 `1`（触发打包的那一条）时，才在打包前把版本号 **+1**；
  其它任何说法的「打包 / 重新打包」（例如带文字的「打包」「重新打包」「打包 1.3.0」）都**不自动 +1**，除非用户明确给了版本号。
- 自增规则：末位 0～9 递增，满 10 进位，依次类推。
  - 例：`1.2.0 → 1.2.1`、`1.1.9 → 1.2.0`、`1.2.9 → 1.3.0`、`1.9.9 → 2.0.0`。
- 执行`scripts\bump-version.ps1`：同步改三处版本（`SteamEyaWinUI.csproj` 的 Version/FileVersion/AssemblyVersion/InformationalVersion、`scripts\build-installer.ps1` 的默认 `$Version`、`build\installer\SteamEYA.iss` 的默认 AppVersion），输出 `NewVersion=x.y.z`。
- 然后打包：`scripts\build-installer.ps1 -Version <新版本>`；一步到位也可用 `-Bump`。
- 只算不写（不动文件）：`scripts\bump-version.ps1 -DryRun`。
## 触发词：裸回复「2」= 打包 + 覆盖发布页附件（用户明确要求）
- 用户消息就是单个数字 `2`（前后无其它文字）→ 与 `1` 一样先按版本号自增规则 **+1** 打包，
  然后用 `scripts\publish-release.ps1 -Version <新版本>` 把**新安装包与 latest.json 上传到 release（tag `正式exe`）**，
  并删掉 release 上旧的 `SteamEYA-*-win-x64-setup.exe`，等效于在发布页「重新保存文件」。
- 凭据：脚本自动取 `GH_TOKEN`/`GITHUB_TOKEN`，否则读本机 Git 凭据管理器（与 git push 同一份凭据，实测登录用户 NFJ1337）。
- 该触发词的回复：安装包链接 + **一行**发布结果确认（如「已替换 release 正式exe 下的安装包」）；不罗列体积/哈希/清单。
- **发布前先本地提交**（用户明确要求：「2 时版本号也提交」）：`publish-release.ps1` 加 `-Commit` 即会在上传前
  `git add -A` + `git commit -m "SteamEYA <版本>：版本号与本次改动（本地提交，未推送）"`；**只提交、不推送**，推送仍归 `3`。
- **版本号自增规则与 `1` 完全相同**：每按一次 `2` 也先把版本号 +1（末位 0～9，满 10 进位），
  例：`1.2.5 → 1.2.6 → 1.2.7 → 1.2.8 → 1.2.9 → 1.3.0`；不会因为「只是替换附件」而跳过自增。
- 任何其它内容（包括 `2.`、带文字的 2）都不触发；`1` 只打包、不上传。
## 触发词：裸回复「3」= 打包 + 覆盖发布页附件 + 源码同步到 GitHub（用户明确要求）
- 用户消息就是单个数字 `3`（前后无其它文字）→ **先做 `2` 的全部动作**：版本号 +1 → 打包 →
  `scripts\publish-release.ps1 -Version <新版本>`（替换 release 上的安装包 + latest.json）；
- 再执行 `scripts\publish-source.ps1 -Version <新版本>`：
  1) 把源码打包成 `artifacts\SteamEYA-<版本>-source.zip`（排除 .git/.vs/artifacts/bin/obj），
  2) `git add -A` → 有改动就 commit → `git push origin main`（把源码改动同步到 GitHub），
  3) 把源码 zip 上传到 release（同名覆盖）。
- 版本号自增规则与 `1`/`2` 相同（末位 0～9，满 10 进位）。
- 该触发词的回复：安装包链接 + **一行**确认（例：已替换 release 附件、已推送源码 n 条改动、源码包已上传）。
- 凭据同 `2`：`GH_TOKEN`/`GITHUB_TOKEN` → 本机 Git 凭据管理器（git push 与 gh 用同一份）。
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

## 改动范围
- 不做用户未要求的重构；**既有代码路径保持原样**，新功能集中放在用户指定的模块内（例如「账号核验」模块）。
- 需要动到共享文件时，优先采用「新增文件 + 一行挂钩」的方式，并在完成后说明改了什么。
- 交付前必须：编译 0 警告 0 错误、启动冒烟无 `crash.log`、临时自检代码全部删除。

## 排查提示
- 界面文字显示成键名/英文（如 `Login_Btn_ClearAll`）：通常是语言包缺键（`Loc.T` 取不到会回落成键名）。修法：比对 `SteamEyaWinUI\Languages\*.json` 三份键集，补齐缺失键并保持三语一致。
- 打包命令：`scripts\build-installer.ps1 -Version <x.y.z>`（Native AOT + Inno Setup，产物在 `artifacts\`）。