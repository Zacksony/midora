# Event Instrument Loop 进入条件修复

## 需求与根因

用户给定 Template=768、Loop=[192,384)、一个覆盖模板的持续 Note，以及 tick 192/288 的 PitchBend=-7701/8191。Logical Gate=576/768 时应重复 Loop，而不是等到 769 才开始。

源码把 `Gate > Template Length` 得到的 `longNote` 同时用作所有 Loop 时间映射的开关。旧 SRS §10.9.5 的“长音/持续”与后面的“到 Loop End 且 Gate 未结束即跳回”混用了不同判据；两个 Pre-Roll 回归测试也固定了该错误限制。本次以用户明确的 Loop End 判据澄清规格，不能仅对 PitchBend 特判。

## 实施决定

1. 长/短/等长分类继续按 Logical Gate Length 与 Template Length 比较；Mapping 的 gateLength、Pre-Roll、One-Shot 与 End At Template Length 的既有长度/结束规则不改。
2. Loop 的模板时钟单独判定：完整有效 Loop，实际实例局部 Gate horizon 严格超过 Loop End，且不是短音 One-Shot。到 Loop End 才跳回，不从实例起点循环；Gate 恰在 Loop End 或首轮之前结束不重复。Pre-Roll 纳入实例局部 Gate horizon，而非长短音分类。
3. 原始 Template Event、Value Curve、Envelope 映射原始状态及 MappingContext.TemplateTick 必须使用相同 Loop 开关。Note 从 Loop Start 之前开始且覆盖整个 Loop 时不重触发，并保持到已有生命周期的释放/结束点。
4. 短音 Tail 若实际进入循环，尾部按 Gate End 后的 `[Loop End, Template Length)` 映射，所需长度也必须按这一映射计算，不能截掉移位后的尾部；CutAtNoteOff 不新增 Tail。Template/Segment 硬结束继续优先。
5. Full/Incremental/Preview 共用正式编译路径，消费者不补写事件。canonical fingerprint 包含实际输出事件，新增循环事件会改变结果身份；不改变 Project 格式、Mapping ABI、BASS 或 UI。

## 验证门

- 原始复现 Gate=384、576、768、769 及 Loop/Template 两边邻接 tick 的精确 PitchBend 序列、NoteOn/Off 与 Segment reset。
- 同一 compiler 修改 Gate 后 Incremental/Full 事件、来源、分配、诊断和 fingerprint 一致；重复编译确定。
- Curve、模板事件映射、Envelope 与 Logical Parameter Mapping 的 TemplateTick，Pre-Roll、短音 One-Shot、Tail、EndAtTemplate、Segment 截断、Loop 端点、跨 Loop 持续 Note、预览。
- 构建及 Compiler 全套回归；相关 Application、Playback/MIDI Export 回归按影响范围验证。不使用 computer-use，不提交、不发布，不进入下一阶段。

## 已确认的实现与验证证据

- `MidoraCompiler.ExpandInstance` 统一计算独立 `looping`，长短分类只用于生命周期。所有事件枚举及模板时钟映射共用该值；短音 Tail 计算同步采用实际退出点，跨 Loop 持续 Note 使用已有结束点。
- 原始复现先运行于未修复编译器：12 个 Gate 边界用例中 5 个失败，分别为 385、575、576、767、768；失败均为缺少后续 PitchBend。并非播放后端没有响应事件，而是编译结果根本没有重复事件。
- Compiler 全套 437/437 通过；包含本轮 28 个 Loop 进入/生命周期/Mapping/预览用例及修正的两个 Pre-Roll 旧错误断言。固定 Gate 与 held-preview 在 576/768/769 的事件时钟一致。
- Playback 全套 115/115 通过。新增投影测试在 48 kHz、TPQN=480、120 BPM 下确认 Gate=576 的 PitchBend 实际调度到 sample frame 9600/14400/19200/24000；Fragment semantic fingerprint 与最终 PCM key 均因重复事件变化，不复用旧未循环内容。
- MIDI Export 全套 43/43 通过。新增测试编码并重新解析真实 Type 1 SMF 字节，验证 Gate=576/768 的 PitchBend tick/value 序列，而非只检查 Compiler 内存对象。
- Application 全套 1019/1019 通过（2 分 18 秒）；合计四套 1614 项通过，无失败 / 跳过。
- Desktop Release 隔离输出构建成功，0 warning / 0 error；没有改写 `dist` 或启动 UI / 原生发声测试。
- 实施阶段验证为自动化编译、调度、导出和构建；用户随后确认本次修复验收通过，并授权提交、推送。不进行本地发布。
