// Admin, Import (slice 9, D-91): old contracts from a spreadsheet (match columns, dry run, import), then their files from ZIPs.
// Nothing changes until the admin imports; every row and file is audited.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  CheckCircle2,
  FileArchive,
  FileDown,
  FileSpreadsheet,
  MinusCircle,
  XCircle,
} from "lucide-react";
import { useRef, useState, type ReactNode } from "react";
import { Link } from "react-router";
import { toast } from "sonner";
import { Dialog } from "@/components/Dialog";
import { DropZone } from "@/components/DropZone";
import { PageHeader } from "@/components/Help";
import { Combobox } from "@/components/Combobox";
import { Select } from "@/components/form";
import {
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  Field,
  Segmented,
  type Tone,
} from "@/components/ui";
import { errorText } from "@/lib/actions";
import { api, ApiError } from "@/lib/api";
import {
  allDocumentKinds,
  categoryPaths,
  withAddedKinds,
  type Lists,
} from "@/lib/contracts";
import { formatBytes } from "@/lib/format";
import { useMe } from "@/lib/me";
import { help } from "@/help";

type Target = {
  key: string;
  label: string;
  group: string;
  required: boolean;
  help: string | null;
};
type Columns = {
  fileName: string;
  rows: number;
  remembered: boolean;
  columns: { header: string; examples: string[]; suggested: string | null }[];
  targets: Target[];
};
type Outcome = "Effective" | "Ended" | "Already" | "Refused" | "LeftOut";
type Plan = {
  effective: number;
  ended: number;
  already: number;
  refused: number;
  /** Archived in the old system, left out (D-109). */
  leftOut: number;
  newSuppliers: string[];
  /** List items to add under Admin, Lists first, by list. */
  missing: { list: string; names: string[] }[];
  rows: {
    row: number;
    legacyReference: string | null;
    label: string;
    outcome: Outcome;
    notes: string[];
    problems: string[];
    existingReference: string | null;
  }[];
};
type FileOutcome = "Match" | "Already" | "Unmatched" | "Refused";
type FilesPlan = {
  matched: number;
  already: number;
  unmatched: number;
  refused: number;
  contracts: number;
  files: {
    path: string;
    size: number;
    outcome: FileOutcome;
    reference: string | null;
    title: string | null;
    reason: string | null;
    kind: string;
    kindName: string;
    kindWhy: string | null;
  }[];
};

/** Sends a form with a file (the anti-forgery header included). */
async function postForm<T>(path: string, form: FormData): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, {
      method: "POST",
      credentials: "same-origin",
      headers: { "X-JBS-Contracts": "1", Accept: "application/json" },
      body: form,
    });
  } catch {
    throw new ApiError(0, {
      code: "offline",
      message:
        "We couldn't reach JBS Contracts. Check your connection and try again.",
    });
  }
  const body = await response.json().catch(() => undefined);
  if (!response.ok)
    throw new ApiError(
      response.status,
      body?.message
        ? body
        : { code: "error", message: "That didn't work. Try again." },
    );
  return body as T;
}

const outcomeBadge: Record<
  Outcome,
  { tone: Tone; label: string; icon: ReactNode }
> = {
  Effective: {
    tone: "success",
    label: "Effective",
    icon: <CheckCircle2 aria-hidden className="size-3.5" />,
  },
  Ended: {
    tone: "neutral",
    label: "Ended",
    icon: <CheckCircle2 aria-hidden className="size-3.5" />,
  },
  Already: {
    tone: "info",
    label: "Already imported",
    icon: <MinusCircle aria-hidden className="size-3.5" />,
  },
  Refused: {
    tone: "danger",
    label: "Can't import",
    icon: <XCircle aria-hidden className="size-3.5" />,
  },
  LeftOut: {
    tone: "neutral",
    label: "Left out (archived)",
    icon: <MinusCircle aria-hidden className="size-3.5" />,
  },
};

/** Old values in each drop-down column and what they become (D-112). */
type Values = {
  fields: {
    key: string;
    label: string;
    list: string;
    values: {
      value: string;
      count: number;
      to: string | null;
      remembered: boolean;
    }[];
  }[];
  choices: Record<string, string[]>;
};
type TranslationMap = Record<string, Record<string, string>>;
const BLANK = "__blank__";

async function downloadForm(form: FormData, name: string) {
  try {
    const response = await fetch("/api/import/legacy/values", {
      method: "POST",
      credentials: "same-origin",
      headers: { "X-JBS-Contracts": "1" },
      body: form,
    });
    if (!response.ok) throw new Error();
    const url = URL.createObjectURL(await response.blob());
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    a.click();
    URL.revokeObjectURL(url);
  } catch {
    toast.error("That didn't download. Try again.");
  }
}

/**
 * Translate values (D-112): every old value in each drop-down column, with how many contracts use it, becomes one of your list
 * values or is left blank. Remembered after an import; download them to use again on Production.
 */
function TranslateValues({
  form,
  translations,
  setTranslations,
}: {
  form: () => FormData;
  translations: TranslationMap;
  setTranslations: (t: TranslationMap) => void;
}) {
  const [values, setValues] = useState<Values | null>(null);
  const [error, setError] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const load = useMutation({
    mutationFn: (extra?: File) => {
      const f = form();
      if (extra) f.append("translationsFile", extra);
      return postForm<Values>("/api/import/legacy/values", f);
    },
    onSuccess: (v) => {
      setValues(v);
      setError(null);
      // What's remembered, on screen or already the same name in your lists starts chosen.
      const next: TranslationMap = {};
      for (const field of v.fields)
        for (const x of field.values)
          if (x.to !== null) (next[field.key] ??= {})[x.value] = x.to;
      setTranslations(next);
    },
    onError: (e) => setError(errorText(e)),
  });
  const choose = (field: string, value: string, to: string) => {
    const next = {
      ...translations,
      [field]: { ...(translations[field] ?? {}) },
    };
    if (to === "") delete next[field][value];
    else next[field][value] = to === BLANK ? "" : to;
    setTranslations(next);
  };
  return (
    <section aria-labelledby="translate" className="flex flex-col gap-3">
      <div>
        <h3 id="translate" className="font-semibold">
          Translate drop-down values
        </h3>
        <p className="text-sm text-muted-foreground">
          Choose what each old value becomes in JBS Contracts: one of your list
          values, or blank. Set up your lists under Admin, Lists first (then
          click Refresh). Values with nothing chosen must already be in your
          lists, or they're flagged in the check. For owners who've left, choose
          the current contract owner who takes their contracts.
        </p>
      </div>
      <div className="flex flex-wrap gap-2">
        <Button loading={load.isPending} onClick={() => load.mutate(undefined)}>
          {values ? "Refresh" : "Show the values"}
        </Button>
        {values && (
          <>
            <Button
              variant="ghost"
              icon={<FileDown aria-hidden className="size-4" />}
              onClick={() => {
                const f = form();
                f.append("download", "translations");
                void downloadForm(f, "jbs-contracts-import-translations.xlsx");
              }}
            >
              Download translations
            </Button>
            <input
              ref={fileInput}
              type="file"
              hidden
              accept=".xlsx"
              onChange={(e) => {
                const f = e.target.files?.[0];
                if (f) load.mutate(f);
                e.target.value = "";
              }}
            />
            <Button
              variant="ghost"
              icon={<FileSpreadsheet aria-hidden className="size-4" />}
              onClick={() => fileInput.current?.click()}
            >
              Use translations from a file
            </Button>
          </>
        )}
      </div>
      {error && <Alert tone="danger" title={error} />}
      {values?.fields.map((field) => {
        const chosen = field.values.filter(
          (x) => translations[field.key]?.[x.value] !== undefined,
        ).length;
        const choices = values.choices[field.list] ?? [];
        return (
          <details
            key={field.key}
            className="rounded-[var(--radius-card)] border border-border"
            open={chosen < field.values.length}
          >
            <summary className="flex cursor-pointer flex-wrap items-center justify-between gap-2 px-3 py-2">
              <span className="font-medium">{field.label}</span>
              <Badge
                tone={chosen === field.values.length ? "success" : "warning"}
              >
                {chosen} of {field.values.length} chosen
              </Badge>
            </summary>
            <ul className="divide-y divide-border border-t border-border">
              {field.values.map((x) => {
                const current = translations[field.key]?.[x.value];
                return (
                  <li
                    key={x.value}
                    className="grid gap-2 px-3 py-2 sm:grid-cols-[minmax(0,1fr)_minmax(14rem,1fr)] sm:items-center"
                  >
                    <span className="min-w-0 break-words text-sm">
                      {x.value}{" "}
                      <span className="text-muted-foreground">({x.count})</span>
                    </span>
                    {field.list === "Category" ? (
                      // The spend taxonomy is long (D-115): found by typing.
                      <Combobox
                        aria-label={`${field.label}: what "${x.value}" becomes`}
                        placeholder="Not chosen: type to find"
                        options={[
                          { value: "", label: "Not chosen" },
                          { value: BLANK, label: "Leave blank" },
                          ...(current && !choices.includes(current)
                            ? [
                                {
                                  value: current,
                                  label: `${current} (not in your lists)`,
                                },
                              ]
                            : []),
                          ...choices.map((ch) => ({
                            value: ch,
                            label: ch.replaceAll(" > ", " › "),
                          })),
                        ]}
                        value={
                          current === undefined
                            ? ""
                            : current === ""
                              ? BLANK
                              : current
                        }
                        onChange={(v) => choose(field.key, x.value, v)}
                      />
                    ) : (
                      <Select
                        aria-label={`${field.label}: what "${x.value}" becomes`}
                        value={
                          current === undefined
                            ? ""
                            : current === ""
                              ? BLANK
                              : current
                        }
                        onChange={(e) =>
                          choose(field.key, x.value, e.target.value)
                        }
                      >
                        <option value="">Not chosen</option>
                        <option value={BLANK}>
                          {field.key === "owner"
                            ? "Use the default owner"
                            : "Leave blank"}
                        </option>
                        {current && !choices.includes(current) && (
                          <option value={current}>
                            {current} (not in your lists)
                          </option>
                        )}
                        {choices.map((ch) => (
                          <option key={ch} value={ch}>
                            {ch.replaceAll(" > ", " › ")}
                          </option>
                        ))}
                      </Select>
                    )}
                  </li>
                );
              })}
            </ul>
          </details>
        );
      })}
    </section>
  );
}

/** The missing list items as a sheet for Admin, Lists, Import from Excel (D-109). */
async function downloadMissing(form: FormData) {
  form.append("download", "missing");
  try {
    const response = await fetch("/api/import/legacy/preview", {
      method: "POST",
      credentials: "same-origin",
      headers: { "X-JBS-Contracts": "1" },
      body: form,
    });
    if (!response.ok) throw new Error();
    const url = URL.createObjectURL(await response.blob());
    const a = document.createElement("a");
    a.href = url;
    a.download = "jbs-contracts-lists-to-add.xlsx";
    a.click();
    URL.revokeObjectURL(url);
  } catch {
    toast.error("That didn't download. Try again.");
  }
}
const fileBadge: Record<FileOutcome, { tone: Tone; label: string }> = {
  Match: { tone: "success", label: "Matched" },
  Already: { tone: "info", label: "Already there" },
  Unmatched: { tone: "warning", label: "Not matched" },
  Refused: { tone: "danger", label: "Can't add" },
};

function Stat({
  label,
  value,
  tone,
}: {
  label: string;
  value: number;
  tone?: "danger" | "success";
}) {
  return (
    <div className="rounded-[var(--radius-control)] bg-muted/60 p-3">
      <p className="text-xs text-muted-foreground">{label}</p>
      <p
        className={`tabular font-display text-xl font-semibold ${tone === "danger" && value > 0 ? "text-danger" : tone === "success" && value > 0 ? "text-success" : ""}`}
      >
        {value}
      </p>
    </div>
  );
}

// ------------------------------------------------------------------ Step 1: contracts

function ContractsStep() {
  const queryClient = useQueryClient();
  const lists = useQuery({
    queryKey: ["lists"],
    queryFn: () => api.get<Lists>("/api/lists"),
  });
  const [file, setFile] = useState<File | null>(null);
  const [columns, setColumns] = useState<Columns | null>(null);
  const [mapping, setMapping] = useState<Record<string, string>>({});
  const [defaultOwnerId, setDefaultOwnerId] = useState("");
  const [defaultCategoryId, setDefaultCategoryId] = useState("");
  const [leaveOutArchived, setLeaveOutArchived] = useState(true);
  const [translations, setTranslations] = useState<TranslationMap>({});
  const [plan, setPlan] = useState<Plan | null>(null);
  const [show, setShow] = useState<"problems" | "ready" | "all">("problems");
  const [confirming, setConfirming] = useState(false);
  const [done, setDone] = useState<{
    imported: number;
    first: string | null;
    last: string | null;
  } | null>(null);
  const [error, setError] = useState<string | null>(null);

  const form = () => {
    const f = new FormData();
    f.append("file", file!);
    f.append("mapping", JSON.stringify(mapping));
    if (defaultOwnerId) f.append("defaultOwnerId", defaultOwnerId);
    if (defaultCategoryId) f.append("defaultCategoryId", defaultCategoryId);
    if (leaveOutArchived) f.append("leaveOutArchived", "true");
    if (Object.keys(translations).length > 0)
      f.append("translations", JSON.stringify(translations));
    return f;
  };
  const read = useMutation({
    mutationFn: (f: File) => {
      const data = new FormData();
      data.append("file", f);
      return postForm<Columns>("/api/import/legacy/columns", data);
    },
    onSuccess: (c) => {
      setColumns(c);
      setMapping(
        Object.fromEntries(
          c.columns
            .filter((x) => x.suggested)
            .map((x) => [x.header, x.suggested!]),
        ),
      );
      setPlan(null);
      setDone(null);
      setError(null);
    },
    onError: (e) => setError(errorText(e)),
  });
  const check = useMutation({
    mutationFn: () => postForm<Plan>("/api/import/legacy/preview", form()),
    onSuccess: (p) => {
      setPlan(p);
      setShow(p.refused > 0 ? "problems" : "ready");
      setError(null);
    },
    onError: (e) => setError(errorText(e)),
  });
  const run = useMutation({
    mutationFn: () =>
      postForm<{
        imported: number;
        first: string | null;
        last: string | null;
        summary: Plan;
      }>("/api/import/legacy/import", form()),
    onSuccess: (r) => {
      setDone(r);
      setPlan(r.summary);
      setConfirming(false);
      void queryClient.invalidateQueries();
      toast.success(`${r.imported} contracts imported.`);
    },
    onError: (e) => {
      setConfirming(false);
      setError(errorText(e));
    },
  });

  const choose = (f: File | null) => {
    setFile(f);
    setColumns(null);
    setPlan(null);
    setDone(null);
    if (f) read.mutate(f);
  };
  const targets = columns?.targets ?? [];
  const groups = [...new Set(targets.map((t) => t.group))];
  const used = new Set(Object.values(mapping));
  const needed = targets.filter(
    (t) => t.required && !t.key.startsWith("field."),
  );
  const ready = (plan?.effective ?? 0) + (plan?.ended ?? 0);
  const rows = (plan?.rows ?? []).filter(
    (r) =>
      show === "all" ||
      (show === "problems"
        ? r.outcome === "Refused"
        : r.outcome === "Effective" || r.outcome === "Ended"),
  );
  // Categories and subcategories as paths (D-115), found by typing.
  const categories = categoryPaths(lists.data?.categories ?? []).filter(
    (p) => p.isActive,
  );

  return (
    <Card className="flex flex-col gap-4 p-5">
      <div className="flex items-start gap-3">
        <FileSpreadsheet
          aria-hidden
          className="mt-1 size-6 shrink-0 text-primary"
        />
        <div>
          <h2 className="font-display text-lg font-semibold">
            1. Contracts from a spreadsheet
          </h2>
          <p className="text-sm text-muted-foreground">
            An Excel workbook (.xlsx) or CSV, for example an export from Medius,
            one contract per row. Nothing changes until you import.
          </p>
        </div>
      </div>
      <DropZone
        extensions={[".xlsx", ".csv"]}
        what="an Excel workbook (.xlsx) or CSV"
        label="Choose the spreadsheet"
        file={file}
        onFile={choose}
      />
      {error && <Alert tone="danger" title={error} />}

      {columns && (
        <section aria-labelledby="match" className="flex flex-col gap-3">
          <div>
            <h3 id="match" className="font-semibold">
              Match the columns
            </h3>
            <p className="text-sm text-muted-foreground">
              {columns.rows.toLocaleString("en-AU")} rows. Choose where each
              column goes; leave a column as "Don't import" to skip it.
              {columns.remembered
                ? " Columns you've matched before are filled in."
                : ""}
            </p>
          </div>
          <ul className="flex flex-wrap gap-1.5" aria-label="Needed fields">
            {needed.map((t) => {
              const ok =
                used.has(t.key) ||
                (t.key === "owner" && defaultOwnerId) ||
                (t.key === "category" && defaultCategoryId);
              return (
                <li key={t.key}>
                  <Badge
                    tone={ok ? "success" : "warning"}
                    icon={
                      ok ? (
                        <CheckCircle2 aria-hidden className="size-3.5" />
                      ) : (
                        <AlertTriangle aria-hidden className="size-3.5" />
                      )
                    }
                  >
                    {t.label}
                    {ok ? "" : ": not matched"}
                  </Badge>
                </li>
              );
            })}
          </ul>
          {/* One row per column in the file: stacked on a phone, three columns side by side on a wider screen. */}
          <div className="rounded-[var(--radius-card)] border border-border">
            <div
              aria-hidden
              className="hidden grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(14rem,1.2fr)] gap-3 border-b border-border bg-muted/40 px-3 py-2 text-xs font-medium text-muted-foreground sm:grid"
            >
              <span>Column in the file</span>
              <span>For example</span>
              <span>Goes to</span>
            </div>
            <ul className="divide-y divide-border">
              {columns.columns.map((col) => {
                const help = mapping[col.header]
                  ? targets.find((t) => t.key === mapping[col.header])?.help
                  : null;
                return (
                  <li
                    key={col.header}
                    className="grid gap-1 px-3 py-3 text-sm sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(14rem,1.2fr)] sm:gap-3"
                  >
                    <span className="font-medium">{col.header}</span>
                    <span className="text-xs text-muted-foreground">
                      <span className="sm:sr-only">For example: </span>
                      {col.examples.join(" · ") || "(empty)"}
                    </span>
                    <span className="mt-1 sm:mt-0">
                      <Select
                        aria-label={`Where ${col.header} goes`}
                        value={mapping[col.header] ?? ""}
                        onChange={(e) => {
                          const next = { ...mapping };
                          if (e.target.value) next[col.header] = e.target.value;
                          else delete next[col.header];
                          setMapping(next);
                          setPlan(null);
                        }}
                      >
                        <option value="">Don't import</option>
                        {groups.map((g) => (
                          <optgroup key={g} label={g}>
                            {targets
                              .filter((t) => t.group === g)
                              .map((t) => (
                                <option
                                  key={t.key}
                                  value={t.key}
                                  disabled={
                                    used.has(t.key) &&
                                    mapping[col.header] !== t.key
                                  }
                                >
                                  {t.label}
                                </option>
                              ))}
                          </optgroup>
                        ))}
                      </Select>
                      {help && (
                        <span className="mt-1 block text-xs text-muted-foreground">
                          {help}
                        </span>
                      )}
                    </span>
                  </li>
                );
              })}
            </ul>
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field
              label="If a row has no owner, use"
              help="Optional. Otherwise a row without an owner can't be imported."
            >
              {(p) => (
                <Select
                  {...p}
                  value={defaultOwnerId}
                  onChange={(e) => {
                    setDefaultOwnerId(e.target.value);
                    setPlan(null);
                  }}
                >
                  <option value="">Nobody: flag it</option>
                  {(lists.data?.owners ?? []).map((o) => (
                    <option key={o.id} value={o.id}>
                      {o.name}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
            <Field
              label="If a row has no category, use"
              help="Optional. Otherwise a row without a category can't be imported."
            >
              {(p) => (
                <Combobox
                  {...p}
                  placeholder="None: flag it"
                  options={[
                    { value: "", label: "None: flag it" },
                    ...categories.map((c) => ({
                      value: String(c.id),
                      label: c.path,
                    })),
                  ]}
                  value={defaultCategoryId}
                  onChange={(v) => {
                    setDefaultCategoryId(v);
                    setPlan(null);
                  }}
                />
              )}
            </Field>
          </div>
          <Checkbox
            label="Leave out archived contracts"
            description="Rows whose status says Archived (Medius keeps these outside its main list). Untick to bring them in as Ended."
            checked={leaveOutArchived}
            onChange={(v) => {
              setLeaveOutArchived(v);
              setPlan(null);
            }}
          />
          <TranslateValues
            form={form}
            translations={translations}
            setTranslations={(t) => {
              setTranslations(t);
              setPlan(null);
            }}
          />
          <div>
            <Button
              variant={plan ? "secondary" : "primary"}
              loading={check.isPending}
              onClick={() => check.mutate()}
            >
              {plan ? "Check again" : "Check (nothing changes yet)"}
            </Button>
          </div>
        </section>
      )}

      {plan && (
        <section aria-labelledby="dry-run" className="flex flex-col gap-3">
          <h3 id="dry-run" className="font-semibold">
            {done ? "Imported" : "What would happen"}
          </h3>
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            <Stat
              label="As Effective (locked)"
              value={plan.effective}
              tone="success"
            />
            <Stat label="As Ended" value={plan.ended} />
            <Stat label="Already imported" value={plan.already} />
            <Stat label="Can't import" value={plan.refused} tone="danger" />
          </div>
          {plan.leftOut > 0 && (
            <p className="text-sm text-muted-foreground">
              {plan.leftOut} archived{" "}
              {plan.leftOut === 1 ? "contract" : "contracts"} left out.
            </p>
          )}
          {plan.missing.length > 0 && (
            <Alert tone="warning" title="Add these under Admin, Lists first">
              <div className="flex flex-col gap-2">
                <ul className="flex flex-col gap-1">
                  {plan.missing.map((m) => (
                    <li key={m.list}>
                      <strong>
                        {m.list === "Category"
                          ? "Categories and subcategories"
                          : m.list}{" "}
                        ({m.names.length}):
                      </strong>{" "}
                      {m.names.slice(0, 8).join(", ")}
                      {m.names.length > 8
                        ? ` and ${m.names.length - 8} more`
                        : ""}
                    </li>
                  ))}
                </ul>
                <p>
                  Download them as a sheet, check it, then use{" "}
                  <Link
                    to="/admin/lists"
                    className="font-medium text-primary underline"
                  >
                    Lists
                  </Link>
                  , Import from Excel. Then check again here.
                </p>
                <div>
                  <Button
                    icon={<FileDown aria-hidden className="size-4" />}
                    onClick={() => void downloadMissing(form())}
                  >
                    Download the lists to add
                  </Button>
                </div>
              </div>
            </Alert>
          )}
          {plan.newSuppliers.length > 0 && (
            <p className="text-sm">
              <strong>
                {plan.newSuppliers.length} new{" "}
                {plan.newSuppliers.length === 1 ? "supplier" : "suppliers"}
              </strong>{" "}
              will be added: {plan.newSuppliers.slice(0, 12).join(", ")}
              {plan.newSuppliers.length > 12
                ? ` and ${plan.newSuppliers.length - 12} more`
                : ""}
              .
            </p>
          )}
          <div>
            <Segmented
              label="Show"
              value={show}
              onChange={setShow}
              options={[
                {
                  value: "problems",
                  label: "Can't import",
                  count: plan.refused,
                },
                { value: "ready", label: "Ready", count: ready },
                { value: "all", label: "All rows", count: plan.rows.length },
              ]}
            />
          </div>
          {rows.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              {show === "problems"
                ? "Every row can be imported."
                : "Nothing to show."}
            </p>
          ) : (
            <ul className="max-h-[28rem] divide-y divide-border overflow-y-auto rounded-[var(--radius-card)] border border-border">
              {rows.slice(0, 500).map((r) => (
                <li
                  key={r.row}
                  className="flex flex-col gap-1 p-3 text-sm sm:flex-row sm:items-start sm:justify-between"
                >
                  <div className="min-w-0">
                    <p className="font-medium">
                      <span className="tabular text-muted-foreground">
                        Row {r.row}
                      </span>{" "}
                      {r.label}
                      {r.legacyReference && (
                        <span className="text-muted-foreground">
                          {" "}
                          · {r.legacyReference}
                        </span>
                      )}
                    </p>
                    {r.problems.map((x, i) => (
                      <p key={i} className="text-danger">
                        {x}
                      </p>
                    ))}
                    {r.notes.map((x, i) => (
                      <p key={i} className="text-muted-foreground">
                        {x}
                      </p>
                    ))}
                  </div>
                  <span className="shrink-0">
                    <Badge
                      tone={outcomeBadge[r.outcome].tone}
                      icon={outcomeBadge[r.outcome].icon}
                    >
                      {outcomeBadge[r.outcome].label}
                    </Badge>
                  </span>
                </li>
              ))}
            </ul>
          )}
          {done ? (
            <Alert
              tone="success"
              title={`${done.imported} contracts imported${done.first ? `, ${done.first} to ${done.last}` : ""}.`}
            >
              They're on the{" "}
              <Link to="/contracts?view=all" className="font-medium underline">
                Contracts
              </Link>{" "}
              list and the register. Next, add their files from ZIPs below.
            </Alert>
          ) : (
            <div className="flex flex-wrap items-center gap-3">
              <Button
                variant="primary"
                disabled={ready === 0}
                onClick={() => setConfirming(true)}
              >
                {ready === 0
                  ? "Nothing ready to import"
                  : `Import ${ready} ${ready === 1 ? "contract" : "contracts"}`}
              </Button>
              {plan.refused > 0 && ready > 0 && (
                <span className="text-sm text-muted-foreground">
                  The {plan.refused} that can't be imported are left out; fix
                  them in the file and import it again later (imported rows are
                  skipped).
                </span>
              )}
            </div>
          )}
        </section>
      )}
      {confirming && plan && (
        <Dialog
          open
          onOpenChange={(o) => !o && setConfirming(false)}
          title={`Import ${ready} ${ready === 1 ? "contract" : "contracts"}?`}
          description="Current ones become Effective and locked straight away; expired ones Ended. Each is recorded in its Activity and the audit trail."
          footer={
            <>
              <Button onClick={() => setConfirming(false)}>Cancel</Button>
              <Button
                variant="primary"
                loading={run.isPending}
                onClick={() => run.mutate()}
              >
                Import {ready}
              </Button>
            </>
          }
        >
          <ul className="flex flex-col gap-1 text-sm">
            <li>
              {plan.effective} as Effective, {plan.ended} as Ended.
            </li>
            {plan.newSuppliers.length > 0 && (
              <li>
                {plan.newSuppliers.length} new{" "}
                {plan.newSuppliers.length === 1 ? "supplier" : "suppliers"}{" "}
                added.
              </li>
            )}
            {plan.already + plan.refused + plan.leftOut > 0 && (
              <li>
                {plan.already + plan.refused + plan.leftOut}{" "}
                {plan.already + plan.refused + plan.leftOut === 1
                  ? "row"
                  : "rows"}{" "}
                left out.
              </li>
            )}
          </ul>
        </Dialog>
      )}
    </Card>
  );
}

// ------------------------------------------------------------------ Step 2: files

type FileRow = FilesPlan["files"][number] & {
  key: string;
  zip: string;
  at: number;
};
type ZipResult = { zip: File; plan: FilesPlan | null; error: string | null };

/** Runs the work for each item, a few at a time, in order of the list, reporting each one as it finishes. */
async function eachOf<T>(
  items: T[],
  at: number,
  work: (item: T, index: number) => Promise<void>,
) {
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const i = next++;
      await work(items[i], i);
    }
  };
  await Promise.all(Array.from({ length: Math.min(at, items.length) }, worker));
}

function FilesStep() {
  const lists = useQuery({
    queryKey: ["lists"],
    queryFn: () => api.get<Lists>("/api/lists"),
  });
  const kinds = withAddedKinds(allDocumentKinds, lists.data);
  const kindLabel = (k: string) => kinds.find((x) => x.value === k)?.label ?? k;
  // Any number of ZIPs (D-127): Medius gives one per contract. Each is checked and added on its own, one after another,
  // so each stays within the size limit and a problem with one doesn't stop the rest.
  const [results, setResults] = useState<ZipResult[]>([]);
  const [kind, setKind] = useState("Supporting");
  // The admin's own choice of kind for a file (by its ZIP and path), over what was worked out from its name.
  const [chosen, setChosen] = useState<Record<string, string>>({});
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [show, setShow] = useState<"problems" | "matched" | "all">("matched");
  const [progress, setProgress] = useState<{
    verb: string;
    done: number;
    total: number;
  } | null>(null);
  const [done, setDone] = useState<number | null>(null);
  const form = (f: File, kinds: Record<string, string>, defaultKind = kind) => {
    const data = new FormData();
    data.append("file", f);
    data.append("kind", defaultKind);
    data.append("kinds", JSON.stringify(kinds));
    return data;
  };

  const check = async (zips: File[], defaultKind = kind) => {
    const out: ZipResult[] = zips.map((zip) => ({
      zip,
      plan: null,
      error: null,
    }));
    setResults(out);
    setChosen({});
    setDone(null);
    setProgress({ verb: "Checking", done: 0, total: zips.length });
    let count = 0;
    await eachOf(zips, 3, async (zip, i) => {
      try {
        out[i] = {
          zip,
          plan: await postForm<FilesPlan>(
            "/api/import/files/preview",
            form(zip, {}, defaultKind),
          ),
          error: null,
        };
      } catch (e) {
        out[i] = { zip, plan: null, error: errorText(e) };
      }
      setProgress({ verb: "Checking", done: ++count, total: zips.length });
    });
    setProgress(null);
    setResults([...out]);
    const plans = out.flatMap((r) => (r.plan ? [r.plan] : []));
    setShow(
      plans.some((p) => p.unmatched + p.refused > 0) ? "problems" : "matched",
    );
  };

  const rows: FileRow[] = results.flatMap((r, at) =>
    (r.plan?.files ?? []).map((f) => ({
      ...f,
      key: `${at}|${f.path}`,
      zip: r.zip.name,
      at,
    })),
  );
  const kindOf = (f: FileRow) => chosen[f.key] ?? f.kind;
  const matchedFiles = rows.filter((f) => f.outcome === "Match");
  const count = (o: FileOutcome) => rows.filter((f) => f.outcome === o).length;
  const stats = {
    matched: matchedFiles.length,
    already: count("Already"),
    unmatched: count("Unmatched"),
    refused: count("Refused"),
    contracts: new Set(matchedFiles.map((f) => f.reference)).size,
  };
  const failed = results.filter((r) => r.error);
  const checked = results.length > 0 && !progress;

  const run = async () => {
    const todo = results
      .map((r, at) => ({ r, at }))
      .filter(({ r }) => r.plan && r.plan.matched > 0);
    const out = [...results];
    let added = 0;
    let finished = 0;
    setProgress({ verb: "Adding files from", done: 0, total: todo.length });
    // One at a time: each ZIP's files go onto their contracts before the next.
    await eachOf(todo, 1, async ({ r, at }) => {
      const mine = Object.fromEntries(
        Object.entries(chosen)
          .filter(([k]) => k.startsWith(`${at}|`))
          .map(([k, v]) => [k.slice(k.indexOf("|") + 1), v]),
      );
      try {
        const result = await postForm<{ imported: number; summary: FilesPlan }>(
          "/api/import/files/import",
          form(r.zip, mine),
        );
        added += result.imported;
        out[at] = { zip: r.zip, plan: result.summary, error: null };
      } catch (e) {
        out[at] = { zip: r.zip, plan: r.plan, error: errorText(e) };
      }
      setProgress({
        verb: "Adding files from",
        done: ++finished,
        total: todo.length,
      });
    });
    setProgress(null);
    setResults(out);
    setDone(added);
    toast.success(`${added} files added.`);
  };

  const kindCounts = kinds
    .map((k) => ({
      ...k,
      count: matchedFiles.filter((f) => kindOf(f) === k.value).length,
    }))
    .filter((k) => k.count > 0);
  const changeAll = () => {
    if (!from || !to || from === to) return;
    const next = { ...chosen };
    for (const f of matchedFiles) if (kindOf(f) === from) next[f.key] = to;
    setChosen(next);
    setFrom("");
  };
  const files = rows.filter(
    (f) =>
      show === "all" ||
      (show === "matched"
        ? f.outcome === "Match"
        : f.outcome === "Unmatched" || f.outcome === "Refused"),
  );
  const many = results.length > 1;
  return (
    <Card className="flex flex-col gap-4 p-5">
      <div className="flex items-start gap-3">
        <FileArchive
          aria-hidden
          className="mt-1 size-6 shrink-0 text-primary"
        />
        <div>
          <h2 className="font-display text-lg font-semibold">
            2. Their files from ZIPs
          </h2>
          <p className="text-sm text-muted-foreground">
            Choose as many ZIPs as you like, or drag the whole folder of them
            here: they're checked and added one after another. Each file is
            matched to a contract by the old reference (or JBSAUS reference) in
            its name or folder, for example MED-1042 Signed.pdf or
            MED-1042/agreement.pdf, or else in the ZIP's own name (Medius's one
            ZIP per contract, for example JBSAUS0000300_Supply_agreement.zip). A
            ZIP can mix signed copies, drafts, variations, cover pages and other
            files: what each one is comes from words in its folder or name, and
            you can change any of them before adding. Up to 25 MB per ZIP. A
            file already on its contract is skipped, so ZIPs can be added twice
            safely.
          </p>
        </div>
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field
          label="When a file's name doesn't say what it is"
          help="Words like signed, executed, draft, variation, amendment, cover page or sign off in a file's folder or name decide its kind. Other files get this one."
        >
          {(p) => (
            <Select
              {...p}
              value={kind}
              disabled={!!progress}
              onChange={(e) => {
                setKind(e.target.value);
                if (results.length > 0)
                  void check(
                    results.map((r) => r.zip),
                    e.target.value,
                  );
              }}
            >
              {kinds.map((k) => (
                <option key={k.value} value={k.value}>
                  {k.label}
                </option>
              ))}
            </Select>
          )}
        </Field>
      </div>
      <DropZone
        extensions={[".zip"]}
        what="ZIPs"
        label="Choose ZIPs"
        multiple
        files={results.map((r) => r.zip)}
        busy={!!progress}
        onFiles={(list) => {
          if (!progress) void check(list);
        }}
      />
      {results.length > 0 && !progress && (
        <p className="text-sm text-muted-foreground" aria-live="polite">
          {results.length === 1
            ? `${results[0].zip.name} (${formatBytes(results[0].zip.size)})`
            : `${results.length} ZIPs (${formatBytes(results.reduce((s, r) => s + r.zip.size, 0))})`}
        </p>
      )}
      {progress && (
        <div className="flex flex-col gap-1" aria-live="polite">
          <p className="text-sm">
            {progress.verb}{" "}
            {progress.total === 1
              ? "the ZIP"
              : `ZIP ${Math.min(progress.done + 1, progress.total)} of ${progress.total}`}
            …
          </p>
          <progress
            value={progress.done}
            max={Math.max(progress.total, 1)}
            aria-label={`${progress.verb} ZIPs`}
            className="h-2 w-full accent-primary"
          />
        </div>
      )}
      {checked && failed.length > 0 && (
        <Alert
          tone="danger"
          title={`${failed.length} ${failed.length === 1 ? "ZIP" : "ZIPs"} couldn't be ${done !== null ? "added" : "checked"}`}
        >
          <ul className="list-disc pl-5">
            {failed.slice(0, 20).map((r) => (
              <li key={r.zip.name}>
                {r.zip.name}: {r.error}
              </li>
            ))}
          </ul>
          {failed.length > 20 && <p>And {failed.length - 20} more.</p>}
          <p className="mt-1">
            The others aren't affected. Fix these and choose them again.
          </p>
        </Alert>
      )}
      {checked && results.some((r) => r.plan) && (
        <section
          aria-labelledby="files-dry-run"
          className="flex flex-col gap-3"
        >
          <h3 id="files-dry-run" className="font-semibold">
            {done !== null ? "Added" : "What would happen"}
          </h3>
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            <Stat
              label={`Matched (${stats.contracts} contracts)`}
              value={stats.matched}
              tone="success"
            />
            <Stat label="Already there" value={stats.already} />
            <Stat label="Not matched" value={stats.unmatched} tone="danger" />
            <Stat label="Can't add" value={stats.refused} tone="danger" />
          </div>
          <div>
            <Segmented
              label="Show"
              value={show}
              onChange={setShow}
              options={[
                { value: "matched", label: "Matched", count: stats.matched },
                {
                  value: "problems",
                  label: "Not matched or not allowed",
                  count: stats.unmatched + stats.refused,
                },
                { value: "all", label: "All files", count: rows.length },
              ]}
            />
          </div>
          {done === null && kindCounts.length > 0 && (
            <div className="flex flex-col gap-2 rounded-[var(--radius-card)] bg-muted p-3 text-sm">
              <p>
                <span className="font-medium">
                  What the matched files are:{" "}
                </span>
                {kindCounts.map((k) => `${k.label} (${k.count})`).join(", ")}.
              </p>
              <div className="flex flex-wrap items-end gap-2">
                <label className="flex flex-col gap-1">
                  <span>Change every</span>
                  <Select
                    value={from}
                    onChange={(e) => setFrom(e.target.value)}
                  >
                    <option value="">Choose a kind</option>
                    {kindCounts.map((k) => (
                      <option key={k.value} value={k.value}>
                        {k.label} ({k.count})
                      </option>
                    ))}
                  </Select>
                </label>
                <label className="flex flex-col gap-1">
                  <span>to</span>
                  <Select value={to} onChange={(e) => setTo(e.target.value)}>
                    <option value="">Choose a kind</option>
                    {kinds.map((k) => (
                      <option key={k.value} value={k.value}>
                        {k.label}
                      </option>
                    ))}
                  </Select>
                </label>
                <Button
                  disabled={!from || !to || from === to}
                  onClick={changeAll}
                >
                  Change
                </Button>
              </div>
            </div>
          )}
          {files.length === 0 ? (
            <p className="text-sm text-muted-foreground">Nothing to show.</p>
          ) : (
            <ul className="max-h-[24rem] divide-y divide-border overflow-y-auto rounded-[var(--radius-card)] border border-border">
              {files.slice(0, 500).map((f) => (
                <li
                  key={f.key}
                  className="flex flex-col gap-1 p-3 text-sm sm:flex-row sm:items-start sm:justify-between"
                >
                  <div className="min-w-0">
                    <p className="font-medium break-all">{f.path}</p>
                    <p className="text-muted-foreground">
                      {many && <span className="break-all">In {f.zip} · </span>}
                      {formatBytes(f.size)}
                      {f.reference && ` · to ${f.reference} ${f.title ?? ""}`}
                      {f.reason && ` · ${f.reason}`}
                    </p>
                  </div>
                  <div className="flex shrink-0 flex-col gap-1 sm:items-end">
                    <Badge tone={fileBadge[f.outcome].tone}>
                      {fileBadge[f.outcome].label}
                    </Badge>
                    {f.outcome === "Match" && done === null && (
                      <>
                        <Select
                          aria-label={`What ${f.path.split("/").pop()} is`}
                          value={kindOf(f)}
                          onChange={(e) =>
                            setChosen({ ...chosen, [f.key]: e.target.value })
                          }
                          className="min-h-9 sm:w-72"
                        >
                          {kinds.map((k) => (
                            <option key={k.value} value={k.value}>
                              {k.label}
                            </option>
                          ))}
                        </Select>
                        <span className="text-xs text-muted-foreground">
                          {chosen[f.key]
                            ? "You chose"
                            : `From ${f.kindWhy ?? "your default"}`}
                        </span>
                      </>
                    )}
                    {f.outcome === "Match" && done !== null && (
                      <span className="text-xs text-muted-foreground">
                        {kindLabel(kindOf(f))}
                      </span>
                    )}
                  </div>
                </li>
              ))}
              {files.length > 500 && (
                <li className="p-3 text-sm text-muted-foreground">
                  Showing the first 500 of {files.length}. All of them are
                  added.
                </li>
              )}
            </ul>
          )}
          {done !== null ? (
            <Alert
              tone="success"
              title={`${done} files added to their contracts.`}
            >
              Choose more ZIPs to carry on.
            </Alert>
          ) : (
            <div>
              <Button
                variant="primary"
                disabled={stats.matched === 0}
                loading={!!progress}
                onClick={() => void run()}
              >
                {stats.matched === 0
                  ? "No files to add"
                  : `Add ${stats.matched} ${stats.matched === 1 ? "file" : "files"}`}
              </Button>
            </div>
          )}
        </section>
      )}
    </Card>
  );
}

export function ImportPage() {
  const { person } = useMe();
  if (!person.roles.admin)
    return (
      <>
        <PageHeader title="Import old contracts" />
        <Alert tone="info" title="Only admins can import old contracts." />
      </>
    );
  return (
    <>
      <PageHeader
        title="Import old contracts"
        description="Bring contracts across from the old system, then their files. Try it on QA first."
        help={help.import}
      />
      <ContractsStep />
      <FilesStep />
    </>
  );
}
