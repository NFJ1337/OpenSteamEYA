# SteamEYA

[中文](README.md) | [English](README_EN.md)

用 EYA 令牌登录和管理 Steam 账号的 Windows 桌面工具。

[![][latest-version-shield]][latest-version-link]
[![][github-downloads-shield]][github-downloads-link]
[![][github-stars-shield]][github-stars-link]
[![][github-license-shield]][github-license-link]

SteamEYA 用 EYA 令牌（一种 Steam 登录凭据）代替账号密码登录，不需要手动输入密码或处理令牌验证器。除了上号，它还能查询账号的优先分、CS2 等级和冷却状态，管理用过的账号，并清理创意工坊订阅。

**👉 [前往 Releases 下载最新版本](https://github.com/NFJ1337/OpenSteamEYA/releases)**

## 界面截图

<p align="center">
  <img src="docs/screenshots/One.png" alt="SteamEYA 界面截图 1"><br><br>
  <img src="docs/screenshots/Two.png" alt="SteamEYA 界面截图 2"><br><br>
  <img src="docs/screenshots/Three.png" alt="SteamEYA 界面截图 3">
</p>

<h1>南方见 重制版</h1>

本重制版仅对界面 UI 进行全面重构，同时再原有基础上增加若干自定义实用小功能。

此 UI重构 99.99%采用DeepSeek AI编写，介意请勿使用。

如果你在使用过程中有 Bug 反馈、优化建议、新功能 或 源码 需求，欢迎添加本人 QQ：261064327。

> [!CAUTION]
> **重要声明：**
>
> 如果您介意因使此软件黑号登录错误或售后问题 请勿使用！请勿使用！请勿使用！
>
> 以及本软件永久免费开源。若有人假借本软件名义向你收取费用，均非作者行为，请提高警惕，谨防诈骗。

如需原版程序，请访问下方 GitHub 开源项目地址获取。

> **原版项目地址**
>
> <https://github.com/hvh-software/OpenSteamEYA/>
>
> [![打开 GitHub](https://img.shields.io/badge/%E6%89%93%E5%BC%80-GitHub-181717?style=for-the-badge&logo=github&logoColor=white)](https://github.com/hvh-software/OpenSteamEYA/)

## 安装

1. 在 [Releases](https://github.com/NFJ1337/OpenSteamEYA/releases) 下载最新的 `SteamEYANFJ-<版本>-win-x64-setup.exe`。
2. 双击安装包，按安装向导完成安装。
3. 从开始菜单或桌面快捷方式启动 SteamEYA。

环境要求：Windows 10 1809（Build 17763）及以上，并已安装 Steam 客户端。无需额外安装任何运行时。

## 常见问题

**安装器启动失败怎么办？**

请先检查是否被安全软件拦截，或前往 [Releases](https://github.com/NFJ1337/OpenSteamEYA/releases) 重新下载最新安装包后重试。

**EYA 令牌是什么？**

一种以 `eyAi` 开头的 Steam 登录凭据（JWT）。手动模式直接粘贴它即可登录，无需账号密码。

**为什么上号后没切换账号？**

上号前需要先完全退出正在运行的 Steam。如果 Steam 以管理员权限运行而本程序没有，会无法结束它，程序会提示你手动退出后重试。

## 从源码构建

需要 .NET 10 SDK 和 Visual Studio 的 C++ 工具链。

```bash
git clone https://github.com/NFJ1337/OpenSteamEYA.git
cd OpenSteamEYA
dotnet build SteamEyaWinUI/SteamEyaWinUI.csproj -c Release
```

构建安装包（需先安装 Inno Setup 6）：

```powershell
./scripts/build-installer.ps1 -Version 1.1.0
```

## 🤝 参与贡献

如果您对这个项目感兴趣，欢迎参与贡献，也欢迎 "Star" 支持一下 ^_^ <br>
以下为提 PR 并合并的小伙伴，在此感谢项目中所有的贡献者。

<a href="https://github.com/NFJ1337/OpenSteamEYA/graphs/contributors" target="_blank">
  <table>
    <tr>
      <th colspan="2">
        <br><img src="https://contrib.rocks/image?repo=NFJ1337/OpenSteamEYA"><br><br>
      </th>
    </tr>
  </table>
</a>

1. Fork 本项目
2. 创建新分支：`git checkout -b feature/amazing-feature`
3. 提交更改：`git commit -m "Add amazing feature"`
4. 推送分支：`git push origin feature/amazing-feature`
5. 发起 Pull Request，等待合并

## 许可证

本项目基于 [MIT 许可证](LICENSE) 开源。

<!-- LINK GROUP -->

[latest-version-shield]: https://img.shields.io/github/v/release/hvh-software/OpenSteamEYA?style=flat-square&label=latest%20version&labelColor=black
[latest-version-link]: https://github.com/NFJ1337/OpenSteamEYA/releases
[github-downloads-shield]: https://img.shields.io/github/downloads/hvh-software/OpenSteamEYA/total?style=flat-square&logo=github&label=downloads&labelColor=black
[github-downloads-link]: https://github.com/NFJ1337/OpenSteamEYA/releases
[github-stars-shield]: https://img.shields.io/github/stars/hvh-software/OpenSteamEYA?style=flat-square&logo=github&labelColor=black
[github-stars-link]: https://github.com/NFJ1337/OpenSteamEYA/stargazers
[github-license-shield]: https://img.shields.io/github/license/hvh-software/OpenSteamEYA?style=flat-square&logo=github&labelColor=black
[github-license-link]: https://github.com/NFJ1337/OpenSteamEYA/blob/main/LICENSE
