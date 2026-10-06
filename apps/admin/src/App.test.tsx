import {
  act,
  cleanup,
  render,
  renderHook,
  screen,
  waitFor,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider, Gate, useAuth, type Session } from "./auth";
import { App } from "./App";
import { ConfirmDialog, Status, useRemote } from "./components";
import { request, setSession, ApiError } from "./api";
afterEach(() => {
  cleanup();
  setSession(null);
  vi.unstubAllGlobals();
});
function ProtectedPreview() {
  const { session } = useAuth();
  return session ? (
    <Gate permission="customer.pii.view">
      <div>Unmasked contact</div>
    </Gate>
  ) : null;
}
function Seed({ session }: { session: Session }) {
  const auth = useAuth();
  return (
    <button onClick={() => auth.setCurrent(session)}>Start session</button>
  );
}
const staff = (permissions: string[]): Session => ({
  actorId: "staff-1",
  permissions,
  recentMfa: true,
});
function mount(path: string, permissions: string[]) {
  const fetch = vi.fn().mockResolvedValue({
    ok: true,
    status: 200,
    json: async () => ({ items: [], nextCursor: null }),
  });
  vi.stubGlobal("fetch", fetch);
  render(
    <MemoryRouter initialEntries={[path]}>
      <AuthProvider>
        <Seed session={staff(permissions)} />
        <App />
      </AuthProvider>
    </MemoryRouter>,
  );
  return fetch;
}
describe("permissions and routes", () => {
  it("redirects anonymous deep links to staff login", () => {
    mount("/reports/123", []);
    expect(screen.getByRole("heading", { name: "تسجيل الدخول" })).toBeVisible();
  });
  it("does not fetch a forbidden screen", async () => {
    const fetch = mount("/audit", ["customer.view"]);
    await userEvent.click(screen.getByText("Start session"));
    expect(
      screen.getByRole("heading", { name: "الوصول غير مسموح" }),
    ).toBeVisible();
    expect(fetch).not.toHaveBeenCalled();
  });
  it("renders only granted navigation and real loading/empty states", async () => {
    mount("/customers", ["customer.view"]);
    await userEvent.click(screen.getByText("Start session"));
    expect(
      screen.queryByRole("link", { name: "الأدوار والصلاحيات" }),
    ).not.toBeInTheDocument();
    await screen.findByRole("heading", { name: "لا توجد سجلات" });
    expect(
      screen.getByRole("link", { name: "العملاء والمحافظ" }),
    ).toBeVisible();
  });
  it("session expiry removes the private screen", async () => {
    mount("/customers", ["customer.view"]);
    await userEvent.click(screen.getByText("Start session"));
    await screen.findByRole("heading", { name: "البحث عن العملاء" });
    act(() => window.dispatchEvent(new Event("session-expired")));
    expect(screen.getByRole("heading", { name: "تسجيل الدخول" })).toBeVisible();
    expect(screen.queryByText("البحث عن العملاء")).not.toBeInTheDocument();
  });
  it("does not render sensitive content behind permission gate", async () => {
    render(
      <MemoryRouter>
        <AuthProvider>
          <Seed session={staff(["customer.view"])} />
          <ProtectedPreview />
        </AuthProvider>
      </MemoryRouter>,
    );
    await userEvent.click(screen.getByText("Start session"));
    expect(screen.queryByText("Unmasked contact")).not.toBeInTheDocument();
  });
  it("requires all permissions for compound routes", async () => {
    mount("/", ["operations.view"]);
    await userEvent.click(screen.getByText("Start session"));
    expect(
      screen.getByRole("heading", { name: "الوصول غير مسموح" }),
    ).toBeVisible();
  });
  it("allows report generation without view and selects an authorized source", async () => {
    const fetch = mount("/reports", ["report.generate", "customer.view"]);
    await userEvent.click(screen.getByText("Start session"));
    expect(
      screen.getByRole("heading", { name: "طلب تقرير جديد" }),
    ).toBeVisible();
    expect(screen.getByLabelText("نوع التقرير")).toHaveValue("CustomerSummary");
    expect(screen.getAllByRole("option")).toHaveLength(1);
    expect(fetch).not.toHaveBeenCalled();
    expect(screen.queryByText("البيانات المكشوفة")).not.toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "التقارير والتصدير" }),
    ).toBeVisible();
  });
  it("lets a report viewer read the list without rendering creation controls", async () => {
    mount("/reports", ["report.view", "customer.view"]);
    await userEvent.click(screen.getByText("Start session"));
    await screen.findByRole("heading", { name: "لا توجد سجلات" });
    expect(
      screen.queryByRole("button", { name: "إنشاء التقرير" }),
    ).not.toBeInTheDocument();
  });
});
describe("sensitive controls", () => {
  it("requires a reason before confirmation and preserves input on failure", async () => {
    const submit = vi
      .fn()
      .mockRejectedValue(new ApiError(409, "CONCURRENCY_CONFLICT"));
    render(
      <ConfirmDialog title="تجميد المحفظة" cancel={vi.fn()} submit={submit} />,
    );
    expect(
      screen.getByRole("button", { name: "تأكيد العملية" }),
    ).toBeDisabled();
    await userEvent.type(screen.getByLabelText("سبب العملية"), "Review");
    await userEvent.click(
      screen.getByRole("button", { name: "تأكيد العملية" }),
    );
    await screen.findByRole("alert");
    expect(screen.getByLabelText("سبب العملية")).toHaveValue("Review");
    expect(submit).toHaveBeenCalledWith("Review");
  });
  it("provides a named keyboard accessible cancellation button", async () => {
    const cancel = vi.fn();
    render(<ConfirmDialog title="Review" cancel={cancel} submit={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: "إلغاء" }));
    expect(cancel).toHaveBeenCalledOnce();
  });
  it("uses a textual state label alongside color", () => {
    render(<Status value="Frozen" />);
    expect(screen.getByText("مجمّد")).toBeVisible();
  });
});
describe("API session boundaries", () => {
  it("clears the previous resource while the next route is loading", async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({ id: "first" }),
      })
      .mockImplementation(() => new Promise(() => {}));
    vi.stubGlobal("fetch", fetch);
    const { result, rerender } = renderHook(
      ({ path }) => useRemote<{ id: string }>(path),
      { initialProps: { path: "/api/v1/admin/customers/first" } },
    );
    await waitFor(() => expect(result.current.data?.id).toBe("first"));
    rerender({ path: "/api/v1/admin/customers/second" });
    expect(result.current.loading).toBe(true);
    expect(result.current.data).toBeUndefined();
  });
  it("sends authorization from memory and expires on a 401", async () => {
    const fetch = vi.fn().mockResolvedValue({
      ok: false,
      status: 401,
      json: async () => ({ code: "UNAUTHORIZED" }),
    });
    vi.stubGlobal("fetch", fetch);
    setSession("session-access");
    await expect(
      request("/api/v1/admin/customers/search"),
    ).rejects.toMatchObject({ status: 401 });
    expect(fetch.mock.calls[0][1].headers.Authorization).toBe(
      "Bearer session-access",
    );
    expect(localStorage.length).toBe(0);
  });
  it("discards a response from a previous session", async () => {
    let resolve!: (value: unknown) => void;
    vi.stubGlobal(
      "fetch",
      vi.fn(
        () =>
          new Promise((r) => {
            resolve = r;
          }),
      ),
    );
    setSession("one");
    const result = request("/api/v1/admin/customers/search");
    setSession("two");
    resolve({
      ok: true,
      status: 200,
      json: async () => ({ items: [{ firstName: "private" }] }),
    });
    await expect(result).rejects.toMatchObject({ code: "SESSION_EXPIRED" });
  });
});
