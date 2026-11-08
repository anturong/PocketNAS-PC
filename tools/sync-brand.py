#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PocketNAS 界面 UI 复用：共用品牌片段同步器（幂等，可反复跑）。

单一真源
  easy_webdav/src/WebDAVService/web/brand.css                 品牌 CSS
  src/PocketNAS.PC/Assets/brand/pocketnas_64.png             方块标志（深底 + 橙 p）

同步目标（三处 CSS 一字不差、同一张字标图）
  PC    web/browse.html                     顶栏一行式（标志 + PocketNAS + -PC）
  PC    web/login.html                      登录页大号一行式
  手机  app/src/main/java/com/filemgr/net/BrowsePages.kt   BRAND_CSS / BRAND_ICON

两端都用 data: base64 内嵌：PC 的 WebDAVServer 只 serve login/logout/browse
三个 HTML 模板，没有静态资源路由，<img src="xxx.png"> 会 404 破图；手机端同理。

用法
  python tools/sync-brand.py                 # 同步两端
  python tools/sync-brand.py --check        # 只检查是否已同步
  python tools/sync-brand.py --pc-repo X --phone-repo Y
"""
import argparse
import base64
import io
import os
import re
import sys

CSS_BEGIN = "/* <pnsBrandCss> */"
CSS_END = "/* </pnsBrandCss> */"
KT_BEGIN = "    // <pnsBrand>"
KT_END = "    // </pnsBrand>"
IMG_RE = re.compile(
    r'<img class="pnsIc(?P<extra>[^"]*)" '
    r'src="data:image/png;base64,[A-Za-z0-9+/=]*" alt="">'
)


def load(path):
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        s = f.read()
    return s.replace("\r\n", "\n"), ("\r\n" in s)


def save(path, s, crlf):
    if crlf:
        s = s.replace("\n", "\r\n")
    with io.open(path, "w", encoding="utf-8", newline="") as f:
        f.write(s)


def swap(s, old, new, label, log):
    """一次性迁移：旧串存在就换；不存在视为已就位。"""
    if old in s:
        s = s.replace(old, new, 1)
        log.append("migrate: " + label)
    return s


def img_tag(b64, lg=False):
    cls = "pnsIc lg" if lg else "pnsIc"
    return '<img class="%s" src="data:image/png;base64,%s" alt="">' % (cls, b64)


def css_block(html, css):
    block = CSS_BEGIN + "\n" + css.strip() + "\n" + CSS_END
    pat = re.compile(re.escape(CSS_BEGIN) + r".*?" + re.escape(CSS_END), re.S)
    if pat.search(html):
        return pat.sub(lambda m: block, html, count=1)
    if "</style>" not in html:
        raise RuntimeError("找不到 </style>，无法插入品牌 CSS")
    return html.replace("</style>", block + "\n</style>", 1)


def sync_html(path, css, b64, lg, migrate, log):
    s, crlf = load(path)
    orig = s
    for old, new, label in migrate:
        s = swap(s, old, new, label, log)
    s = css_block(s, css)
    found = IMG_RE.search(s)
    if not found:
        raise RuntimeError("%s 里找不到 .pnsBrand 占位图（迁移失败？）" % path)
    s = IMG_RE.sub(lambda m: img_tag(b64, "lg" in m.group("extra")), s, count=1)
    if s != orig:
        save(path, s, crlf)
        log.append("write: " + os.path.basename(path))
    else:
        log.append("unchanged: " + os.path.basename(path))
    return s != orig


def sync_kotlin(path, css, b64, log):
    s, crlf = load(path)
    orig = s

    # ---- 一次性结构迁移 ----
    s = re.sub(
        r"    /\*\* 官方橙色 p 形图标[^\n]*\*/\n"
        r'    private const val LOGO_MARK = "[A-Za-z0-9+/=]*"\n',
        "", s, count=1,
    )
    s = swap(
        s,
        r'''        sb.append("<header><span class=\"brand\"><img class=\"lg\" src=\"data:image/png;base64,")
            .append(LOGO_MARK).append("\" alt=\"\">")
            .append("<span class=\"bt\">PocketNAS-Phone</span></span>")''',
        r'''        sb.append("<header><span class=\"brand\"><img class=\"pnsBrand\" src=\"data:image/png;base64,")
            .append(BRAND_MARK).append("\" alt=\"PocketNAS\"></span>")''',
        "手机顶栏换成共用字标", log,
    )
    s = swap(s, 'header .brand .lg{display:block;width:24px;height:24px;object-fit:contain;flex-shrink:0}\n',
             "", "删旧 .lg 规则", log)
    s = swap(s, 'header .brand .bt{color:var(--ac)}\n', "", "删旧 .bt 规则", log)
    s = swap(s, '  header .brand .bt{display:none}\n', "", "删窄屏 .bt 规则", log)
    s = swap(s, '""".trimIndent()\n\n    internal fun esc',
             '""".trimIndent() + BRAND_CSS\n\n    internal fun esc',
             "THEME_CSS 拼接 BRAND_CSS", log)

    # ---- 手机版面色彩/边距靠近 PC（topbar 52px / 14px，表格行 10px 16px）----
    for old, new, label in [
        ("z-index:20;display:flex;align-items:center;gap:12px;",
         "z-index:20;display:flex;align-items:center;gap:8px;", "顶栏 gap 对齐 PC"),
        ("height:56px;min-height:56px;margin:0;padding:0 20px;",
         "height:52px;min-height:52px;margin:0;padding:0 14px;", "顶栏高度/内边距对齐 PC"),
        ("font-size:12px;margin:12px auto 8px;padding:0 20px}",
         "font-size:12px;margin:12px auto 8px;padding:0 14px}", "说明行边距"),
        (".crumbs{color:var(--t2);padding:0 20px;", ".crumbs{color:var(--t2);padding:0 14px;", "面包屑边距"),
        ("#list{padding:0 20px}", "#list{padding:0 14px}", "文件区边距"),
        (".foot{padding:0 20px;margin-top:14px}", ".foot{padding:0 14px;margin-top:14px}", "页脚边距"),
        ("padding:10px 20px;margin-top:16px;", "padding:10px 14px;margin-top:16px;", "状态条边距"),
        (".lHead{padding:8px 12px;", ".lHead{padding:10px 16px;", "表头内边距对齐 PC"),
        ("border-radius:0;padding:10px 12px}", "border-radius:0;padding:10px 16px}", "表格行内边距对齐 PC"),
    ]:
        s = swap(s, old, new, label, log)

    # ---- 幂等写入生成块 ----
    block = (
        KT_BEGIN + " 由 tools/sync-brand.py 生成，勿手改；源：web/brand.css + pocketnas_64.png\n"
        '    private const val BRAND_CSS = """\n' + css.strip() + '\n"""\n'
        '    private const val BRAND_ICON = "' + b64 + '"\n'
        + KT_END + "\n"
    )
    pat = re.compile(re.escape(KT_BEGIN) + r".*?" + re.escape(KT_END) + r"\n", re.S)
    if pat.search(s):
        s = pat.sub(lambda m: block, s, count=1)
    elif "object BrowsePages {\n" in s:
        s = s.replace("object BrowsePages {\n", "object BrowsePages {\n\n" + block, 1)
    else:
        raise RuntimeError("BrowsePages.kt 里找不到 object 声明")

    if s != orig:
        save(path, s, crlf)
        log.append("write: BrowsePages.kt")
    else:
        log.append("unchanged: BrowsePages.kt")
    return s != orig


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pc-repo", default=r"D:\RAID_NAS备份\x\PocketNAS-PC")
    ap.add_argument("--phone-repo", default=r"C:\Users\41456\WorkBuddy\2026-10-31-10-56-24\filemgr")
    ap.add_argument("--check", action="store_true")
    a = ap.parse_args()

    web = os.path.join(a.pc_repo, "easy_webdav", "src", "WebDAVService", "web")
    css = io.open(os.path.join(web, "brand.css"), encoding="utf-8").read()
    png = os.path.join(a.pc_repo, "src", "PocketNAS.PC", "Assets", "brand", "pocketnas_64.png")
    b64 = base64.b64encode(open(png, "rb").read()).decode()
    if "$" in css:
        raise RuntimeError("brand.css 不能含 $（Kotlin 三引号字符串会当插值）")

    log = []
    if a.check:
        for name in ("browse.html", "login.html"):
            s, _ = load(os.path.join(web, name))
            ok = css.strip() in s and b64 in s
            print("%-14s %s" % (name, "SYNCED" if ok else "STALE"))
        kt, _ = load(os.path.join(a.phone_repo, "app", "src", "main", "java",
                                  "com", "filemgr", "net", "BrowsePages.kt"))
        print("%-14s %s" % ("BrowsePages.kt", "SYNCED" if (css.strip() in kt and b64 in kt) else "STALE"))
        return 0

    sync_html(
        os.path.join(web, "browse.html"), css, b64, False,
        [("        .topbar .brandI{width:26px;height:26px;object-fit:contain;display:block}\n",
          "", "删 PC 旧 .brandI 规则"),
         ('<div class="brand"><img class="brandI" src="logo-mark.png" alt=""><span>PocketNAS</span></div>',
          '<div class="brand">' + img_tag(b64) + "</div>", "PC 顶栏换成共用字标")],
        log,
    )
    sync_html(
        os.path.join(web, "login.html"), css, b64, True,
        [(".brandBox{display:flex;align-items:center;justify-content:center;gap:12px;background:#fff;"
          "border-radius:12px;padding:14px 18px;margin-bottom:24px}\n"
          ".brandBox img{width:44px;height:44px;object-fit:contain;display:block}\n"
          ".brandBox h1{text-align:left;font-size:21px;font-weight:800;color:#111;margin:0 0 2px}\n"
          ".brandBox .sub{text-align:left;color:#777;font-size:12px;margin:0}\n",
          "", "删登录页手拼 brandBox CSS"),
         ("    .brandBox{padding:12px 14px;gap:10px}\n"
          "    .brandBox img{width:38px;height:38px}\n"
          "    .brandBox h1{font-size:19px}\n",
          "", "删登录页窄屏 brandBox CSS"),
         ('<div class="brandBox"><img src="logo-mark.png" alt="">'
          '<div><h1>PocketNAS</h1><p class="sub">本地与远程，一个入口</p></div></div>',
          img_tag(b64, True), "登录页换成黑色版字标")],
        log,
    )
    sync_kotlin(
        os.path.join(a.phone_repo, "app", "src", "main", "java", "com", "filemgr",
                     "net", "BrowsePages.kt"), css, b64, log,
    )
    for line in log:
        print(" ", line)
    print("SYNC DONE  css=%d chars  icon=%d bytes  b64=%d chars" % (len(css), os.path.getsize(png), len(b64)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
