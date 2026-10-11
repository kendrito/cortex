---
description: "Records a persistence type transition and its compatibility acknowledgement."
kind: persistence-change
---

# 2026-10-10-cortex-local-capabilities

English | [中文](2026-10-10-cortex-local-capabilities.zh.md)

## Summary

Adds durable working-directory transitions and their attribution-only model notices, and a versioned subagent catalog payload for managed external and local activations.

## Table of Contents

- [Declaration](#declaration)
- [Compatibility](#compatibility)
- [Verification](#verification)
- [Dev Note](#dev-note)

<a id="declaration"></a>
## Declaration

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
## Compatibility

Session format V4 is retained. Existing catalog payload versions and one-shot descriptors remain readable; new event and attribution kinds extend the vocabulary without changing historical declarations. The original project directory in the immutable header is unchanged. Plugin records use their registered namespaces and preserve replayable Session state.

<a id="verification"></a>
## Verification

Focused native Windows working-directory and Worktree acceptance: 58 passed, 3 platform-only skipped. SDK wait/shutdown, persistent PowerShell, and plugin record catalog/caller checks: 174 passed. Managed activation cold-resume tests passed after regenerating known event types. Final build and MemOS integration acceptance are recorded separately.

<a id="dev-note"></a>
## Dev Note

None.
