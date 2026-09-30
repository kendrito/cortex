---
description: "Records a persistence type transition and its compatibility acknowledgement."
kind: persistence-change
---

# 2026-09-29-cortex-testy-audit

English | [中文](2026-09-29-cortex-testy-audit.zh.md)

## Summary

Add durable Testy model-request and model-response audit events and attribution-only Testy message sources. Preserve inert historical Atlassian event types so existing conversation logs remain readable after removal of the Atlassian integration.

## Table of Contents

- [Declaration](#declaration)
- [Compatibility](#compatibility)
- [Verification](#verification)
- [Dev Note](#dev-note)

<a id="declaration"></a>
## Declaration

```yaml persistence-change
schemaVersion: 1
id: 2026-09-29-cortex-testy-audit
baseline: false
changes:
  - root: "event:agent/inbox/spliced"
    previous: "2026-09-21-user-question-reply"
    after: "a3cac4d0844d6d8eb5900153009549a33ae9a11f4cdeaba98301909070783858"
    decision: same-version
  - root: "event:atlassian/activity"
    previous: null
    after: "35ca6eb5e122fff23628cb85059989535c385dc4e1c0c0e95c89534e74be9841"
    decision: same-version
  - root: "event:atlassian/pin"
    previous: null
    after: "a9a9fb417d5f971af68468ead7b86c75659ca6f62c810cdbe4ac76f7e5512827"
    decision: same-version
  - root: "event:atlassian/review"
    previous: null
    after: "fc60875004c05d7d4f8a41f726433dac8d0b7d5e3065c8170efaff9c799ba6e6"
    decision: same-version
  - root: "event:atlassian/search"
    previous: null
    after: "4a1656481a5a101f2a1f9a7b2db274daacb96b74c22095c7334a48023a2cc2e9"
    decision: same-version
  - root: "event:atlassian/snapshot"
    previous: null
    after: "366ab81bccb08eeca47bc99ee43a9eb2e8fd9878ac967369422f91c2638bf204"
    decision: same-version
  - root: "event:developer/message"
    previous: "2026-09-21-user-question-reply"
    after: "4da88a3a4d61b71b96aaa981c0f43f84cd9025f491d30eb7dd9c7215614ce34b"
    decision: same-version
  - root: "event:session/title-llm-request"
    previous: "2026-09-21-user-question-reply"
    after: "d5909d535b6af81afad34c49e7658cf564102421fb65946cb32d2d5125ca27ba"
    decision: same-version
  - root: "event:testy/model-request"
    previous: null
    after: "4f9379aa03c3267cd97b631ec73c12ce22992906b1bdfaa5ac20aa3b2cb904d3"
    decision: same-version
  - root: "event:testy/model-response"
    previous: null
    after: "fc9ee2b0e23bad4ecadd05de3a2f0e5bef734b104ded8b2290bad7cf2d05154b"
    decision: same-version
  - root: "event:user/message"
    previous: "2026-09-21-user-question-reply"
    after: "afc21b1c1fa4da801d4c1161763e4f37982f5d238bafde4498bfd70c13f55780"
    decision: same-version
```

<a id="compatibility"></a>
## Compatibility

These changes add independent event roots and attribution-only source variants. Existing event payloads and the Session header keep their current versions. The historical dsh-session-title-llm wire identifier and persistence digest domains remain stable. Historical Atlassian types do not register tools, network clients, or a live integration.

<a id="verification"></a>
## Verification

Validated all 26 preceding historical schema snapshots and the freshly extracted schema. The structural change classifier marks every addition as same-version compatible. The Testy bridge and real Loader composition tests passed, including exact audit records, selected-model binding, and cancellation.

<a id="dev-note"></a>
## Dev Note

None.
