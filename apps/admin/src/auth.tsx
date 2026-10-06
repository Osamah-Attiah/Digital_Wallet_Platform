import {
  createContext,
  useContext,
  useEffect,
  useState,
  type ReactNode,
  type FormEvent,
} from "react";
import { Navigate, useLocation } from "react-router-dom";
import { request, post, setSession, logout } from "./api";
import { ErrorNotice } from "./components";
export type Session = {
  actorId: string;
  permissions: string[];
  recentMfa: boolean;
};
type Auth = {
  session: Session | null;
  setCurrent: (session: Session | null) => void;
  expired: boolean;
  has: (permission: string) => boolean;
  signOut: () => Promise<void>;
};
const Context = createContext<Auth>(null!);
export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setCurrent] = useState<Session | null>(null);
  const [expired, setExpired] = useState(false);
  useEffect(() => {
    const handler = () => {
      setCurrent(null);
      setExpired(true);
    };
    window.addEventListener("session-expired", handler);
    return () => window.removeEventListener("session-expired", handler);
  }, []);
  return (
    <Context.Provider
      value={{
        session,
        setCurrent: (s) => {
          setCurrent(s);
          setExpired(false);
        },
        expired,
        has: (p) => Boolean(session?.permissions.includes(p)),
        signOut: async () => {
          try {
            await logout();
          } finally {
            setCurrent(null);
          }
        },
      }}
    >
      {children}
    </Context.Provider>
  );
}
export const useAuth = () => useContext(Context);
export function Gate({
  permission,
  anyOf = false,
  children,
}: {
  permission: string | string[];
  anyOf?: boolean;
  children: ReactNode;
}) {
  const { session, has } = useAuth();
  const location = useLocation();
  if (!session)
    return (
      <Navigate
        to="/login"
        state={{ from: location.pathname + location.search }}
        replace
      />
    );
  const required = Array.isArray(permission) ? permission : [permission];
  if (!(anyOf ? required.some(has) : required.every(has)))
    return (
      <div className="notice error" role="alert">
        <h1>الوصول غير مسموح</h1>
        <p>
          هذه الشاشة تتطلب صلاحية إضافية. يمكنك متابعة العمل من القائمة المتاحة.
        </p>
      </div>
    );
  return <>{children}</>;
}
export function Login() {
  const { setCurrent, expired } = useAuth();
  const location = useLocation();
  const [pending, setPending] = useState<Session | null>(null);
  const [challenge, setChallenge] = useState<string>();
  const [code, setCode] = useState("");
  const [error, setError] = useState<unknown>();
  const [busy, setBusy] = useState(false);
  async function login(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    const data = new FormData(e.currentTarget);
    try {
      const auth = await post<{ accessToken: string; refreshToken: string }>(
        "/api/v1/auth/login",
        {
          email: data.get("email"),
          password: data.get("password"),
          device: { deviceId: crypto.randomUUID(), name: "Admin browser" },
        },
      );
      setSession(auth.accessToken, auth.refreshToken);
      const staff = await request<Session>("/api/v1/admin/session");
      setPending(staff);
      const otp = await post<{ challengeId: string }>(
        "/api/v1/admin/session/mfa",
        {},
      );
      setChallenge(otp.challengeId);
    } catch (err) {
      setSession(null);
      setPending(null);
      setError(err);
    } finally {
      setBusy(false);
    }
  }
  async function verify(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      await post("/api/v1/admin/session/mfa/verify", {
        challengeId: challenge,
        code,
      });
      setCurrent(await request<Session>("/api/v1/admin/session"));
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }
  const auth = useAuth();
  if (auth.session)
    return (
      <Navigate
        to={(location.state as { from?: string })?.from || "/"}
        replace
      />
    );
  return (
    <main className="login">
      <section className="login-context">
        <div className="wordmark">
          DW<span>Digital Wallet</span>
        </div>
        <p className="eyebrow">مكتب العمليات</p>
        <h1>
          قرارات واضحة.
          <br />
          أثر يمكن تتبعه.
        </h1>
        <p>
          إدارة العملاء والمحافظ ومراجعة العمليات، ضمن الصلاحيات المعتمدة لكل
          موظف.
        </p>
        <p className="login-foot">وصول إداري · بيانات مقنّعة · سجل تدقيق</p>
      </section>
      <section className="login-form">
        <p className="eyebrow">وصول الموظفين</p>
        <h2>{pending ? "التحقق من الجلسة" : "تسجيل الدخول"}</h2>
        {expired && (
          <p className="notice" role="status">
            انتهت الجلسة. سجّل الدخول للعودة إلى الشاشة السابقة.
          </p>
        )}
        {!!error && <ErrorNotice error={error} />}
        <form onSubmit={pending ? verify : login}>
          {pending ? (
            <>
              <p>أدخل الرمز المرسل عبر قناة التحقق المعتمدة.</p>
              <label>
                رمز التحقق
                <input
                  autoComplete="one-time-code"
                  inputMode="numeric"
                  required
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                />
              </label>
            </>
          ) : (
            <>
              <label>
                البريد الوظيفي
                <input
                  name="email"
                  type="email"
                  autoComplete="username"
                  dir="ltr"
                  required
                />
              </label>
              <label>
                كلمة المرور
                <input
                  name="password"
                  type="password"
                  autoComplete="current-password"
                  required
                />
              </label>
            </>
          )}
          <button className="primary" disabled={busy}>
            {busy ? "جارٍ التحقق…" : pending ? "تأكيد الدخول" : "تسجيل الدخول"}
          </button>
          {pending && (
            <button
              type="button"
              onClick={() => {
                setSession(null);
                setPending(null);
                setCode("");
              }}
            >
              العودة إلى تسجيل الدخول
            </button>
          )}
        </form>
      </section>
    </main>
  );
}
export function MfaPanel() {
  const { setCurrent } = useAuth();
  const [challenge, setChallenge] = useState<string>();
  const [code, setCode] = useState("");
  const [error, setError] = useState<unknown>();
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  return (
    <section className="panel">
      <h2>التحقق الإضافي</h2>
      <p>
        التجميد وتعديل الصلاحيات والإعدادات تتطلب تحققًا خلال آخر خمس دقائق.
      </p>
      {!!error && <ErrorNotice error={error} />}
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          setError(undefined);
          try {
            if (!challenge) {
              const r = await post<{ challengeId: string }>(
                "/api/v1/admin/session/mfa",
                {},
              );
              setChallenge(r.challengeId);
            } else {
              await post("/api/v1/admin/session/mfa/verify", {
                challengeId: challenge,
                code,
              });
              setCurrent(await request("/api/v1/admin/session"));
              setDone(true);
              setCode("");
              setChallenge(undefined);
            }
          } catch (err) {
            setError(err);
          } finally {
            setBusy(false);
          }
        }}
      >
        {challenge && (
          <label>
            رمز التحقق
            <input
              autoComplete="one-time-code"
              inputMode="numeric"
              required
              value={code}
              onChange={(e) => setCode(e.target.value)}
            />
          </label>
        )}
        <button disabled={busy}>
          {busy ? "جارٍ التحقق…" : challenge ? "تأكيد الرمز" : "إرسال رمز جديد"}
        </button>
        {done && <p role="status">تم التحقق من الجلسة.</p>}
      </form>
    </section>
  );
}
