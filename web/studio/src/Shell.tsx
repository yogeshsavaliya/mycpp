import type { ReactNode } from "react";
import { NavLink } from "react-router-dom";

const links = [
  ["/", "Dashboard"],
  ["/import", "Import Package"],
  ["/projects", "Projects"],
  ["/jobs", "Jobs"],
  ["/settings", "Settings"]
] as const;

export function Shell({ children }: { children: ReactNode }) {
  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="mark">IR</div>
          <div>
            <strong>IL2CPP Recovery Studio</strong>
            <span>Local analysis</span>
          </div>
        </div>
        <nav className="nav">
          {links.map(([to, label]) => (
            <NavLink key={to} to={to} end={to === "/"} className={({ isActive }) => isActive ? "active" : ""}>
              {label}
            </NavLink>
          ))}
        </nav>
        <footer>Packages stay on this computer. Nothing is uploaded to an external service.</footer>
      </aside>
      <main className="main">{children}</main>
    </div>
  );
}

export function Status({ state }: { state: string | null }) {
  const label = state ?? "Not started";
  const tone = state === "Failed" ? "bad"
    : state === "CompletedWithLimitations" || state === "Cancelled" ? "warn"
    : state === "Completed" ? "good"
    : "neutral";
  const text = state === "CompletedWithLimitations" ? "Completed with Limitations"
    : state === "ReadingMetadata" ? "Reading Metadata"
    : state === "AnalyzingNativeCode" ? "Analyzing Native Code"
    : state === "GeneratingOutput" ? "Generating Output"
    : label;
  return <span className={"status " + tone}>{text}</span>;
}
