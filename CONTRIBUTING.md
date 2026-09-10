# 贡献与维护指南

面向本仓库的开发、打包与发版流程。**使用说明请看 [README](README.md)。**

## 环境要求

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## 本地开发

```bash
git clone https://github.com/Mux6201/FolderJumpTool.git
cd FolderJumpTool
dotnet run                # 直接运行（托盘常驻，无主窗口）
dotnet build -c Release   # 只编译
```

## 打包发布版

两种形态的完整命令见 [README → 打包发布版](README.md#打包发布版)（框架依赖 / 自包含），与 CI 完全一致。

## 持续集成

| 工作流 | 触发 | 作用 |
|---|---|---|
| `ci.yml` | push `main` / PR | 只做 Release 编译验证（不产出发布包） |
| `release.yml` | push `v*` 标签 / 手动触发 | 构建两种发布包 + 生成 SHA256；**标签触发**时创建 draft Release，**手动触发**时上传为 Actions Artifacts |

不配置本地环境也可以出包：仓库 **Actions** → 左侧 **release** → **Run workflow** → 填版本号 → 跑完在该运行页面的 **Artifacts** 下载。

## 发版流程

1. 确认 `FolderJumpTool.csproj` 里的 `<Version>` 与即将发布的版本号一致
2. 打标签并推送：

   ```bash
   git tag v0.3.1 && git push origin v0.3.1
   ```

3. 等 `release` 工作流跑完——会自动创建一个 **draft** Release，含两个 zip 与 `SHA256.txt`
4. 在网页补写 Release Notes（中文），确认资产齐全后 **Publish**
5. 若 draft 的标签显示成 `untagged-<sha>`（偶发），在其编辑页重新指定为对应标签即可

## 提交约定

- trunk-based：直接提交到 `main`
- 提交信息用英文，说明"做了什么、为什么"
- 保持构建 **0 错误 0 警告**
