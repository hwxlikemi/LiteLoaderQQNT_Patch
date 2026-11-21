# -*- coding: utf-8 -*-
"""QQNT LiteLoader 一键安装器

功能：
1. 自动定位本机 QQNT 安装目录与当前版本目录
2. 安装 LiteLoaderQQNT 宿主（内置 1.4.1 + dirent.path 兼容补丁）到 <QQ>/LiteLoaderQQNT
3. 生成并部署注入式 resources\\app.asar（不动原生 resources\\app，QQ 完整性校验不受影响）
4. 查看/启用/禁用 LiteLoader 插件，一键恢复 QQ 原生启动

用法：直接双击运行；需要管理员权限（会自动请求提权）。
"""

import os
import sys
import json
import struct
import zipfile
import shutil
import subprocess
import tempfile
import time
import traceback
import winreg

APP_TITLE = "QQNT LiteLoader 一键安装器"
HOST_DIR_NAME = "LiteLoaderQQNT"
LOG_PATH = os.path.join(tempfile.gettempdir(), "qqnt-litelloader-installer.log")


def log_line(text):
    stamp = time.strftime("%Y-%m-%d %H:%M:%S")
    try:
        with open(LOG_PATH, "a", encoding="utf-8") as fp:
            fp.write("[%s] %s\n" % (stamp, text))
    except Exception:
        pass


def resource_path(name):
    base = getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base, "assets", name)


def read_text(name):
    with open(resource_path(name), "r", encoding="utf-8") as fp:
        return fp.read()


# ---------------------------------------------------------------- QQ 定位

def _reg_install_dirs():
    result = []
    keys = [
        r"SOFTWARE\Tencent\QQNT",
        r"SOFTWARE\Tencent\QQ",
        r"SOFTWARE\WOW6432Node\Tencent\QQNT",
        r"SOFTWARE\WOW6432Node\Tencent\QQ",
        r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QQ",
        r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\QQ",
    ]
    values = ["InstallDir", "InstallPath", "install_path", "Path", "DisplayIcon", "UninstallString"]
    for root in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        for key in keys:
            try:
                with winreg.OpenKey(root, key) as hk:
                    for name in values:
                        try:
                            val, _ = winreg.QueryValueEx(hk, name)
                        except OSError:
                            continue
                        if not isinstance(val, str):
                            continue
                        val = val.strip().strip('"')
                        if val.lower().endswith(".exe"):
                            val = os.path.dirname(val)
                        if val:
                            result.append(val)
            except OSError:
                continue
    return result


def _candidate_dirs():
    dirs = list(_reg_install_dirs())
    roots = [os.environ.get("ProgramFiles"), os.environ.get("ProgramFiles(x86)"),
             os.environ.get("ProgramW6432")]
    for root in roots:
        if not root:
            continue
        dirs += [
            os.path.join(root, "Tencent", "QQNT"),
            os.path.join(root, "Tencent", "QQ"),
            os.path.join(root, "QQNT"),
        ]
    for drive in "CDEFGAB":
        d = drive + ":\\"
        if not os.path.exists(d):
            continue
        dirs += [
            os.path.join(d, "Program Files", "Tencent", "QQNT"),
            os.path.join(d, "Program Files (x86)", "Tencent", "QQNT"),
            os.path.join(d, "Tencent", "QQNT"),
            os.path.join(d, "App", "QQNT"),
            os.path.join(d, "QQNT"),
        ]
    seen, out = set(), []
    for d in dirs:
        if not d:
            continue
        key = os.path.normcase(os.path.normpath(d))
        if key in seen:
            continue
        seen.add(key)
        out.append(os.path.normpath(d))
    return out


def find_qq_dir():
    for d in _candidate_dirs():
        exe = os.path.join(d, "QQ.exe")
        if os.path.isfile(exe) and os.path.isdir(os.path.join(d, "versions")):
            return d
    return None


def list_version_dirs(qq_dir):
    vroot = os.path.join(qq_dir, "versions")
    if not os.path.isdir(vroot):
        return []
    items = []
    for name in os.listdir(vroot):
        path = os.path.join(vroot, name)
        if os.path.isdir(path) and os.path.isdir(os.path.join(path, "resources", "app")):
            items.append(name)
    return items


def pick_version_dir(qq_dir):
    versions = list_version_dirs(qq_dir)
    if not versions:
        return None
    cur = None
    cfg = os.path.join(qq_dir, "versions", "config.json")
    if os.path.isfile(cfg):
        try:
            with open(cfg, "r", encoding="utf-8") as fp:
                cur = json.load(fp).get("curVersion")
        except Exception:
            cur = None
    if cur and cur in versions:
        return cur
    def sort_key(name):
        main = name.split("-")[0].split(".")
        nums = []
        for part in main:
            nums.append(int(part) if part.isdigit() else 0)
        return nums
    return sorted(versions, key=sort_key)[-1]


def qq_paths(qq_dir, version):
    resources = os.path.join(qq_dir, "versions", version, "resources")
    return {
        "qq": qq_dir,
        "version": version,
        "resources": resources,
        "app_dir": os.path.join(resources, "app"),
        "app_asar": os.path.join(resources, "app.asar"),
        "host": os.path.join(qq_dir, HOST_DIR_NAME),
    }


def qq_running():
    """用 Toolhelp 快照枚举进程，避免 tasklist 中文输出/编码问题"""
    import ctypes
    from ctypes import wintypes

    TH32CS_SNAPPROCESS = 0x00000002

    class PROCESSENTRY32(ctypes.Structure):
        _fields_ = [
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ProcessID", wintypes.DWORD),
            ("th32DefaultHeapID", ctypes.c_void_p),
            ("th32ModuleID", wintypes.DWORD),
            ("cntThreads", wintypes.DWORD),
            ("th32ParentProcessID", wintypes.DWORD),
            ("pcPriClassBase", ctypes.c_long),
            ("dwFlags", wintypes.DWORD),
            ("szExeFile", ctypes.c_char * 260),
        ]

    try:
        kernel32 = ctypes.windll.kernel32
        snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
        if snap == -1:
            return False
        entry = PROCESSENTRY32()
        entry.dwSize = ctypes.sizeof(PROCESSENTRY32)
        found = False
        if kernel32.Process32First(snap, ctypes.byref(entry)):
            while True:
                if entry.szExeFile.decode("mbcs", "ignore").lower() in ("qq.exe", "qqex.exe"):
                    found = True
                    break
                if not kernel32.Process32Next(snap, ctypes.byref(entry)):
                    break
        kernel32.CloseHandle(snap)
        return found
    except Exception:
        return False


def kill_qq():
    for image in ("QQ.exe", "QQEX.exe"):
        subprocess.run(["taskkill", "/F", "/IM", image],
                       capture_output=True, creationflags=0x08000000)


# ---------------------------------------------------------------- 状态

def injection_state(paths):
    app_asar = paths["app_asar"]
    if not os.path.isfile(app_asar):
        return "未注入"
    try:
        with open(app_asar, "rb") as fp:
            head = fp.read(16 * 1024)
        return "已注入" if b"[LQ-Inject]" in head or b"lq-build.json" in head else "未知 app.asar"
    except Exception:
        return "未知 app.asar"


def host_version(host):
    pkg = os.path.join(host, "package.json")
    if not os.path.isfile(pkg):
        return None
    try:
        with open(pkg, "r", encoding="utf-8") as fp:
            data = json.load(fp)
        if data.get("name") != "liteloader-qqnt":
            return "目录被非宿主内容占用"
        return data.get("version")
    except Exception:
        return None


def host_patched(host):
    store = os.path.join(host, "src", "main", "store.js")
    if not os.path.isfile(store):
        return False
    with open(store, "r", encoding="utf-8", errors="ignore") as fp:
        return "parentPath" in fp.read()


# ---------------------------------------------------------------- asar 生成

def build_asar(files):
    """files: [(name, bytes)] -> asar 二进制内容"""
    ordered = list(files)
    header_json = ""

    def make_nodes(offsets):
        nodes = {}
        for (name, data), off in zip(ordered, offsets):
            parts = name.split("/")
            node = nodes
            for part in parts[:-1]:
                node = node.setdefault(part, {"files": {}})["files"]
            node[parts[-1]] = {"size": len(data), "offset": str(off)}
        return {"files": nodes}

    # asar 的 offset 是相对于数据区起点的偏移（第一个文件 offset = 0）
    offsets = []
    running = 0
    for _, data in ordered:
        offsets.append(running)
        running += len(data)
    header_json = json.dumps(make_nodes(offsets), separators=(",", ":"),
                             ensure_ascii=False).encode("utf-8")
    payload = b"".join(data for _, data in ordered)
    head = (struct.pack("<I", 4) + struct.pack("<I", len(header_json) + 8)
            + struct.pack("<I", len(header_json) + 4) + struct.pack("<I", len(header_json)))
    return head + header_json + payload


def make_injection_asar(paths):
    app_pkg_path = os.path.join(paths["app_dir"], "package.json")
    with open(app_pkg_path, "r", encoding="utf-8") as fp:
        app_pkg = json.load(fp)
    app_pkg["main"] = "./app_launcher/index.js"

    launcher = read_text("app_launcher.js").replace(
        "__LQ_LOADER_PATH__", json.dumps(paths["host"], ensure_ascii=False)
    )
    background = read_text("background.js")
    build_info = {
        "builder": {"name": "QQNT-LiteLoader-OneClick", "version": "1.0.0"},
        "builtAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "qq": {"name": app_pkg.get("name"), "version": app_pkg.get("version"),
               "buildVersion": app_pkg.get("buildVersion")},
        "loader": {"path": paths["host"], "mode": "构建时固定"},
    }
    files = [
        ("package.json", json.dumps(app_pkg, indent=2, ensure_ascii=False).encode("utf-8")),
        ("lq-build.json", json.dumps(build_info, indent=2, ensure_ascii=False).encode("utf-8")),
        ("app_launcher/index.js", launcher.encode("utf-8")),
        ("application/background.js", background.encode("utf-8")),
    ]
    return build_asar(files)


# ---------------------------------------------------------------- 安装 / 恢复

def install_host(paths, progress=None):
    host = paths["host"]
    temp = tempfile.mkdtemp(prefix="lq-host-")
    try:
        with zipfile.ZipFile(resource_path("host.zip")) as z:
            z.extractall(temp)
        if os.path.isdir(host):
            backup = host + ".bak-" + time.strftime("%Y%m%d-%H%M%S")
            try:
                os.rename(host, backup)
            except OSError:
                shutil.rmtree(host, ignore_errors=True)
        shutil.copytree(temp, host)
        if not host_patched(host):
            raise RuntimeError("宿主补丁未生效，安装包可能已损坏")
    finally:
        shutil.rmtree(temp, ignore_errors=True)
    if progress:
        progress("宿主已安装到 %s" % host)


def deploy_asar(paths, progress=None):
    app_asar = paths["app_asar"]
    if os.path.isfile(app_asar):
        bak = app_asar + ".bak"
        if not os.path.isfile(bak):
            shutil.copy2(app_asar, bak)
        else:
            shutil.copy2(app_asar, app_asar + ".bak-" + time.strftime("%Y%m%d-%H%M%S"))
    data = make_injection_asar(paths)
    with open(app_asar, "wb") as fp:
        fp.write(data)
    if progress:
        progress("注入点已写入 %s (%d 字节)" % (app_asar, len(data)))


def do_install(qq_dir, progress=None):
    version = pick_version_dir(qq_dir)
    if not version:
        raise RuntimeError("没有找到带 resources\\app 的 QQ 版本目录")
    paths = qq_paths(qq_dir, version)
    if qq_running():
        if progress:
            progress("检测到 QQ 正在运行，先结束进程")
        kill_qq()
        time.sleep(2)
    install_host(paths, progress)
    deploy_asar(paths, progress)
    # 安装后确保插件总开关为开启，否则宿主起来了也看不到插件
    profile = find_profile(qq_dir)
    set_plugins_enabled(profile, True)
    if progress:
        progress("插件总开关已设为开启")
    if progress:
        progress("QQ %s 安装完成，重启 QQ 即可生效" % version)
    log_line("install ok: %s %s" % (qq_dir, version))
    return paths


def do_restore(qq_dir, progress=None):
    version = pick_version_dir(qq_dir)
    if not version:
        raise RuntimeError("没有找到 QQ 版本目录")
    paths = qq_paths(qq_dir, version)
    if qq_running():
        if progress:
            progress("结束 QQ 进程")
        kill_qq()
        time.sleep(2)
    app_asar = paths["app_asar"]
    if os.path.isfile(app_asar):
        os.remove(app_asar)
        if progress:
            progress("已移除注入点 app.asar")
    bak = app_asar + ".bak"
    if os.path.isfile(bak):
        shutil.copy2(bak, app_asar)
        if progress:
            progress("已恢复原始 app.asar 备份")
    log_line("restore ok: %s %s" % (qq_dir, version))
    return paths


# ---------------------------------------------------------------- 插件

def documents_dir():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders") as hk:
            val, _ = winreg.QueryValueEx(hk, "Personal")
            val = os.path.expandvars(val)
            if os.path.isdir(val):
                return val
    except OSError:
        pass
    return os.path.join(os.environ.get("USERPROFILE", ""), "Documents")


def find_profile(qq_dir):
    candidates = [
        os.path.join(documents_dir(), "LiteloaderQQNT"),
        os.path.join(os.environ.get("USERPROFILE", ""), "LiteloaderQQNT"),
    ]
    if qq_dir:
        candidates.append(os.path.join(qq_dir, HOST_DIR_NAME))
    for path in candidates:
        if os.path.isdir(os.path.join(path, "plugins")):
            return path
    return candidates[0]


def loader_config_path(profile):
    return os.path.join(profile, "data", "LiteLoader", "config.json")


def load_loader_config(profile):
    path = loader_config_path(profile)
    default = {"enable_plugins": True, "disabled_plugins": [], "deleting_plugins": {}}
    if os.path.isfile(path):
        try:
            with open(path, "r", encoding="utf-8") as fp:
                data = json.load(fp)
            for k, v in default.items():
                data.setdefault(k, v)
            return data
        except Exception:
            return dict(default)
    return dict(default)


def save_loader_config(profile, data):
    path = loader_config_path(profile)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fp:
        json.dump(data, fp, indent=4, ensure_ascii=False)


def scan_plugins(profile):
    plugins_dir = os.path.join(profile, "plugins")
    config = load_loader_config(profile)
    disabled = set(config.get("disabled_plugins", []))
    result = []
    if not os.path.isdir(plugins_dir):
        return result, config
    for name in sorted(os.listdir(plugins_dir)):
        folder = os.path.join(plugins_dir, name)
        manifest_path = os.path.join(folder, "manifest.json")
        if not os.path.isdir(folder) or not os.path.isfile(manifest_path):
            continue
        try:
            with open(manifest_path, "r", encoding="utf-8") as fp:
                manifest = json.load(fp)
        except Exception:
            manifest = {}
        slug = manifest.get("slug") or name
        result.append({
            "folder": name,
            "path": folder,
            "slug": slug,
            "name": manifest.get("name") or slug,
            "version": str(manifest.get("version") or "-"),
            "description": (manifest.get("description") or "").replace("\n", " "),
            "type": manifest.get("type") or "",
            "enabled": slug not in disabled and config.get("enable_plugins", True),
        })
    return result, config


def toggle_plugin(profile, slugs, enable):
    config = load_loader_config(profile)
    disabled = set(config.get("disabled_plugins", []))
    for slug in slugs:
        if enable:
            disabled.discard(slug)
        else:
            disabled.add(slug)
    config["disabled_plugins"] = sorted(disabled)
    save_loader_config(profile, config)


def set_plugins_enabled(profile, enabled):
    config = load_loader_config(profile)
    config["enable_plugins"] = bool(enabled)
    save_loader_config(profile, config)


# ---------------------------------------------------------------- 权限

def is_admin():
    try:
        import ctypes
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return True


def relaunch_as_admin():
    import ctypes
    if getattr(sys, "frozen", False):
        params = subprocess.list2cmdline(sys.argv[1:])
    else:
        params = subprocess.list2cmdline([os.path.abspath(sys.argv[0])] + sys.argv[1:])
    try:
        ret = ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, params, None, 1)
        return ret > 32
    except Exception:
        return False


# ---------------------------------------------------------------- CLI

def cli_mode(argv):
    cmd = argv[0]
    qq = find_qq_dir()
    if cmd == "--status":
        if not qq:
            print(json.dumps({"qq": None}, ensure_ascii=False))
            return 0
        version = pick_version_dir(qq)
        paths = qq_paths(qq, version)
        print(json.dumps({
            "qq": qq, "version": version,
            "inject": injection_state(paths),
            "host_version": host_version(paths["host"]),
            "host_patched": host_patched(paths["host"]),
            "plugins": len(scan_plugins(find_profile(qq))[0]),
        }, ensure_ascii=False, indent=2))
        return 0
    if cmd in ("--install", "--restore"):
        if not qq:
            print("未找到 QQNT 安装目录")
            return 2
        if not is_admin():
            print("需要管理员权限")
            return 3
        if cmd == "--install":
            do_install(qq, print)
        else:
            do_restore(qq, print)
        return 0
    return None


# ---------------------------------------------------------------- GUI

def run_gui():
    import tkinter as tk
    from tkinter import ttk, messagebox

    qq_dir = find_qq_dir()
    version = pick_version_dir(qq_dir) if qq_dir else None
    paths = qq_paths(qq_dir, version) if qq_dir else None

    root = tk.Tk()
    root.title(APP_TITLE)
    root.geometry("900x620")
    root.minsize(820, 560)

    style = ttk.Style()
    try:
        style.theme_use("vista")
    except tk.TclError:
        pass

    status_var = tk.StringVar()
    path_var = tk.StringVar()
    version_var = tk.StringVar()
    switch_var = tk.BooleanVar(value=True)

    frame_top = ttk.LabelFrame(root, text="QQ 状态")
    frame_top.pack(fill="x", padx=12, pady=(12, 6))

    ttk.Label(frame_top, text="安装目录：").grid(row=0, column=0, sticky="w", padx=8, pady=4)
    ttk.Label(frame_top, textvariable=path_var, foreground="#333").grid(row=0, column=1, columnspan=2, sticky="w")
    ttk.Label(frame_top, text="当前版本：").grid(row=1, column=0, sticky="w", padx=8, pady=4)
    ttk.Label(frame_top, textvariable=version_var).grid(row=1, column=1, sticky="w")
    ttk.Label(frame_top, text="注入状态：").grid(row=2, column=0, sticky="w", padx=8, pady=4)
    ttk.Label(frame_top, textvariable=status_var).grid(row=2, column=1, sticky="w")
    frame_top.columnconfigure(1, weight=1)

    frame_btn = ttk.Frame(root)
    frame_btn.pack(fill="x", padx=12, pady=4)

    notebook = ttk.Notebook(root)
    notebook.pack(fill="both", expand=True, padx=12, pady=(4, 12))

    # 插件页
    tab_plugins = ttk.Frame(notebook)
    notebook.add(tab_plugins, text="插件列表")

    bar = ttk.Frame(tab_plugins)
    bar.pack(fill="x", pady=6)
    ttk.Checkbutton(bar, text="启用插件总开关", variable=switch_var,
                    command=lambda: on_switch()).pack(side="left", padx=(0, 12))

    tree = ttk.Treeview(tab_plugins, columns=("name", "version", "status", "type", "desc"),
                        show="headings", selectmode="extended")
    tree.heading("name", text="插件")
    tree.heading("version", text="版本")
    tree.heading("status", text="状态")
    tree.heading("type", text="类型")
    tree.heading("desc", text="简介")
    tree.column("name", width=190, anchor="w")
    tree.column("version", width=80, anchor="center")
    tree.column("status", width=80, anchor="center")
    tree.column("type", width=80, anchor="center")
    tree.column("desc", width=380, anchor="w")
    tree.pack(fill="both", expand=True, side="left")

    scroll = ttk.Scrollbar(tab_plugins, orient="vertical", command=tree.yview)
    scroll.pack(side="right", fill="y")
    tree.configure(yscrollcommand=scroll.set)

    # 日志页
    tab_log = ttk.Frame(notebook)
    notebook.add(tab_log, text="日志")
    log_text = tk.Text(tab_log, height=10, wrap="word", state="disabled")
    log_text.pack(fill="both", expand=True, padx=4, pady=4)

    def append_log(text):
        log_line(text)
        log_text.configure(state="normal")
        log_text.insert("end", text + "\n")
        log_text.see("end")
        log_text.configure(state="disabled")

    plugin_cache = {"items": []}

    def refresh_status():
        nonlocal qq_dir, version, paths
        qq_dir = find_qq_dir()
        version = pick_version_dir(qq_dir) if qq_dir else None
        paths = qq_paths(qq_dir, version) if qq_dir else None
        if not qq_dir:
            path_var.set("未检测到 QQNT，请确认已安装 QQ")
            version_var.set("-")
            status_var.set("无法安装")
        else:
            path_var.set(qq_dir)
            version_var.set(version or "未找到版本目录")
            if paths:
                status_var.set(injection_state(paths))
        profile = find_profile(qq_dir)
        items, config = scan_plugins(profile)
        switch_var.set(bool(config.get("enable_plugins", True)))
        plugin_cache["items"] = items
        tree.delete(*tree.get_children())
        for item in items:
            state = "启用" if item["enabled"] else "禁用"
            tree.insert("", "end", iid=item["folder"],
                        values=(item["name"], item["version"], state, item["type"], item["description"]))

    def on_switch():
        profile = find_profile(find_qq_dir())
        set_plugins_enabled(profile, switch_var.get())
        append_log("插件总开关 -> %s（重启 QQ 生效）" % ("开" if switch_var.get() else "关"))
        refresh_status()

    def do_click_install():
        if not qq_dir:
            messagebox.showerror(APP_TITLE, "没有找到 QQNT 安装目录")
            return
        if not is_admin():
            if messagebox.askyesno(APP_TITLE, "写入 QQ 安装目录需要管理员权限，是否提权重启安装器？"):
                relaunch_as_admin()
                root.destroy()
            return
        if qq_running() and not messagebox.askokcancel(APP_TITLE, "QQ 正在运行，安装前需要先结束 QQ 进程，是否继续？"):
            return
        try:
            notebook.select(tab_log)
            append_log("=== 开始安装 ===")
            do_install(qq_dir, append_log)
            append_log("=== 安装完成，请启动 QQ ===")
        except Exception as exc:
            append_log("安装失败：%s" % exc)
            append_log(traceback.format_exc())
            messagebox.showerror(APP_TITLE, "安装失败：%s" % exc)
        refresh_status()

    def do_click_restore():
        if not qq_dir:
            return
        if not is_admin():
            if messagebox.askyesno(APP_TITLE, "需要管理员权限，是否提权重启安装器？"):
                relaunch_as_admin()
                root.destroy()
            return
        if not messagebox.askokcancel(APP_TITLE, "将移除注入点并恢复 QQ 原生启动，继续？"):
            return
        try:
            notebook.select(tab_log)
            do_restore(qq_dir, append_log)
        except Exception as exc:
            append_log("恢复失败：%s" % exc)
        refresh_status()

    def toggle_selected():
        selection = tree.selection()
        if not selection:
            messagebox.showinfo(APP_TITLE, "请先在列表中选择插件")
            return
        profile = find_profile(qq_dir)
        items = {item["folder"]: item for item in plugin_cache["items"]}
        slugs = [items[f]["slug"] for f in selection if f in items]
        enable = not all(items[f]["enabled"] for f in selection if f in items)
        toggle_plugin(profile, slugs, enable)
        append_log("%s插件：%s（重启 QQ 生效）" % ("启用" if enable else "禁用", ", ".join(slugs)))
        refresh_status()

    def open_plugins_dir():
        profile = find_profile(qq_dir)
        pdir = os.path.join(profile, "plugins")
        os.makedirs(pdir, exist_ok=True)
        subprocess.Popen(["explorer", pdir])

    def open_qq():
        if qq_dir:
            exe = os.path.join(qq_dir, "QQ.exe")
            if os.path.isfile(exe):
                subprocess.Popen([exe], cwd=qq_dir)

    tree.bind("<Double-1>", lambda e: toggle_selected())

    ttk.Button(frame_btn, text="一键安装 / 修复", command=do_click_install).pack(side="left")
    ttk.Button(frame_btn, text="恢复原生启动", command=do_click_restore).pack(side="left", padx=8)
    ttk.Button(frame_btn, text="刷新状态", command=refresh_status).pack(side="left")
    ttk.Button(frame_btn, text="启用/禁用选中插件", command=toggle_selected).pack(side="left", padx=8)
    ttk.Button(frame_btn, text="打开插件目录", command=open_plugins_dir).pack(side="left")
    ttk.Button(frame_btn, text="启动 QQ", command=open_qq).pack(side="right")

    refresh_status()
    append_log("安装器已启动。QQ 目录：%s" % (qq_dir or "未检测到"))
    append_log("提示：安装/修复会先结束 QQ 进程；插件启用状态修改后需重启 QQ 生效。")
    root.mainloop()


def main():
    argv = sys.argv[1:]
    if not is_admin():
        if relaunch_as_admin():
            sys.exit(0)
    if argv and argv[0].startswith("--"):
        result = cli_mode(argv)
        if result is not None:
            sys.exit(result)
    run_gui()


if __name__ == "__main__":
    main()
