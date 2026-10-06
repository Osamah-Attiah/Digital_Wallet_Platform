export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
  ) {
    super(code);
  }
}
let accessToken: string | null = null;
let refreshToken: string | null = null;
let generation = 0;
export function setSession(
  access: string | null,
  refresh: string | null = null,
) {
  accessToken = access;
  refreshToken = refresh;
  generation++;
}
export async function request<T>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
  const current = generation;
  const response = await fetch(path, {
    ...options,
    headers: {
      ...(options.body ? { "Content-Type": "application/json" } : {}),
      ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
      ...options.headers,
    },
    cache: "no-store",
  });
  if (current !== generation) throw new ApiError(401, "SESSION_EXPIRED");
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    if (response.status === 401) {
      setSession(null);
      window.dispatchEvent(new Event("session-expired"));
    }
    throw new ApiError(response.status, problem.code || "REQUEST_FAILED");
  }
  const data = response.status === 204 ? undefined : await response.json();
  if (current !== generation) throw new ApiError(401, "SESSION_EXPIRED");
  return data as T;
}
export const post = <T>(path: string, body: unknown, key?: string) =>
  request<T>(path, {
    method: "POST",
    body: JSON.stringify(body),
    headers: key ? { "Idempotency-Key": key } : {},
  });
export const put = <T>(path: string, body: unknown) =>
  request<T>(path, { method: "PUT", body: JSON.stringify(body) });
export function query(params: Record<string, string | number | undefined>) {
  const q = new URLSearchParams();
  Object.entries(params).forEach(([k, v]) => {
    if (v !== undefined && v !== "") q.set(k, String(v));
  });
  return q.toString();
}
export async function download(id: string) {
  const current = generation;
  const response = await fetch(`/api/v1/admin/reports/${id}/download`, {
    headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
    cache: "no-store",
  });
  if (current !== generation) throw new ApiError(401, "SESSION_EXPIRED");
  if (!response.ok) {
    if (response.status === 401) {
      setSession(null);
      window.dispatchEvent(new Event("session-expired"));
    }
    const p = await response.json().catch(() => ({}));
    throw new ApiError(response.status, p.code || "REQUEST_FAILED");
  }
  const blob = await response.blob();
  if (current !== generation) throw new ApiError(401, "SESSION_EXPIRED");
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = `report-${id}-v1.csv`;
  a.click();
  URL.revokeObjectURL(url);
}
export async function logout() {
  const token = refreshToken;
  try {
    if (token) await post("/api/v1/auth/logout", { refreshToken: token });
  } finally {
    setSession(null);
  }
}
