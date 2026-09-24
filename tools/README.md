# tools

环境修复脚本与联调工具，都不依赖 .NET，直接双击或在 PowerShell 里运行。

## fix_powershell_path.ps1 / .bat

把 `WindowsPowerShell\v1.0` 等目录写回**用户** `PATH`（`HKCU\Environment`），解决某些宿主进程派生的终端里
`powershell` 报 `ENOENT` 的问题。同目录的 `.bat` 是双击运行的包装。
加 `-Machine` 并「以管理员身份运行」才改系统 `PATH`。改完必须完全退出并重启编辑器/agent 宿主，重载窗口无效。

## wecom_calendar_probe.ps1

企业微信日程接入的第一步：**不写 GUI、不装任何依赖**，先把「企微给的同步账号到底能不能读到日程、
用的是哪种协议」测出来。仅用 .NET 自带的 `HttpWebRequest`（PowerShell 5.1 的 `Invoke-WebRequest`
发不出 `PROPFIND` / `REPORT`）。

### 凭据在哪拿

企业微信客户端：**工作台 → 日程 → 右上角「三」→ 日历设置 → 同步到其他日历**，
页面上给出「服务器」「帐号/用户名」「密码」三项。密码打码显示、且**每次使用都要重新获取**，
所以每次跑都现贴，不要存档。

### 运行

```powershell
# 交互式：服务器、用户名、密码逐个问，命令行里不留密码
powershell -NoProfile -ExecutionPolicy Bypass -File tools/wecom_calendar_probe.ps1

# 常用参数
... -Server wecom.work -User <帐号> -Days 60                 # 只列日历和前 60 天日程
... -NoAuth                                                 # 不给密码，只问协议（可无人值守跑）
... -Ews                                                    # 用 EWS SOAP 而不是 CalDAV 试
... -ListOnly                                               # 只做发现，不查日程
... -CalendarPath /dav/xxx/home/calendar/                   # 跳过发现，直接打这个日历
... -OutDir C:\temp\ics                                     # 把每条 VEVENT 存成 .ics 供核对
... -PasswordFile C:\temp\wecom.txt                         # 从文件读密码（自己负责删除）
```

密码提示用的是 `Read-Host -AsSecureString`，**需要真实控制台**：把脚本挂在管道、CI 或别人的
进程下跑时必须带 `-NoAuth`，否则会永久卡在提示处。

脚本默认走 `1) 协议判定 → 2) current-user-principal → 3) calendar-home-set → 4) 日历列表 →
5) REPORT calendar-query → 6) 结论清单`；带 `-Ews` 时改走 `E0) 密码是否被接受 → E1) GetFolder →
E2) GetCalendarView → E3) 结论清单`。失败时打印 HTTP 状态、`WWW-Authenticate` 和响应片段。

### 已验证 / 未验证

已在本机验证：脚本语法（PowerShell 5.1 解析 0 错误、文件保持纯 ASCII）、协议判定与两类失败出口
（`403` 与完全连不上）、Basic 头按 UTF-8 编码、`PROPFIND` / `REPORT` 用 `HttpWebRequest` 发得出
自定义动词。XML/ICS 的解析路径（principal、`calendar-home-set`、集合列表过滤、`calendar-data`
取用、RFC 5545 折行还原、全天/重复/`TZID`/无时区判定）此前是用 RFC 风格的样例响应离线跑通的，
那份一次性校验脚本没有入库，所以这几条要在拿到凭据的那次运行里再核一遍。

`wecom.work` 与 `caldav.wecom.work` 两台主机的协议判定都已实测跑通（无凭据，见下）；`-Ews` 分支在
无凭据下会干净地报出 502 并给出解读。**未验证**：真实凭据下的认证结果与日程数据 —— 这一步只能由
拿到密码的人跑。

### 实测到的事实（全部无凭据，本机直连）

- 官方《日程数据同步配置指引》写的是 **Microsoft Exchange / Exchange ActiveSync**，并且
  「密码需在每次使用时单独获取」、「Mac 系统日历暂不支持」。EAS 的报文是 **WBXML 二进制**，
  不是 XML，也通常要求设备先 Provision。
- **企微页面上那串 `wecom.work` 不是 CalDAV 入口**：它的 `/`、`/dav/`、`/caldav/`、`/Calendar/`、
  `/.well-known/caldav`、`/.well-known/principal` 对 `OPTIONS` / `PROPFIND` 一律回 **403**
  （`Server: Wwebsvr`，无 `DAV:` 头、无 `WWW-Authenticate`），即请求在边缘就被挡下，
  还没到校验密码那一步。这台主机上只有 EAS 与 EWS 两条路径会回 **401 +
  `WWW-Authenticate: Basic realm="qq.com"`**（EAS 的正文是「帐号异常、服务未开通、密码不正确、
  登录频率受限或系统繁忙」）—— 也就是说 Basic 这套通道活着，但在 `wecom.work` 上它不通向 DAV。
- **但 `caldav.wecom.work` 是另一个虚拟主机，路由表完全不同**：RFC 5352 的 well-known 路径
  `/.well-known/caldav`、`/.well-known/caldav/`、`/.well-known/carddav`、`/.well-known/principal`
  在 `OPTIONS` / `GET` / `PROPFIND` 下都回 **401 Basic realm="qq.com"**，而该主机上的 `/`、`/dav/`、
  `/calendar`、`/users/…`、`/remote.php/dav` 乃至 `Microsoft-Server-ActiveSync` 与
  `EWS/Exchange.asmx` 全是 403。"只把 well-known 那几条挂到要密码的后端上"正是
  **一个只做日历同步的 CalDAV 网关的形状** —— 方案 A 的入口在这里，不在页面上那串裸域名上。
  另外 `GET /calendar`（不带尾斜杠）会 301 到 `http://caldav.wecom.work:1443/calendar/`，
  而 1443 端口对外不通（http 502、https 握手失败），说明那是内部上游端口，不要直连。
- **EWS 半死不活**：`wecom.work/EWS/Exchange.asmx` 的 `GET` / `OPTIONS` 回 401 Basic，但带 SOAP 体的
  `POST` 回 **502**（页面署名 `upstreamserver`，正文 gb2312 编码）。注意不带 `Cmd=` 的 EAS `POST`
  同样是 502，所以 502 只代表「请求在鉴权之前就被上游处理器拒了」，**不能**单独判定 EWS 不存在；
  要定论得带真凭据跑 `-Ews`（`E0` 先分辨密码是否被接受，`E1/E2` 再发真正的 SOAP）。
  不过 `caldav.wecom.work` 上 EWS 直接 403，所以就算 EWS 存在也不是给这个账号用的。
- 只监听 443，明文 http 不通；`/owa/`、`/autodiscover/autodiscover.xml`、
  `_autodiscover._tcp.wecom.work` 分别 403 / 403 / 无记录。
- `wecom.work` 解析到多组 IP、证书是 Let's Encrypt 签的 `*.wecom.work` 泛域名，
  `caldav.` / `eas.` / `mail.` 都能解析到同一批 IP —— 所以「能不能用」取决于 Host 头路由，
  换子域名是有意义的动作，不是随便试试。
- 页面上的用户名与「专用密码」不能拿手机登录企微的那个密码代替；密码是否真的用完即失效，
  看下面 `E0` / 第二次认证的结果。

### 下一步命令

```powershell
# 无凭据，只问协议（几十秒）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/wecom_calendar_probe.ps1 `
  -Server wecom.work -User probe -NoAuth

# 带凭据打 CalDAV 入口：服务器填 caldav.wecom.work，不要填页面上那串裸域名
powershell -NoProfile -ExecutionPolicy Bypass -File tools/wecom_calendar_probe.ps1 `
  -Server caldav.wecom.work -User <页面上的用户名>

# 带凭据定论 EWS（密码在提示里粘贴，不进命令行、不落盘）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/wecom_calendar_probe.ps1 `
  -Server wecom.work -User <页面上的用户名> -Ews
```

结论：方案 A 没有被排除，只是入口不是页面上那串 `wecom.work`，而是 `caldav.wecom.work`
的 well-known 路径 —— 那里已经在要 Basic 密码了，说明凭据会被真正校验。剩下的三种走向取决于
上面第二条的输出：列出日历并取回 VEVENT 就在 WPF 里做 CalDAV 只读拉取（A）；401 拒不认密码
就走 `.ics` 导出导入（B，一定可用但不实时）；EWS 那台顺手一起测，作为 A 失败时的备选（A′）。
