# QQNT LiteLoader 一键安装器 —— 使用说明

单文件 exe，双击即可。适用于任何已安装 QQNT（QQ 9.9.x 新版架构）的 Windows 10/11 电脑，**不需要装 Node、不需要 git、不需要联网**。

## 一、怎么用

1. 把 `QQNT LiteLoader 一键安装器.exe` 拷到目标电脑（U 盘、网盘随便）。
2. **双击运行**，Windows 会弹 UAC，点“是”（需要管理员权限才能写 QQ 安装目录）。
3. 界面上会自动显示：QQ 安装目录、版本号、注入状态、宿主状态、插件数量与插件列表。
4. 点 **“一键安装”**。安装器会自动：
   - 结束正在运行的 QQ（单实例机制，不结束新实例会静默退出）
   - 把 LiteLoaderQQNT 1.4.1（已打 `parentPath` 兼容补丁）释放到 `QQNT\LiteLoaderQQNT`
   - 重建并写入注入点 `versions\<版本>\resources\app.asar`（**不动原生 `resources\app`**）
   - 把插件总开关 `enable_plugins` 强制设为 `true`
5. 点 **“启动 QQ”**，登录后插件就生效了。

## 二、界面上的其他按钮

| 按钮 | 作用 |
| --- | --- |
| 一键安装 | 装宿主 + 写注入点 + 开总开关（可反复点，幂等） |
| 恢复原生 QQ | 删除注入点 `app.asar`（若备份是第三方注入文件则跳过还原），QQ 回到未改状态 |
| 插件总开关 | 全局启用/禁用所有插件，写 `config.json` 的 `enable_plugins` |
| 启用选中 / 禁用选中 | 对列表里选中的插件批量启用/禁用（写 `disabled_plugins`），重启 QQ 生效 |
| 打开插件目录 | 打开 `文档\LiteloaderQQNT\plugins`，拖插件文件夹进去即可 |
| 查看日志 | 打开安装器日志（`%TEMP%\qqnt-litelloader-installer.log`） |
| 手动指定 QQ… | 便携版/非默认路径时手动选 QQ 根目录（含 `QQ.exe` 与 `versions`） |

## 三、技术要点（出问题时看这里）

- **为什么用 app.asar 注入而不是改 `resources\app`**：新版本 QQ 启动时会校验/覆盖原生 `app` 目录，而 `resources\app.asar` 优先级更高且不会被覆盖。asar 里的 `package.json` 把 `main` 指向 `app_launcher/index.js`，先 `require` LiteLoader 宿主，失败再回退到 QQ 原生入口，所以**宿主坏了也不会导致 QQ 打不开**。
- **asar 偏移必须相对数据区**（第一个文件 `"0"`）。用绝对 offset 生成的 asar 结构自洽、`@electron/asar` 也能解包，但 Electron 会拒绝加载，QQ 静默退回原生 `app`——这是最容易踩的坑。
- **插件“检测不到”的两个经典原因**：
  1. 宿主 `store.js` 用了 `dirent.path`，新版 Electron 只有 `dirent.parentPath` → 宿主加载抛错。安装器内嵌的宿主已改好兼容写法。
  2. `config.json` 里 `enable_plugins` 为 `false`（之前关过总开关）→ 宿主正常加载但插件全不显示。安装时会强制置 true。
- **插件目录被主题包覆盖**：把 MSpring 之类的“QwQNT 版”整包解压到宿主根目录，会覆盖宿主 `package.json`/`manifest.json`，导致 QQ 报 `qwqnt is not defined`。所以宿主放在 QQ 安装目录下的受保护子目录，不要往里面塞插件；插件一律放 `文档\LiteloaderQQNT\plugins`。

## 四、命令行模式（可选，无界面自检）

```
"QQNT LiteLoader 一键安装器.exe" --status  C:\temp\status.json
"QQNT LiteLoader 一键安装器.exe" --install C:\temp\result.json
"QQNT LiteLoader 一键安装器.exe" --restore C:\temp\result.json
```

输出 JSON 含：`ok`、`qqRoot`、`version`、`asar`（未注入/已注入）、`host`、`pluginsEnabled`、`plugins[]`。适合批量给多台机器部署、或远程排障。

## 五、恢复成原生 QQ

点界面“恢复原生 QQ”，或 `--restore`。会删除 `app.asar` 并把 `app.asar.bak*` 留着（不删，出问题可人工还原）。宿主目录会保留，不影响原生启动。
