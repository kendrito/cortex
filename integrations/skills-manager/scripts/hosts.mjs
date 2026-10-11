import assert from "node:assert/strict";
// 已验证的同版本官方宿主组合；发布元数据由契约测试核对。
export const supportedHosts = Object.freeze(["0.1.2-rc.1", "0.1.5-rc.1", "0.1.5-rc.2", "0.1.5-rc.3", "0.1.7-rc.1", "0.1.7-rc.2", "0.2.0-rc.1", "0.2.0-rc.2"]);
export const peerRange = supportedHosts.join(" || ");
export const developmentHost = "0.2.0-rc.2";

export const requiredHostPeers = Object.freeze([
  "@cortex/client-locale",
  "@cortex/client-ui-slots",
  "@cortex/host-webserver",
  "@cortex/session",
  "@cortex/skill",
  "@cortex/tools",
  "@cortex/web-app",
]);
export function assertHostPeers(peers) {
  assert.deepEqual(Object.keys(peers).filter(name => name.startsWith("@cortex/")).sort(), [...requiredHostPeers].sort(), "官方 peer 集合与必需清单不一致");
}
