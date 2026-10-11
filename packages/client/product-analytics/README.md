# UI event types

Cortex retains upstream event types so optional UI callbacks remain source-compatible. This package has no collector, Cordis plugin, RPC, storage, network client, or exporter. No `productAnalytics` service is installed in shipped profiles, so optional calls do nothing.

## Model Experience

No model input, token use, or KV-cache effect.

## Known Limitations and Deferred Work

The declarations intentionally provide no runtime implementation.

No runtime invariant companion is published because this package retains only compatibility types and owns no live service or state.
