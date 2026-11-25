// The Team page (D-85): who has what coming up and outstanding, so a lead can see where people need to focus.
// For admins, auditors and Group Heads. It only shows; the daily reminders already email owners.
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, UsersRound } from "lucide-react";
import { useState } from "react";
import { Link, useSearchParams } from "react-router";
import { PageHeader } from "@/components/Help";
import {
  Alert,
  Avatar,
  Card,
  EmptyState,
  Segmented,
  Skeleton,
} from "@/components/ui";
import { api } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDate, formatMoney } from "@/lib/format";
import { useMe } from "@/lib/me";
import { help } from "@/help";

type What =
  | "Overdue"
  | "ComingUp"
  | "Draft"
  | "BackFromLegal"
  | "Signing"
  | "Variation"
  | "WithLegal"
  | "InApproval"
  | "LegalTurn";
type Item = {
  contractId: number;
  reference: string;
  title: string;
  what: What;
  kind: "Notice" | "End" | "Ends" | null;
  date: string | null;
  days: number | null;
  variationId: number | null;
  variationNumber: number | null;
  supplierName: string | null;
  value: number | null;
  currencyCode: string;
  category: string | null;
  endDate: string | null;
  isEvergreen: boolean;
};
type Person = {
  id: number;
  name: string;
  isContractOwner: boolean;
  isLegal: boolean;
  overdue: number;
  comingUp: number;
  theirTurn: number;
  waitingOnOthers: number;
  legalTurn: number;
  owns: number;
  backsUp: number;
  supplierWork: number;
  suppliers: {
    kind: "Action" | "ReviewDue";
    supplierId: number;
    supplierName: string;
    what: string;
    due: string;
    days: number;
  }[];
  items: Item[];
};
type Data = {
  days: number;
  totals: {
    overdue: number;
    comingUp: number;
    theirTurn: number;
    legalTurn: number;
    legalNotPickedUp: number;
  };
  people: Person[];
};

const windows = ["30", "60", "90", "180"] as const;
const n = (count: number, one: string, many = `${one}s`) =>
  `${count} ${count === 1 ? one : many}`;
const dayCount = (d: number) => (d === 1 ? "1 day" : `${d} days`);
const ago = (d: number) => (d === 0 ? "today" : `${dayCount(d)} ago`);
const waiting = (d: number) => (d === 0 ? "since today" : `for ${dayCount(d)}`);

const groups: { title: string; whats: What[] }[] = [
  { title: "Overdue: no decision and the date has passed", whats: ["Overdue"] },
  { title: "Coming up: no renewal decision yet", whats: ["ComingUp"] },
  {
    title: "Their turn",
    whats: ["Draft", "BackFromLegal", "Signing", "Variation"],
  },
  { title: "Legal reviews they have picked up", whats: ["LegalTurn"] },
  { title: "Waiting on someone else", whats: ["WithLegal", "InApproval"] },
];

function describe(i: Item): string {
  const deadline = i.kind === "Notice" ? "Notice deadline" : "Ends";
  switch (i.what) {
    case "Overdue":
      return `${deadline} ${formatDate(i.date)}, ${ago(-(i.days ?? 0))}`;
    case "ComingUp":
      return `${deadline} ${formatDate(i.date)}, ${i.days === 0 ? "today" : `in ${dayCount(i.days ?? 0)}`}`;
    case "Draft":
      return `Draft to finish, started ${ago(i.days ?? 0)}`;
    case "BackFromLegal":
      return `Back from Legal ${ago(i.days ?? 0)}`;
    case "Signing":
      return `Signed copy to attach, waiting ${waiting(i.days ?? 0)}`;
    case "Variation":
      return `Variation ${i.variationNumber} to finish, waiting ${waiting(i.days ?? 0)}`;
    case "LegalTurn":
      return `${i.variationNumber ? `Variation ${i.variationNumber}: ` : ""}Legal's turn ${waiting(i.days ?? 0)}`;
    case "WithLegal":
      return `${i.variationNumber ? `Variation ${i.variationNumber} ` : ""}with Legal ${waiting(i.days ?? 0)}`;
    case "InApproval":
      return `${i.variationNumber ? `Variation ${i.variationNumber} ` : ""}waiting for approval ${waiting(i.days ?? 0)}`;
  }
}

function Pill({
  count,
  label,
  tone,
}: {
  count: number;
  label: string;
  tone?: "danger" | "warning" | "primary";
}) {
  if (count === 0) return null;
  return (
    <span
      className={cn(
        "tabular inline-flex min-h-7 items-center rounded-full px-2.5 text-xs font-medium whitespace-nowrap",
        tone === "danger"
          ? "bg-danger-soft text-danger"
          : tone === "warning"
            ? "bg-warning-soft text-warning"
            : tone === "primary"
              ? "bg-primary-soft text-primary-soft-foreground"
              : "bg-muted text-muted-foreground",
      )}
    >
      {count} {label}
    </span>
  );
}

function PersonCard({ p }: { p: Person }) {
  const [open, setOpen] = useState(false);
  const nothing = p.items.length === 0 && p.supplierWork === 0;
  const roles = [p.isContractOwner && "Contract owner", p.isLegal && "Legal"]
    .filter(Boolean)
    .join(" · ");
  return (
    <li>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        disabled={nothing}
        className="flex w-full flex-col gap-2 p-4 text-left enabled:hover:bg-muted/50 sm:flex-row sm:items-center sm:justify-between"
      >
        <span className="flex min-w-0 items-center gap-3">
          <Avatar name={p.name} size={36} />
          <span className="min-w-0">
            <span className="block truncate font-medium">{p.name}</span>
            <span className="block truncate text-xs text-muted-foreground">
              {roles || "No contract role"}
              {(p.owns > 0 || p.backsUp > 0) &&
                ` · Owns ${p.owns} Effective${p.backsUp > 0 ? `, backup for ${p.backsUp}` : ""}`}
            </span>
          </span>
        </span>
        <span className="flex flex-wrap items-center gap-1.5 sm:justify-end">
          {nothing ? (
            <span className="text-sm text-muted-foreground">
              Nothing outstanding
            </span>
          ) : (
            <>
              <Pill count={p.overdue} label="overdue" tone="danger" />
              <Pill count={p.comingUp} label="coming up" tone="warning" />
              <Pill count={p.theirTurn} label="their turn" tone="primary" />
              <Pill
                count={p.legalTurn}
                label={p.legalTurn === 1 ? "Legal review" : "Legal reviews"}
                tone="primary"
              />
              <Pill count={p.waitingOnOthers} label="waiting on others" />
              <Pill count={p.supplierWork} label="supplier" tone="primary" />
              <ChevronDown
                aria-hidden
                className={cn(
                  "size-5 text-muted-foreground transition-transform",
                  open && "rotate-180",
                )}
              />
            </>
          )}
        </span>
      </button>
      {open && (
        <div className="flex flex-col gap-4 border-t border-border bg-muted/30 px-4 py-4 sm:pl-16">
          {groups.map((g) => {
            const list = p.items.filter((i) => g.whats.includes(i.what));
            if (list.length === 0) return null;
            return (
              <section key={g.title}>
                <h2
                  className={cn(
                    "text-sm font-semibold",
                    g.whats[0] === "Overdue" && "text-danger",
                  )}
                >
                  {g.title}{" "}
                  <span className="tabular font-normal text-muted-foreground">
                    ({list.length})
                  </span>
                </h2>
                <ul className="mt-1 flex flex-col gap-2">
                  {list.map((i) => (
                    <li
                      key={`${i.what}-${i.contractId}-${i.variationId ?? 0}`}
                      className="text-sm"
                    >
                      <Link
                        to={
                          i.variationId
                            ? `/contracts/${i.contractId}/variations/${i.variationId}`
                            : `/contracts/${i.contractId}`
                        }
                        className="font-medium hover:underline"
                      >
                        {i.title}
                      </Link>{" "}
                      <span className="text-muted-foreground">
                        {i.reference} ·{" "}
                        <span
                          className={cn(i.what === "Overdue" && "text-danger")}
                        >
                          {describe(i)}
                        </span>
                      </span>
                      <span className="block text-xs text-muted-foreground">
                        {[
                          i.supplierName ?? "No supplier",
                          i.value !== null &&
                            formatMoney(i.value, i.currencyCode, true),
                          i.category,
                          i.isEvergreen
                            ? "Evergreen"
                            : i.endDate && `Ends ${formatDate(i.endDate)}`,
                        ]
                          .filter(Boolean)
                          .join(" · ")}
                      </span>
                    </li>
                  ))}
                </ul>
              </section>
            );
          })}
          {p.suppliers.length > 0 && (
            <section>
              <h2 className="text-sm font-semibold">
                Suppliers{" "}
                <span className="tabular font-normal text-muted-foreground">
                  ({p.suppliers.length})
                </span>
              </h2>
              <ul className="mt-1 flex flex-col gap-2">
                {p.suppliers.map((x, i) => (
                  <li key={i} className="text-sm">
                    <Link
                      to={`/suppliers/${x.supplierId}?tab=reviews`}
                      className="font-medium hover:underline"
                    >
                      {x.supplierName}
                    </Link>{" "}
                    <span
                      className={cn(
                        "text-muted-foreground",
                        x.days < 0 && "text-danger",
                      )}
                    >
                      ·{" "}
                      {x.kind === "Action"
                        ? `Action: "${x.what}"`
                        : "Business review"}{" "}
                      due {formatDate(x.due)}
                      {x.days < 0 ? `, ${-x.days} days overdue` : ""}
                    </span>
                  </li>
                ))}
              </ul>
            </section>
          )}
        </div>
      )}
    </li>
  );
}

export function TeamPage() {
  const { person } = useMe();
  const [params, setParams] = useSearchParams();
  const days = (windows as readonly string[]).includes(params.get("days") ?? "")
    ? params.get("days")!
    : "90";
  const allowed =
    person.roles.admin || person.roles.auditor || person.roles.groupHead;
  const data = useQuery({
    queryKey: ["team", days],
    queryFn: () => api.get<Data>(`/api/team?days=${days}`),
    placeholderData: (previous) => previous,
    enabled: allowed,
  });

  if (!allowed) {
    return (
      <>
        <PageHeader title="Team" />
        <Alert
          tone="info"
          title="The Team page is for admins, auditors and Group Heads."
        >
          Your own work is on Home.
        </Alert>
      </>
    );
  }

  const t = data.data?.totals;
  return (
    <>
      <PageHeader
        title="Team"
        description="Who has what coming up and outstanding. Click a person to see their contracts."
        help={help.team}
      />
      <div>
        <Segmented
          label="Coming up in the next"
          value={days}
          onChange={(v) =>
            setParams(
              (p) => {
                const next = new URLSearchParams(p);
                next.set("days", v);
                return next;
              },
              { replace: true },
            )
          }
          options={windows.map((w) => ({ value: w, label: `${w} days` }))}
        />
      </div>
      {data.isPending ? (
        <Skeleton className="h-64 w-full" />
      ) : data.isError ? (
        <Alert tone="danger" title="We couldn't load the Team page.">
          {(data.error as Error).message} Refresh the page to try again.
        </Alert>
      ) : data.data.people.length === 0 ? (
        <Card>
          <EmptyState
            icon={<UsersRound />}
            title="No contract owners or Legal people yet. Add them on the People page."
          />
        </Card>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <Card className="p-4">
              <p className="text-sm text-muted-foreground">Overdue</p>
              <p
                className={cn(
                  "tabular mt-1 font-display text-2xl font-semibold",
                  t!.overdue > 0 && "text-danger",
                )}
              >
                {t!.overdue}
              </p>
              <p className="mt-1 text-xs text-muted-foreground">
                Date passed, no decision
              </p>
            </Card>
            <Card className="p-4">
              <p className="text-sm text-muted-foreground">Coming up</p>
              <p className="tabular mt-1 font-display text-2xl font-semibold">
                {t!.comingUp}
              </p>
              <p className="mt-1 text-xs text-muted-foreground">
                In the next {days} days, no decision
              </p>
            </Card>
            <Card className="p-4">
              <p className="text-sm text-muted-foreground">Owners' turn</p>
              <p className="tabular mt-1 font-display text-2xl font-semibold">
                {t!.theirTurn}
              </p>
              <p className="mt-1 text-xs text-muted-foreground">
                Drafts, back from Legal, signing, Variations
              </p>
            </Card>
            <Card className="p-4">
              <p className="text-sm text-muted-foreground">With Legal</p>
              <p className="tabular mt-1 font-display text-2xl font-semibold">
                {t!.legalTurn + t!.legalNotPickedUp}
              </p>
              <p className="mt-1 text-xs text-muted-foreground">
                {t!.legalNotPickedUp > 0
                  ? `${n(t!.legalNotPickedUp, "not picked up", "not picked up")} yet`
                  : "All picked up"}
              </p>
            </Card>
          </div>
          <Card>
            <ul className="divide-y divide-border">
              {data.data.people.map((p) => (
                <PersonCard key={p.id} p={p} />
              ))}
            </ul>
          </Card>
        </>
      )}
    </>
  );
}
