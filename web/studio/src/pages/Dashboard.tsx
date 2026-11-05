import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, formatWhen, type Dashboard } from "../api";
import { Status } from "../Shell";

export function DashboardPage() {
  const [data, setData] = useState<Dashboard | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    api<Dashboard>("/api/dashboard").then(setData).catch((reason: Error) => setError(reason.message));
  }, []);

  return (
    <>
      <header className="page-head">
        <div>
          <h1>Unity IL2CPP Reverse Engineering Workbench</h1>
          <p>Import a Unity package, inspect its structure, and recover supported C# code in one guided workflow.</p>
        </div>
        <Link className="btn" to="/import">Import Package</Link>
      </header>
      {error && <p className="error">{error}</p>}
      {data && data.projects === 0 && (
        <div className="card empty">
          <p>No projects yet. Import an APK to begin.</p>
          <Link to="/import">Go to Import Package</Link>
        </div>
      )}
      {data && data.projects > 0 && (
        <>
          <section className="grid stats">
            <article className="card stat"><span>Imported projects</span><strong>{data.projects}</strong></article>
            <article className="card stat"><span>Running jobs</span><strong>{data.runningJobs}</strong></article>
            <article className="card stat"><span>Completed analyses</span><strong>{data.completedAnalyses}</strong></article>
            <article className="card stat"><span>Failed analyses</span><strong>{data.failedAnalyses}</strong></article>
          </section>
          <section className="card" style={{ marginTop: 16 }}>
            <h2>Recent projects</h2>
            {data.recentProjects.map((project) => (
              <div className="row" key={project.id} style={{ justifyContent: "space-between", marginBottom: 10 }}>
                <div>
                  <strong>{project.name}</strong>
                  <div className="muted">{project.originalFileName} · {formatWhen(project.createdUtc)}</div>
                </div>
                <div className="row">
                  <Status state={project.latestState} />
                  <Link to={"/projects?project=" + project.id}>Open</Link>
                </div>
              </div>
            ))}
          </section>
        </>
      )}
    </>
  );
}
