// Reports for admins and auditors (slice 8; product-spec sections 11 and 13; D-82 to D-84): the register, the pipeline, approvals and the audit trail.
// Each can be downloaded to Excel; the register also prints cleanly (or saves as PDF) from the browser.
import { useQuery } from "@tanstack/react-query";
import {
  BadgeCheck,
  FileDown,
  Lock,
  Printer,
  Search,
  ShieldCheck,
  Undo2,
} from "lucide-react";
import { useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { PageHeader } from "@/components/Help";
import {
  Alert,
  Badge,
  Button,
  Card,
  EmptyState,
  Segmented,
  Skeleton,
  TextInput,
} from "@/components/ui";
import { api } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDate, formatDateTime, formatMoney } from "@/lib/format";
import { useMe } from "@/lib/me";
import { help } from "@/help";

const tabs = [
  { value: "register", label: "Register" },
  { value: "pipeline", label: "Pipeline" },
  { value: "approvals", label: "Approvals" },
  { value: "audit", label: "Audit trail" },
] as const;
type Tab = (typeof tabs)[number]["value"];

function useParam<T extends string>(
  key: string,
  allowed: readonly T[],
  fallback: T,
): [T, (v: T) => void] {
  const [params, setParams] = useSearchParams();
  const raw = params.get(key) as T | null;
  const value = raw && allowed.includes(raw) ? raw : fallback;
  const set = (v: T) =>
    setParams(
      (p) => {
        const n = new URLSearchParams(p);
        n.set(key, v);
        return n;
      },
      { replace: true },
    );
  return [value, set];
}

const downloadClass =
  "inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] px-4 font-medium hover:bg-muted";

function Download({
  href,
  children = "Download Excel",
}: {
  href: string;
  children?: string;
}) {
  return (
    <a href={href} className={downloadClass}>
      <FileDown aria-hidden className="size-5" /> {children}
    </a>
  );
}

function Loading() {
  return <Skeleton className="h-64 w-full" />;
}

function Failed({ error }: { error: unknown }) {
  return (
    <Alert tone="danger" title="We couldn't load this report.">
      {(error as Error).message} Refresh the page to try again.
    </Alert>
  );
}

function Stat({
  label,
  value,
  note,
}: {
  label: string;
  value: string | number;
  note?: string;
}) {
  return (
    <Card className="p-4">
      <p className="text-sm text-muted-foreground">{label}</p>
      <p className="tabular mt-1 font-display text-2xl font-semibold">
        {value}
      </p>
      {note && <p className="mt-1 text-xs text-muted-foreground">{note}</p>}
    </Card>
  );
}

const plural = (n: number, one: string, many = `${one}s`) =>
  `${n.toLocaleString("en-AU")} ${n === 1 ? one : many}`;
const dayText = (d: number) =>
  d < 1 ? "under a day" : plural(Math.round(d), "day");

// ------------------------------------------------------------------ Register

type RegisterRow = {
  id: number;
  reference: string;
  title: string;
  stage: string;
  stageName: string;
  supplierName: string | null;
  category: string | null;
  ownerName: string;
  isConfidential: boolean;
  value: number | null;
  currencyCode: string;
  effectiveDate: string | null;
  endDate: string | null;
  isEvergreen: boolean;
  noticeDeadline: string | null;
  registeredUtc: string | null;
  registeredByName: string | null;
  renewalDecision: string | null;
};
type Register = {
  rows: RegisterRow[];
  totals: { count: number; value: { currency: string; total: number }[] };
};

const scopes = [
  { value: "effective", label: "Effective now", print: "Effective contracts" },
  {
    value: "ended",
    label: "Effective and ended",
    print: "Effective and ended contracts",
  },
  {
    value: "all",
    label: "All, including archived",
    print: "Effective, ended and archived contracts",
  },
] as const;
type Scope = (typeof scopes)[number]["value"];

/** Prints in the light theme whatever the screen uses, so paper and PDFs are always black on white. */
function usePrintLight() {
  useEffect(() => {
    const root = document.documentElement;
    let saved: string | null = null;
    const before = () => {
      saved = root.getAttribute("data-theme");
      root.setAttribute("data-theme", "light");
    };
    const after = () => {
      if (saved === null) root.removeAttribute("data-theme");
      else root.setAttribute("data-theme", saved);
    };
    window.addEventListener("beforeprint", before);
    window.addEventListener("afterprint", after);
    return () => {
      window.removeEventListener("beforeprint", before);
      window.removeEventListener("afterprint", after);
    };
  }, []);
}

function RegisterReport() {
  const { person } = useMe();
  const [scope, setScope] = useParam<Scope>(
    "scope",
    scopes.map((s) => s.value),
    "effective",
  );
  const data = useQuery({
    queryKey: ["report", "register", scope],
    queryFn: () => api.get<Register>(`/api/reports/register?scope=${scope}`),
    placeholderData: (p) => p,
  });
  usePrintLight();
  const rows = data.data?.rows ?? [];
  const scopeName = scopes.find((s) => s.value === scope)!.print;

  return (
    <section aria-label="Register" className="flex flex-col gap-4">
      <div className="flex flex-col gap-3 print:hidden lg:flex-row lg:items-center lg:justify-between">
        <Segmented
          label="Which contracts"
          value={scope}
          onChange={setScope}
          options={scopes.map((s) => ({ value: s.value, label: s.label }))}
        />
        <div className="flex flex-wrap gap-1">
          <Download href={`/api/reports/register.xlsx?scope=${scope}`} />
          <Button
            variant="ghost"
            icon={<Printer aria-hidden className="size-5" />}
            onClick={() => window.print()}
            disabled={rows.length === 0}
          >
            Print or save as PDF
          </Button>
        </div>
      </div>

      {/* Only on paper: what this is, when and who. */}
      <div className="hidden print:block">
        <h2 className="font-display text-xl font-semibold">
          JBS Contracts register: {scopeName.toLowerCase()}
        </h2>
        <p className="text-sm">
          Printed {formatDateTime(new Date())} by {person.name}.{" "}
          {plural(rows.length, "contract")}.
        </p>
      </div>

      {data.isPending ? (
        <Loading />
      ) : data.isError ? (
        <Failed error={data.error} />
      ) : rows.length === 0 ? (
        <Card>
          <EmptyState
            icon={<BadgeCheck />}
            title="No contracts to show yet. They appear here once the Group Head approves them."
          />
        </Card>
      ) : (
        <>
          <p className="text-sm text-muted-foreground print:hidden">
            {plural(data.data!.totals.count, "contract")}
            {data.data!.totals.value.length > 0 &&
              `, worth ${data.data!.totals.value.map((v) => formatMoney(v.total, v.currency, true)).join(" and ")} in total`}
            . Confidential contracts are included and marked. The Excel download
            also has every added field.
          </p>
          <Card className="overflow-x-auto print:overflow-visible print:rounded-none print:border-0 print:shadow-none">
            <table className="w-full min-w-[56rem] text-left text-sm print:min-w-0 print:text-[9pt]">
              <thead className="border-b border-border text-xs text-muted-foreground print:text-[8pt]">
                <tr>
                  {[
                    "Reference",
                    "Contract",
                    "Owner",
                    "Value",
                    "Effective",
                    "Ends",
                    "Notice deadline",
                    "Registered",
                  ].map((h) => (
                    <th
                      key={h}
                      scope="col"
                      className="px-3 py-2 font-medium whitespace-nowrap first:pl-5 last:pr-5"
                    >
                      {h}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {rows.map((r) => (
                  <tr key={r.id} className="align-top break-inside-avoid">
                    <td className="px-3 py-2 pl-5 whitespace-nowrap">
                      <Link
                        to={`/contracts/${r.id}`}
                        className="font-medium hover:underline"
                      >
                        {r.reference}
                      </Link>
                      {r.stage !== "Effective" && (
                        <span className="block text-xs text-muted-foreground">
                          {r.stageName}
                        </span>
                      )}
                    </td>
                    <td className="px-3 py-2">
                      <span className="font-medium">{r.title}</span>
                      {r.isConfidential && (
                        <span className="ml-1 inline-flex items-center gap-0.5 text-xs text-muted-foreground">
                          <Lock aria-hidden className="size-3" /> Confidential
                        </span>
                      )}
                      <span className="block text-xs text-muted-foreground">
                        {[r.supplierName ?? "No supplier", r.category]
                          .filter(Boolean)
                          .join(" · ")}
                      </span>
                    </td>
                    <td className="px-3 py-2">{r.ownerName}</td>
                    <td className="tabular px-3 py-2 whitespace-nowrap">
                      {r.value === null
                        ? ""
                        : formatMoney(r.value, r.currencyCode, true)}
                    </td>
                    <td className="tabular px-3 py-2 whitespace-nowrap">
                      {formatDate(r.effectiveDate)}
                    </td>
                    <td className="tabular px-3 py-2 whitespace-nowrap">
                      {r.isEvergreen ? "Evergreen" : formatDate(r.endDate)}
                    </td>
                    <td className="tabular px-3 py-2 whitespace-nowrap">
                      {formatDate(r.noticeDeadline)}
                      {r.renewalDecision && (
                        <span className="block text-xs text-muted-foreground">
                          {r.renewalDecision}
                        </span>
                      )}
                    </td>
                    <td className="px-3 py-2 pr-5 whitespace-nowrap">
                      {r.registeredUtc &&
                        formatDate(r.registeredUtc.slice(0, 10))}
                      {r.registeredByName && (
                        <span className="block text-xs text-muted-foreground">
                          {r.registeredByName}
                        </span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
        </>
      )}
    </section>
  );
}

// ------------------------------------------------------------------ Pipeline

type Pipeline = {
  stages: {
    stage: string;
    name: string;
    count: number;
    averageDays: number;
    longestDays: number;
  }[];
  legal: { legalsTurn: number; ownersTurn: number; notPickedUp: number };
  totals: Record<string, number>;
  variations: Record<string, number>;
  waitingLongest: {
    id: number;
    reference: string;
    title: string;
    stageName: string;
    days: number;
    with: string;
    supplierName: string | null;
  }[];
};

function PipelineReport() {
  const data = useQuery({
    queryKey: ["report", "pipeline"],
    queryFn: () => api.get<Pipeline>("/api/reports/pipeline"),
  });
  if (data.isPending) return <Loading />;
  if (data.isError) return <Failed error={data.error} />;
  const p = data.data;
  const open = p.stages.reduce((n, s) => n + s.count, 0);
  const openVariations = Object.values(p.variations).reduce((n, v) => n + v, 0);
  return (
    <section aria-label="Pipeline" className="flex flex-col gap-4">
      <p className="text-muted-foreground">
        {plural(open, "contract")} on the way,{" "}
        {plural(p.totals.Effective ?? 0, "contract")} Effective
        {openVariations > 0 && `, ${plural(openVariations, "Variation")} open`}.
        Days are counted from when each contract reached its stage.
      </p>
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        {p.stages.map((s) => (
          <Stat
            key={s.stage}
            label={s.name}
            value={s.count}
            note={
              s.count === 0
                ? "None waiting"
                : `Average ${dayText(s.averageDays)}, longest ${dayText(s.longestDays)}`
            }
          />
        ))}
      </div>
      {(p.totals.LegalReview ?? 0) > 0 && (
        <Card className="p-4 text-sm">
          <h2 className="font-display text-base font-semibold">
            In Legal review
          </h2>
          <p className="mt-1 text-muted-foreground">
            {plural(p.legal.legalsTurn, "contract")} with Legal (
            {p.legal.notPickedUp} not picked up yet),{" "}
            {plural(p.legal.ownersTurn, "contract")} back with the owner.
          </p>
        </Card>
      )}
      <h2 className="font-display text-lg font-semibold">Waiting longest</h2>
      {p.waitingLongest.length === 0 ? (
        <Card>
          <EmptyState
            icon={<BadgeCheck />}
            title="Nothing is on the way right now."
          />
        </Card>
      ) : (
        <Card>
          <ul className="divide-y divide-border">
            {p.waitingLongest.map((w) => (
              <li
                key={w.id}
                className="flex flex-wrap items-center justify-between gap-3 p-4"
              >
                <div className="min-w-0">
                  <Link
                    to={`/contracts/${w.id}`}
                    className="font-medium hover:underline"
                  >
                    {w.title}
                  </Link>
                  <p className="text-xs text-muted-foreground">
                    {w.reference} · {w.supplierName ?? "No supplier"} · With{" "}
                    {w.with}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  <Badge>{w.stageName}</Badge>
                  <span
                    className={cn(
                      "tabular text-sm font-medium",
                      w.days >= 14 ? "text-warning" : "text-muted-foreground",
                    )}
                  >
                    {dayText(w.days)}
                  </span>
                </div>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </section>
  );
}

// ------------------------------------------------------------------ Approvals

type Approvals = {
  days: number;
  rows: {
    contractId: number;
    reference: string;
    title: string;
    variation: number | null;
    decision: "Approved" | "SentBack";
    byName: string;
    asBackupForName: string | null;
    atUtc: string;
    reason: string | null;
    turnaroundDays: number | null;
  }[];
  summary: {
    approved: number;
    sentBack: number;
    averageDays: number | null;
    byApprover: {
      name: string;
      approved: number;
      sentBack: number;
      averageDays: number | null;
    }[];
  };
};

const periods = [
  { value: "30", label: "30 days" },
  { value: "90", label: "90 days" },
  { value: "365", label: "12 months" },
] as const;
type Period = (typeof periods)[number]["value"];

function ApprovalsReport() {
  const [days, setDays] = useParam<Period>(
    "days",
    periods.map((p) => p.value),
    "90",
  );
  const data = useQuery({
    queryKey: ["report", "approvals", days],
    queryFn: () => api.get<Approvals>(`/api/reports/approvals?days=${days}`),
    placeholderData: (p) => p,
  });
  return (
    <section aria-label="Approvals" className="flex flex-col gap-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <Segmented
          label="Over the last"
          value={days}
          onChange={setDays}
          options={[...periods]}
        />
        <Download href={`/api/reports/approvals.xlsx?days=${days}`} />
      </div>
      {data.isPending ? (
        <Loading />
      ) : data.isError ? (
        <Failed error={data.error} />
      ) : data.data.rows.length === 0 ? (
        <Card>
          <EmptyState
            icon={<BadgeCheck />}
            title="No approvals or send-backs in this period."
          />
        </Card>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-3">
            <Stat label="Approved" value={data.data.summary.approved} />
            <Stat label="Sent back" value={data.data.summary.sentBack} />
            <Stat
              label="Average wait"
              value={
                data.data.summary.averageDays === null
                  ? "Not known"
                  : dayText(data.data.summary.averageDays)
              }
              note="From sent for approval to the decision"
            />
          </div>
          <Card className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <caption className="px-5 pt-4 text-left font-display text-base font-semibold">
                By approver
              </caption>
              <thead className="border-b border-border text-xs text-muted-foreground">
                <tr>
                  {["Approver", "Approved", "Sent back", "Average wait"].map(
                    (h) => (
                      <th
                        key={h}
                        scope="col"
                        className="px-3 py-2 font-medium first:pl-5 last:pr-5"
                      >
                        {h}
                      </th>
                    ),
                  )}
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {data.data.summary.byApprover.map((a) => (
                  <tr key={a.name}>
                    <th scope="row" className="px-3 py-2 pl-5 font-medium">
                      {a.name}
                    </th>
                    <td className="tabular px-3 py-2">{a.approved}</td>
                    <td className="tabular px-3 py-2">{a.sentBack}</td>
                    <td className="tabular px-3 py-2 pr-5">
                      {a.averageDays === null ? "" : dayText(a.averageDays)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
          <Card>
            <ul className="divide-y divide-border">
              {data.data.rows.map((r, n) => (
                <li
                  key={n}
                  className="flex flex-wrap items-start justify-between gap-3 p-4"
                >
                  <div className="min-w-0">
                    <Link
                      to={`/contracts/${r.contractId}`}
                      className="font-medium hover:underline"
                    >
                      {r.title}
                      {r.variation !== null && `: Variation ${r.variation}`}
                    </Link>
                    <p className="text-xs text-muted-foreground">
                      {r.reference} · {r.byName}
                      {r.asBackupForName &&
                        ` (as backup for ${r.asBackupForName})`}{" "}
                      · {formatDateTime(r.atUtc)}
                      {r.turnaroundDays !== null &&
                        ` · waited ${dayText(r.turnaroundDays)}`}
                    </p>
                    {r.reason && <p className="mt-1 text-sm">“{r.reason}”</p>}
                  </div>
                  {r.decision === "Approved" ? (
                    <Badge
                      tone="success"
                      icon={<BadgeCheck aria-hidden className="size-3.5" />}
                    >
                      Approved
                    </Badge>
                  ) : (
                    <Badge
                      tone="warning"
                      icon={<Undo2 aria-hidden className="size-3.5" />}
                    >
                      Sent back
                    </Badge>
                  )}
                </li>
              ))}
            </ul>
          </Card>
        </>
      )}
    </section>
  );
}

// ------------------------------------------------------------------ Audit trail

type Audit = {
  days: number;
  intact: boolean;
  brokenAt: number | null;
  total: number;
  rows: {
    id: number;
    occurredUtc: string;
    actorName: string;
    action: string;
    summary: string;
    contractId: number | null;
    reference: string | null;
  }[];
};

const auditPeriods = [
  { value: "7", label: "7 days" },
  { value: "30", label: "30 days" },
  { value: "90", label: "90 days" },
  { value: "365", label: "12 months" },
  { value: "3650", label: "Everything" },
] as const;
type AuditPeriod = (typeof auditPeriods)[number]["value"];

function AuditReport() {
  const [days, setDays] = useParam<AuditPeriod>(
    "days",
    auditPeriods.map((p) => p.value),
    "30",
  );
  const [search, setSearch] = useState("");
  const [q, setQ] = useState("");
  // 50 at a time keeps the page short, especially on a phone.
  const [shown, setShown] = useState(50);
  useEffect(() => {
    const t = setTimeout(() => {
      setQ(search.trim());
      setShown(50);
    }, 300);
    return () => clearTimeout(t);
  }, [search]);
  const query = `days=${days}&q=${encodeURIComponent(q)}`;
  const data = useQuery({
    queryKey: ["report", "audit", days, q],
    queryFn: () => api.get<Audit>(`/api/reports/audit?${query}`),
    placeholderData: (p) => p,
  });
  return (
    <section aria-label="Audit trail" className="flex flex-col gap-4">
      {data.data &&
        (data.data.intact ? (
          <Alert tone="success" title="The audit trail is intact.">
            All {data.data.total.toLocaleString("en-AU")} events check out: none
            has been changed or removed since it was recorded.
          </Alert>
        ) : (
          <Alert tone="danger" title="The audit trail doesn't check out.">
            Event {data.data.brokenAt} or the one before it has been changed or
            removed outside JBS Contracts. Tell IT straight away; don't change
            anything else first.
          </Alert>
        ))}
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
          <Segmented
            label="Over the last"
            value={days}
            onChange={(v) => {
              setDays(v);
              setShown(50);
            }}
            options={[...auditPeriods]}
          />
          <div className="relative sm:w-72">
            <Search
              aria-hidden
              className="pointer-events-none absolute top-1/2 left-3 size-5 -translate-y-1/2 text-muted-foreground"
            />
            <TextInput
              type="search"
              aria-label="Search the audit trail"
              placeholder="Person, reference or words"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="pl-10"
            />
          </div>
        </div>
        <Download href={`/api/reports/audit.xlsx?${query}`} />
      </div>
      {data.isPending ? (
        <Loading />
      ) : data.isError ? (
        <Failed error={data.error} />
      ) : data.data.rows.length === 0 ? (
        <Card>
          <EmptyState
            icon={<Search />}
            title={
              q
                ? `Nothing matches “${q}” in this period.`
                : "Nothing happened in this period."
            }
          />
        </Card>
      ) : (
        <>
          {data.data.rows.length === 500 && (
            <p className="text-sm text-muted-foreground">
              Showing the newest 500. The Excel download has them all, with old
              and new values.
            </p>
          )}
          <Card>
            <ol className="divide-y divide-border">
              {data.data.rows.slice(0, shown).map((r) => (
                <li
                  key={r.id}
                  className="flex flex-col gap-0.5 p-4 sm:flex-row sm:items-start sm:gap-4"
                >
                  <span className="tabular shrink-0 text-xs text-muted-foreground sm:w-36 sm:pt-0.5">
                    {formatDateTime(r.occurredUtc)}
                  </span>
                  <div className="min-w-0">
                    <p>{r.summary}</p>
                    <p className="text-xs text-muted-foreground">
                      {r.actorName}
                      {r.contractId !== null && r.reference && (
                        <>
                          {" · "}
                          <Link
                            to={`/contracts/${r.contractId}`}
                            className="hover:underline"
                          >
                            {r.reference}
                          </Link>
                        </>
                      )}
                      {" · "}Event {r.id}
                    </p>
                  </div>
                </li>
              ))}
            </ol>
          </Card>
          {data.data.rows.length > shown && (
            <div>
              <Button variant="ghost" onClick={() => setShown((n) => n + 50)}>
                Show {Math.min(50, data.data.rows.length - shown)} more
              </Button>
            </div>
          )}
        </>
      )}
    </section>
  );
}

// ------------------------------------------------------------------ Page

export function ReportsPage() {
  const { person } = useMe();
  const [tab, setTab] = useParam<Tab>(
    "tab",
    tabs.map((t) => t.value),
    "register",
  );
  if (!person.roles.admin && !person.roles.auditor) {
    return (
      <>
        <PageHeader title="Reports" />
        <Alert tone="info" title="Reports are for admins and auditors.">
          Ask an admin if you need one.
        </Alert>
      </>
    );
  }
  return (
    <>
      <div className="print:hidden">
        <PageHeader
          title="Reports"
          description="The register, what's on the way, approvals and the audit trail. Download any of them to Excel."
          help={help.reports}
        />
      </div>
      <div className="print:hidden">
        <Segmented
          label="Report"
          value={tab}
          onChange={setTab}
          options={[...tabs]}
        />
      </div>
      {tab === "register" && <RegisterReport />}
      {tab === "pipeline" && <PipelineReport />}
      {tab === "approvals" && <ApprovalsReport />}
      {tab === "audit" && <AuditReport />}
      {tab === "audit" && (
        <p className="flex items-center gap-1 text-xs text-muted-foreground">
          <ShieldCheck aria-hidden className="size-4" /> Downloading the audit
          trail is itself recorded in it.
        </p>
      )}
    </>
  );
}
