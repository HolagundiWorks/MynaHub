import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import {
  Content, ContainedList, ContainedListItem, GlobalTheme, Header, HeaderGlobalAction, HeaderGlobalBar, HeaderMenuButton, HeaderName, HeaderPanel, Modal, Search,
  SideNav, SideNavItems, SideNavMenu, SideNavMenuItem, SkipToContent, Switcher, SwitcherDivider, SwitcherItem,
} from "@carbon/react";
import { Calculator, Folder, Receipt, Search as SearchIcon, Settings } from "@carbon/icons-react";
import { api, type AuthStatus } from "../api";
import { DensityContext, usePersisted, type Density } from "../design/density";
import { GroupContext } from "../components/PageHeader";
import { DialogHost, confirmDialog } from "../components/dialogs";
import ClientsPage from "../features/clients/ClientsPage";
import ProjectsPage from "../features/projects/ProjectsPage";
import InvoicesPage from "../features/invoices/InvoicesPage";
import BankPage from "../features/bank/BankPage";
import SettingsPage from "../features/settings/SettingsPage";
import DocumentsPage from "../features/documents/DocumentsPage";
import TemplatesPage from "../features/documents/TemplatesPage";
import CompanyPage from "../features/company/CompanyPage";
import {
  AccountSetup, AssetsSection, ExpensesSection, IncomeSection, JournalsSection, ReportsSection, SalariesSection, TaxPaymentsSection,
} from "../features/accounting/AccountingPage";

type NavItem = { id: string; label: string; group: string; keywords?: string };

/** One Carbon icon per navigation group, used the same way everywhere. */
const GROUP_ICON = { Work: Folder, Billing: Receipt, Accounting: Calculator, Settings: Settings } as const;

const NAV: NavItem[] = [
  { id: "documents", label: "Letters and agreements", group: "Work", keywords: "letters agreements inward outward register" },
  { id: "clients", label: "Clients", group: "Work" },
  { id: "projects", label: "Projects", group: "Work" },
  { id: "invoices", label: "Invoices and receipts", group: "Billing", keywords: "invoice pro forma credit note receipt payment tds" },
  { id: "bank", label: "Bank reconciliation", group: "Billing", keywords: "bank statement csv match" },
  { id: "acc-reports", label: "Reports", group: "Accounting", keywords: "trial balance profit loss balance sheet gst tds ledger" },
  { id: "acc-journals", label: "Journals", group: "Accounting", keywords: "ledger journal voucher" },
  { id: "acc-expenses", label: "Expenses", group: "Accounting", keywords: "vendor bill" },
  { id: "acc-income", label: "Rent and interest", group: "Accounting", keywords: "income rent interest" },
  { id: "acc-salaries", label: "Salaries", group: "Accounting", keywords: "payroll pf esi" },
  { id: "acc-assets", label: "Fixed assets", group: "Accounting", keywords: "depreciation" },
  { id: "acc-tax", label: "Tax payments", group: "Accounting", keywords: "tds gst challan" },
  { id: "acc-setup", label: "Chart and financial years", group: "Accounting", keywords: "accounts periods close" },
  { id: "templates", label: "Templates", group: "Settings" },
  { id: "company", label: "Company profile", group: "Settings", keywords: "gst upi bank logo" },
  { id: "settings", label: "Security, users and backup", group: "Settings", keywords: "password users audit backup restore lock" },
];

function financialYear(): string {
  const d = new Date();
  const y = d.getMonth() >= 3 ? d.getFullYear() : d.getFullYear() - 1;
  return `${y}-${String((y + 1) % 100).padStart(2, "0")}`;
}

type PaletteItem = { label: string; hint: string; run: () => void };

function Palette({ open, items, onClose }: { open: boolean; items: PaletteItem[]; onClose: () => void }) {
  const [q, setQ] = useState("");
  useEffect(() => { if (open) setQ(""); }, [open]);
  const shown = items.filter((x) => `${x.label} ${x.hint}`.toLowerCase().includes(q.toLowerCase()));
  const pick = (it?: PaletteItem) => { if (it) { onClose(); it.run(); } };
  return (
    <Modal passiveModal open={open} size="sm" modalHeading="Go to" onRequestClose={onClose}>
      <Search
        id="palette-search" labelText="Go to a page or run a command" placeholder="Go to a page or run a command…" size="lg" value={q} data-modal-primary-focus
        onChange={(e: { target: { value: string } }) => setQ(e.target.value)}
        onKeyDown={(e: { key: string }) => { if (e.key === "Enter") pick(shown[0]); }}
      />
      <ContainedList label="Results" kind="disclosed" size="md">
        {shown.map((x) => <ContainedListItem key={x.label} onClick={() => pick(x)}>{x.label} <span className="ao-muted">· {x.hint}</span></ContainedListItem>)}
        {shown.length === 0 && <ContainedListItem>No match</ContainedListItem>}
      </ContainedList>
    </Modal>
  );
}

export default function Shell({ status, refresh }: { status: AuthStatus; refresh: () => void }) {
  const role = status.user?.role ?? "";
  const [density, setDensity] = usePersisted<Density>("ao.density", "compact");
  const [page, setPage] = usePersisted<string>("ao.page", "documents");
  const [navOpen, setNavOpen] = usePersisted<boolean>("ao.navOpen", true);
  const [panelOpen, setPanelOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [company, setCompany] = useState("");

  const current = NAV.find((n) => n.id === page) ?? NAV[0];
  const loadCompany = useCallback(() => { api.companyGet().then((c) => setCompany(c.name)).catch(() => setCompany("")); }, []);
  useEffect(loadCompany, [loadCompany, page]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); setPaletteOpen((o) => !o); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const lock = async () => { await api.authLock(); refresh(); };
  const logout = async () => { await api.authLogout(); refresh(); };
  const closeCompany = async () => {
    if (!(await confirmDialog(`Close ${status.company?.name ?? "this company"}? Anything you are typing that is not saved will be lost.`))) return;
    await api.companyClose();
    refresh();
  };

  const paletteItems: PaletteItem[] = useMemo(() => [
    ...NAV.map((n) => ({ label: n.label, hint: n.group, run: () => setPage(n.id) })),
    { label: "Lock the screen", hint: "Command", run: lock },
    { label: "Log out", hint: "Command", run: logout },
    { label: "Close company", hint: "Command", run: closeCompany },
    ...(["compact", "comfortable", "expanded"] as Density[]).map((d) => ({ label: `Table size: ${d}`, hint: "Command", run: () => setDensity(d) })),
  // eslint-disable-next-line react-hooks/exhaustive-deps
  ], []);

  const body: Record<string, ReactNode> = {
    documents: <DocumentsPage />,
    clients: <ClientsPage />,
    projects: <ProjectsPage />,
    invoices: <InvoicesPage />,
    bank: <BankPage />,
    "acc-reports": <ReportsSection />,
    "acc-journals": <JournalsSection role={role} />,
    "acc-expenses": <ExpensesSection role={role} />,
    "acc-income": <IncomeSection role={role} />,
    "acc-salaries": <SalariesSection role={role} />,
    "acc-assets": <AssetsSection role={role} />,
    "acc-tax": <TaxPaymentsSection role={role} />,
    "acc-setup": <AccountSetup role={role} />,
    templates: <TemplatesPage role={role} />,
    company: <CompanyPage />,
    settings: <SettingsPage role={role} onRestored={refresh} />,
  };

  const groups = Array.from(new Set(NAV.map((n) => n.group)));
  const accountingBlocked = role === "DocumentEditor" && current.group === "Accounting";
  const densityLabel: Record<Density, string> = { compact: "Compact", comfortable: "Comfortable", expanded: "Expanded" };

  return (
    <GlobalTheme theme="white">
      <DensityContext.Provider value={density}>
        <Header aria-label="ADMS">
          <SkipToContent />
          <HeaderMenuButton aria-label={navOpen ? "Close navigation" : "Open navigation"} isCollapsible onClick={() => setNavOpen(!navOpen)} isActive={navOpen} />
          <HeaderName href="#" prefix="ADMS" onClick={(e: { preventDefault: () => void }) => e.preventDefault()}>{company || "Company not set up"} · FY {financialYear()}</HeaderName>
          <HeaderGlobalBar>
            <HeaderGlobalAction aria-label="Go to page or command (Ctrl+K)" tooltipAlignment="end" onClick={() => setPaletteOpen(true)}><SearchIcon size={20} /></HeaderGlobalAction>
            <HeaderGlobalAction aria-label="Preferences and session" tooltipAlignment="end" isActive={panelOpen} onClick={() => setPanelOpen(!panelOpen)}><Settings size={20} /></HeaderGlobalAction>
          </HeaderGlobalBar>
          <HeaderPanel aria-label="Preferences and session" expanded={panelOpen} onHeaderPanelFocus={() => undefined}>
            <Switcher aria-label="Preferences and session">
              <SwitcherItem aria-label={`Signed in as ${status.user?.username}`} href="#" onClick={(e: { preventDefault: () => void }) => e.preventDefault()}>{status.user?.username} · {role}</SwitcherItem>
              <SwitcherDivider />
              {(["compact", "comfortable", "expanded"] as Density[]).map((d) => (
                <SwitcherItem key={d} aria-label={`Table size ${densityLabel[d]}`} href="#" isSelected={density === d} onClick={(e: { preventDefault: () => void }) => { e.preventDefault(); setDensity(d); }}>
                  Table size: {densityLabel[d]}{density === d ? " (current)" : ""}
                </SwitcherItem>
              ))}
              <SwitcherDivider />
              <SwitcherItem aria-label="Lock the screen" href="#" onClick={(e: { preventDefault: () => void }) => { e.preventDefault(); lock(); }}>Lock the screen</SwitcherItem>
              <SwitcherItem aria-label="Log out" href="#" onClick={(e: { preventDefault: () => void }) => { e.preventDefault(); logout(); }}>Log out</SwitcherItem>
              <SwitcherItem aria-label="Close company" href="#" onClick={(e: { preventDefault: () => void }) => { e.preventDefault(); setPanelOpen(false); closeCompany(); }}>Close company</SwitcherItem>
            </Switcher>
          </HeaderPanel>
        </Header>

        <SideNav aria-label="Side navigation" expanded={navOpen} isPersistent={navOpen} isFixedNav onOverlayClick={() => setNavOpen(false)}>
          <SideNavItems>
            {groups.map((g) => (
              <SideNavMenu key={g} title={g} renderIcon={GROUP_ICON[g as keyof typeof GROUP_ICON]} defaultExpanded isActive={current.group === g}>
                {NAV.filter((n) => n.group === g).map((n) => (
                  <SideNavMenuItem key={n.id} href="#" isActive={n.id === current.id} aria-current={n.id === current.id ? "page" : undefined}
                    onClick={(e: { preventDefault: () => void }) => { e.preventDefault(); setPage(n.id); }}>
                    {n.label}
                  </SideNavMenuItem>
                ))}
              </SideNavMenu>
            ))}
          </SideNavItems>
        </SideNav>

        <Content id="main-content" key={current.id}>
          <GroupContext.Provider value={current.group}>
            {accountingBlocked ? <p>Accounting is not available for your role.</p> : body[current.id]}
          </GroupContext.Provider>
        </Content>

        <Palette open={paletteOpen} items={paletteItems} onClose={() => setPaletteOpen(false)} />
        <DialogHost />
      </DensityContext.Provider>
    </GlobalTheme>
  );
}
