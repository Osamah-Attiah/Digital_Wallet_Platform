import {
  useEffect,
  useRef,
  useState,
  type ReactNode,
  type FormEvent,
} from "react";
import { ApiError, request } from "./api";
export type Page<T> = { items: T[]; nextCursor: string | null };
export function useRemote<T>(path: string | undefined, revision = 0) {
  const [state, setState] = useState<{
    path?: string;
    data?: T;
    error?: unknown;
    loading: boolean;
  }>({ loading: true });
  useEffect(() => {
    if (!path) {
      setState({ path, loading: false });
      return;
    }
    let active = true;
    const abort = new AbortController();
    setState({ path, loading: true });
    request<T>(path, { signal: abort.signal })
      .then((data) => {
        if (active) setState({ path, data, loading: false });
      })
      .catch((error) => {
        if (active) setState({ path, error, loading: false });
      });
    return () => {
      active = false;
      abort.abort();
    };
  }, [path, revision]);
  return state.path === path
    ? state
    : { path, data: undefined, error: undefined, loading: Boolean(path) };
}
const messages: Record<string, string> = {
  RECENT_MFA_REQUIRED:
    "يلزم تحقق إضافي حديث. افتح التحقق من الجلسة ثم أعد المحاولة.",
  CONCURRENCY_CONFLICT: "تغيّر السجل. حدّث الصفحة وراجع القيم قبل المحاولة.",
  REPORT_NOT_READY: "التقرير قيد التنفيذ. يمكنك متابعة حالته هنا.",
  REPORT_EXPIRED: "انتهت صلاحية التقرير. أنشئ طلبًا جديدًا.",
  DATE_RANGE_TOO_LARGE: "النطاق الزمني يتجاوز الحد المسموح.",
  REPORT_ROW_LIMIT_EXCEEDED:
    "تجاوز التقرير الحد الأقصى للصفوف. اختر نطاقًا أصغر.",
  SELF_APPROVAL_FORBIDDEN: "لا يمكنك اتخاذ القرار في طلب أنشأته.",
  SELF_PRIVILEGE_CHANGE_FORBIDDEN: "لا يمكنك تعديل صلاحيات دورك.",
  OTP_INVALID: "تعذر التحقق من الرمز. راجعه ثم أعد المحاولة.",
  IDEMPOTENCY_KEY_REUSED: "استُخدم مفتاح الطلب مع مدخلات مختلفة.",
  REPORT_FORBIDDEN: "لم تعد الصلاحيات المطلوبة متاحة لهذا التقرير.",
};
export function ErrorNotice({
  error,
  retry,
}: {
  error: unknown;
  retry?: () => void;
}) {
  const api = error instanceof ApiError ? error : null;
  return (
    <div className="notice error" role="alert">
      <strong>
        {api?.status === 403
          ? "الوصول غير مسموح"
          : api?.status === 404
            ? "السجل غير متاح"
            : "تعذر إكمال الطلب"}
      </strong>
      <p>
        {messages[api?.code || ""] ||
          "راجع البيانات وصلاحياتك، ثم أعد المحاولة."}
      </p>
      {retry && <button onClick={retry}>إعادة المحاولة</button>}
    </div>
  );
}
export function RemoteState({
  loading,
  error,
  empty,
  children,
}: {
  loading: boolean;
  error?: unknown;
  empty?: boolean;
  children: ReactNode;
}) {
  if (loading)
    return (
      <div className="notice" role="status">
        جارٍ تحميل السجلات…
      </div>
    );
  if (error) return <ErrorNotice error={error} />;
  if (empty)
    return (
      <div className="empty">
        <span aria-hidden="true">—</span>
        <h2>لا توجد سجلات</h2>
        <p>جرّب تعديل الفلاتر أو النطاق الزمني.</p>
      </div>
    );
  return <>{children}</>;
}
export function Heading({
  eyebrow,
  title,
  description,
  action,
}: {
  eyebrow: string;
  title: string;
  description: string;
  action?: ReactNode;
}) {
  return (
    <header className="page-heading">
      <div>
        <p className="eyebrow">{eyebrow}</p>
        <h1>{title}</h1>
        <p>{description}</p>
      </div>
      {action}
    </header>
  );
}
const labels: Record<string, string> = {
  Active: "نشط",
  Frozen: "مجمّد",
  Restricted: "مقيّد",
  Closed: "مغلق",
  Pending: "بانتظار المراجعة",
  Completed: "مكتمل",
  Failed: "فشل",
  Expired: "منتهي",
  Queued: "في الطابور",
  Running: "قيد التنفيذ",
  Rejected: "مرفوض",
  Verified: "موثّق",
  NotStarted: "لم يبدأ",
  PendingVerification: "بانتظار التوثيق",
  Suspended: "معلّق",
  Blocked: "محظور",
  Success: "نجاح",
  Denied: "ممنوع",
  Reversed: "معكوس",
  InternalTransfer: "تحويل داخلي",
  Reversal: "عكس",
  PendingConfirmation: "بانتظار التأكيد",
  PendingReview: "بانتظار المراجعة",
};
export function Status({ value }: { value: string | number }) {
  const text = String(value);
  return (
    <span className={`status s-${text}`}>
      <span aria-hidden="true" className="status-dot" />
      {labels[text] || text}
    </span>
  );
}
export function Table<T>({
  rows,
  columns,
  caption,
}: {
  rows: T[];
  columns: { label: string; render: (row: T) => ReactNode }[];
  caption: string;
}) {
  return (
    <div
      className="table-scroll"
      role="region"
      aria-label={caption}
      tabIndex={0}
    >
      <table>
        <caption className="sr-only">{caption}</caption>
        <thead>
          <tr>
            {columns.map((c) => (
              <th key={c.label} scope="col">
                {c.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, i) => (
            <tr key={i}>
              {columns.map((c) => (
                <td key={c.label}>{c.render(row)}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
export function NextPage({
  cursor,
  onNext,
  onReset,
}: {
  cursor: string | null | undefined;
  onNext: (c: string) => void;
  onReset: () => void;
}) {
  return (
    <div className="pagination">
      <button onClick={onReset}>الصفحة الأولى</button>
      <button disabled={!cursor} onClick={() => cursor && onNext(cursor)}>
        الصفحة التالية ←
      </button>
    </div>
  );
}
export function ConfirmDialog({
  title,
  children,
  submit,
  cancel,
}: {
  title: string;
  children?: ReactNode;
  submit: (reason: string) => Promise<void>;
  cancel: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>();
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const dialog = ref.current;
    dialog?.showModal?.();
    return () => {
      dialog?.close?.();
      previous?.focus();
    };
  }, []);
  async function confirm(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      await submit(reason);
      cancel();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }
  return (
    <dialog
      open={
        typeof HTMLDialogElement.prototype.showModal !== "function"
          ? true
          : undefined
      }
      ref={ref}
      onCancel={(e) => {
        if (busy) e.preventDefault();
        else cancel();
      }}
      aria-labelledby="confirm-title"
    >
      <form onSubmit={confirm}>
        <p className="eyebrow">تأكيد عملية حساسة</p>
        <h2 id="confirm-title">{title}</h2>
        {children}
        <label>
          سبب العملية
          <textarea
            autoFocus
            required
            maxLength={500}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
          />
        </label>
        {!!error && <ErrorNotice error={error} />}
        <div className="actions">
          <button className="primary" disabled={busy || !reason.trim()}>
            {busy ? "جارٍ تنفيذ الطلب…" : "تأكيد العملية"}
          </button>
          <button type="button" onClick={cancel} disabled={busy}>
            إلغاء
          </button>
        </div>
      </form>
    </dialog>
  );
}
export const date = (v: string) =>
  new Intl.DateTimeFormat("ar-SA", {
    dateStyle: "medium",
    timeStyle: "short",
    timeZone: "Asia/Riyadh",
    calendar: "gregory",
  }).format(new Date(v));
export const amount = (minor: number) =>
  new Intl.NumberFormat("ar-SA", { style: "currency", currency: "SAR" }).format(
    minor / 100,
  );
