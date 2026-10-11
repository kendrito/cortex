/** Validate paths before attaching viewer credentials to an API request. */
export function localApiPath(path: string): string {
  if (!path.startsWith("/api/v1/") || /[\\\r\n\t]/.test(path)) {
    throw new Error("Viewer requests must use the local memory API");
  }
  const url = new URL(path, "http://cortex.invalid");
  if (url.origin !== "http://cortex.invalid" || !url.pathname.startsWith("/api/v1/")) {
    throw new Error("Viewer requests must stay within the local memory API");
  }
  return `${url.pathname}${url.search}`;
}
