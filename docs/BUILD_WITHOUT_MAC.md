# 没有 Mac 怎么构建（云端 macOS）

构建那一步（Xcode / Godot iOS 导出 / NativeAOT / 首次签名与大内存权限）是 **macOS 独占**。没有自己的 Mac，就借一台云端 macOS。下面两条都是真路，先说红线。

## 红线（先读）

- 构建要用到**你自己的正版游戏文件**（`sts2.dll` + `Slay the Spire 2.pck` + 一堆依赖 dll）。这些是**版权文件，绝不能上传到公共仓库或公共 CI**。云端方案里，游戏文件只能走你**私有、临时**的通道传上去，用完即删。
- 第三方库（FMOD/Spine）同理，各自去官网下（见 [`THIRD_PARTY_LIBS.md`](THIRD_PARTY_LIBS.md)），别转发。

## 方案 A（推荐，最省心）：租一台云 Mac

远程进去，把它当本地 Mac，**照着 [`../AGENTS.md`](../AGENTS.md) 的步骤原样做**（含在 Xcode GUI 里登录你自己的 Apple ID、加 Increased Memory capability、签名装机）。与文档 100% 一致，没有额外坑。

- **服务**（按小时/按天计费，构建一次通常一两小时内，退租即停）：AWS EC2 Mac、MacStadium、MacinCloud、Scaleway Mac mini，以及其它按小时的云 Mac。挑一个能远程桌面/SSH 的即可。
- **把游戏文件弄上去**：本机 Steam 里的 `SlayTheSpire2.app` 打包，经你自己的网盘 / `scp` 传到云 Mac；FMOD/Spine 库同理。到了之后填 `ios-export/config.sh` 指向它们。
- **优点**：免费 Apple ID 在 Xcode GUI 里登录就能签，`increased-memory-limit` 能正常注册（这点在无头 CI 上很难，见方案 B）。
- **缺点**：要花点租金；首次配环境（Xcode、.NET 9、Godot 4.5.1 mono、iOS 导出模板）要些时间。云 Mac 的 Xcode/工具版本尽量对齐本项目环境要求。

## 方案 B（进阶）：GitHub Actions macOS runner 自动化命令行部分

GitHub 提供预装 Xcode 的 macOS runner，可把 `build-ios.sh` 的**命令行六步**（织入 → AOT → 导出 → pck 三连）自动跑出产物（Xcode 工程 + `StS2.pck` + `sts2.dylib`）。适合想重复自动构建的人。**诚实的边界**：

1. **游戏文件不能进公共仓库**。用**私有仓库**，游戏文件/库通过加密方式带入——例如从你的私有存储用 `secrets` 里的凭据拉取；或干脆用**自托管 runner**（你自己的机器/云 Mac 当 runner）。
2. **签名仍需你的 Apple 身份**。免费账号在无头 CI 上签名很别扭。可行做法：CI 只产出**未签名/ad-hoc** 的包，**签名交给手机上的 SideStore**（它用你的 Apple ID 重签，见 [`RENEWAL.md`](RENEWAL.md)）。
3. **大内存权限**（`increased-memory-limit`）需在你的 App ID 能力里登记，realistically 要**用一次 Xcode**。完全不碰 Xcode 时这个权限较难拿到——这正是方案 A 更省心的原因。

骨架（**模板**，需你自己补私有游戏文件/库/签名，不能原样跑）：

```yaml
name: build-ios (template)
on: workflow_dispatch
jobs:
  build:
    runs-on: macos-latest          # 预装 Xcode；版本尽量对齐本项目
    steps:
      - uses: actions/checkout@v4
      - name: 装依赖
        run: |
          # 装 .NET 9 SDK；下载 Godot 4.5.1 mono；导入 iOS 导出模板 (templates.tpz)
      - name: 取回你的游戏文件 + FMOD/Spine 库（私有，勿入公共仓库！）
        env:
          FETCH_TOKEN: ${{ secrets.FETCH_TOKEN }}
        run: |
          # 从你的私有存储拉 sts2.dll / *.pck / ios-libs / fmod-ios
          # cp ios-export/config.example.sh ios-export/config.sh 并按拉到的路径填好
      - name: 命令行构建（到 pck 三连为止）
        run: bash ios-export/build-ios.sh
      - uses: actions/upload-artifact@v4
        with:
          name: sts2-build
          path: ios-export/build      # Xcode 工程 + StS2.pck；signing 交给手机 SideStore
```

## 结论

- **想省心、一次到位、含大内存权限** → 方案 A（云 Mac），照文档原样走。
- **想自动化重复构建、且愿意处理私有文件 + 签名** → 方案 B（GitHub Actions 命令行部分），签名走 SideStore / 自托管 runner。

无论哪条，**游戏文件始终只在你私有的通道里流动**，不进任何公共位置。
