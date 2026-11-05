import { useEffect, useState } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { boot, type Settings } from "./api";
import { Shell } from "./Shell";
import { DashboardPage } from "./pages/Dashboard";
import { ImportPage } from "./pages/Import";
import { JobsPage } from "./pages/Jobs";
import { ProjectsPage } from "./pages/Projects";
import { ResultsPage } from "./pages/Results";
import { SettingsPage } from "./pages/Settings";

export function App() {
  const [settings, setSettings] = useState<Settings | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    boot().then((value) => {
      document.documentElement.dataset.theme = value.theme;
      setSettings(value);
    }).catch((reason: Error) => setError(reason.message));
  }, []);

  if (error)
    return <p className="error" style={{ margin: 24 }}>{error}</p>;
  if (!settings)
    return <p className="muted" style={{ margin: 24 }}>Opening the local workbench…</p>;

  return (
    <Shell>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/import" element={<ImportPage />} />
        <Route path="/projects" element={<ProjectsPage />} />
        <Route path="/jobs" element={<JobsPage />} />
        <Route path="/jobs/:jobId" element={<JobsPage />} />
        <Route path="/results/:projectId" element={<ResultsPage />} />
        <Route path="/settings" element={<SettingsPage current={settings} onChange={setSettings} />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </Shell>
  );
}
