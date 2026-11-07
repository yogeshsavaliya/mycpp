export type FormatInfo = {
  id: string;
  label: string;
  extensions: string[];
  state: "supported" | "unavailable";
  detail: string;
};

export type Capabilities = {
  product: string;
  formats: FormatInfo[];
  notSupported: string[];
};

export type Project = {
  id: string;
  name: string;
  originalFileName: string;
  sizeBytes: number;
  sha256: string;
  packageKind: string;
  createdUtc: string;
  unityVersion: string | null;
  unitySource: string | null;
  architecture: string | null;
  metadataVersion: number | null;
  abi: string | null;
  notes: string;
  latestJobId: string | null;
  latestState: string | null;
  latestStage: string | null;
  members: { entryName: string; sizeBytes: number; sha256: string }[];
  files: { role: string; origin: string; normalizedPath: string; sha256: string; sizeBytes: number }[];
  scope: string;
};

export type Job = {
  id: string;
  projectId: string;
  projectName: string;
  state: string;
  stateLabel: string;
  stage: string | null;
  createdUtc: string;
  startedUtc: string | null;
  finishedUtc: string | null;
  error: string | null;
  exitCode: number | null;
  failureDetail: string | null;
  log: string;
  methodDone: number | null;
  methodTotal: number | null;
  progressKnown: boolean;
  bodyProgress: boolean;
};

export type MethodSummary = {
  index: number;
  assembly: string;
  namespace: string;
  declaringType: string;
  name: string;
  status: string;
  statusLabel: string;
  placeholder: boolean;
  isStatic: boolean;
};

export type MethodDetail = {
  summary: MethodSummary;
  signature: string;
  metadataToken: number | null;
  nativeVirtualAddress: string | null;
  nativeFileOffset: string | null;
  boundary: string;
  compilation: string;
  behaviour: string;
  diagnostics: string[];
  instructions: { address: string; bytes: string; text: string }[];
  csharp: string | null;
  csharpIsPlaceholder: boolean;
  candidateCSharp: string | null;
  provenance: { operation: string; nativeAddresses: string[] }[];
  sharedImplementationCount: number;
  placeholderNotice: string | null;
  validationNotice: string;
};

export type Settings = {
  theme: string;
  timeoutSeconds: number;
  defaultAbi: string;
  bodyScope: string;
  dataDirectory: string;
  bindUrl: string | null;
};

export type Dashboard = {
  projects: number;
  runningJobs: number;
  completedAnalyses: number;
  failedAnalyses: number;
  recentProjects: Project[];
};

let token = "";

export class ApiError extends Error {}

export async function boot(): Promise<Settings> {
  const session = await fetch("/api/session").then((response) => response.json() as Promise<{ token: string }>);
  token = session.token;
  return api<Settings>("/api/settings");
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  const method = (init.method ?? "GET").toUpperCase();
  if (method !== "GET" && method !== "HEAD" && token)
    headers.set("X-Studio-Token", token);
  if (init.body && !(init.body instanceof FormData) && !headers.has("Content-Type"))
    headers.set("Content-Type", "application/json");
  const response = await fetch(path, { ...init, headers });
  const text = await response.text();
  if (!response.ok) {
    let message = text || "The request failed.";
    try {
      const body = JSON.parse(text) as { error?: string; errors?: string[] };
      message = body.error || body.errors?.join(" ") || message;
    } catch {
      /* The body is not JSON. */
    }
    throw new ApiError(message);
  }
  if (!text)
    return undefined as T;
  return JSON.parse(text) as T;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024)
    return bytes + " B";
  if (bytes < 1024 * 1024)
    return (bytes / 1024).toFixed(1) + " KB";
  return (bytes / (1024 * 1024)).toFixed(1) + " MB";
}

export function formatWhen(iso: string | null): string {
  if (!iso)
    return "Not available";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime()))
    return iso;
  return date.toLocaleString();
}

export function elapsed(start: string | null, end: string | null): string {
  if (!start)
    return "Not started";
  const ms = (end ? Date.parse(end) : Date.now()) - Date.parse(start);
  const seconds = Math.max(0, Math.floor(ms / 1000));
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  return minutes > 0 ? minutes + "m " + rest + "s" : rest + "s";
}

export function isRunning(state: string | null): boolean {
  return !!state && !["Completed", "CompletedWithLimitations", "CompletedWithInternalErrors", "Failed", "TimedOut", "Cancelled"].includes(state);
}

export async function download(path: string, filename: string): Promise<void> {
  const response = await fetch(path);
  if (!response.ok)
    throw new ApiError("The export could not be downloaded.");
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
}
