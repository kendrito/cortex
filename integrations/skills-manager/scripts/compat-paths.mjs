import { realpath } from "node:fs/promises";
import { resolve } from "node:path";

function identity(path) {
  const canonical = resolve(path);
  return process.platform === "win32" ? canonical.toLowerCase() : canonical;
}

/** 展开 8.3 短路径后再比较，避免 Windows CI 临时目录与 realpath 后的会话 cwd 对不上。 */
export async function sameExistingPath(left, right) {
  try {
    return identity(await realpath(left)) === identity(await realpath(right));
  } catch {
    return identity(left) === identity(right);
  }
}

export async function resolveTempRoot(path) {
  return realpath(path);
}
