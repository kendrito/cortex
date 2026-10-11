import type { ApiCall } from '../../src/client-types.js';
import type { InstallRecord, RepositoryDependencies } from '../../src/types.js';
import { createSkill, setSkillEnabled } from '../../src/core.js';
import { parseRepositoryInput } from '../../src/repositories.js';
// 仅由 tsc 检查，不在运行时执行；确保跨模块错误不会重新退化为动态类型。
declare const api: ApiCall;
declare const dependencies: RepositoryDependencies;
const repo = parseRepositoryInput({ url: 'owner/repo', ref: 'main' });
repo.owner.toUpperCase();
// @ts-expect-error 仓库分支必须是文本。
parseRepositoryInput({ url: 'owner/repo', ref: 123 });
// @ts-expect-error 启停状态必须是布尔值。
setSkillEnabled('dsh', 'sample', 'false');
// @ts-expect-error 技能正文不能误传为数字。
createSkill({ name: 'sample', description: '测试', body: 42 });
api('/state').then(snapshot => {
    snapshot.roots[0].skills[0].name.toUpperCase();
    // @ts-expect-error 技能名不是数字。
    snapshot.roots[0].skills[0].name.toFixed();
});
api('/repositories/preview').then(preview => {
    preview.token.toUpperCase();
    // @ts-expect-error 预览响应没有安装结果的名称字段。
    preview.name;
});
// @ts-expect-error 未登记接口不能被请求。
api('/repositories/unknown-action');
const serialResult: Promise<number> = dependencies.serialize(async () => 1);
void serialResult;
// @ts-expect-error 串行队列必须保留任务返回值的类型。
const wrongResult: Promise<string> = dependencies.serialize(async () => 1);
void wrongResult;
declare const record: InstallRecord;
// @ts-expect-error 文件摘要必须包含哈希，不能只记录路径。
record.files.push({ path: 'SKILL.md' });
