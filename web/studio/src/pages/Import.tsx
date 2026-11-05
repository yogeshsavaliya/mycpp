import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { api, formatBytes, type Capabilities } from "../api";

export function ImportPage() {
  const navigate = useNavigate();
  const [caps, setCaps] = useState<Capabilities | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [name, setName] = useState("");
  const [abi, setAbi] = useState("arm64-v8a");
  const [unity, setUnity] = useState("");
  const [advanced, setAdvanced] = useState(false);
  const [over, setOver] = useState(false);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api<Capabilities>("/api/capabilities").then(setCaps).catch((reason: Error) => setError(reason.message));
    api<{ defaultAbi: string }>("/api/settings").then((settings) => setAbi(settings.defaultAbi)).catch(() => undefined);
  }, []);

  function choose(next: File | null) {
    setFile(next);
    setError("");
    if (next && name.trim().length === 0)
      setName(next.name.replace(/\.[^.]+$/, ""));
  }

  async function start() {
    if (!file) {
      setError("Choose a package file first.");
      return;
    }
    setBusy(true);
    setError("");
    try {
      const body = new FormData();
      body.set("file", file);
      body.set("name", name);
      body.set("abi", abi);
      body.set("unityVersion", unity);
      const created = await api<{ job: { id: string } }>("/api/projects", { method: "POST", body });
      navigate("/jobs/" + created.job.id);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "The package could not be imported.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <header className="page-head">
        <div>
          <h1>Import Package</h1>
          <p>Start Analysis checks the file, creates a project, and queues analysis on this computer.</p>
        </div>
      </header>
      <div className="steps">
        <span className="step now">1. Import</span>
        <span className="step">2. Validate</span>
        <span className="step">3. Create project</span>
        <span className="step">4. Analyze</span>
        <span className="step">5. Browse results</span>
        <span className="step">6. Export</span>
      </div>
      <div className="split">
        <section className="stack">
          <div
            className={over ? "drop over" : "drop"}
            onDragOver={(event) => { event.preventDefault(); setOver(true); }}
            onDragLeave={() => setOver(false)}
            onDrop={(event) => { event.preventDefault(); setOver(false); choose(event.dataTransfer.files[0] ?? null); }}
          >
            <div>
              <strong>Drop a package here</strong>
              <p className="muted">or choose a file from this computer</p>
              <label className="btn-secondary" style={{ display: "inline-block" }}>
                Choose File
                <input type="file" hidden onChange={(event) => choose(event.target.files?.[0] ?? null)} />
              </label>
            </div>
          </div>
          {file && (
            <div className="card">
              <strong>{file.name}</strong>
              <div className="muted">{formatBytes(file.size)}</div>
            </div>
          )}
          {error && <div className="error">{error}</div>}
          <label>
            <span>Project name</span>
            <input value={name} onChange={(event) => setName(event.target.value)} placeholder="Name shown in Projects" />
          </label>
          <button className="btn" disabled={busy} onClick={() => void start()}>{busy ? "Checking the package…" : "Start Analysis"}</button>
        </section>
        <section className="stack">
          <article className="card">
            <h2>Supported formats</h2>
            <div className="formats">
              {caps?.formats.map((format) => (
                <div className="format" key={format.id}>
                  <div>
                    <strong>{format.label}</strong>
                    <div className="muted">{format.extensions.join(", ")}</div>
                    <p>{format.detail}</p>
                  </div>
                  <span className={"badge " + (format.state === "supported" ? "supported" : "unavailable")}>
                    {format.state === "supported" ? "Supported" : "Not available yet"}
                  </span>
                </div>
              ))}
            </div>
            {caps && <p className="muted">Not supported: {caps.notSupported.join(", ")}.</p>}
          </article>
          <article className="card">
            <button className="btn-secondary" onClick={() => setAdvanced((value) => !value)}>{advanced ? "Hide advanced options" : "Advanced options"}</button>
            {advanced && (
              <div className="stack" style={{ marginTop: 12 }}>
                <label>
                  <span>ABI</span>
                  <input value={abi} onChange={(event) => setAbi(event.target.value)} />
                </label>
                <label>
                  <span>Unity version, only if the package does not contain one</span>
                  <input value={unity} onChange={(event) => setUnity(event.target.value)} placeholder="2022.3.62f3" />
                </label>
                <p className="muted">A version typed here never replaces a different version read from the package. Leave this empty unless you know the package has no version string.</p>
              </div>
            )}
          </article>
        </section>
      </div>
    </>
  );
}
