# DingConfig

基于钉钉云文档的游戏配表协作工具 —— 策划在浏览器实时编辑在线表格后，在Unity里一键拉取钉钉最新表格数据进行后续操作，实现多人协作时零冲突、零锁文件。

## 安装

### Unity Package Manager

1. 打开 Unity → **Window → Package Manager**
2. 点击左上角 **+** → **Add package from git URL...**
3. 输入：
   ```
   https://github.com/XBlueC/DingConfig.git#main
   ```
4. 点击 **Add**

## 使用

1. 打开 `Tools > 配表工具`
2. **空间配置** Tab：
   - 填入钉钉知识库或文件夹 URL
   - 设置本地 Datas 目录路径
   - 设置导出脚本路径（如 Luban 的 gen.bat）
3. **文件映射** Tab：
   - 点击「刷新映射」— 扫描在线文档和本地文件，标注变更状态
   - 点击「拉取变更」— 增量下载有变更的文件
   - 点击「导出」— 在新终端窗口执行导出脚本

日常操作：**刷新映射 → 拉取变更 → 导出**，三步两次点击。

### 文件状态说明

| 状态 | 含义 |
|------|------|
| 🟠 有变更 | 在线修改时间比上次同步新 |
| 🟢 最新 | 与在线一致 |
| 🔵 新增 | 在线有但本地没有 |
| ⚫ 仅本地 | 本地有但在线没有 |


## 为什么需要它？

游戏项目的配表（xlsx）丢在 Git/SVN 里，多人协作时经常遇到：

- 两人同时改同一张表 → 合并冲突，xlsx 没法 merge
- "这张表我在改，你们别动" → 人肉锁，效率低
- 手动导出拷贝到项目目录 → 容易漏、容易错

DingConfig 的思路：**把在线表格作为 Single Source of Truth，xlsx 只是本地缓存。**

```
策划在浏览器编辑在线表格
        ↓
dws CLI 通过 API 拉取数据
        ↓
Unity Editor 一键同步到本地 Datas/
        ↓
导出脚本（Luban 等）生成运行时数据
```

## 功能

- **增量同步**：基于 `modifiedTime` 时间戳对比，只下载有变更的文件。
- **dws CLI 集成**：自动检测安装状态和登录状态

## 前置条件

1. [**dws CLI**](https://open.dingtalk.com/document/development/dingtalk-cli-performing-tasks-within) — 钉钉官方命令行工具

2. **钉钉知识库/文件夹** — 在其中创建在线电子表格（axls）作为配表载体

3. **Unity 2021.3+**

## 版本管理

- **在线表格 = 当前工作区**：始终维护当前开发版本的配表
- **多版本并行**：知识库里建多个文件夹，DingConfig 换个 URL 就切过去了
- 虽然云文档变成了数据源头，但也可使用Git作为版本档案馆，拉取下来的 xlsx 进 Git，`git log` 随时回溯

## todo 

- 在云表格更新时只下载变更范围

## License

MIT
