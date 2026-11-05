import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api, elapsed, isRunning, type Job } from "../api";

export function JobsPage() {
  const params = useParams();
  const [jobs, setJobs] = useState<Job[]>([]);
  const [selected, setSelected] = useState<string | null>(params.jobId ?? null);
  const [error, setError] = useState("");
  const [, setTick] = useState(0);

  async function load() {
    const list = await api<Job[]>("/api/jobs");
    setJobs(list);
    setSelected((current) => current && list.some((job) => job.id === current) ? current : params.jobId && list.some((job) => job.id === params.jobId) ? params.jobId : list[0]?.id ?? null);
  }

  useEffect(() => {
    load().catch((reason: Error) => setError(reason.message));
    const timer = window.setInterval(() => {
      setTick((value) => value + 1);
      load().catch((reason: Error) => setError(reason.message));
    }, 1500);
    return () => window.clearInterval(timer);
  }, [params.jobId]);

  const job = jobs.find((item) => item.id === selected) ?? null;

  async function cancel() {
    if (!job)
      return;
    await api("/api/jobs/" + job.id + "/cancel", { method: "POST" });
    await load();
  }

  async function retry() {
    if (!job)
      return;
    const created = await api<Job>("/api/jobs/" + job.id + "/retry", { method: "POST" });
    setSelected(created.id);
    await load();
  }

  return (
    <>
      <header className="page-head">
        <div>
          <h1>Jobs</h1>
          <p>Each analysis runs in a separate process. The stage comes from that process.</p>
        </div>
      </header>
      {error && <p className="error">{error}</p>}
      <div className="split">
        <section className="card">
          {jobs.length === 0 && <div className="empty">No jobs yet. Import a package to start one.</div>}
          {jobs.map((item) => (
            <button key={item.id} className={item.id === selected ? "list-button selected" : "list-button"} onClick={() => setSelected(item.id)}>
              <strong>{item.projectName}</strong>
              <small>{item.stateLabel}</small>
              <small>{item.stage || "Waiting"}</small>
            </button>
          ))}
        </section>
        <section className="card stack">
          {!job && <div className="empty">Select a job to see its log.</div>}
          {job && (
            <>
              <div className="row" style={{ justifyContent: "space-between" }}>
                <h2 style={{ margin: 0 }}>{job.projectName}</h2>
                <span className="status neutral">{job.stateLabel}</span>
              </div>
              <p>{job.stage || "Waiting to start"}</p>
              <p className="muted">Elapsed: {elapsed(job.startedUtc, job.finishedUtc)}</p>
              {job.progressKnown && job.methodTotal ? (
                <>
                  <div>Methods inspected: {job.methodDone ?? 0} of {job.methodTotal}</div>
                  <div className="bar"><span style={{ width: Math.min(100, ((job.methodDone ?? 0) / job.methodTotal) * 100) + "%" }} /></div>
                </>
              ) : isRunning(job.state) ? (
                <>
                  <div className="muted">Overall percent is not known for this stage.</div>
                  <div className="bar indeterminate"><span /></div>
                </>
              ) : null}
              {job.error && <div className="error">{job.error}</div>}
              <div className="row">
                {isRunning(job.state) && <button className="btn-secondary" onClick={() => cancel().catch((reason: Error) => setError(reason.message))}>Cancel</button>}
                {!isRunning(job.state) && <button className="btn-secondary" onClick={() => retry().catch((reason: Error) => setError(reason.message))}>Retry</button>}
                {!isRunning(job.state) && job.state !== "Cancelled" && job.state !== "Failed" && <Link className="btn" to={"/results/" + job.projectId}>Open results</Link>}
              </div>
              <h3>Log</h3>
              <div className="log">{job.log || "No log lines yet."}</div>
            </>
          )}
        </section>
      </div>
    </>
  );
}
