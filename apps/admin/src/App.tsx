import {
  NavLink,
  Navigate,
  useLocation,
  Outlet,
  Route,
  Routes,
} from "react-router-dom";
import { useAuth, Gate, Login, MfaPanel } from "./auth";
import * as Pages from "./pages";
export const navigation: {
  to: string;
  label: string;
  permissions: string[];
  anyOf?: boolean;
}[] = [
  {
    to: "/",
    label: "نظرة على المنصة",
    permissions: ["operations.view", "customer.view", "transaction.view.any"],
  },
  {
    to: "/customers",
    label: "العملاء والمحافظ",
    permissions: ["customer.view"],
  },
  {
    to: "/transactions",
    label: "سجل الحركات",
    permissions: ["transaction.view.any"],
  },
  { to: "/transfers", label: "التحويلات", permissions: ["transfer.view.any"] },
  {
    to: "/reversals",
    label: "طلبات العكس",
    permissions: ["transaction.view.any"],
  },
  { to: "/audit", label: "سجل التدقيق", permissions: ["audit.view"] },
  {
    to: "/reports",
    label: "التقارير والتصدير",
    permissions: ["report.view", "report.generate"],
    anyOf: true,
  },
  { to: "/roles", label: "الأدوار والصلاحيات", permissions: ["role.manage"] },
  { to: "/settings", label: "الإعدادات", permissions: ["configuration.view"] },
  {
    to: "/messaging",
    label: "Messaging",
    permissions: ["transaction.view.any"],
  },
];
function Layout() {
  const { session, has, signOut } = useAuth();
  const location = useLocation();
  if (!session)
    return (
      <Navigate
        to="/login"
        state={{ from: location.pathname + location.search }}
        replace
      />
    );
  return (
    <div className="app">
      <a className="skip-link" href="#main">
        انتقل إلى المحتوى
      </a>
      <aside className="sidebar">
        <div className="wordmark">
          DW
          <span>
            Digital Wallet<small>مكتب العمليات</small>
          </span>
        </div>
        <p className="nav-label">مساحة العمل</p>
        <nav aria-label="القائمة الرئيسية">
          {navigation
            .filter((n) =>
              n.anyOf ? n.permissions.some(has) : n.permissions.every(has),
            )
            .map((n) => (
              <NavLink end={n.to === "/"} key={n.to} to={n.to}>
                <span className="nav-line" aria-hidden="true" />
                {n.label}
              </NavLink>
            ))}
        </nav>
        <div className="sidebar-foot">
          <span>البيانات مقنّعة افتراضيًا</span>
          <small>كل قرار حساس قابل للتتبع.</small>
        </div>
      </aside>
      <div className="workspace">
        <header className="topbar">
          <span>
            Digital Wallet Platform <span className="topbar-divider">/</span>{" "}
            الإدارة
          </span>
          <div>
            <NavLink to="/session">التحقق من الجلسة</NavLink>
            <button onClick={() => void signOut()}>تسجيل الخروج</button>
          </div>
        </header>
        <main id="main" tabIndex={-1}>
          <Outlet />
        </main>
        <footer>مكتب العمليات · التوقيت: الرياض UTC+3</footer>
      </div>
    </div>
  );
}
export function App() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route element={<Layout />}>
        <Route
          index
          element={
            <Gate
              permission={[
                "operations.view",
                "customer.view",
                "transaction.view.any",
              ]}
            >
              <Pages.Dashboard />
            </Gate>
          }
        />
        <Route
          path="customers"
          element={
            <Gate permission="customer.view">
              <Pages.Customers />
            </Gate>
          }
        />
        <Route
          path="customers/:id"
          element={
            <Gate permission="customer.view">
              <Pages.CustomerDetails />
            </Gate>
          }
        />
        <Route
          path="wallets/:id"
          element={
            <Gate permission="wallet.view.any">
              <Pages.WalletDetails />
            </Gate>
          }
        />
        <Route
          path="transactions"
          element={
            <Gate permission="transaction.view.any">
              <Pages.History />
            </Gate>
          }
        />
        <Route
          path="transactions/:id"
          element={
            <Gate permission="transaction.view.any">
              <Pages.FinancialDetails />
            </Gate>
          }
        />
        <Route
          path="transfers"
          element={
            <Gate permission="transfer.view.any">
              <Pages.History transfers />
            </Gate>
          }
        />
        <Route
          path="transfers/:id"
          element={
            <Gate permission="transfer.view.any">
              <Pages.FinancialDetails transfer />
            </Gate>
          }
        />
        <Route
          path="reversals"
          element={
            <Gate permission="transaction.view.any">
              <Pages.Reversals />
            </Gate>
          }
        />
        <Route
          path="reversals/:id"
          element={
            <Gate permission="transaction.view.any">
              <Pages.ReversalDetails />
            </Gate>
          }
        />
        <Route
          path="audit"
          element={
            <Gate permission="audit.view">
              <Pages.Audit />
            </Gate>
          }
        />
        <Route
          path="reports"
          element={
            <Gate permission={["report.view", "report.generate"]} anyOf>
              <Pages.Reports />
            </Gate>
          }
        />
        <Route
          path="reports/:id"
          element={
            <Gate permission="report.view">
              <Pages.ReportDetails />
            </Gate>
          }
        />
        <Route
          path="roles"
          element={
            <Gate permission="role.manage">
              <Pages.Roles />
            </Gate>
          }
        />
        <Route
          path="settings"
          element={
            <Gate permission="configuration.view">
              <Pages.Settings />
            </Gate>
          }
        />
        <Route
          path="messaging"
          element={
            <Gate permission="transaction.view.any">
              <Pages.Messaging />
            </Gate>
          }
        />
        <Route path="session" element={<MfaPanel />} />
        <Route
          path="*"
          element={
            <div className="notice">
              <h1>الصفحة غير موجودة</h1>
              <NavLink to="/">العودة إلى مساحة العمل</NavLink>
            </div>
          }
        />
      </Route>
    </Routes>
  );
}
