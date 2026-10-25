# 图片资源

- `src/ACML.App/Assets/launcher-logo.jpg`：用户本次提供的 `98F05870FED7F36A5C92DF49940692FB.jpg`，保留原图。
- `launcher.ico`：由上述原图生成的多尺寸 Windows 图标（16、32、48、64、128、256）。
- `logo-original.png`、森林背景：用户之前提供的旧启动器截图及 Logo；保留旧资源供背景与回归验证使用。
- `fabric.png`：[Fabric 官方站点](https://fabricmc.net/assets/logo.png)。
- `MinecraftForge.png`：[MinecraftForge 官方 GitHub 组织头像](https://avatars.githubusercontent.com/u/1390178?v=4)。
- `neoforged.png`：[NeoForged 官方 GitHub 组织头像](https://avatars.githubusercontent.com/u/138629134?v=4)。
- 原版草方块标识：使用 Minecraft 原始 `grass_block_top.png`、`grass_block_side.png`、`grass_block_side_overlay.png`、`dirt.png` 及 `models/block/grass_block.json`，在 WPF 中按方块模型渲染并应用草色。原始文件保留不变，版权归 Mojang / Microsoft。
- 当前 NeoForge 狐狸头像使用 Minecraft 1.21.1 原版 `textures/entity/fox/fox.png` 的头部纹理；Forge 铁砧使用原版 `textures/block/anvil.png`、`anvil_top.png` 和 `models/block/template_anvil.json` 模型，通过 `MinecraftIconRenderer` 显示。原始文件从本地游戏客户端 JAR 读取，未重绘纹理，版权归 Mojang / Microsoft。之前下载的加载器组织头像保留在资源目录中，已不用于这两个图标。

加载器图标用于识别对应项目；相关名称、图标与商标权利归各项目权利人所有。模组图标按 API 返回的 HTTPS 地址显示，不随源码包附带。
