# Midora 版本与兼容性策略

状态：已接受；自 2026-08-25 起执行

## 1. 当前版本矩阵

| 契约 | 当前版本 | 作用 |
|---|---:|---|
| Midora 产品版本 | `1.0.0-dev` | 发布、EXE、诊断、Project manifest 与 MIDI Export Readme |
| CLR AssemblyVersion | `1.0.0.0` | 1.x 程序集绑定身份 |
| Windows FileVersion | `1.0.0.0` | Windows 文件属性 |
| `.midora` Project Format | 写 `4`，读 `1/2/3/4` | 当前组合格式；旧格式 detached 迁移及永久原字节备份 |
| Format 1 JSON/object schema | `1` | 各 Format 1 结构性文件与对象 wire contract |
| Mapping Function ABI | `3` | Project 内受限表达式语言与求值契约 |

用户可编辑的 `metadata.projectVersion` 是作品标签自由文本，不属于上述任何兼容性契约。

## 2. 产品版本

产品版本使用 Semantic Versioning：

```text
MAJOR.MINOR.PATCH[-prerelease]
```

- PATCH：BUG、性能和 UI 修复，不故意破坏公开语义。
- MINOR：向后兼容的新能力；允许写出新的 Project Format，但新软件必须能读取本产品线先前正式发布的 Project Format。
- MAJOR：有意破坏正式用户工作流、项目读取承诺或其他公开契约。

版本唯一来源是 `eng/Version.props`。源码、Project manifest、MIDI Export Readme 和未来 About/发布信息不得维护第二份产品版本常量。开发构建的 `AssemblyInformationalVersion` 允许追加 Git commit build metadata，例如 `1.0.0-dev+<commit>`；`ProductVersion` 仍为 `1.0.0-dev`。

1.x 的 `AssemblyVersion` 固定为 `1.0.0.0`。`FileVersion` 跟随三段产品版本并使用数值第四段 `0`。

## 3. Project Format 1 冻结

从本策略生效时起，当前 `.midora` Format 1 是第一首完整 Midora 作品及 1.0.0 的持久化兼容基线：

- 已存在的 Format 1 Project 必须在后续修复版中持续可读、可编译、可编辑并可安全重存。
- Format 1 JSON schema、字段含义、protobuf 字段号/含义、Pure MIDI page-pack wire 规则和包结构不得原地改变。
- 不得把旧字段重新解释为新语义，不得复用已发布 protobuf 字段号，不得通过猜测缺失字段静默升级。
- 不改变已冻结契约的 codec BUG 修复仍须通过 Format 1 golden、旧文件重开和 deterministic round-trip 回归。
- 需要新持久化字段或新 wire 语义时，必须引入 Project Format 2、独立 schema/codec，以及 detached 的 `V1 → current` 内存迁移；不得修改 `PersistenceContractV1` 冒充原格式。
- 打开旧格式只在内存中迁移，不覆盖源文件；保存前明确提示升级，保存事务只写当前格式且保持原子。
- 新软件必须读取本产品线已经正式发布的全部旧格式。旧软件不承诺读取未来格式；遇到未来版本必须在修改任何 Project 状态前明确拒绝。

Format 1 使用严格未知字段拒绝，因此当前固定：

```text
minimumReadableVersion == fileFormatVersion == 1
```

在真正设计、实现并验证前向兼容读取前，不得用 `minimumReadableVersion` 宣称旧 reader 可以读取新格式。

## 4. 组件 schema 与 ABI

`fileFormatVersion` 表示整个 package 的组合版本；每个 JSON/protobuf/content-pack 的 schema 或 wire version 表示对应组件。未来 Project Format 可以引用未变化组件的旧 schema，不要求所有组件一起升版。

Mapping Function ABI、Application Preferences schema、Batch Edit preset schema、Worker IPC 与缓存 generation 均独立管理：

- Mapping ABI 只有在既有表达式语义可能变化时升版；旧 ABI 必须保留安全执行器或显式迁移/拒绝，不能静默按新语义执行。
- Application Preferences 与用户预设使用独立轻量迁移，不影响 `.midora` 兼容判断。
- Worker IPC 由同一发布包内主程序和 Worker 精确匹配，失配时启动失败。
- 音频/图形/编译缓存不是用户数据；语义改变时提升 generation 并淘汰旧缓存，不做数据迁移。

## 5. Git 与发布

- `main` 是开发主线；正式版本使用 annotated、不可移动的 `v<version>` tag。
- 当前阶段：`1.0.0-dev` → `1.0.0-rc.1` → 必要的后续 RC → `1.0.0`。
- RC 后只接受发布阻断修复、文档/许可修复和有明确回归证据的低风险变更。
- 每个正式版本更新 `CHANGELOG.md`、兼容矩阵和发布检查结果。
- 发布 tag 必须指向干净工作区验证过的 commit；同一个版本号和 tag 不得重打或移动。
- 正式产物、SHA-256、许可证、第三方 notices 与 BASS 分发授权核验结果共同归属于该 release。

只有在需要同时维护 1.0.x hotfix 与 1.1 开发时才建立 `release/1.0`；在此之前不增加长期发布分支。
