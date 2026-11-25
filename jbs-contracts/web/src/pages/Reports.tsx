// Reports for admins and auditors (slice 8; product-spec sections 11 and 13; D-82 to D-84): the register, the pipeline, approvals and the audit trail.
// Each can be downloaded to Excel; the register also prints cleanly (or saves as PDF) from the browser.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  BadgeCheck,
  Columns3,
  FileDown,
  ListFilter,
  Lock,
  Printer,
  Search,
  ShieldCheck,
  Undo2,
} from "lucide-react";
import { useEffect, useState } from "react";
import { flushSync } from "react-dom";
import { Link, useSearchParams } from "react-router";
import { toast } from "sonner";
import { ContractFilters } from "@/components/ContractFilters";
import { Dialog } from "@/components/Dialog";
import { PageHeader } from "@/components/Help";
import {
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  EmptyState,
  Segmented,
  Skeleton,
  TextInput,
} from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { chosenFilters, filterFields, filterPrefix } from "@/lib/filters";
import type { Layout } from "@/lib/layout";
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

type ColumnType = "text" | "date" | "money" | "number" | "yesno";
type RegisterColumn = {
  key: string;
  label: string;
  group: string;
  type: ColumnType;
};
type RegisterRow = {
  id: number;
  reference: string;
  isConfidential: boolean;
  currencyCode: string;
  values: Record<string, string | number | boolean | null>;
};
type Register = {
  columns: RegisterColumn[];
  chosen: string[];
  defaults: string[];
  filtered: boolean;
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
  // When it was printed, for the "Printed ... by ..." line; set as printing starts.
  const [printedAt, setPrintedAt] = useState(() => new Date());
  useEffect(() => {
    const root = document.documentElement;
    let saved: string | null = null;
    const before = () => {
      flushSync(() => setPrintedAt(new Date())); // drawn before the page is captured
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
  return printedAt;
}

/** A value as the screen and printout show it. */
function cellText(column: RegisterColumn, row: RegisterRow): string {
  const v = row.values[column.key];
  if (v === null || v === undefined || v === "") return "";
  switch (column.type) {
    case "date":
      return formatDate(String(v));
    case "money":
      return formatMoney(Number(v), row.currencyCode, true);
    case "yesno":
      return v ? "Yes" : "No";
    case "number":
      return Number(v).toLocaleString("en-AU");
    default:
      return String(v);
  }
}

function ColumnsDialog({
  register,
  onClose,
}: {
  register: Register;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [picked, setPicked] = useState<string[]>(register.chosen);
  const groups = [...new Set(register.columns.map((c) => c.group))];
  const save = useMutation({
    mutationFn: (columns: string[]) =>
      api.put<{ chosen: string[] }>("/api/reports/register/columns", {
        columns,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["report", "register"] });
      toast.success("Columns saved. They're kept for next time.");
      onClose();
    },
    onError: (e) =>
      toast.error(
        e instanceof ApiError ? e.message : "That didn't save. Try again.",
      ),
  });
  // Keep the columns in the order the fields are arranged, so the table always reads the same way.
  const ordered = register.columns
    .map((c) => c.key)
    .filter((k) => picked.includes(k));
  const toggle = (key: string, on: boolean) =>
    setPicked((p) => (on ? [...p, key] : p.filter((k) => k !== key)));
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Choose columns"
      description="Tick what to show and print. Reference is always first. The Excel download always has every column."
      footer={
        <>
          <Button variant="ghost" onClick={() => setPicked(register.defaults)}>
            Back to the usual columns
          </Button>
          <Button
            variant="primary"
            onClick={() => save.mutate(ordered)}
            disabled={ordered.length === 0 || save.isPending}
          >
            {ordered.length === 0
              ? "Tick at least one"
              : `Show ${ordered.length} ${ordered.length === 1 ? "column" : "columns"}`}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {groups.map((g) => (
          <fieldset key={g}>
            <legend className="text-sm font-semibold">{g}</legend>
            <div className="mt-1 grid sm:grid-cols-2 sm:gap-x-4">
              {register.columns
                .filter((c) => c.group === g)
                .map((c) => (
                  <Checkbox
                    key={c.key}
                    label={c.label}
                    checked={picked.includes(c.key)}
                    onChange={(on) => toggle(c.key, on)}
                  />
                ))}
            </div>
          </fieldset>
        ))}
      </div>
    </Dialog>
  );
}

function RegisterReport() {
  const { person } = useMe();
  const [params, setParams] = useSearchParams();
  const [scope, setScope] = useParam<Scope>(
    "scope",
    scopes.map((s) => s.value),
    "effective",
  );
  // Filters: the same as the Contracts list (fields an admin made filterable), kept in the address.
  const layout = useQuery({
    queryKey: ["layout"],
    queryFn: () => api.get<Layout>("/api/layout"),
  });
  const filterList = filterFields(layout.data?.fields);
  const chosenFilter = Object.fromEntries(
    Object.entries(chosenFilters(params)).filter(([k]) =>
      filterList.some((f) => f.key === k),
    ),
  );
  const filterCount = Object.keys(chosenFilter).length;
  const filterQuery = Object.entries(chosenFilter)
    .map(
      ([k, v]) =>
        `&${encodeURIComponent(filterPrefix + k)}=${encodeURIComponent(v)}`,
    )
    .join("");
  const [showFilters, setShowFilters] = useState(
    () => Object.keys(chosenFilters(params)).length > 0,
  );
  const setFilter = (key: string, value: string) =>
    setParams(
      (p) => {
        const n = new URLSearchParams(p);
        if (value) n.set(filterPrefix + key, value);
        else n.delete(filterPrefix + key);
        return n;
      },
      { replace: true },
    );
  const clearFilters = () =>
    setParams(
      (p) => {
        const n = new URLSearchParams(p);
        for (const key of [...n.keys()])
          if (key.startsWith(filterPrefix)) n.delete(key);
        return n;
      },
      { replace: true },
    );
  const [choosing, setChoosing] = useState(false);

  const data = useQuery({
    queryKey: ["report", "register", scope, filterQuery],
    queryFn: () =>
      api.get<Register>(`/api/reports/register?scope=${scope}${filterQuery}`),
    placeholderData: (p) => p,
  });
  const printedAt = usePrintLight();
  const rows = data.data?.rows ?? [];
  const columns = (data.data?.chosen ?? [])
    .map((k) => data.data!.columns.find((c) => c.key === k))
    .filter((c): c is RegisterColumn => c !== undefined);
  const scopeName = scopes.find((s) => s.value === scope)!.print;
  const filterWords = Object.entries(chosenFilter)
    .map(([k]) => filterList.find((f) => f.key === k)?.label)
    .filter(Boolean)
    .join(", ");

  return (
    <section aria-label="Register" className="flex flex-col gap-4">
      <div className="flex flex-col gap-3 print:hidden lg:flex-row lg:flex-wrap lg:items-center lg:justify-between">
        <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap sm:items-center">
          <Segmented
            label="Which contracts"
            value={scope}
            onChange={setScope}
            options={scopes.map((s) => ({ value: s.value, label: s.label }))}
          />
          <div className="flex flex-wrap gap-2">
            {filterList.length > 0 && (
              <Button
                aria-expanded={showFilters}
                aria-controls="register-filters"
                icon={<ListFilter aria-hidden className="size-5" />}
                onClick={() => setShowFilters((v) => !v)}
              >
                Filters
                {filterCount > 0 && (
                  <span className="tabular rounded-full bg-primary px-2 text-xs text-primary-foreground">
                    {filterCount}
                  </span>
                )}
              </Button>
            )}
            <Button
              icon={<Columns3 aria-hidden className="size-5" />}
              onClick={() => setChoosing(true)}
              disabled={!data.data}
            >
              Columns
            </Button>
          </div>
        </div>
        <div className="flex flex-wrap gap-1">
          <Download
            href={`/api/reports/register.xlsx?scope=${scope}${filterQuery}`}
          />
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

      {showFilters && filterList.length > 0 && (
        <div id="register-filters" className="print:hidden">
          <ContractFilters
            fields={filterList}
            chosen={chosenFilter}
            onChange={setFilter}
            onClear={clearFilters}
          />
        </div>
      )}

      {/* Only on paper: what this is, when and who. */}
      <div className="hidden print:block">
        <h2 className="font-display text-xl font-semibold">
          JBS Contracts register: {scopeName.toLowerCase()}
        </h2>
        <p className="text-sm">
          Printed {formatDateTime(printedAt)} by {person.name}.{" "}
          {plural(rows.length, "contract")}
          {filterWords && `, filtered by ${filterWords}`}.
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
            title={
              data.data.filtered
                ? "No contracts match these filters."
                : "No contracts to show yet. They appear here once the Group Head approves them."
            }
            action={
              data.data.filtered ? (
                <Button onClick={clearFilters}>Clear filters</Button>
              ) : undefined
            }
          />
        </Card>
      ) : (
        <>
          <p className="text-sm text-muted-foreground print:hidden">
            {plural(data.data.totals.count, "contract")}
            {data.data.totals.value.length > 0 &&
              `, worth ${data.data.totals.value.map((v) => formatMoney(v.total, v.currency, true)).join(" and ")} in total`}
            . Confidential contracts are included and marked. The Excel download
            has every column.
          </p>
          <Card className="overflow-x-auto print:overflow-visible print:rounded-none print:border-0 print:shadow-none">
            <table className="w-full text-left text-sm print:text-[8pt]">
              <thead className="border-b border-border text-xs text-muted-foreground print:text-[7pt]">
                <tr>
                  {["Reference", ...columns.map((c) => c.label)].map((h) => (
                    <th
                      key={h}
                      scope="col"
                      className="px-3 py-2 align-bottom font-medium first:pl-5 last:pr-5"
                    >
                      {h}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {rows.map((r) => (
                  <tr key={r.id} className="break-inside-avoid align-top">
                    <td className="px-3 py-2 pl-5 whitespace-nowrap">
                      <Link
                        to={`/contracts/${r.id}`}
                        className="font-medium hover:underline"
                      >
                        {r.reference}
                      </Link>
                      {r.isConfidential && (
                        <span className="flex items-center gap-0.5 text-xs text-muted-foreground">
                          <Lock aria-hidden className="size-3" /> Confidential
                        </span>
                      )}
                    </td>
                    {columns.map((c) => (
                      <td
                        key={c.key}
                        className={cn(
                          "px-3 py-2 last:pr-5",
                          c.type === "text"
                            ? "min-w-36"
                            : "tabular whitespace-nowrap",
                          c.key === "description" && "min-w-64",
                        )}
                      >
                        {cellText(c, r)}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
        </>
      )}
      {choosing && data.data && (
        <ColumnsDialog
          register={data.data}
          onClose={() => setChoosing(false)}
        />
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
