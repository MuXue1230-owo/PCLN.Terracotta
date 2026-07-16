# Changelog

## 0.1.0-alpha.5

- Push IPC events: `peer.joined` / `peer.left` / `peer.updated` / `network.updated` / `room.state-changed`
- Helper capability `events.push`; background membership/quality poll every 2s
- Connected ↔ Reconnecting based on network health probes
- Duplex plugin IPC client with demuxed responses and event channel
- Plugin consumes push events to update snapshots without full manual refresh

## 0.1.0-alpha.4

- Register four plugin exports via `pcl.exports`: `room-service`, `session-service`, `network-status`, `diagnostics`
- Expand Contracts with session/network/diagnostics interfaces and `RefreshStatusAsync`
- Poll helper room status while connected; diagnose updates members/quality
- Helper live refresh: Scaffolding player list, TCP RTT probe, coarse NAT classification
- Package optional `easytier-core` native assets; CI can download when `EASYTIER_VERSION` is set
- State machine allows Connected/Reconnecting ↔ Diagnosing

## 0.1.0-alpha.3

- Deterministic mesh endpoints and host mesh ingress
- Member EasyTier `--port-forward` path with local-discovery fast path
- Optional `TERRACOTTA_EASYTIER_ALLOW_TUN`

## 0.1.0-alpha.2

- Default `EasyTierRoomBackend`, room credentials, Scaffolding integration
- Fail closed with `network.easytier-missing`

## 0.1.0-alpha.1

- Initial dual-process plugin/Helper vertical slice
