import { useState } from "react";
import { api, type Settings } from "../api";

export function SettingsPage({ current, onChange }: { current: Settings; onChange: (settings: Settings) => void }) {
  const [theme, setTheme] = useState(current.theme);
  const [timeoutSeconds, setTimeoutSeconds] = useState(current.timeoutSeconds);
  const [defaultAbi, setDefaultAbi] = useState(current.defaultAbi);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  async function save() {
    setError("");
    const saved = await api<Settings>("/api/settings", {
      method: "PUT",
      body: JSON.stringify({ theme, timeoutSeconds, defaultAbi })
    });
    document.documentElement.dataset.theme = saved.theme;
    onChange(saved);
    setMessage("Settings saved on this computer.");
  }

  return (
    <>
      <header className="page-head">
        <div>
          <h1>Settings</h1>
          <p>These options apply to analyses started on this computer.</p>
        </div>
      </header>
      <section className="card stack" style={{ maxWidth: 640 }}>
        <label>
          <span>Appearance</span>
          <select value={theme} onChange={(event) => setTheme(event.target.value)}>
            <option value="light">Light</option>
            <option value="dark">Dark</option>
          </select>
        </label>
        <label>
          <span>Analysis timeout, in seconds</span>
          <input type="number" min={10} max={3600} value={timeoutSeconds} onChange={(event) => setTimeoutSeconds(Number(event.target.value))} />
        </label>
        <label>
          <span>Default ABI</span>
          <input type="text" value={defaultAbi} onChange={(event) => setDefaultAbi(event.target.value)} />
        </label>
        <p className="muted">The recovery lifter reads little-endian ARM64. Another ABI can be inspected, and its methods stay unrecovered.</p>
        <button className="btn" onClick={() => save().catch((reason: Error) => setError(reason.message))}>Save settings</button>
        {message && <p>{message}</p>}
        {error && <p className="error">{error}</p>}
        <h2>Local data</h2>
        <p>Projects are stored on this computer at:</p>
        <p><code>{current.dataDirectory}</code></p>
        <p className="muted">The server listens only on loopback{current.bindUrl ? " at " + current.bindUrl : ""}. Stop it by pressing Ctrl+C in the window that started it. Stopping the server does not delete projects.</p>
      </section>
    </>
  );
}
