# A Cigarette Minecraft Launcher 正式版 v2.0.1

C# / .NET 10 / WPF，Windows x64 自包含单文件启动器。

## 正式版 v2.0.1 更新

- 编译文件名为 `A Cigarette Minecraft Launcher.exe`，界面版本号为“正式版 v2.0.1”。发布目录仅包含此 EXE，运行所需 DLL 内嵌并在启动时自动释放。
- 禁用 CurseForge 模组下载源，移除来源选项及 API Key 设置入口。模组浏览、搜索和下载保留 Modrinth。
- 整合包导入遇到 CurseForge 文件引用或 ForgeCDN 下载地址时明确提示来源已禁用；仍保留 CurseForge ZIP 的本地覆盖文件导入和导出格式。

以下为历史快照更新记录。

## snapshot.5 更新

- 启动器每次打开都会检测 Java，包括 PATH、JAVA_HOME / JDK_HOME / JRE_HOME、Windows Java 注册信息、常见安装位置、Minecraft 自带运行时及已登记游戏目录中的运行时。Java 下拉列表显示完整版本、位数与路径；全局和单版本设置都可选择，刷新保留已有选择。未登记的自定义便携位置仍可手动浏览选择。
- 启动时若选定 Java 的主版本或位数不符合清单要求，会用启动器自定义弹窗询问是否切换；只显示当前和目标 Java 版本，不显示路径。点击“切换并继续”选用匹配版本，缺少时自动下载；点击“取消启动”停止本次准备。
- 修复 New Era 1.21.1 / NeoForge 的 `Duplicate key ... gson-2.10.1.jar`：合并清单中部分运行库出现两次，现在生成启动类路径时按实际路径去重，保留原游戏清单与模组。
- 支持同时启动多个游戏进程。每次启动拥有独立的设置快照、进程跟踪、本地库目录、启动日志和取消状态；完成启动后按钮仍为“启动游戏”。多进程时“结束游戏”展开按版本名和 PID 区分的菜单，可结束指定进程或全部本启动器启动的游戏。运行中的版本不能更名。
- 用户主动结束与游戏崩溃分开处理：主动关闭、强制结束或在启动中结束进程，不会因为非零退出码再弹出错误报告，只显示“已结束当前运行中的Minecraft进程”。后台游戏日志由独立线程读取，界面更新回到 UI 线程处理。
- 错误窗口增加 Java、内存、模组前置 / 冲突、图形窗口、文件缺失等常见问题分析，并提示“如果你要解决问题，请把错误报告发给其他人，而不是发送这个窗口的截图。”可保存包含环境、分析、启动日志和本次游戏日志摘要的文本报告；敏感令牌会脱敏，较大日志保留头部与末尾并标明省略。同目录多开时只采集各进程独立输出，避免混入另一进程的日志。
- NeoForge 改为 Minecraft 原版狐狸头像，Forge 使用 Minecraft 原版铁砧方块的纹理与模型，原版游戏使用原版草方块纹理与模型，侧栏及版本列表共用；首页启动按钮左侧的账号信息已移除。

NeoForge 1.21.1 的 Java 需求参考[官方文档](https://docs.neoforged.net/docs/1.21.1/gettingstarted/)，启动器根据每个游戏清单中的要求检查运行时。

## snapshot.4 更新

- 左侧选择版本卡片显示当前版本对应的图标，切换版本时同步更新；侧栏收起后保留图标。主动结束游戏后的提示为“已结束当前运行中的Minecraft进程”。
- 兼容其他启动器的原版、Fabric、Forge、NeoForge 及已安装 Quilt 版本：在内存中合并 `inheritsFrom`，解析共享原版 JAR、Maven 依赖、旧式字符串参数及改名后的清单 ID；扫描和启动不会改写原清单。版本描述显示游戏类型、真实 Minecraft 版本及加载器版本，不再显示 Java。
- 游戏隔离目录统一为 `.minecraft/versions/<版本名>`。旧 `instances/<版本名>` 内容在首次使用时复制到版本目录，保留原备份；发现内容冲突会报错，避免覆盖存档。缺失或损坏的客户端日志配置（如 `client-1.12.xml`）会在启动前下载并校验。
- 启动过程显示账号、Java、文件校验、创建进程和等待窗口的进度。窗口稳定出现后关闭进度并提示成功；失败显示原因、输出摘要、本次启动日志和游戏日志路径。顶部“结束游戏”只结束本启动器本次启动的游戏进程。
- 版本列表左键立即选择当前版本；右键或右侧齿轮打开菜单，包含版本设置、导出整合包、补全文件和打开目录。版本设置直接显示在主界面，合法修改自动保存；Java、内存上限、游戏标题、隔离和窗口尺寸可分别继承全局或单独覆盖，默认全部继承。
- 导入 / 导出 Modrinth `.mrpack` 与 CurseForge 整合包 ZIP，恢复游戏及指定加载器、模组、配置和其他覆盖文件。导出默认不含存档，可手动勾选；不包含客户端、共享运行库、日志或机器 Java 路径。能解析来源的模组使用来源索引，其他本地文件放入 `overrides`。供本地导入与分享使用，网站上架还需遵守各平台的审核和模组再分发规定。
- 左键模组文件列表中的版本，选择保存目录后下载；默认打开当前所选游戏版本的 Mods 目录，自动解析必需前置并校验文件。可以选择其他目录。

整合包格式依据 [Modrinth 格式说明](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack) 与 [CurseForge 导出说明](https://support.curseforge.com/support/solutions/articles/9000198500-exporting-a-modpack-for-curseforge-project-submission)。

## v2.0.1 快照版更新

- 多游戏根目录：版本管理页顶部目录列表左键切换；设置中填写路径和可选目录名称，点击“添加 / 使用此目录”。已登记目录可右键选择“改名”，只修改启动器中的名称，不移动磁盘目录，也不切换当前目录。每个目录记住其所选版本，扫描、补全、模组安装与游戏启动均使用当前目录；旧配置自动登记原有目录。
- 版本管理在没有本地版本时也可打开，显示“没有可用版本”，点击“前往下载”进入下载页。
- 底部状态提示默认关闭，可在设置勾选“显示底部状态提示”并保存；任务进行时仍保留收起的任务入口。
- 设置中的“从列表移除当前目录”只移除登记，不删除游戏文件；至少保留一个根目录。在其他安装目录完成游戏安装后，该目录会自动登记并选中。
- 下载线程数默认 16，可在设置中修改为 1–64，保存后对后续游戏、Java、模组及加载器依赖下载生效。多个文件并发下载；已知大小及哈希、至少 8 MiB 的文件最多分为 4 段，各段共享总并发上限。设为 1 时使用单连接；服务器忽略 Range 时自动退回普通下载。
- NeoForge 的安装器、SHA1、版本列表与 Maven 依赖支持 BMCLAPI。自动模式优先尝试 NeoForge 镜像，失败后使用官方；选择 BMCLAPI 时镜像优先，缺失条目仍可回退官方。选择官方时直接访问官方源。映射依据 [BMCLAPI 官方文档](https://bmclapidoc.bangbang93.com/)。
- 账号管理新增明确的“关闭”按钮，右上角关闭按钮也保留；输入焦点在账号管理中时可按 Esc。关闭将取消进行中的微软登录。

下载仍会校验完整文件，校验通过后才提交；取消时清理临时分段文件。当前不提供跨重启断点续传。

## demo.4 更新

- 使用本次用户提供的头像作为侧栏 Logo、窗口图标及 EXE 图标，内嵌多尺寸 Windows ICO。
- 安装页支持原版、Fabric、Forge、NeoForge，可选择兼容的加载器版本。左键游戏版本直接打开安装页。
- Forge / NeoForge 使用官方安装器执行客户端补丁处理；所需 Java 自动准备，安装依赖提前下载校验。版本管理显示草方块、Fabric、Forge 铁砧、NeoForge 狐狸图标。
- 模组页未搜索时随机推荐，显示图标、支持的 Minecraft 版本及加载器。左键进入详情，按游戏版本和加载器筛选文件。
- 详情显示所选文件的必需前置及可选依赖，左键可打开对应模组页面并返回。安装前再次检查当前实例兼容性，只自动安装必需依赖。

## 登录

修复 Xbox Live / XSTS 请求序列化时字段名被默认 Web JSON 选项改写的问题。现在严格保留官方协议要求的 `Properties`、`AuthMethod`、`RpsTicket`、`RelyingParty`、`TokenType`、`SandboxId` 和 `UserTokens` 大小写，Minecraft 登录请求继续使用其要求的 camelCase 字段。补充 JSON Accept 头，HTTP 400 等错误仅显示状态和数字 Xbox 错误码，不输出上游原文或 Token。

升级后重新点击“微软登录”。保留现有配置与账号；Client ID 和 `http://localhost` 回调保持原值。没有使用用户账号完成真实正版登录，需更新后在用户自己的授权环境中复验。

请求格式参考：[微软 Xbox 官方认证协议](https://learn.microsoft.com/en-us/gaming/gdk/docs/services/fundamentals/s2s-auth-calls/service-authentication/live-website-authentication)。

## 运行与更新

关闭旧版启动器，运行编译包中的 `A Cigarette Minecraft Launcher.exe`。发布文件夹只需这一个 EXE，无需安装 .NET。运行时 DLL 内嵌于 EXE，启动时解压到用户临时缓存；缓存缺失时由 EXE 自动重新释放。游戏和 Java 文件由下载功能另外生成。

普通配置兼容 demo.1，位于 `%LOCALAPPDATA%\ACML\Demo`。默认游戏根目录为实际 EXE 旁的 `.minecraft`，可在设置和安装时选择其他 `.minecraft` 目录。不得将启动器放在无写入权限的位置。游戏文件不随发布包附带。

设置仍使用同一数据目录，以便保留已有账号和背景。多个根目录中的同名版本各自拥有独立的版本设置和隔离目录；全局 Java、内存、下载线程数等设置仍由启动器统一管理。

## 使用

- 账号：左侧账号卡片添加离线或微软账号，可以缓存多个账号，同时只有一个当前账号。微软登录使用你提供的 Client ID `c9431eb5-d47e-489c-aa55-850f42fbc5aa`，系统浏览器 + `http://localhost` 回调；应用注册须允许个人微软账号和桌面公共客户端。密码只在微软页面输入。
- 游戏安装：进入“下载”即自动刷新目录。左键版本打开安装页，可更改安装根目录、**版本文件夹名称**，选择原版、Fabric、Forge 或 NeoForge，以及兼容的加载器版本。最新正式版和快照版由在线目录提供，可搜索历史条目。下载源支持官方、BMCLAPI 和自动回退；加载器安装器来自相应官方站点。
- 版本管理：左侧“版本管理”始终进入本地列表，可在顶部切换根目录；空列表显示前往下载的入口。左键版本切换，右键或齿轮打开管理菜单。列表下方提供整合包导入入口。
- 单版本设置：勾选“启用此版本的独立设置”，再取消对应的“使用全局”选项即可修改。合法输入约 450 毫秒后自动保存，无需保存按钮；正在输入的不完整数字不会覆盖已保存配置。Java 路径可以留空，启动时自动查找和下载。自定义游戏标题在游戏窗口创建后应用。
- 更名：下载时填写的名称就是 `.minecraft\versions\<名称>`。管理中更名会同步更新 `<名称>.json`、`<名称>.jar`、清单内 ID、当前选择、安装备份、版本独立设置和对应隔离实例目录。名称允许中文和空格；重名、非法路径或 Windows 保留名称会被拒绝。任务或游戏运行时不能更名，失败时回退已完成的文件移动。
- Mods：单版本 Mods 页列出当前生效目录内的 `.jar` 和 `.jar.disabled`，可启用、停用、刷新、打开文件夹或转到下载。下载页可浏览随机推荐或搜索，再左键进入详情，按 Minecraft 版本和加载器筛选。详情显示前置及可选依赖，左键可进入依赖页面。左键可下载的文件条目后选择目录，从 Modrinth 下载文件和必需依赖。CurseForge 下载源已禁用。
- 游戏启动：首页分体按钮显示所选版本。启动前验证账号、游戏文件和 Java，再启动真实游戏进程并记录日志。没有所需主版本的 64 位 Java 时，自动通过 Eclipse Adoptium 官方 API 下载 Temurin，验证 SHA256，解压、执行版本检查后使用；不修改系统 Java 或 PATH。原有 Java 和已下载 Java 可复用。
- 任务：下载、安装、补全或 Java 准备进行时，底部才出现任务入口；默认收起，点击展开进度与取消按钮。完成、失败或取消后入口隐藏。
- 背景：设置中选择 PNG / JPG / BMP 自定义背景并调节遮罩，可恢复默认森林背景。图像保存为启动器数据目录内的独立副本。

## 目录与安全保存

```text
.minecraft/
  versions/<版本名>/<版本名>.json
  versions/<版本名>/<版本名>.jar
  libraries/                         共享依赖
  assets/                            共享资源
  versions/<版本名>/mods/            开启隔离时的模组
  versions/<版本名>/saves/           开启隔离时的存档
  versions/<版本名>/config/          开启隔离时的配置
  instances/<版本名>/               旧版隔离数据备份（如存在）
  .acml/instance-settings/<版本名>.json
  .acml/install-records/<版本名>.json
  .acml/java/                        启动器下载的 Java
  .acml/loader-installers/           已验证的官方加载器安装器
  .acml/loader-logs/                 加载器安装日志
```

关闭隔离时，该版本使用 `.minecraft` 根目录的存档和 `mods`。切换隔离不会自动搬迁根目录存档；更名保留版本目录里的内容。多个关闭隔离的版本共享同一个 Mods 列表。选择游戏根目录时也可选择包含 `.minecraft` 的启动器目录、`versions` 或具体版本目录，启动器会识别相应根目录。

微软 MSAL 缓存和 Minecraft 会话使用 Windows DPAPI 加密后写入 `HKCU\Software\ACML\Accounts`，不以明文写入配置。旧版已保存的 CurseForge Key 不会用于网络请求，当前界面不提供 Key 设置。日志对已知游戏 Token 和 Bearer 参数脱敏。配置通过临时文件原子替换，损坏配置保留并报错。

开发测试可设置 `ACML_DEMO_DATA_DIR` 隔离配置和注册表缓存；不要把该变量设为日常数据目录之外的不明路径。

## 当前范围

游戏下载及加载器安装主要支持 1.13 及以上，Forge 安装器须使用现代处理器格式；已有第三方版本支持标准独立或继承清单，但仍需要完整的基础版本和已生成加载器补丁。旧版虚拟资源目录、古老 Forge 安装器、缺失本地补丁的第三方版本自动重建尚未适配。已安装 Quilt 可识别，暂不支持新装 Quilt 或 Quilt 整合包导入。模组自动升级、启动器在线更新尚未完成，模组停用不会自动停用依赖它的其他模组。

微软真实账号授权未使用你的账号完成验收；认证链与服务拒绝情况已通过模拟接口测试。当前 Client ID 尚未申请 Minecraft API 访问审核，仍需完成官方应用审核；Client ID 本身不能替代 Minecraft 服务的访问资格。CurseForge 下载源在本版禁用。

## 构建与验证

需要 Windows x64 与 .NET 10 SDK。界面为 WPF，微软授权使用官方 MSAL；核心下载、实例管理和启动逻辑在 `ACML.Core`。

```powershell
.\build.ps1
# 可指定 SDK 和单文件输出目录
.\build.ps1 -DotnetPath 'C:\SDK\dotnet.exe' -OutputDirectory '.\publish-single'
```

脚本执行核心与 Windows 测试，再发布仅包含 `A Cigarette Minecraft Launcher.exe` 的文件夹。为防止旧 DLL 混入，已有输出目录只能包含该 EXE。测试记录见 `TESTING.md`。

## 来源与许可

当前头像 Logo 与森林截图素材由用户提供；当前版本号由程序显示。Fabric 图标来自项目官方资源；草方块、Forge 铁砧和 NeoForge 狐狸图标使用 Minecraft 原版纹理及模型，仅用于标识版本，来源见 `ASSETS.md`。`.NET`、Windows Desktop、MSAL 等第三方许可保存在源码 `licenses` 中并嵌入单文件运行时缓存的 `licenses` 子目录。下载的 Temurin 自带其发行许可。

- [Eclipse Adoptium 官方下载 API 文档](https://github.com/adoptium/api.adoptium.net/blob/main/docs/cookbook.adoc)
- [微软桌面应用注册](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-registration)
- [Modrinth API](https://docs.modrinth.com/api/)
- [CurseForge API](https://docs.curseforge.com/rest-api/)

本仓库沿用现有 `LICENSE` 中的 GNU Affero General Public License v3.0。第三方依赖和图片素材的权利归属及许可见 `licenses/` 与 `ASSETS.md`。
