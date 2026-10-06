import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, useParams } from "react-router-dom";
import { download, post, put, query } from "./api";
import { useAuth, MfaPanel } from "./auth";
import {
  amount,
  ConfirmDialog,
  date,
  ErrorNotice,
  Heading,
  NextPage,
  type Page,
  RemoteState,
  Status,
  Table,
  useRemote,
} from "./components";
type Customer = {
  id: string;
  firstName: string;
  lastName: string;
  phoneNumber: string | null;
  status: string | number;
  kycStatus: string | number;
  createdAt: string;
  concurrencyVersion: string;
};
type Wallet = {
  id: string;
  customerId: string;
  status: string;
  currency: string;
  publicHandle: string;
  concurrencyVersion: string;
};
type Transfer = {
  id: string;
  publicReference: string;
  status: string;
  amountMinor: number;
  feeMinor: number;
  currency: string;
  maskedSenderName: string;
  maskedRecipientName: string;
  createdAt: string;
  occurredAt?: string;
  type?: string;
};
type Reversal = {
  id: string;
  originalTransactionId: string;
  status: string;
  createdAt: string;
  requestedBy: string;
  decidedBy?: string;
  reason?: string;
  decisionNote?: string;
};
type Report = {
  id: string;
  type: string;
  status: string;
  from: string;
  to: string;
  createdAt: string;
  expiresAt: string;
  rowCount: number | null;
  includesPii: boolean;
  failureCode: string | null;
  schemaVersion: number;
};
export function Dashboard() {
  const data = useRemote<{
    customerCount: number;
    pendingReversals: number;
    messaging: {
      pendingCount: number;
      failedCount: number;
      brokerAvailable: boolean;
      dlqCount: number | null;
      oldestUnpublishedSeconds: number;
    };
    asOf: string;
    isStale: boolean;
    ageSeconds: number;
  }>("/api/v1/admin/dashboard");
  return (
    <>
      <Heading
        eyebrow="العمليات اليومية"
        title="نظرة على المنصة"
        description="مؤشرات لحالة التشغيل، مع وقت آخر قراءة من الخادم."
      />
      <RemoteState {...data}>
        <section className="metrics">
          <div>
            <span>العملاء</span>
            <strong>{data.data?.customerCount.toLocaleString("ar-SA")}</strong>
            <Link to="/customers">فتح سجل العملاء ←</Link>
          </div>
          <div>
            <span>طلبات عكس تنتظر القرار</span>
            <strong>
              {data.data?.pendingReversals.toLocaleString("ar-SA")}
            </strong>
            <Link to="/reversals">مراجعة الطلبات ←</Link>
          </div>
          <div>
            <span>رسائل تنتظر النشر</span>
            <strong>
              {data.data?.messaging.pendingCount.toLocaleString("ar-SA")}
            </strong>
            <small>
              أقدم رسالة:{" "}
              {Math.round(data.data?.messaging.oldestUnpublishedSeconds || 0)}{" "}
              ثانية
            </small>
          </div>
        </section>
        <div className="overview-split">
          <section className="panel">
            <p className="eyebrow">MESSAGING / OUTBOX</p>
            <h2>حالة توصيل الأحداث</h2>
            <p className="service-state">
              <span aria-hidden="true" className="service-mark" />
              {data.data?.messaging.brokerAvailable
                ? "RabbitMQ متاح"
                : "تعذر الوصول إلى RabbitMQ"}
            </p>
            <dl>
              <div>
                <dt>آخر محاولات النشر الفاشلة</dt>
                <dd>{data.data?.messaging.failedCount}</dd>
              </div>
              <div>
                <dt>رسائل DLQ</dt>
                <dd>{data.data?.messaging.dlqCount ?? "غير متاح"}</dd>
              </div>
            </dl>
            <p className="muted">
              يبقى Outbox مصدر الأعمال القابلة للاستكمال عند تعطل الوسيط.
            </p>
            <small>
              وقت القراءة: {data.data?.asOf && date(data.data.asOf)}
              {data.data?.isStale && ` · لقطة محفوظة قبل ${Math.ceil(data.data.ageSeconds)} ثانية`}
              <br />
              مؤشرات للاستعراض؛ راجع سجل العملية قبل اتخاذ قرار مالي.
            </small>
          </section>
          <section className="desk-note">
            <p className="eyebrow">مسار المراجعة</p>
            <h2>
              ابدأ من السجل.
              <br />
              راجع الأثر قبل القرار.
            </h2>
            <ol>
              <li>ابحث عن العميل وراجع حالته.</li>
              <li>افتح المحفظة أو الحركة المرتبطة.</li>
              <li>دوّن سبب العملية وأكّد القرار.</li>
            </ol>
            <Link to="/audit">تتبع القرارات في سجل التدقيق ←</Link>
          </section>
        </div>
      </RemoteState>
    </>
  );
}
export function Messaging() {
  const data = useRemote<{
    pendingCount: number;
    failedCount: number;
    publishAttempts: number;
    oldestUnpublishedSeconds: number;
    dlqCount: number | null;
    brokerAvailable: boolean;
  }>("/api/v1/admin/operations/messaging");
  return (
    <>
      <Heading
        eyebrow="التشغيل"
        title="Messaging وOutbox"
        description="حالة نشر الأحداث والطابور المعزول للرسائل غير المعالجة."
      />
      <RemoteState {...data}>
        <section className="panel">
          <h2>
            {data.data?.brokerAvailable
              ? "الوسيط متاح"
              : "الوسيط غير متاح حاليًا"}
          </h2>
          <dl>
            {data.data &&
              Object.entries(data.data).map(([key, value]) => (
                <div key={key}>
                  <dt dir="ltr">{key}</dt>
                  <dd>
                    {value === null
                      ? "غير متاح"
                      : typeof value === "boolean"
                        ? value
                          ? "متاح"
                          : "غير متاح"
                        : String(value)}
                  </dd>
                </div>
              ))}
          </dl>
        </section>
      </RemoteState>
    </>
  );
}
export function Customers() {
  const [filters, setFilters] = useState({ id: "", status: "", kycStatus: "" });
  const [active, setActive] = useState(filters);
  const [cursor, setCursor] = useState<string>();
  const data = useRemote<Page<Customer>>(
    `/api/v1/admin/customers/search?${query({ ...active, cursor })}`,
  );
  return (
    <>
      <Heading
        eyebrow="سجل العملاء"
        title="البحث عن العملاء"
        description="الأسماء وجهات الاتصال مقنّعة. كشف البيانات يتطلب طلبًا مستقلًا وصلاحية إضافية."
      />
      <form
        className="filters"
        onSubmit={(e) => {
          e.preventDefault();
          setActive(filters);
          setCursor(undefined);
        }}
      >
        <label>
          معرّف العميل
          <input
            dir="ltr"
            value={filters.id}
            onChange={(e) => setFilters({ ...filters, id: e.target.value })}
          />
        </label>
        <label>
          حالة العميل
          <select
            value={filters.status}
            onChange={(e) => setFilters({ ...filters, status: e.target.value })}
          >
            <option value="">الكل</option>
            {[
              "Active",
              "PendingVerification",
              "Suspended",
              "Blocked",
              "Closed",
            ].map((s) => (
              <option key={s}>{s}</option>
            ))}
          </select>
        </label>
        <label>
          حالة KYC
          <select
            value={filters.kycStatus}
            onChange={(e) =>
              setFilters({ ...filters, kycStatus: e.target.value })
            }
          >
            <option value="">الكل</option>
            {["NotStarted", "Pending", "Verified", "Rejected", "Expired"].map(
              (s) => (
                <option key={s}>{s}</option>
              ),
            )}
          </select>
        </label>
        <button className="primary">بحث</button>
      </form>
      <RemoteState {...data} empty={data.data?.items.length === 0}>
        <Table
          caption="نتائج بحث العملاء"
          rows={data.data?.items || []}
          columns={[
            {
              label: "العميل",
              render: (c) => (
                <Link to={`/customers/${c.id}`}>
                  <strong>
                    {c.firstName} {c.lastName}
                  </strong>
                  <small className="mono">{c.id}</small>
                </Link>
              ),
            },
            {
              label: "جهة الاتصال",
              render: (c) => <span dir="ltr">{c.phoneNumber || "—"}</span>,
            },
            { label: "الحالة", render: (c) => <Status value={c.status} /> },
            { label: "KYC", render: (c) => <Status value={c.kycStatus} /> },
            { label: "أُنشئ", render: (c) => date(c.createdAt) },
          ]}
        />
        <NextPage
          cursor={data.data?.nextCursor}
          onNext={setCursor}
          onReset={() => setCursor(undefined)}
        />
      </RemoteState>
    </>
  );
}
const customerStatus = [
  "PendingVerification",
  "Active",
  "Suspended",
  "Blocked",
  "Closed",
];
const kycStatus = ["NotStarted", "Pending", "Verified", "Rejected", "Expired"];
export function CustomerDetails() {
  const { id } = useParams();
  const { has } = useAuth();
  const [revision, setRevision] = useState(0);
  const c = useRemote<Customer>(`/api/v1/admin/customers/${id}`, revision);
  const w = useRemote<Wallet[]>(
    has("wallet.view.any")
      ? `/api/v1/admin/customers/${id}/wallets`
      : undefined,
    revision,
  );
  const [reveal, setReveal] = useState<Customer>();
  const [dialog, setDialog] = useState<string>();
  useEffect(() => {
    if (!reveal) return;
    const timer = setTimeout(() => setReveal(undefined), 60000);
    return () => clearTimeout(timer);
  }, [reveal]);
  const status =
    typeof c.data?.status === "number"
      ? customerStatus[c.data.status]
      : c.data?.status;
  const kyc =
    typeof c.data?.kycStatus === "number"
      ? kycStatus[c.data.kycStatus]
      : c.data?.kycStatus;
  return (
    <>
      <Heading
        eyebrow="العملاء / التفاصيل"
        title={
          c.data ? `${c.data.firstName} ${c.data.lastName}` : "تفاصيل العميل"
        }
        description="ابدأ بالبيانات المقنّعة، ثم راجع الحالة والمحافظ المرتبطة."
        action={<Link to="/customers">العودة إلى البحث</Link>}
      />
      <RemoteState {...c}>
        <div className="detail-grid">
          <section className="panel">
            <h2>ملف العميل</h2>
            <dl>
              <div>
                <dt>الحالة</dt>
                <dd>
                  <Status value={status || "—"} />
                </dd>
              </div>
              <div>
                <dt>KYC</dt>
                <dd>
                  <Status value={kyc || "—"} />
                </dd>
              </div>
              <div>
                <dt>جهة الاتصال</dt>
                <dd dir="ltr">{c.data?.phoneNumber || "—"}</dd>
              </div>
              <div>
                <dt>المعرّف</dt>
                <dd className="mono">{id}</dd>
              </div>
            </dl>
            {has("customer.pii.view") && (
              <button onClick={() => setDialog("pii")}>
                كشف البيانات الحساسة
              </button>
            )}
            {reveal && reveal.id === id && has("customer.pii.view") && (
              <div className="pii" role="status">
                <strong>بيانات مكشوفة لمدة دقيقة</strong>
                <p>
                  {reveal.firstName} {reveal.lastName}
                </p>
                <p dir="ltr">{reveal.phoneNumber}</p>
                <button onClick={() => setReveal(undefined)}>
                  إخفاء البيانات الآن
                </button>
              </div>
            )}
          </section>
          <section className="panel">
            <h2>إجراءات حالة العميل</h2>
            <p>
              تحتاج كل عملية سببًا. تُحفظ مع إصدار السجل لمنع الكتابة فوق تعديل
              متزامن.
            </p>
            <div className="actions">
              {[
                ["suspend", "customer.suspend", "تعليق العميل"],
                ["block", "customer.block", "حظر العميل"],
                ["unblock", "customer.unblock", "إلغاء الحظر"],
                ["kyc/approve", "kyc.review", "اعتماد KYC"],
                ["kyc/reject", "kyc.review", "رفض KYC"],
              ]
                .filter(([, p]) => has(p))
                .map(([action, , label]) => (
                  <button key={action} onClick={() => setDialog(action)}>
                    {label}
                  </button>
                ))}
            </div>
          </section>
        </div>
        {has("wallet.view.any") && (
          <section className="panel">
            <h2>المحافظ</h2>
            <RemoteState {...w} empty={w.data?.length === 0}>
              <Table
                caption="محافظ العميل"
                rows={w.data || []}
                columns={[
                  {
                    label: "المحفظة",
                    render: (x) => (
                      <Link to={`/wallets/${x.id}`} className="mono">
                        {x.publicHandle}
                      </Link>
                    ),
                  },
                  {
                    label: "الحالة",
                    render: (x) => <Status value={x.status} />,
                  },
                  { label: "العملة", render: (x) => x.currency },
                ]}
              />
            </RemoteState>
          </section>
        )}
      </RemoteState>
      {dialog && (
        <ConfirmDialog
          title={dialog === "pii" ? "كشف بيانات العميل" : "تغيير حالة العميل"}
          cancel={() => setDialog(undefined)}
          submit={async (reason) => {
            const result = await post<Customer>(
              `/api/v1/admin/customers/${id}/${dialog}`,
              dialog === "pii"
                ? { reason }
                : { reason, concurrencyVersion: c.data?.concurrencyVersion },
            );
            if (dialog === "pii") setReveal(result);
            else setRevision((x) => x + 1);
          }}
        >
          <p>
            {dialog === "pii"
              ? "يُسجّل كشف البيانات في Audit. ستُخفى البيانات تلقائيًا بعد دقيقة."
              : "راجع سبب القرار قبل إرساله."}
          </p>
        </ConfirmDialog>
      )}
    </>
  );
}
export function WalletDetails() {
  const { id } = useParams();
  const { has } = useAuth();
  const [revision, setRevision] = useState(0);
  const data = useRemote<{
    wallet: Wallet;
    balance: {
      ledgerBalance: number;
      availableBalance: number;
      asOf: string;
    } | null;
  }>(`/api/v1/admin/wallets/${id}`, revision);
  const [action, setAction] = useState<string>();
  return (
    <>
      <Heading
        eyebrow="المحافظ"
        title="حالة المحفظة"
        description="الرصيد يُقرأ من Ledger. إجراءات الحالة تؤثر في أهلية التحويل."
      />
      <RemoteState {...data}>
        <section className="panel">
          <p className="mono">{data.data?.wallet.publicHandle}</p>
          <Status value={data.data?.wallet.status || "—"} />
          <dl>
            <div>
              <dt>العملة</dt>
              <dd>{data.data?.wallet.currency}</dd>
            </div>
            <div>
              <dt>الرصيد المسجّل</dt>
              <dd>
                {data.data?.balance
                  ? amount(data.data.balance.ledgerBalance)
                  : "تجهيز Ledger قيد الانتظار"}
              </dd>
            </div>
            <div>
              <dt>الرصيد المتاح</dt>
              <dd>
                {data.data?.balance
                  ? amount(data.data.balance.availableBalance)
                  : "غير متاح"}
              </dd>
            </div>
          </dl>
          <div className="actions">
            {has("wallet.freeze") && data.data?.wallet.status === "Active" && (
              <button className="danger" onClick={() => setAction("freeze")}>
                تجميد المحفظة
              </button>
            )}
            {has("wallet.unfreeze") &&
              data.data?.wallet.status === "Frozen" && (
                <button onClick={() => setAction("unfreeze")}>
                  إلغاء التجميد
                </button>
              )}
            <Link to={`/customers/${data.data?.wallet.customerId}`}>
              فتح العميل المرتبط
            </Link>
          </div>
        </section>
        <MfaPanel />
      </RemoteState>
      {action && (
        <ConfirmDialog
          title={action === "freeze" ? "تجميد المحفظة" : "إلغاء تجميد المحفظة"}
          cancel={() => setAction(undefined)}
          submit={async (reason) => {
            await post(`/api/v1/admin/wallets/${id}/${action}`, {
              reason,
              concurrencyVersion: data.data?.wallet.concurrencyVersion,
            });
            setRevision((x) => x + 1);
          }}
        >
          <p>تتطلب العملية تحققًا إضافيًا خلال آخر خمس دقائق.</p>
        </ConfirmDialog>
      )}
    </>
  );
}
export function History({ transfers = false }: { transfers?: boolean }) {
  const [cursor, setCursor] = useState<string>();
  const [filters, setFilters] = useState({
    from: "",
    to: "",
    type: "",
    status: "",
    reference: "",
  });
  const [active, setActive] = useState(filters);
  const params = transfers
    ? { cursor }
    : {
        ...active,
        cursor,
        from: active.from ? new Date(active.from).toISOString() : undefined,
        to: active.to ? new Date(active.to).toISOString() : undefined,
      };
  const data = useRemote<Page<Transfer>>(
    `${transfers ? "/api/v1/admin/transfers" : "/api/v1/admin/transactions"}?${query(params)}`,
  );
  return (
    <>
      <Heading
        eyebrow="العمليات المالية"
        title={transfers ? "التحويلات" : "سجل الحركات"}
        description={
          transfers
            ? "راجع دورة التحويل من انتظار التأكيد إلى اكتمال العملية."
            : "الحركات المالية المكتملة وعمليات العكس المرتبطة بها. الأسماء مقنّعة."
        }
      />
      {!transfers && (
        <form
          className="filters"
          onSubmit={(e) => {
            e.preventDefault();
            setActive(filters);
            setCursor(undefined);
          }}
        >
          <label>
            من
            <input
              type="datetime-local"
              value={filters.from}
              onChange={(e) => setFilters({ ...filters, from: e.target.value })}
            />
          </label>
          <label>
            إلى
            <input
              type="datetime-local"
              value={filters.to}
              onChange={(e) => setFilters({ ...filters, to: e.target.value })}
            />
          </label>
          <label>
            المرجع
            <input
              value={filters.reference}
              onChange={(e) =>
                setFilters({ ...filters, reference: e.target.value })
              }
            />
          </label>
          <label>
            النوع
            <select
              value={filters.type}
              onChange={(e) => setFilters({ ...filters, type: e.target.value })}
            >
              <option value="">الكل</option>
              <option value="InternalTransfer">تحويل داخلي</option>
              <option value="Reversal">عكس</option>
            </select>
          </label>
          <button className="primary">تطبيق الفلاتر</button>
        </form>
      )}
      <RemoteState {...data} empty={data.data?.items.length === 0}>
        <Table
          caption="العمليات المالية"
          rows={data.data?.items || []}
          columns={[
            {
              label: "المرجع",
              render: (t) => (
                <Link
                  to={`/${transfers ? "transfers" : "transactions"}/${t.id}`}
                  className="mono"
                >
                  {t.publicReference}
                </Link>
              ),
            },
            {
              label: "الأطراف",
              render: (t) => (
                <>
                  {t.maskedSenderName}
                  <span className="muted"> ← </span>
                  {t.maskedRecipientName}
                </>
              ),
            },
            { label: "المبلغ", render: (t) => amount(t.amountMinor) },
            { label: "الرسوم", render: (t) => amount(t.feeMinor) },
            { label: "الحالة", render: (t) => <Status value={t.status} /> },
            {
              label: "التوقيت",
              render: (t) => date(t.occurredAt || t.createdAt),
            },
          ]}
        />
        <NextPage
          cursor={data.data?.nextCursor}
          onNext={setCursor}
          onReset={() => setCursor(undefined)}
        />
      </RemoteState>
    </>
  );
}
export function FinancialDetails({ transfer = false }: { transfer?: boolean }) {
  const { id } = useParams();
  const { has } = useAuth();
  const data = useRemote<Transfer>(
    transfer ? `/api/v1/admin/transfers/${id}` : `/api/v1/transactions/${id}`,
  );
  const [dialog, setDialog] = useState(false);
  const [receipt, setReceipt] = useState<Reversal>();
  const key = useRef(crypto.randomUUID());
  return (
    <>
      <Heading
        eyebrow="العمليات / التفاصيل"
        title="تفاصيل العملية"
        description="راجع الأطراف المقنّعة والمبلغ والرسوم قبل طلب العكس."
      />
      <RemoteState {...data}>
        <section className="panel">
          <h2 className="mono">{data.data?.publicReference}</h2>
          <Status value={data.data?.status || "—"} />
          <dl>
            <div>
              <dt>المرسل</dt>
              <dd>{data.data?.maskedSenderName}</dd>
            </div>
            <div>
              <dt>المستلم</dt>
              <dd>{data.data?.maskedRecipientName}</dd>
            </div>
            <div>
              <dt>المبلغ</dt>
              <dd>{amount(data.data?.amountMinor || 0)}</dd>
            </div>
            <div>
              <dt>الرسوم</dt>
              <dd>{amount(data.data?.feeMinor || 0)}</dd>
            </div>
          </dl>
          {has("transaction.reverse.request") &&
            data.data?.status === "Completed" && (
              <button className="danger" onClick={() => setDialog(true)}>
                طلب عكس كامل
              </button>
            )}
          {receipt && (
            <p role="status">
              أُنشئ طلب العكس.{" "}
              <Link to={`/reversals/${receipt.id}`}>فتح الطلب</Link>
            </p>
          )}
        </section>
      </RemoteState>
      {dialog && (
        <ConfirmDialog
          title="طلب عكس العملية بالكامل"
          cancel={() => setDialog(false)}
          submit={async (reason) =>
            setReceipt(
              await post(
                `/api/v1/admin/transfers/${id}/reversal-requests`,
                { reason },
                key.current,
              ),
            )
          }
        >
          <p>يتطلب اعتماد الطلب موظفًا آخر وتأكيد OTP مرتبطًا بالعملية.</p>
        </ConfirmDialog>
      )}
    </>
  );
}
export function Reversals() {
  const [cursor, setCursor] = useState<string>();
  const [status, setStatus] = useState("Pending");
  const data = useRemote<Page<Reversal>>(
    `/api/v1/admin/reversal-requests?${query({ status, cursor })}`,
  );
  return (
    <>
      <Heading
        eyebrow="Maker–Checker"
        title="طلبات العكس"
        description="مراجعة الطلبات واتخاذ القرار من موظف مستقل عن منشئها."
      />
      <div className="filters">
        <label>
          الحالة
          <select
            value={status}
            onChange={(e) => {
              setStatus(e.target.value);
              setCursor(undefined);
            }}
          >
            <option value="">الكل</option>
            {["Pending", "Completed", "Rejected"].map((s) => (
              <option key={s}>{s}</option>
            ))}
          </select>
        </label>
      </div>
      <RemoteState {...data} empty={data.data?.items.length === 0}>
        <Table
          caption="طلبات العكس"
          rows={data.data?.items || []}
          columns={[
            {
              label: "الطلب",
              render: (r) => (
                <Link className="mono" to={`/reversals/${r.id}`}>
                  {r.id}
                </Link>
              ),
            },
            {
              label: "العملية الأصلية",
              render: (r) => (
                <Link
                  className="mono"
                  to={`/transactions/${r.originalTransactionId}`}
                >
                  {r.originalTransactionId}
                </Link>
              ),
            },
            { label: "الحالة", render: (r) => <Status value={r.status} /> },
            { label: "أُنشئ", render: (r) => date(r.createdAt) },
          ]}
        />
        <NextPage
          cursor={data.data?.nextCursor}
          onNext={setCursor}
          onReset={() => setCursor(undefined)}
        />
      </RemoteState>
    </>
  );
}
export function ReversalDetails() {
  const { id } = useParams();
  const { has, session } = useAuth();
  const [revision, setRevision] = useState(0);
  const data = useRemote<Reversal>(
    `/api/v1/admin/reversal-requests/${id}`,
    revision,
  );
  const [action, setAction] = useState<string>();
  const [challenge, setChallenge] = useState<string>();
  const [code, setCode] = useState("");
  const [error, setError] = useState<unknown>();
  const [sending, setSending] = useState(false);
  const key = useRef(crypto.randomUUID());
  const canDecide =
    has("transaction.reverse.approve") &&
    data.data?.requestedBy !== session?.actorId &&
    data.data?.status === "Pending";
  return (
    <>
      <Heading
        eyebrow="Maker–Checker / القرار"
        title="مراجعة طلب العكس"
        description="يتحقق الخادم من استقلال المعتمد ومن ارتباط رمز التأكيد بالطلب."
      />
      <RemoteState {...data}>
        <section className="panel">
          <Status value={data.data?.status || "—"} />
          <dl>
            <div>
              <dt>سبب الطلب</dt>
              <dd>{data.data?.reason}</dd>
            </div>
            <div>
              <dt>منشئ الطلب</dt>
              <dd className="mono">{data.data?.requestedBy}</dd>
            </div>
            <div>
              <dt>العملية الأصلية</dt>
              <dd>
                <Link to={`/transactions/${data.data?.originalTransactionId}`}>
                  فتح الحركة الأصلية
                </Link>
              </dd>
            </div>
          </dl>
          {!!error && <ErrorNotice error={error} />}
          <div className="actions">
            {canDecide && (
              <>
                <button
                  disabled={sending}
                  onClick={async () => {
                    setSending(true);
                    setError(undefined);
                    try {
                      const r = await post<{ challengeId: string }>(
                        `/api/v1/admin/reversal-requests/${id}/otp`,
                        {},
                      );
                      setChallenge(r.challengeId);
                      setCode("");
                      setAction("approve");
                    } catch (err) {
                      setError(err);
                    } finally {
                      setSending(false);
                    }
                  }}
                >
                  إرسال رمز الاعتماد
                </button>
                <button className="danger" onClick={() => setAction("reject")}>
                  رفض الطلب
                </button>
              </>
            )}
            {data.data?.requestedBy === session?.actorId && (
              <p className="notice">أنت منشئ الطلب. يجب أن يراجعه موظف آخر.</p>
            )}
          </div>
        </section>
      </RemoteState>
      {action && (
        <ConfirmDialog
          title={action === "approve" ? "اعتماد العكس الكامل" : "رفض طلب العكس"}
          cancel={() => setAction(undefined)}
          submit={async (reason) => {
            await post(
              `/api/v1/admin/reversal-requests/${id}/${action}`,
              action === "approve"
                ? { note: reason, challengeId: challenge, code }
                : { reason },
              action === "approve" ? key.current : undefined,
            );
            setRevision((x) => x + 1);
            setCode("");
          }}
        >
          {action === "approve" && (
            <label>
              رمز اعتماد العملية
              <input
                required
                autoComplete="one-time-code"
                inputMode="numeric"
                value={code}
                onChange={(e) => setCode(e.target.value)}
              />
            </label>
          )}
        </ConfirmDialog>
      )}
    </>
  );
}
type AuditRow = {
  id: string;
  actorId: string;
  actorType: string;
  action: string;
  resourceType: string;
  resourceId: string;
  result: string;
  occurredAt: string;
  correlationId: string;
  reason: string | null;
};
export function Audit() {
  const [filters, setFilters] = useState({
    actor: "",
    action: "",
    resource: "",
    result: "",
    from: "",
    to: "",
  });
  const [active, setActive] = useState(filters);
  const [cursor, setCursor] = useState<string>();
  const data = useRemote<Page<AuditRow>>(
    `/api/v1/admin/audit-logs?${query({ ...active, from: active.from ? new Date(active.from).toISOString() : undefined, to: active.to ? new Date(active.to).toISOString() : undefined, cursor })}`,
  );
  return (
    <>
      <Heading
        eyebrow="الأدلة والتتبع"
        title="سجل التدقيق"
        description="من نفّذ القرار، وعلى أي مورد، وبأي نتيجة. أقصى نطاق للبحث 31 يومًا."
      />
      <form
        className="filters"
        onSubmit={(e) => {
          e.preventDefault();
          setActive(filters);
          setCursor(undefined);
        }}
      >
        {[
          ["actor", "معرّف الموظف"],
          ["action", "الإجراء"],
          ["resource", "نوع المورد"],
        ].map(([key, label]) => (
          <label key={key}>
            {label}
            <input
              dir="ltr"
              value={filters[key as keyof typeof filters]}
              onChange={(e) =>
                setFilters({ ...filters, [key]: e.target.value })
              }
            />
          </label>
        ))}
        <label>
          النتيجة
          <select
            value={filters.result}
            onChange={(e) => setFilters({ ...filters, result: e.target.value })}
          >
            <option value="">الكل</option>
            {["Success", "Denied", "Failed"].map((s) => (
              <option key={s}>{s}</option>
            ))}
          </select>
        </label>
        <label>
          من
          <input
            type="datetime-local"
            value={filters.from}
            onChange={(e) => setFilters({ ...filters, from: e.target.value })}
          />
        </label>
        <label>
          إلى
          <input
            type="datetime-local"
            value={filters.to}
            onChange={(e) => setFilters({ ...filters, to: e.target.value })}
          />
        </label>
        <button className="primary">بحث في Audit</button>
      </form>
      <RemoteState {...data} empty={data.data?.items.length === 0}>
        <Table
          caption="سجل التدقيق"
          rows={data.data?.items || []}
          columns={[
            {
              label: "الإجراء",
              render: (r) => (
                <>
                  <strong className="mono">{r.action}</strong>
                  <small>{r.reason}</small>
                </>
              ),
            },
            {
              label: "الموظف",
              render: (r) => <span className="mono">{r.actorId}</span>,
            },
            {
              label: "المورد",
              render: (r) => (
                <>
                  {r.resourceType}
                  <small className="mono">{r.resourceId}</small>
                </>
              ),
            },
            { label: "النتيجة", render: (r) => <Status value={r.result} /> },
            { label: "التوقيت", render: (r) => date(r.occurredAt) },
            {
              label: "Correlation",
              render: (r) => <span className="mono">{r.correlationId}</span>,
            },
          ]}
        />
        <NextPage
          cursor={data.data?.nextCursor}
          onNext={setCursor}
          onReset={() => setCursor(undefined)}
        />
      </RemoteState>
    </>
  );
}
export function Reports() {
  const { has } = useAuth();
  const [revision, setRevision] = useState(0);
  const [cursor, setCursor] = useState<string>();
  const data = useRemote<Page<Report>>(
    has("report.view")
      ? `/api/v1/admin/reports?${query({ cursor })}`
      : undefined,
    revision,
  );
  const [type, setType] = useState(() =>
    has("transaction.view.any")
      ? "Activity"
      : has("transfer.view.any")
        ? "Transfers"
        : has("customer.view")
          ? "CustomerSummary"
          : "",
  );
  const [created, setCreated] = useState<string>();
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [pii, setPii] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [error, setError] = useState<unknown>();
  const [busy, setBusy] = useState(false);
  const key = useRef({ payload: "", key: "" });
  useEffect(() => {
    if (!data.data?.items.some((x) => ["Queued", "Running"].includes(x.status)))
      return;
    const timer = setTimeout(() => setRevision((x) => x + 1), 5000);
    return () => clearTimeout(timer);
  }, [data.data]);
  async function create(reason: string) {
    setBusy(true);
    setError(undefined);
    try {
      const payload = {
        type,
        from: new Date(from).toISOString(),
        to: new Date(to).toISOString(),
        includePii: pii,
        reason,
      };
      const signature = JSON.stringify(payload);
      if (key.current.payload !== signature)
        key.current = { payload: signature, key: crypto.randomUUID() };
      const report = await post<Report>(
        "/api/v1/admin/reports",
        payload,
        key.current.key,
      );
      setCreated(report.id);
      setRevision((x) => x + 1);
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <Heading
        eyebrow="التقارير والتصدير"
        title="تقارير CSV"
        description="إنشاء غير متزامن، بحد أقصى 31 يومًا و10,000 صف. التقارير خاصة بمنشئها وتنتهي بعد 24 ساعة."
      />
      {!!error && <ErrorNotice error={error} />}
      {created && (
        <div className="notice" role="status">
          تم قبول طلب التقرير. <span className="mono">{created}</span>
        </div>
      )}
      {has("report.generate") && (
        <section className="panel">
          <h2>طلب تقرير جديد</h2>
          <form
            className="filters"
            onSubmit={(e) => {
              e.preventDefault();
              setConfirm(true);
            }}
          >
            <label>
              نوع التقرير
              <select
                value={type}
                onChange={(e) => {
                  setType(e.target.value);
                  setPii(false);
                }}
              >
                {[
                  ["Activity", "نشاط الحركات", "transaction.view.any"],
                  ["Transfers", "التحويلات", "transfer.view.any"],
                  ["Reversals", "العكس", "transaction.view.any"],
                  ["CustomerSummary", "ملخص العملاء", "customer.view"],
                ]
                  .filter(([, , p]) => has(p))
                  .map(([v, label]) => (
                    <option value={v} key={v}>
                      {label}
                    </option>
                  ))}
              </select>
            </label>
            <label>
              من
              <input
                type="datetime-local"
                required
                value={from}
                onChange={(e) => setFrom(e.target.value)}
              />
            </label>
            <label>
              إلى
              <input
                type="datetime-local"
                required
                value={to}
                onChange={(e) => setTo(e.target.value)}
              />
            </label>
            {type === "CustomerSummary" && has("customer.pii.view") && (
              <label className="checkbox">
                <input
                  type="checkbox"
                  checked={pii}
                  onChange={(e) => setPii(e.target.checked)}
                />
                كشف PII داخل التقرير (مدقّق)
              </label>
            )}
            <button className="primary" disabled={busy || !type}>
              {busy ? "جارٍ إرسال الطلب…" : "إنشاء التقرير"}
            </button>
          </form>
        </section>
      )}
      {has("report.view") && (
        <RemoteState {...data} empty={data.data?.items.length === 0}>
          <Table
            caption="التقارير المطلوبة"
            rows={data.data?.items || []}
            columns={[
              {
                label: "التقرير",
                render: (r) => (
                  <Link to={`/reports/${r.id}`}>
                    {r.type}
                    <small className="mono">{r.id}</small>
                  </Link>
                ),
              },
              { label: "الحالة", render: (r) => <Status value={r.status} /> },
              {
                label: "البيانات",
                render: (r) => (r.includesPii ? "PII مكشوفة" : "مقنّعة"),
              },
              { label: "الصفوف", render: (r) => r.rowCount ?? "—" },
              { label: "الانتهاء", render: (r) => date(r.expiresAt) },
              {
                label: "تنزيل",
                render: (r) => (
                  <button
                    disabled={r.status !== "Completed"}
                    onClick={async () => {
                      try {
                        await download(r.id);
                      } catch (err) {
                        setError(err);
                      }
                    }}
                  >
                    CSV
                  </button>
                ),
              },
            ]}
          />
          <NextPage
            cursor={data.data?.nextCursor}
            onNext={setCursor}
            onReset={() => setCursor(undefined)}
          />
        </RemoteState>
      )}
      {confirm && (
        <ConfirmDialog
          title="إنشاء تقرير CSV"
          cancel={() => setConfirm(false)}
          submit={create}
        >
          <p>
            {pii
              ? "سيتضمن التقرير بيانات PII مكشوفة. يسجل الخادم الطلب والتنفيذ والتنزيل."
              : "سيُنشأ التقرير في الخلفية مع تقنيع البيانات."}
          </p>
        </ConfirmDialog>
      )}
    </>
  );
}
export function ReportDetails() {
  const { id } = useParams();
  const [revision, setRevision] = useState(0);
  const data = useRemote<Report>(`/api/v1/admin/reports/${id}`, revision);
  const [error, setError] = useState<unknown>();
  useEffect(() => {
    if (!["Queued", "Running"].includes(data.data?.status || "")) return;
    const timer = setTimeout(() => setRevision((x) => x + 1), 5000);
    return () => clearTimeout(timer);
  }, [data.data]);
  return (
    <>
      <Heading
        eyebrow="التقارير / متابعة الطلب"
        title="حالة التقرير"
        description="يبدأ التنزيل بعد اكتمال التنفيذ وفحص الصلاحيات الحالي."
      />
      <RemoteState {...data}>
        <section className="panel">
          <h2>{data.data?.type}</h2>
          <Status value={data.data?.status || "—"} />
          <dl>
            <div>
              <dt>الأعمدة</dt>
              <dd>CSV · الإصدار {data.data?.schemaVersion}</dd>
            </div>
            <div>
              <dt>النطاق</dt>
              <dd>
                {data.data && `${date(data.data.from)} — ${date(data.data.to)}`}
              </dd>
            </div>
            <div>
              <dt>الصفوف</dt>
              <dd>{data.data?.rowCount ?? "لم يكتمل"}</dd>
            </div>
            <div>
              <dt>الخصوصية</dt>
              <dd>
                {data.data?.includesPii ? "PII مكشوفة بصلاحية صريحة" : "مقنّعة"}
              </dd>
            </div>
            <div>
              <dt>آخر حالة فشل</dt>
              <dd>{data.data?.failureCode || "—"}</dd>
            </div>
          </dl>
          {!!error && <ErrorNotice error={error} />}
          <button
            className="primary"
            disabled={data.data?.status !== "Completed"}
            onClick={async () => {
              try {
                await download(id!);
              } catch (err) {
                setError(err);
              }
            }}
          >
            تنزيل CSV
          </button>
        </section>
      </RemoteState>
    </>
  );
}
type Role = {
  id: string;
  name: string;
  displayName: string;
  concurrencyVersion: string;
  permissions: string[];
};
export function Roles() {
  const { has } = useAuth();
  const [revision, setRevision] = useState(0);
  const data = useRemote<Role[]>("/api/v1/admin/roles", revision);
  const catalog = useRemote<string[]>(
    has("permission.assign") ? "/api/v1/admin/permissions" : undefined,
  );
  const [editing, setEditing] = useState<Role>();
  const [name, setName] = useState("");
  const [display, setDisplay] = useState("");
  const [permissions, setPermissions] = useState<string[]>([]);
  return (
    <>
      <Heading
        eyebrow="الهوية الإدارية"
        title="الأدوار والصلاحيات"
        description="تُحفظ التعديلات بإصدار السجل، ويُمنع تعديل دورك أو منح صلاحيات تتجاوز صلاحياتك."
        action={
          has("permission.assign") && (
            <button
              className="primary"
              onClick={() => {
                setEditing({
                  id: "",
                  name: "",
                  displayName: "",
                  concurrencyVersion: "",
                  permissions: [],
                });
                setDisplay("");
                setName("");
                setPermissions([]);
              }}
            >
              إنشاء دور
            </button>
          )
        }
      />
      <RemoteState {...data} empty={data.data?.length === 0}>
        <Table
          caption="الأدوار"
          rows={data.data || []}
          columns={[
            { label: "الدور", render: (r) => <strong>{r.displayName}</strong> },
            {
              label: "الصلاحيات",
              render: (r) => (
                <div className="permission-list">
                  {r.permissions.map((p) => (
                    <code key={p}>{p}</code>
                  ))}
                </div>
              ),
            },
            {
              label: "إجراء",
              render: (r) => (
                <button
                  disabled={!has("permission.assign") || r.name === "Customer"}
                  onClick={() => {
                    setEditing(r);
                    setDisplay(r.displayName);
                    setPermissions(r.permissions);
                  }}
                >
                  تعديل الدور
                </button>
              ),
            },
          ]}
        />
      </RemoteState>
      <MfaPanel />
      {editing && (
        <ConfirmDialog
          title="حفظ صلاحيات الدور"
          cancel={() => setEditing(undefined)}
          submit={async (reason) => {
            if (editing.id)
              await put(`/api/v1/admin/roles/${editing.id}`, {
                displayName: display,
                permissions,
                concurrencyVersion: editing.concurrencyVersion,
                reason,
              });
            else
              await post("/api/v1/admin/roles", {
                name,
                displayName: display,
                permissions,
                reason,
              });
            setRevision((x) => x + 1);
          }}
        >
          {!editing.id && (
            <label>
              اسم الدور الثابت
              <input
                required
                dir="ltr"
                maxLength={80}
                pattern="[A-Za-z0-9_-]+"
                value={name}
                onChange={(e) => setName(e.target.value)}
              />
            </label>
          )}
          <label>
            اسم العرض
            <input
              required
              maxLength={128}
              value={display}
              onChange={(e) => setDisplay(e.target.value)}
            />
          </label>
          <fieldset>
            <legend>الصلاحيات المتاحة لإسنادها</legend>
            <div className="permission-picker">
              {(catalog.data || []).filter(has).map((p) => (
                <label className="checkbox" key={p}>
                  <input
                    type="checkbox"
                    checked={permissions.includes(p)}
                    onChange={(e) =>
                      setPermissions(
                        e.target.checked
                          ? [...permissions, p]
                          : permissions.filter((x) => x !== p),
                      )
                    }
                  />
                  <span dir="ltr">{p}</span>
                </label>
              ))}
            </div>
          </fieldset>
        </ConfirmDialog>
      )}
    </>
  );
}
type Setting = { key: string; value: string; concurrencyVersion: string };
export function Settings() {
  const { has } = useAuth();
  const [revision, setRevision] = useState(0);
  const data = useRemote<Setting[]>("/api/v1/admin/settings", revision);
  const [editing, setEditing] = useState<Setting>();
  const [value, setValue] = useState("");
  return (
    <>
      <Heading
        eyebrow="إعدادات التشغيل"
        title="الإعدادات الأساسية"
        description="قيم محدودة للتحكم بالواجهة والدعم، مع حماية من التعديل المتزامن."
      />
      <RemoteState {...data}>
        <Table
          caption="الإعدادات"
          rows={data.data || []}
          columns={[
            { label: "الإعداد", render: (s) => <code>{s.key}</code> },
            { label: "القيمة", render: (s) => s.value },
            {
              label: "إجراء",
              render: (s) => (
                <button
                  disabled={!has("configuration.manage")}
                  onClick={() => {
                    setEditing(s);
                    setValue(s.value);
                  }}
                >
                  تعديل القيمة
                </button>
              ),
            },
          ]}
        />
      </RemoteState>
      <MfaPanel />
      {editing && (
        <ConfirmDialog
          title="تغيير الإعداد"
          cancel={() => setEditing(undefined)}
          submit={async (reason) => {
            await put(`/api/v1/admin/settings/${editing.key}`, {
              value,
              concurrencyVersion: editing.concurrencyVersion,
              reason,
            });
            setRevision((x) => x + 1);
          }}
        >
          <label>
            {editing.key}
            <input
              required
              value={value}
              onChange={(e) => setValue(e.target.value)}
              dir="ltr"
            />
          </label>
        </ConfirmDialog>
      )}
    </>
  );
}
