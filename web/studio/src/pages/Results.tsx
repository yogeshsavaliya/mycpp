import { useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api, download, type MethodDetail, type MethodSummary, type Project } from "../api";
import { highlight } from "../highlight";

type Tab = "code" | "diagnostics" | "input" | "report";

export function ResultsPage() {
  const params = useParams();
  const projectId = params.projectId ?? "";
  const [project, setProject] = useState<Project | null>(null);
  const [methods, setMethods] = useState<MethodSummary[]>([]);
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState<number | null>(null);
  const [detail, setDetail] = useState<MethodDetail | null>(null);
  const [tab, setTab] = useState<Tab>("code");
  const [report, setReport] = useState("");
  const [input, setInput] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  useEffect(() => {
    api<Project>("/api/projects/" + projectId).then(setProject).catch((reason: Error) => setError(reason.message));
    api<{ methods: MethodSummary[] }>("/api/projects/" + projectId + "/methods")
      .then((body) => {
        setMethods(body.methods);
        setSelected(body.methods[0]?.index ?? null);
        setNotice("");
      })
      .catch((reason: Error) => setNotice(reason.message));
  }, [projectId]);

  useEffect(() => {
    if (selected === null)
      return;
    api<MethodDetail>("/api/projects/" + projectId + "/methods/" + selected)
      .then(setDetail)
      .catch((reason: Error) => setError(reason.message));
  }, [projectId, selected]);

  useEffect(() => {
    if (tab === "report")
      api<unknown>("/api/projects/" + projectId + "/report").then((body) => setReport(JSON.stringify(body, null, 2))).catch((reason: Error) => setReport(reason.message));
    if (tab === "input")
      api<unknown>("/api/projects/" + projectId + "/input").then((body) => setInput(JSON.stringify(body, null, 2))).catch((reason: Error) => setInput(reason.message));
  }, [tab, projectId]);

  const tree = useMemo(() => group(methods), [methods]);

  async function exportKind(kind: string, filename: string) {
    await download("/api/projects/" + projectId + "/export/" + kind, filename);
  }

  return (
    <>
      <header className="page-head">
        <div>
          <h1>{project?.name ?? "Results"}</h1>
          <p>Recovered code has not been checked against the original application’s behaviour. This is an analysis view, not a rebuilt Unity Editor project.</p>
        </div>
        <div className="row">
          <button className="btn-secondary" onClick={() => exportKind("csharp", "recovered-csharp.zip").catch((reason: Error) => setError(reason.message))}>Export C#</button>
          <button className="btn-secondary" onClick={() => exportKind("report", "recovery-report.json").catch((reason: Error) => setError(reason.message))}>Export report</button>
          <button className="btn-secondary" onClick={() => exportKind("map", "native-map.json").catch((reason: Error) => setError(reason.message))}>Export map</button>
          <button className="btn-secondary" onClick={() => exportKind("diagnostics", "diagnostics.txt").catch((reason: Error) => setError(reason.message))}>Export diagnostics</button>
          <button className="btn" onClick={() => exportKind("full", "analysis-export.zip").catch((reason: Error) => setError(reason.message))}>Export full results</button>
        </div>
      </header>
      {error && <p className="error">{error}</p>}
      {notice && (
        <div className="card empty">
          <p>{notice}</p>
          <Link to="/projects">Back to Projects</Link>
        </div>
      )}
      {!notice && (
        <div className="results-split">
          <section className="card">
            <input type="search" value={query} placeholder="Search methods" title="Searches method names and recovered code" onChange={(event) => {
              const value = event.target.value;
              setQuery(value);
              api<{ methods: MethodSummary[] }>("/api/projects/" + projectId + "/methods?q=" + encodeURIComponent(value))
                .then((body) => setMethods(body.methods))
                .catch((reason: Error) => setError(reason.message));
            }} />
            <div className="tree">
              {tree.map((assembly) => (
                <details key={assembly.name} open>
                  <summary>{assembly.name}</summary>
                  {assembly.namespaces.map((namespace) => (
                    <details key={namespace.name} open>
                      <summary>{namespace.name || "(no namespace)"}</summary>
                      {namespace.types.map((type) => (
                        <details key={type.name} open>
                          <summary>{type.name}</summary>
                          {type.methods.map((method) => (
                            <button key={method.index} className={method.index === selected ? "method-button selected" : "method-button"} onClick={() => setSelected(method.index)}>
                              {method.name}
                              <small>{method.statusLabel}{method.placeholder ? " · placeholder" : ""}{featureLabel(method)}</small>
                            </button>
                          ))}
                        </details>
                      ))}
                    </details>
                  ))}
                </details>
              ))}
            </div>
          </section>
          <section className="card stack">
            {detail && (
              <>
                <div className="row" style={{ justifyContent: "space-between" }}>
                  <div>
                    <h2 style={{ margin: 0 }}>{detail.summary.declaringType}.{detail.summary.name}</h2>
                    <div className="muted">{detail.signature}</div>
                  </div>
                  <span className="status warn">{detail.summary.statusLabel}{featureLabel(detail.summary)}</span>
                </div>
                {detail.csharpIsPlaceholder && <div className="notice"><strong>Method body not recovered.</strong><div>The text below is a throwing placeholder. It is not recovered logic.</div></div>}
                <div className="tabs">
                  {(["code", "diagnostics", "input", "report"] as Tab[]).map((item) => (
                    <button key={item} className={tab === item ? "on" : ""} onClick={() => setTab(item)}>
                      {item === "code" ? "Code" : item === "diagnostics" ? "Diagnostics" : item === "input" ? "Input" : "Recovery report"}
                    </button>
                  ))}
                </div>
                {tab === "code" && (
                  <div className="code-split">
                    <div>
                      <h3>C#</h3>
                      <pre dangerouslySetInnerHTML={{ __html: highlight(detail.csharp || "// No C# was emitted for this method.") }} />
                    </div>
                    <div>
                      <h3>Native instructions</h3>
                      <pre>{detail.instructions.length === 0 ? "No native instructions were recorded for this method." : detail.instructions.map((item) => item.text).join("\n")}</pre>
                    </div>
                  </div>
                )}
                {tab === "diagnostics" && (
                  <div>
                    {detail.diagnostics.length === 0 && <p>No diagnostics were recorded for this method.</p>}
                    {detail.diagnostics.map((line) => <p key={line}>{line}</p>)}
                    <p className="muted">Boundary: {detail.boundary}. Compilation: {detail.compilation}. Behaviour: {detail.behaviour}.</p>
                    <p className="muted">{detail.validationNotice}</p>
                    <h3>Cross-references</h3>
                    {detail.provenance.length === 0 && <p>No cross-references were produced for this method.</p>}
                    {detail.provenance.map((link) => <p key={link.operation}>{link.operation}: {link.nativeAddresses.join(", ")}</p>)}
                  </div>
                )}
                {tab === "input" && <pre>{input || "Loading input information…"}</pre>}
                {tab === "report" && (
                  <>
                    {featureSummary(report) && <p>{featureSummary(report)}</p>}
                    <pre>{report || "Loading the recovery report…"}</pre>
                  </>
                )}
              </>
            )}
          </section>
        </div>
      )}
    </>
  );
}

function featureLabel(method: MethodSummary) {
  if (method.featureComplete === true)
    return " · feature-complete";
  if (method.featureComplete === false && !method.placeholder && method.status === "RecoveredUnvalidated")
    return " · dependency-incomplete";
  return "";
}

function featureSummary(report: string) {
  try {
    const body = JSON.parse(report) as { breakdown?: { scope?: string; featureComplete?: number; emittedWithoutPlaceholder?: number }[] };
    if (!body.breakdown)
      return "";
    return body.breakdown.map((stage) => (stage.scope ?? "scope") + ": " + (stage.featureComplete ?? 0) + " feature-complete of " + (stage.emittedWithoutPlaceholder ?? 0) + " emitted").join(" · ");
  } catch {
    return "";
  }
}

function group(methods: MethodSummary[]) {
  const assemblies = new Map<string, Map<string, Map<string, MethodSummary[]>>>();
  for (const method of methods) {
    const namespaces = assemblies.get(method.assembly) ?? new Map();
    assemblies.set(method.assembly, namespaces);
    const types = namespaces.get(method.namespace) ?? new Map();
    namespaces.set(method.namespace, types);
    const list = types.get(method.declaringType) ?? [];
    list.push(method);
    types.set(method.declaringType, list);
  }
  return [...assemblies.entries()].map(([name, namespaces]) => ({
    name,
    namespaces: [...namespaces.entries()].map(([namespaceName, types]) => ({
      name: namespaceName,
      types: [...types.entries()].map(([typeName, typeMethods]) => ({ name: typeName, methods: typeMethods }))
    }))
  }));
}
