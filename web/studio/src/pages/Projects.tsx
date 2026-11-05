import { useEffect, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { api, formatBytes, formatWhen, type Project } from "../api";
import { Status } from "../Shell";

type Assembly = { name: string; typeCount: number; methodCount: number; sizeLabel: string };

export function ProjectsPage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [query, setQuery] = useState("");
  const [projects, setProjects] = useState<Project[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [assemblies, setAssemblies] = useState<Assembly[]>([]);
  const [assemblyQuery, setAssemblyQuery] = useState("");
  const [catalogNote, setCatalogNote] = useState("");
  const [scope, setScope] = useState("");
  const [confirm, setConfirm] = useState(false);
  const [error, setError] = useState("");

  async function load(search = query) {
    const list = await api<Project[]>("/api/projects?q=" + encodeURIComponent(search));
    setProjects(list);
    const requested = searchParams.get("project");
    setSelected((current) => {
      if (requested && list.some((item) => item.id === requested))
        return requested;
      return current && list.some((item) => item.id === current) ? current : list[0]?.id ?? null;
    });
  }

  useEffect(() => {
    load("").catch((reason: Error) => setError(reason.message));
    // The first load is intentional.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const project = projects.find((item) => item.id === selected) ?? null;

  useEffect(() => {
    if (!project) {
      setAssemblies([]);
      return;
    }
    api<{ assemblies: Assembly[]; message?: string; sizeExplanation?: string; scope?: string }>("/api/projects/" + project.id + "/catalog")
      .then((body) => {
        setAssemblies(body.assemblies);
        setCatalogNote(body.message || body.sizeExplanation || "");
        setScope(body.scope || project.scope);
      })
      .catch((reason: Error) => setError(reason.message));
  }, [project]);

  async function analyze() {
    if (!project)
      return;
    const job = await api<{ id: string }>("/api/projects/" + project.id + "/analyze", { method: "POST", body: "{}" });
    navigate("/jobs/" + job.id);
  }

  async function remove() {
    if (!project)
      return;
    await api("/api/projects/" + project.id, { method: "DELETE" });
    setConfirm(false);
    await load();
  }

  const visibleAssemblies = assemblies.filter((item) => item.name.toLowerCase().includes(assemblyQuery.toLowerCase()));

  return (
    <>
      <header className="page-head">
        <div>
          <h1>Projects</h1>
          <p>Each project is one imported package stored on this computer.</p>
        </div>
        <button className="btn-secondary" onClick={() => load().catch((reason: Error) => setError(reason.message))}>Refresh</button>
      </header>
      {error && <p className="error">{error}</p>}
      <div className="split">
        <section className="card stack">
          <input type="search" value={query} placeholder="Search projects" onChange={(event) => { setQuery(event.target.value); void load(event.target.value).catch((reason: Error) => setError(reason.message)); }} />
          {projects.length === 0 && <div className="empty">No projects yet. Import an APK to begin.</div>}
          {projects.map((item) => (
            <button key={item.id} className={item.id === selected ? "list-button selected" : "list-button"} onClick={() => setSelected(item.id)}>
              <strong>{item.name}</strong>
              <small>{item.originalFileName}</small>
              <small>{formatBytes(item.sizeBytes)} · {formatWhen(item.createdUtc)}</small>
              <Status state={item.latestState} />
              {item.latestStage && <small>{item.latestStage}</small>}
            </button>
          ))}
        </section>
        <section className="card">
          {!project && <div className="empty">Select a project to see its package details.</div>}
          {project && (
            <div className="stack">
              <div className="row" style={{ justifyContent: "space-between" }}>
                <h2 style={{ margin: 0 }}>{project.name}</h2>
                <Status state={project.latestState} />
              </div>
              <dl>
                <dt>File</dt><dd>{project.originalFileName} · {formatBytes(project.sizeBytes)}</dd>
                <dt>Imported</dt><dd>{formatWhen(project.createdUtc)}</dd>
                <dt>Package kind</dt><dd>{project.packageKind}</dd>
                <dt>Unity version</dt><dd>{project.unityVersion ?? "Not detected yet"}</dd>
                <dt>Version source</dt><dd>{project.unitySource ?? "Not available"}</dd>
                <dt>Architecture</dt><dd>{project.architecture ?? "Not available"}</dd>
                <dt>Metadata version</dt><dd>{project.metadataVersion ?? "Not available"}</dd>
                <dt>ABI</dt><dd>{project.abi ?? "Not available"}</dd>
                <dt>Input hash</dt><dd>{project.sha256}</dd>
              </dl>
              {project.notes && <p className="muted">{project.notes}</p>}
              {project.files.length > 0 && (
                <div>
                  <h3>Files used from nested packages</h3>
                  {project.files.map((file) => <div key={file.origin} className="muted">{file.role}: {file.origin}</div>)}
                </div>
              )}
              <div className="row">
                <button className="btn" onClick={() => analyze().catch((reason: Error) => setError(reason.message))}>Analyze Project</button>
                <Link to={"/results/" + project.id}>Open results</Link>
                <button className="btn-danger" onClick={() => setConfirm(true)}>Delete</button>
              </div>
              <p className="muted">{scope || project.scope}</p>
              {confirm && (
                <div className="notice">
                  Delete {project.name} and its local files?
                  <div className="row" style={{ marginTop: 8 }}>
                    <button className="btn-danger" onClick={() => remove().catch((reason: Error) => setError(reason.message))}>Delete project</button>
                    <button className="btn-secondary" onClick={() => setConfirm(false)}>Keep project</button>
                  </div>
                </div>
              )}
              <h3>Assemblies</h3>
              <input type="search" value={assemblyQuery} placeholder="Search assemblies" onChange={(event) => setAssemblyQuery(event.target.value)} />
              {catalogNote && <p className="muted">{catalogNote}</p>}
              {visibleAssemblies.length === 0 && <p className="muted">No assemblies to show.</p>}
              {visibleAssemblies.map((assembly) => (
                <div key={assembly.name} className="row" style={{ justifyContent: "space-between" }}>
                  <span>{assembly.name}</span>
                  <span className="muted">{assembly.typeCount} types · {assembly.methodCount} methods · size {assembly.sizeLabel}</span>
                </div>
              ))}
            </div>
          )}
        </section>
      </div>
    </>
  );
}
