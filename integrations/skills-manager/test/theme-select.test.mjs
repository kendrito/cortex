// 筛选下拉和操作菜单走 Ant Design，不再自绘触发器。
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { handleSourceMenuEscape } from "../src/source-select-model.ts";

const selectSource = await readFile(new URL("../src/source-select.ts", import.meta.url), "utf8");
assert.ok(selectSource.includes("popupMatchSelectWidth: false"), "筛选下拉按内容展开，不跟触发器等宽");
assert.ok(selectSource.includes("danger: option.danger"), "操作菜单保留危险标记");
assert.ok(selectSource.includes("handleSourceMenuEscape"), "打开的筛选或操作菜单把 Escape 交给独立处理函数");
assert.ok(!selectSource.includes("dsh-client-ui-primitives"), "下拉不再使用宿主菜单");

const escapeCalls = [];
assert.equal(handleSourceMenuEscape({
  key: "Escape",
  preventDefault: () => escapeCalls.push("prevent"),
  stopPropagation: () => escapeCalls.push("stop"),
  stopImmediatePropagation: () => escapeCalls.push("stopImmediate"),
}, () => escapeCalls.push("close")), true, "Escape 关闭打开的菜单");
assert.deepEqual(escapeCalls, ["prevent", "stop", "stopImmediate", "close"], "菜单 Escape 不再传到宿主设置页");
assert.equal(handleSourceMenuEscape({
  key: "Tab",
  preventDefault: () => escapeCalls.push("prevent"),
  stopPropagation: () => escapeCalls.push("stop"),
  stopImmediatePropagation: () => escapeCalls.push("stopImmediate"),
}, () => escapeCalls.push("close")), false, "其它按键不关闭菜单");

const source = await readFile(new URL("../src/client.ts", import.meta.url), "utf8");
assert.ok(source.includes('className: "dssm-search"'), "搜索框使用 Ant Design 输入框");
assert.ok(!source.includes('h("input", { className: "dssm-control'), "可见文本输入不能继续使用自定义控件");
assert.ok(!source.includes("dssm-fallback-switch"), "开关不再保留自绘兜底");
assert.ok(!source.includes("dssm-host-modal"), "弹窗不再套一层自绘外壳");
assert.ok(source.includes('className: "dssm-row-state"'), "状态与开关必须在同一组");
assert.ok(!source.includes("dsh-client-ui-primitives"), "设置页不再依赖宿主界面组件");
const updater = await readFile(new URL("../src/plugin-update-ui.ts", import.meta.url), "utf8");
assert.ok(!updater.includes("fetch("), "Removed updater does not issue network requests");
assert.ok(!source.includes("observePluginUpdate"), "Cortex owns plugin updates; no updater is mounted");
console.log("Ant Design 筛选与残留样式检查通过");
