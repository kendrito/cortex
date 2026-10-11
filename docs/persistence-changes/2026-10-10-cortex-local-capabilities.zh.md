---
description: "记录持久化类型更改及其兼容性确认。"
kind: persistence-change
---

# 2026-10-10-cortex-local-capabilities

[English](2026-10-10-cortex-local-capabilities.md) | 中文

## 概述

新增持久化工作目录变更、仅用于来源标记的模型通知，以及用于托管外部和本地激活的版本化子代理目录载荷。

## 目录

- [声明](#declaration)
- [兼容性](#compatibility)
- [验证](#verification)
- [开发备注](#dev-note)

<a id="declaration"></a>
## 声明

```yaml persistence-change
schemaVersion: 1
id: 2026-10-10-cortex-local-capabilities
baseline: false
changes:
  - root: "event:agent/inbox/spliced"
    previous: "2026-09-29-cortex-testy-audit"
    after: "545272fcca1401d46c61d879251ecc0f3494bda366546f12d479b06bd66efbba"
    decision: same-version
  - root: "event:developer/message"
    previous: "2026-09-29-cortex-testy-audit"
    after: "f74c8066d173216a9f0472a6220f2f725b3f3c52ff5128f042f427e13aaff36d"
    decision: same-version
  - root: "event:session/title-llm-request"
    previous: "2026-09-29-cortex-testy-audit"
    after: "156f49a25aa8bac64a4ac3f29eb66f2654a28596785c094bf3302b5bd70a4621"
    decision: same-version
  - root: "event:subagent/catalog"
    previous: "2026-09-20-unknown-child-catalog"
    after: "53750b9abf61efb7992b0aadc3e247ec927449c63266df28230fd1139f5590ac"
    decision: same-version
  - root: "event:testy/model-request"
    previous: "2026-09-29-cortex-testy-audit"
    after: "189981b5be0a490088b1221d824f6831230aafc858dab9e31c45931112dee3f9"
    decision: same-version
  - root: "event:user/message"
    previous: "2026-09-29-cortex-testy-audit"
    after: "1c76b1cec0068dbcb179ca2156bf164ef7055a1b031e5f3fa63584574257ff4f"
    decision: same-version
  - root: "event:working-directory/change"
    previous: null
    after: "124aa62078ca3311353b39cc3bf6c75da281338be6bbf672f8b361d828cddca3"
    decision: same-version
```

<a id="compatibility"></a>
## 兼容性

保留 Session V4 格式。旧目录载荷版本和一次性子代理描述仍可读取；新增事件和来源类型扩展词汇，不修改历史声明。不可变头部中的原始项目目录保持不变。插件记录使用已注册的命名空间并保留可重放状态。

<a id="verification"></a>
## 验证

Windows 原生工作目录和 Worktree 验证通过 58 项，跳过 3 项仅适用于其他平台的测试。SDK 等待和关闭、持久 PowerShell、插件记录目录和调用检查通过 174 项。重新生成已知事件类型后，托管激活的冷恢复测试通过。最终构建和 MemOS 集成验证另行记录。

<a id="dev-note"></a>
## 开发备注

无。
