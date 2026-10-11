/** Local, read-only explorer of stored learning and its recorded relationships. */
import { useEffect, useMemo, useRef, useState } from "preact/hooks";
import type { BrainEdge, BrainEdgePage, BrainKind, BrainNamespace, BrainNode, BrainPage, BrainSummary } from "../../../agent-contract/brain";
import { api } from "../api/client";
import { locale } from "../stores/i18n";
import { BrainGraph } from "../components/brain/BrainGraph";
import { BRAIN_COLORS, BRAIN_KINDS, GRAPH_LIMIT, brainNamespaceKey, brainNamespaceParams, brainNeighbors, filterBrainNodes, visibleBrainEdges } from "../components/brain/brain-model";
import { bt } from "../components/brain/brain-copy";
import { brainRecordPath } from "../components/brain/brain-navigation";
import "../components/brain/brain.css";

const unique = <T extends { id: string }>(values: readonly T[]): T[] => [...new Map(values.map(value => [value.id, value])).values()];
const namespaceLabel = (value: BrainNamespace) => `${value.ownerAgentKind} / ${value.ownerProfileId}${value.ownerWorkspaceId ? ` / ${value.ownerWorkspaceId}` : ""}`;
const dateLabel = (value: number) => value > 0 ? new Date(value).toLocaleDateString(locale.value === "zh" ? "zh-CN" : "en", { year: "numeric", month: "short", day: "numeric" }) : bt("timeUnknown");
const initialKinds = () => new Set<BrainKind>(BRAIN_KINDS);
type BrainViewSummary = BrainSummary & { storage?: { status: "missing" | "ready" } };

/** @returns The read-only Brain workspace containing only records from the selected local store. */
export function BrainView({ embedded = false }: { embedded?: boolean } = {}) {
  const [summary, setSummary] = useState<BrainViewSummary | null>(null);
  const [pages, setPages] = useState<Partial<Record<BrainKind, BrainPage>>>({});
  const [namespaces, setNamespaces] = useState<BrainNamespace[]>([]);
  const [namespaceCursor, setNamespaceCursor] = useState<string | null>(null);
  const [namespace, setNamespace] = useState("");
  const [kinds, setKinds] = useState(initialKinds);
  const [query, setQuery] = useState("");
  const [days, setDays] = useState(0);
  const [view, setView] = useState<"graph" | "list" | "timeline">("graph");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [focus, setFocus] = useState(false);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(new Set<string>());
  const [error, setError] = useState("");
  const [revision, setRevision] = useState(0);
  const [extraEdges, setExtraEdges] = useState<BrainEdge[]>([]);
  const [edgeCursors, setEdgeCursors] = useState<Record<string, string | null>>({});
  const request = useRef<AbortController | null>(null);

  useEffect(() => {
    const controller = new AbortController(); request.current = controller;
    setSelectedId(null); setFocus(false); setExtraEdges([]); setEdgeCursors({}); setLoadingMore(new Set()); setError("");
    setLoading(true); setPages({}); setSummary(null);
    const params = brainNamespaceParams(namespace);
    void Promise.all([
      api.get<BrainViewSummary>(`/api/v1/brain/summary?${params}`, { signal: controller.signal }),
      ...BRAIN_KINDS.map(kind => api.get<BrainPage>(`/api/v1/brain/nodes?${params}&kind=${kind}&limit=40`, { signal: controller.signal })),
    ]).then(results => {
      if (controller.signal.aborted) return;
      const [nextSummary, ...nextPages] = results as [BrainViewSummary, ...BrainPage[]];
      setSummary(nextSummary); setPages(Object.fromEntries(nextPages.map(page => [page.kind, page])));
      if (!namespace) { setNamespaces(nextSummary.namespaces); setNamespaceCursor(nextSummary.namespacesNextCursor); }
    }).catch((reason: Error) => { if (!controller.signal.aborted) setError(reason.message); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [namespace, revision]);

  const loadMore = async (kind: BrainKind) => {
    const page = pages[kind], controller = request.current;
    if (!page?.nextCursor || !controller || loadingMore.has(kind)) return;
    setLoadingMore(current => new Set(current).add(kind));
    const params = brainNamespaceParams(namespace); params.set("kind", kind); params.set("limit", "40"); params.set("cursor", page.nextCursor);
    try {
      const next = await api.get<BrainPage>(`/api/v1/brain/nodes?${params}`, { signal: controller.signal });
      if (!controller.signal.aborted) setPages(current => ({ ...current, [kind]: { ...next, nodes: unique([...(current[kind]?.nodes ?? []), ...next.nodes]), edges: unique([...(current[kind]?.edges ?? []), ...next.edges]) } }));
    } catch (reason) { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : bt("loadError")); }
    finally { if (!controller.signal.aborted) setLoadingMore(current => { const next = new Set(current); next.delete(kind); return next; }); }
  };
  const loadNamespaces = async () => {
    if (!namespaceCursor || loadingMore.has("namespaces")) return;
    const controller = request.current; if (!controller) return;
    setLoadingMore(current => new Set(current).add("namespaces"));
    try {
      const next = await api.get<BrainSummary>(`/api/v1/brain/summary?namespaceCursor=${encodeURIComponent(namespaceCursor)}`, { signal: controller.signal });
      if (!controller.signal.aborted) { setNamespaces(current => [...new Map([...current, ...next.namespaces].map(item => [brainNamespaceKey(item), item])).values()]); setNamespaceCursor(next.namespacesNextCursor); }
    } catch (reason) { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : bt("loadError")); }
    finally { if (!controller.signal.aborted) setLoadingMore(current => { const next = new Set(current); next.delete("namespaces"); return next; }); }
  };

  const nodes = useMemo(() => unique(BRAIN_KINDS.flatMap(kind => pages[kind]?.nodes ?? [])), [pages]);
  const edges = useMemo(() => unique([...BRAIN_KINDS.flatMap(kind => pages[kind]?.edges ?? []), ...extraEdges]), [pages, extraEdges]);
  const totals = summary?.totals;
  const total = totals ? BRAIN_KINDS.reduce((sum, kind) => sum + totals[kind], 0) : 0;
  const filtered = useMemo(() => filterBrainNodes(nodes, kinds, query, days, Date.now()), [nodes, kinds, query, days]);
  const neighborhood = useMemo(() => selectedId ? brainNeighbors(selectedId, edges) : null, [selectedId, edges]);
  const displayed = useMemo(() => focus && neighborhood ? filtered.filter(node => neighborhood.has(node.id)) : filtered, [filtered, focus, neighborhood]);
  const graphNodes = useMemo(() => {
    if (displayed.length <= GRAPH_LIMIT) return displayed;
    const slice = displayed.slice(0, GRAPH_LIMIT);
    if (selectedId && !slice.some(node => node.id === selectedId)) { const selected = displayed.find(node => node.id === selectedId); if (selected) return [...slice.slice(0, GRAPH_LIMIT - 1), selected]; }
    return slice;
  }, [displayed, selectedId]);
  const graphEdges = useMemo(() => visibleBrainEdges(edges, graphNodes), [edges, graphNodes]);
  const selected = nodes.find(node => node.id === selectedId) ?? null;
  const recordPath = selected ? brainRecordPath(selected, { embedded }) : null;
  const related = useMemo(() => selected ? edges.filter(edge => edge.source === selected.id || edge.target === selected.id) : [], [selected, edges]);
  const sorted = useMemo(() => view === "timeline" ? [...displayed].sort((a, b) => b.createdAt - a.createdAt || a.id.localeCompare(b.id)) : displayed, [displayed, view]);
  const remainingKinds = BRAIN_KINDS.filter(kind => kinds.has(kind) && pages[kind]?.nextCursor);
  const hasMore = BRAIN_KINDS.some(kind => pages[kind]?.nextCursor);
  const selectedCursor = selected ? (selected.id in edgeCursors ? edgeCursors[selected.id] : selected.evidence.nextCursor) : null;
  const choose = (id: string | null) => { setSelectedId(id); if (!id) setFocus(false); };
  const clear = () => { setQuery(""); setDays(0); setKinds(initialKinds()); setNamespace(""); setFocus(false); };
  const loadEvidence = async () => {
    const controller = request.current;
    if (!selected || !selectedCursor || !controller || loadingMore.has("edges")) return;
    const id = selected.id, params = brainNamespaceParams(namespace);
    params.set("nodeId", id); params.set("cursor", selectedCursor); params.set("limit", "40");
    setLoadingMore(current => new Set(current).add("edges"));
    try {
      const next = await api.get<BrainEdgePage>(`/api/v1/brain/edges?${params}`, { signal: controller.signal });
      if (!controller.signal.aborted) { setExtraEdges(current => unique([...current, ...next.edges])); setEdgeCursors(current => ({ ...current, [id]: next.nextCursor })); }
    } catch (reason) { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : bt("loadError")); }
    finally { if (!controller.signal.aborted) setLoadingMore(current => { const next = new Set(current); next.delete("edges"); return next; }); }
  };

  return <section class="brain" aria-label={bt("title")}>
    <header class="brain__heading"><div><h1>{bt("title")}</h1><p>{bt("subtitle")}</p></div><div class="brain__actions">
      <span class="brain__badge">{bt("local")}</span>
      <button class="brain__button" disabled={loading} onClick={() => setRevision(value => value + 1)}>{bt("refresh")}</button>
    </div></header>
    {embedded && summary?.storage?.status === "missing" && <div class="brain__limit" role="status"><p>{bt("embeddedMissingStore")}</p></div>}
    {error && nodes.length > 0 && <div class="brain__limit" role="alert"><p>{bt("loadError")}: {error}</p><button class="brain__button" onClick={() => setRevision(value => value + 1)}>{bt("tryAgain")}</button></div>}
    <div class={`brain__workbench${selected ? " brain__workbench--selected" : ""}`}>
      <aside class="brain__filters" aria-label={bt("filters")}>
        <section><h2>{bt("filters")}</h2><div class="brain__field"><label for="brain-search">{bt("filterSearch")}</label><input id="brain-search" class="brain__search" type="search" value={query} placeholder={bt("search")} onInput={event => setQuery(event.currentTarget.value)} /></div></section>
        <section><h2>{bt("categories")}</h2><div class="brain__types">{BRAIN_KINDS.map(kind => <div key={kind} class="brain__type-row">
          <button class="brain__type" style={{ "--node-color": BRAIN_COLORS[kind] }} aria-pressed={kinds.has(kind)} onClick={() => setKinds(current => { const next = new Set(current); if (next.has(kind)) next.delete(kind); else next.add(kind); return next; })}>
            <span class="brain__dot" /><span>{bt(kind)}</span><b>{nodes.filter(node => node.kind === kind).length}/{totals?.[kind] ?? 0}</b>
          </button>{pages[kind]?.nextCursor && <button class="brain__load-type" disabled={loadingMore.has(kind)} onClick={() => void loadMore(kind)} aria-label={bt("loadType", { type: bt(kind) })}>+ {bt("loadMore")}</button>}
        </div>)}</div></section>
        <div class="brain__field"><label for="brain-namespace">{bt("namespace")}</label><select id="brain-namespace" class="brain__select" value={namespace} onChange={event => setNamespace(event.currentTarget.value)}><option value="">{bt("allNamespaces")}</option>{namespaces.map(item => <option value={brainNamespaceKey(item)} key={brainNamespaceKey(item)}>{namespaceLabel(item)}</option>)}</select>
          {namespaceCursor && <button class="brain__load-type" onClick={() => void loadNamespaces()} disabled={loadingMore.has("namespaces")}>{bt("loadNamespaces")}</button>}
        </div>
        <div class="brain__field"><label for="brain-time">{bt("time")}</label><select id="brain-time" class="brain__select" value={days} onChange={event => setDays(Number(event.currentTarget.value))}><option value="0">{bt("anytime")}</option><option value="7">{bt("week")}</option><option value="30">{bt("month")}</option><option value="90">{bt("quarter")}</option></select></div>
        <button class="brain__button" onClick={clear}>{bt("clear")}</button>
        <div class="brain__rail-note"><strong>{bt("evidenceOnly")}</strong><p>{bt("topology")}</p><p>{bt("loadedFilters")}</p><p>{bt("snapshot")}</p></div>
      </aside>
      <div class="brain__stage">
        <div class="brain__toolbar"><div class="brain__views" role="group" aria-label={bt("views")}>{(["graph", "list", "timeline"] as const).map(mode => <button key={mode} aria-pressed={view === mode} onClick={() => setView(mode)}>{bt(mode)}</button>)}</div><span class="brain__counts" role="status">{view === "graph" ? graphNodes.length : displayed.length} {bt("nodes")} / {graphEdges.length} {bt("connections")}</span></div>
        {loading ? <div class="brain__empty" role="status"><ConstellationMark /><h2>{bt("loading")}</h2></div>
          : error && !nodes.length ? <div class="brain__empty" role="alert"><ConstellationMark /><h2>{bt("loadError")}</h2><p>{error}</p><button class="brain__button" onClick={() => setRevision(value => value + 1)}>{bt("tryAgain")}</button></div>
          : !nodes.length ? <div class="brain__empty"><ConstellationMark /><h2>{bt("emptyTitle")}</h2><p>{bt(embedded ? "embeddedEmptyBody" : "emptyBody")}</p></div>
          : !displayed.length ? <div class="brain__empty"><ConstellationMark /><h2>{bt("noMatches")}</h2><p>{bt("noMatchesBody")}</p><button class="brain__button" onClick={clear}>{bt("clear")}</button></div>
          : view === "graph" ? <BrainGraph nodes={graphNodes} edges={graphEdges} selected={selectedId} onSelect={choose} onList={() => setView("list")} />
          : <div class="brain__results">{sorted.map((node, index) => <div key={node.id}>
            {view === "timeline" && (index === 0 || dateLabel(node.createdAt) !== dateLabel(sorted[index - 1].createdAt)) && <h3 class="brain__timeline-date">{dateLabel(node.createdAt)}</h3>}
            <button class="brain__row" style={{ "--node-color": BRAIN_COLORS[node.kind] }} aria-pressed={selectedId === node.id} onClick={() => choose(node.id)}><span class="brain__dot" /><span class="brain__row-copy"><strong>{node.title || bt("untitled")}</strong><p>{node.content || bt("noContent")}</p><small>{bt(node.kind)} — {namespaceLabel(node.namespace)}</small></span></button>
          </div>)}</div>}
        {!loading && remainingKinds.length > 0 && <div class="brain__load-row"><button class="brain__button" disabled={loadingMore.size > 0} onClick={() => remainingKinds.forEach(kind => void loadMore(kind))}>{bt("loadMore")}</button></div>}
      </div>
      {selected && <aside class="brain__drawer" aria-label={bt("detail")}>
        <div class="brain__drawer-head"><span>{bt("detail")}</span><button aria-label={bt("close")} title={bt("close")} onClick={() => choose(null)}>×</button></div>
        <div class="brain__category" style={{ "--node-color": BRAIN_COLORS[selected.kind] }}><span class="brain__dot" />{bt(selected.kind)}</div>
        <h2>{selected.title || bt("untitled")}</h2><p class="brain__drawer-content">{selected.content || bt("noContent")}</p>
        {selected.contentTruncated && <p class="brain__rail-note">{bt(embedded ? "limitedEmbeddedContent" : "limitedContent")}</p>}
        {recordPath && <a class="brain__record-link" href={recordPath}>{bt("openRecord")}</a>}
        <dl><dt>{bt("namespace")}</dt><dd>{namespaceLabel(selected.namespace)}</dd><dt>{bt("created")}</dt><dd>{dateLabel(selected.createdAt)}</dd><dt>{bt("updated")}</dt><dd>{dateLabel(selected.updatedAt)}</dd></dl>
        <button class="brain__button" aria-pressed={focus} onClick={() => setFocus(current => !current)}>{bt(focus ? "unfocus" : "focus")}</button>
        {focus && <p class="brain__rail-note">{bt("focusNotice")}</p>}
        <h3>{bt("connectionsFor")} <span class="brain__counts">{related.length}</span></h3>
        <p class="brain__rail-note">{bt("evidenceShown", { shown: related.filter(edge => edge.source === selected.id).length, total: selected.evidence.total })}</p>
        {!related.length && <p class="brain__rail-note">{bt("noConnections")}</p>}
        {related.map(edge => {
          const otherId = edge.source === selected.id ? edge.target : edge.source;
          const other = nodes.find(node => node.id === otherId);
          return <div key={edge.id}><button class="brain__connection" disabled={!other} onClick={() => other && choose(other.id)}><small>{bt(edge.relation)}{edge.target === selected.id ? ` (${bt("from")})` : ""}</small><strong>{other?.title || edge.referenceId}</strong><small>{other ? bt(other.kind) : bt(edge.targetStatus === "missing" ? "missing" : edge.targetStatus === "outside-filter" ? "outsideFilter" : "outside")}</small><small>{edge.sourceField}</small></button></div>;
        })}
        {selectedCursor && <button class="brain__button" disabled={loadingMore.has("edges")} onClick={() => void loadEvidence()}>{bt("loadEvidence")}</button>}
        <p class="brain__record-id">{bt("identifier")}: {selected.rawId}</p>
      </aside>}
    </div>
    <footer class="brain__limit"><p>{bt("shown", { n: displayed.length, total: nodes.length })}. {bt("stored", { n: total })}. {hasMore && bt("truncated")} {displayed.length > GRAPH_LIMIT && view === "graph" && bt("graphLimit", { n: GRAPH_LIMIT })}</p></footer>
  </section>;
}

function ConstellationMark() {
  return <svg class="brain__empty-symbol" viewBox="0 0 90 90" aria-hidden="true"><circle cx="45" cy="45" r="36" fill="none" stroke="currentColor" stroke-width=".7" opacity=".4" /><path d="M19 52L39 24L69 39L51 69L19 52M39 24L51 69" fill="none" stroke="currentColor" stroke-width="1" />{[[19, 52], [39, 24], [69, 39], [51, 69]].map(([x, y], i) => <circle key={i} cx={x} cy={y} r={i === 1 ? 4 : 3} fill="currentColor" />)}</svg>;
}
