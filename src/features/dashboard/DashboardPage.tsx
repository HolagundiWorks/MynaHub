import { useEffect, useState } from "react";
import { Button, ClickableTile, Heading, InlineNotification, Stack } from "@carbon/react";
import { api, errorText, type AuditPageRow, type PageQuery } from "../../api";
import PageHeader from "../../components/PageHeader";
import { queueCreate, type CreateTarget } from "../../app/intent";

const query = (filters: Record<string, string> = {}): PageQuery => ({
  search: "", sort: "", dir: "", page: 0, page_size: 5, filters,
});

const rupees = (paise: number) => (paise / 100).toLocaleString("en-IN", { style: "currency", currency: "INR" });

type Metric = { label: string; value: string; hint: string; page: string };

async function settled<T>(work: Promise<T>): Promise<T | null> {
  try { return await work; } catch { return null; }
}

export default function DashboardPage({
  role, company, fy, onOpen,
}: {
  role: string;
  company: string;
  fy: string;
  onOpen: (page: string) => Promise<boolean>;
}) {
  const [metrics, setMetrics] = useState<Metric[] | null>(null);
  const [activity, setActivity] = useState<AuditPageRow[]>([]);
  const [error, setError] = useState("");
  const canEdit = role !== "Auditor";
  const canIssue = role === "Owner" || role === "Principal" || role === "Accounts";
  const canPost = role === "Owner" || role === "Accounts";
  const canAudit = role === "Owner" || role === "Auditor";

  useEffect(() => {
    let cancel = false;
    (async () => {
      const [clients, projects, drafts, invoices, bank, audit] = await Promise.all([
        settled(api.clientList(false)),
        settled(api.projectList(null, false)),
        settled(api.docPage(query({ status: "draft" }))),
        settled(api.invoicePage(query())),
        role === "DocumentEditor" ? Promise.resolve(null) : settled(api.bankSummary(null)),
        canAudit ? settled(api.auditPage(query())) : Promise.resolve(null),
      ]);
      if (cancel) return;
      const next: Metric[] = [];
      if (clients) next.push({ label: "Clients", value: String(clients.length), hint: "Not archived", page: "clients" });
      if (projects) {
        const active = projects.filter((p) => !p.archived && p.status === "active").length;
        next.push({ label: "Active projects", value: String(active), hint: `${projects.filter((p) => !p.archived).length} not archived`, page: "projects" });
      }
      if (drafts) next.push({ label: "Draft documents", value: String(drafts.total), hint: "Letters and agreements not yet issued", page: "documents" });
      if (invoices) {
        const draftInvoices = await settled(api.invoicePage(query({ status: "draft" })));
        if (!cancel && draftInvoices) next.push({ label: "Draft invoices", value: String(draftInvoices.total), hint: "Not issued", page: "invoices" });
        next.push({ label: "Outstanding", value: rupees(invoices.sums.outstanding ?? 0), hint: "Balance on issued tax invoices", page: "invoices" });
      }
      if (bank) next.push({ label: "Unreconciled", value: String(bank.unmatched), hint: `${bank.partial} partly matched · ${bank.rejected_rows} rejected rows`, page: "bank" });
      if (next.length === 0) setError("The overview could not be loaded.");
      setMetrics(next);
      setActivity(audit?.rows ?? []);
    })().catch((e) => { if (!cancel) setError(errorText(e)); });
    return () => { cancel = true; };
  }, [role, canAudit]);

  const start = async (page: CreateTarget) => {
    if (await onOpen(page)) queueCreate(page);
  };

  return (
    <Stack gap={6}>
      <PageHeader
        title="Dashboard"
        description={`${company || "This company"} · Financial year ${fy}. Figures come from the open company file.`}
      />
      {error && <InlineNotification kind="error" lowContrast hideCloseButton title={error} />}
      {metrics === null && !error && <p>Loading the company overview…</p>}
      {metrics && metrics.length > 0 && (
        <div className="ao-cols-4">
          {metrics.map((m) => (
            <ClickableTile
              key={m.label}
              onClick={() => { void onOpen(m.page); }}
              aria-label={`${m.label}, ${m.value}. ${m.hint}. Open ${m.label}`}
            >
              <p className="ao-muted">{m.label}</p>
              <Heading>{m.value}</Heading>
              <p className="ao-muted">{m.hint}</p>
            </ClickableTile>
          ))}
        </div>
      )}
      {metrics && metrics.length === 0 && !error && <p>Nothing to summarise yet.</p>}

      <div>
        <h2>Start work</h2>
        <div className="ao-actions ao-block-start">
          {canEdit && <Button size="sm" onClick={() => { void start("clients"); }}>New client</Button>}
          {canEdit && <Button size="sm" kind="tertiary" onClick={() => { void start("projects"); }}>New project</Button>}
          {canEdit && <Button size="sm" kind="tertiary" onClick={() => { void start("documents"); }}>New letter</Button>}
          {canIssue && <Button size="sm" kind="tertiary" onClick={() => { void start("invoices"); }}>New invoice</Button>}
          {canPost && <Button size="sm" kind="tertiary" onClick={() => { void start("acc-expenses"); }}>Record expense</Button>}
          {canIssue && <Button size="sm" kind="tertiary" onClick={() => { void start("bank"); }}>Import bank statement</Button>}
        </div>
      </div>

      {canAudit && metrics && (
        <div>
          <h2>Recent activity</h2>
          {activity.length === 0 && <p className="ao-muted ao-block-start">No audit events yet.</p>}
          {activity.length > 0 && (
            <Stack gap={2}>
              {activity.map((row) => (
                <p key={row.id}>{row.at} · {row.user} · {row.event.replace(/_/g, " ")}{row.detail ? ` · ${row.detail}` : ""}</p>
              ))}
            </Stack>
          )}
        </div>
      )}
    </Stack>
  );
}
