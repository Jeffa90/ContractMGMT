// The supplier page (D-88, D-90): details, contacts, documents (its own and from its contracts), business reviews and contracts in one place.
// Everyone reads it. Procurement and admins change details, contacts and documents; reviews also by the relationship owner.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  BellRing,
  Building2,
  CalendarClock,
  CheckCircle2,
  ChevronLeft,
  Download,
  FilePlus2,
  FileText,
  Lock,
  Mail,
  Pencil,
  Phone,
  Plus,
  ShieldAlert,
  Trash2,
  Upload,
  UserRound,
  Users,
} from "lucide-react";
import { useRef, useState, type ReactNode } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { toast } from "sonner";
import { Dialog } from "@/components/Dialog";
import { PageHeader } from "@/components/Help";
import { Select, Textarea } from "@/components/form";
import {
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  EmptyState,
  Field,
  Segmented,
  Skeleton,
  TextInput,
  type Tone,
} from "@/components/ui";
import { errorText } from "@/lib/actions";
import { api, ApiError } from "@/lib/api";
import { cn } from "@/lib/cn";
import type { Lists, Supplier } from "@/lib/contracts";
import {
  daysUntil,
  formatBytes,
  formatDate,
  formatDateTime,
  formatMoney,
} from "@/lib/format";
import { useMe } from "@/lib/me";
import {
  scoreNames,
  uploadSupplierDocument,
  type BusinessReview,
  type Rating,
  type Risk,
  type SupplierDocument,
  type SupplierPageData,
} from "@/lib/suppliers";
import { help } from "@/help";
import { SupplierDialog } from "./ContractForm";

const tabs = ["overview", "documents", "reviews", "contracts"] as const;
type Tab = (typeof tabs)[number];
const riskTone: Record<Risk, Tone> = {
  Low: "success",
  Medium: "warning",
  High: "danger",
};
const ratingTone: Record<Rating, Tone> = {
  Good: "success",
  NeedsImprovement: "warning",
  Poor: "danger",
};
const today = () =>
  new Intl.DateTimeFormat("en-CA", { timeZone: "Australia/Brisbane" }).format(
    new Date(),
  );
const formatAbn = (abn: string) =>
  `${abn.slice(0, 2)} ${abn.slice(2, 5)} ${abn.slice(5, 8)} ${abn.slice(8)}`;

function usePeople() {
  return useQuery({
    queryKey: ["people-picker"],
    queryFn: () =>
      api.get<{ id: number; name: string }[]>("/api/people/picker"),
  });
}
function useListItems(listName: string) {
  const lists = useQuery({
    queryKey: ["lists"],
    queryFn: () => api.get<Lists>("/api/lists"),
  });
  return (lists.data?.items ?? []).filter(
    (i) => i.listName === listName && i.isActive,
  );
}

/** A simple "save, toast, refresh" helper for this page's actions. */
function useSave(supplierId: number) {
  const queryClient = useQueryClient();
  return () =>
    queryClient.invalidateQueries({ queryKey: ["suppliers", supplierId] });
}

function Expiry({ on }: { on: string | null }) {
  if (!on) return <span className="text-muted-foreground">Doesn't expire</span>;
  const days = daysUntil(on);
  if (days < 0)
    return (
      <Badge
        tone="danger"
        icon={<AlertTriangle aria-hidden className="size-3.5" />}
      >
        Expired {formatDate(on)}
      </Badge>
    );
  if (days <= 60)
    return (
      <Badge
        tone="warning"
        icon={<CalendarClock aria-hidden className="size-3.5" />}
      >
        Expires {formatDate(on)} (
        {days === 0 ? "today" : days === 1 ? "tomorrow" : `in ${days} days`})
      </Badge>
    );
  return <span>Expires {formatDate(on)}</span>;
}

function DetailRow({
  label,
  children,
}: {
  label: string;
  children: ReactNode;
}) {
  return (
    <div className="flex flex-col gap-0.5 py-3 sm:flex-row sm:gap-4">
      <dt className="text-sm text-muted-foreground sm:w-48 sm:shrink-0">
        {label}
      </dt>
      <dd className="min-w-0 text-sm">{children}</dd>
    </div>
  );
}

// ------------------------------------------------------------------ Overview: relationship and contacts

function RelationshipDialog({
  data,
  onClose,
}: {
  data: SupplierPageData;
  onClose: () => void;
}) {
  const s = data.supplier;
  const people = usePeople();
  const tiers = useListItems("SupplierTier");
  const refresh = useSave(s.id);
  const [form, setForm] = useState({
    relationshipOwnerId: s.relationshipOwnerId
      ? String(s.relationshipOwnerId)
      : "",
    tierId: s.tierId ? String(s.tierId) : "",
    riskRating: (s.riskRating ?? "None") as Risk | "None",
    riskReason: s.riskReason ?? "",
    nextReviewDue: s.nextReviewDue ?? "",
  });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const save = useMutation({
    mutationFn: () =>
      api.put(`/api/suppliers/${s.id}/relationship`, {
        rowVersion: s.rowVersion,
        relationshipOwnerId: form.relationshipOwnerId
          ? Number(form.relationshipOwnerId)
          : null,
        tierId: form.tierId ? Number(form.tierId) : null,
        riskRating: form.riskRating === "None" ? null : form.riskRating,
        riskReason: form.riskReason,
        nextReviewDue: form.nextReviewDue || null,
      }),
    onSuccess: () => {
      void refresh();
      toast.success("Details saved.");
      onClose();
    },
    onError: (e) =>
      setErrors(
        e instanceof ApiError
          ? { ...e.fields, _: Object.keys(e.fields).length ? "" : e.message }
          : { _: errorText(e) },
      ),
  });
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Supplier details"
      description="Who looks after this supplier, how important it is, its risk and when the next business review is due."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={save.isPending}
            onClick={() => save.mutate()}
          >
            Save details
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {errors._ && <Alert tone="danger" title={errors._} />}
        <Field
          label="Relationship owner"
          help="The JBS person who looks after this supplier. They get the business review reminders."
          error={errors.relationshipOwnerId}
        >
          {(p) => (
            <Select
              {...p}
              value={form.relationshipOwnerId}
              onChange={(e) =>
                setForm({ ...form, relationshipOwnerId: e.target.value })
              }
            >
              <option value="">Nobody yet</option>
              {(people.data ?? []).map((x) => (
                <option key={x.id} value={x.id}>
                  {x.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field
          label="Tier"
          help="Admins change the tiers on the Lists page."
          error={errors.tierId}
        >
          {(p) => (
            <Select
              {...p}
              value={form.tierId}
              onChange={(e) => setForm({ ...form, tierId: e.target.value })}
            >
              <option value="">Not set</option>
              {tiers.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.value}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <div className="flex flex-col gap-1.5">
          <span className="text-sm font-medium">Risk</span>
          <div>
            <Segmented
              label="Risk"
              value={form.riskRating}
              onChange={(v) => setForm({ ...form, riskRating: v })}
              options={[
                { value: "None", label: "Not rated" },
                { value: "Low", label: "Low" },
                { value: "Medium", label: "Medium" },
                { value: "High", label: "High" },
              ]}
            />
          </div>
          {errors.riskRating && (
            <p className="text-xs font-medium text-danger">
              {errors.riskRating}
            </p>
          )}
        </div>
        {form.riskRating !== "None" && (
          <Field
            label="Why"
            required={form.riskRating !== "Low"}
            error={errors.riskReason}
          >
            {(p) => (
              <Textarea
                {...p}
                rows={2}
                maxLength={500}
                value={form.riskReason}
                placeholder="For example: single source for cartons; insurance lapsed last year."
                onChange={(e) =>
                  setForm({ ...form, riskReason: e.target.value })
                }
              />
            )}
          </Field>
        )}
        <Field
          label="Next business review due"
          help="Leave it empty if there's no review planned."
          error={errors.nextReviewDue}
        >
          {(p) => (
            <TextInput
              {...p}
              type="date"
              value={form.nextReviewDue}
              onChange={(e) =>
                setForm({ ...form, nextReviewDue: e.target.value })
              }
            />
          )}
        </Field>
      </div>
    </Dialog>
  );
}

type Contact = SupplierPageData["contacts"][number];

function ContactDialog({
  supplierId,
  contact,
  onClose,
}: {
  supplierId: number;
  contact: Contact | null;
  onClose: () => void;
}) {
  const refresh = useSave(supplierId);
  const [form, setForm] = useState({
    name: contact?.name ?? "",
    role: contact?.role ?? "",
    email: contact?.email ?? "",
    phone: contact?.phone ?? "",
  });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const save = useMutation({
    mutationFn: () =>
      contact
        ? api.put(`/api/suppliers/${supplierId}/contacts/${contact.id}`, form)
        : api.post(`/api/suppliers/${supplierId}/contacts`, form),
    onSuccess: () => {
      void refresh();
      toast.success(contact ? "Contact saved." : "Contact added.");
      onClose();
    },
    onError: (e) =>
      setErrors(e instanceof ApiError ? e.fields : { name: errorText(e) }),
  });
  const field = (
    key: keyof typeof form,
    label: string,
    type = "text",
    required = false,
  ) => (
    <Field label={label} required={required} error={errors[key]}>
      {(p) => (
        <TextInput
          {...p}
          type={type}
          value={form[key]}
          onChange={(e) => setForm({ ...form, [key]: e.target.value })}
        />
      )}
    </Field>
  );
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={contact ? "Change contact" : "Add a contact"}
      description="Someone at the supplier."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={save.isPending}
            onClick={() => save.mutate()}
          >
            {contact ? "Save contact" : "Add contact"}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {field("name", "Name", "text", true)}
        {field("role", "Role")}
        {field("email", "Email", "email")}
        {field("phone", "Phone", "tel")}
      </div>
    </Dialog>
  );
}

function Overview({ data }: { data: SupplierPageData }) {
  const s = data.supplier;
  const refresh = useSave(s.id);
  const [editing, setEditing] = useState(false);
  const [contact, setContact] = useState<Contact | "new" | null>(null);
  const remove = useMutation({
    mutationFn: (c: Contact) =>
      api.post(`/api/suppliers/${s.id}/contacts/${c.id}/remove`, {}),
    onSuccess: () => {
      void refresh();
      toast.success("Contact removed.");
    },
    onError: (e) => toast.error(errorText(e)),
  });
  const reviewDays = s.nextReviewDue ? daysUntil(s.nextReviewDue) : null;
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <Card className="p-5">
        <div className="flex items-start justify-between gap-3">
          <h2 className="font-display text-lg font-semibold">Relationship</h2>
          {data.can.manage && (
            <Button
              variant="ghost"
              icon={<Pencil aria-hidden className="size-4" />}
              onClick={() => setEditing(true)}
            >
              Edit
            </Button>
          )}
        </div>
        <dl className="divide-y divide-border">
          <DetailRow label="Relationship owner">
            {s.relationshipOwnerName ?? (
              <span className="text-muted-foreground">Nobody yet</span>
            )}
          </DetailRow>
          <DetailRow label="Tier">
            {s.tierName ?? (
              <span className="text-muted-foreground">Not set</span>
            )}
          </DetailRow>
          <DetailRow label="Risk">
            {s.riskRating ? (
              <span className="flex flex-col gap-1">
                <span>
                  <Badge
                    tone={riskTone[s.riskRating]}
                    icon={<ShieldAlert aria-hidden className="size-3.5" />}
                  >
                    {s.riskRating}
                  </Badge>
                </span>
                {s.riskReason && (
                  <span className="text-muted-foreground">{s.riskReason}</span>
                )}
              </span>
            ) : (
              <span className="text-muted-foreground">Not rated</span>
            )}
          </DetailRow>
          <DetailRow label="Next business review">
            {s.nextReviewDue ? (
              <span
                className={cn(
                  reviewDays !== null &&
                    reviewDays < 0 &&
                    "font-medium text-danger",
                  reviewDays !== null &&
                    reviewDays >= 0 &&
                    reviewDays <= 30 &&
                    "font-medium text-warning",
                )}
              >
                {formatDate(s.nextReviewDue)}
                {reviewDays !== null &&
                  (reviewDays < 0
                    ? `, ${-reviewDays} days overdue`
                    : reviewDays <= 30
                      ? `, in ${reviewDays} days`
                      : "")}
              </span>
            ) : (
              <span className="text-muted-foreground">Not planned</span>
            )}
          </DetailRow>
          {s.notes && <DetailRow label="Notes">{s.notes}</DetailRow>}
        </dl>
      </Card>
      <Card className="p-5">
        <div className="flex items-start justify-between gap-3">
          <h2 className="font-display text-lg font-semibold">Contacts</h2>
          {data.can.manage && (
            <Button
              variant="ghost"
              icon={<Plus aria-hidden className="size-4" />}
              onClick={() => setContact("new")}
            >
              Add contact
            </Button>
          )}
        </div>
        {data.contacts.length === 0 ? (
          <p className="mt-2 text-sm text-muted-foreground">No contacts yet.</p>
        ) : (
          <ul className="mt-1 divide-y divide-border">
            {data.contacts.map((c) => (
              <li
                key={c.id}
                className="flex items-start justify-between gap-3 py-3"
              >
                <div className="min-w-0 text-sm">
                  <p className="font-medium">{c.name}</p>
                  {c.role && <p className="text-muted-foreground">{c.role}</p>}
                  <p className="mt-1 flex flex-wrap gap-x-4 gap-y-1">
                    {c.email && (
                      <a
                        href={`mailto:${c.email}`}
                        className="inline-flex items-center gap-1 text-primary hover:underline"
                      >
                        <Mail aria-hidden className="size-3.5" /> {c.email}
                      </a>
                    )}
                    {c.phone && (
                      <a
                        href={`tel:${c.phone.replace(/\s/g, "")}`}
                        className="inline-flex items-center gap-1 text-primary hover:underline"
                      >
                        <Phone aria-hidden className="size-3.5" /> {c.phone}
                      </a>
                    )}
                  </p>
                </div>
                {data.can.manage && (
                  <div className="flex shrink-0">
                    <Button
                      variant="ghost"
                      aria-label={`Change ${c.name}`}
                      icon={<Pencil aria-hidden className="size-4" />}
                      onClick={() => setContact(c)}
                    />
                    <Button
                      variant="ghost"
                      aria-label={`Remove ${c.name}`}
                      icon={<Trash2 aria-hidden className="size-4" />}
                      onClick={() => remove.mutate(c)}
                    />
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </Card>
      {editing && (
        <RelationshipDialog data={data} onClose={() => setEditing(false)} />
      )}
      {contact && (
        <ContactDialog
          supplierId={s.id}
          contact={contact === "new" ? null : contact}
          onClose={() => setContact(null)}
        />
      )}
    </div>
  );
}

// ------------------------------------------------------------------ Documents

function DocumentDialog({
  supplierId,
  document,
  mode,
  onClose,
}: {
  supplierId: number;
  document: SupplierDocument | null;
  mode: "add" | "version" | "edit";
  onClose: () => void;
}) {
  const refresh = useSave(supplierId);
  const types = useListItems("SupplierDocumentType");
  const fileInput = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [typeId, setTypeId] = useState(document ? String(document.typeId) : "");
  const [title, setTitle] = useState(document?.title ?? "");
  const [expiresOn, setExpiresOn] = useState(
    mode === "edit" ? (document?.expiresOn ?? "") : "",
  );
  const [errors, setErrors] = useState<Record<string, string>>({});
  const save = useMutation({
    mutationFn: async () => {
      if (mode === "edit")
        return api.put(
          `/api/suppliers/${supplierId}/documents/${document!.id}`,
          {
            typeId: Number(typeId),
            title,
            expiresOn: expiresOn || null,
            clearExpiry: !expiresOn,
          },
        );
      if (!file)
        throw new ApiError(400, {
          code: "invalid",
          message: "Choose a file.",
          fields: { file: "Choose a file to upload." },
        } as never);
      return uploadSupplierDocument(supplierId, file, {
        typeId: typeId ? Number(typeId) : undefined,
        documentId: document?.id,
        expiresOn: expiresOn || undefined,
      });
    },
    onSuccess: () => {
      void refresh();
      toast.success(
        mode === "add"
          ? "Document added."
          : mode === "version"
            ? "New version added."
            : "Document saved.",
      );
      onClose();
    },
    onError: (e) =>
      setErrors(
        e instanceof ApiError
          ? Object.keys(e.fields ?? {}).length
            ? e.fields
            : { file: e.message }
          : { file: errorText(e) },
      ),
  });
  const titles = {
    add: "Add a supplier document",
    version: `New version of ${document?.title}`,
    edit: `Change ${document?.title}`,
  };
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={titles[mode]}
      description={
        mode === "version"
          ? "For example a renewed certificate. Earlier versions are kept."
          : undefined
      }
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={save.isPending}
            onClick={() => save.mutate()}
          >
            {mode === "edit" ? "Save" : "Upload"}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {mode !== "version" && (
          <Field
            label="Kind"
            required
            error={errors.typeId}
            help="Admins add kinds on the Lists page."
          >
            {(p) => (
              <Select
                {...p}
                value={typeId}
                onChange={(e) => setTypeId(e.target.value)}
              >
                <option value="">Choose…</option>
                {types.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.value}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        )}
        {mode === "edit" && (
          <Field label="Name" required error={errors.title}>
            {(p) => (
              <TextInput
                {...p}
                value={title}
                maxLength={200}
                onChange={(e) => setTitle(e.target.value)}
              />
            )}
          </Field>
        )}
        {mode !== "edit" && (
          <Field
            label="File"
            required
            error={errors.file}
            help="PDF, Word, Excel, Outlook email, text or an image, up to 25 MB."
          >
            {(p) => (
              <div className="flex flex-wrap items-center gap-3">
                <input
                  ref={fileInput}
                  id={p.id}
                  type="file"
                  hidden
                  accept=".pdf,.doc,.docx,.xls,.xlsx,.msg,.eml,.txt,.csv,.png,.jpg,.jpeg"
                  onChange={(e) => setFile(e.target.files?.[0] ?? null)}
                />
                {/* The button keeps its own name ("Choose file"); the label points at the file input. */}
                <Button
                  aria-describedby={p["aria-describedby"]}
                  aria-invalid={p["aria-invalid"]}
                  icon={<Upload aria-hidden className="size-4" />}
                  onClick={() => fileInput.current?.click()}
                >
                  Choose file
                </Button>
                <span className="text-sm text-muted-foreground">
                  {file
                    ? `${file.name} (${formatBytes(file.size)})`
                    : "No file chosen"}
                </span>
              </div>
            )}
          </Field>
        )}
        <Field
          label={mode === "version" ? "New expiry date" : "Expires"}
          error={errors.expiresOn}
          help={
            mode === "version"
              ? "Leave it empty to keep the current date."
              : "Optional. For example an insurance certificate's expiry."
          }
        >
          {(p) => (
            <TextInput
              {...p}
              type="date"
              value={expiresOn}
              onChange={(e) => setExpiresOn(e.target.value)}
            />
          )}
        </Field>
      </div>
    </Dialog>
  );
}

function DocumentItem({
  supplierId,
  d,
  canChange,
}: {
  supplierId: number;
  d: SupplierDocument;
  canChange: boolean;
}) {
  const refresh = useSave(supplierId);
  const [dialog, setDialog] = useState<"version" | "edit" | null>(null);
  const latest = d.versions[0];
  const remind = useMutation({
    mutationFn: (on: boolean) =>
      api.post(`/api/suppliers/${supplierId}/documents/${d.id}/remind`, { on }),
    // Stays "pending" until the page has refreshed, so the tick shows the new state straight away and never flickers back.
    onSuccess: async (_, on) => {
      toast.success(
        on
          ? "You'll be reminded before it expires."
          : "Expiry reminders stopped.",
      );
      await refresh();
    },
    onError: (e) => toast.error(errorText(e)),
  });
  const remove = useMutation({
    mutationFn: () =>
      api.post(`/api/suppliers/${supplierId}/documents/${d.id}/remove`, {}),
    onSuccess: () => {
      void refresh();
      toast.success("Document removed. Its history is kept.");
    },
    onError: (e) => toast.error(errorText(e)),
  });
  return (
    <li className="flex flex-col gap-3 p-4 md:flex-row md:items-start md:justify-between">
      <div className="min-w-0">
        <a
          href={`/api/supplier-documents/${latest.id}/file`}
          className="inline-flex items-center gap-2 font-medium hover:underline"
        >
          <FileText
            aria-hidden
            className="size-4 shrink-0 text-muted-foreground"
          />
          {d.title}
        </a>
        <p className="mt-0.5 text-xs text-muted-foreground">
          {d.typeName} · version {latest.number} ·{" "}
          {formatBytes(latest.sizeBytes)} · {latest.uploadedByName},{" "}
          {formatDateTime(latest.uploadedUtc)}
        </p>
        <div className="mt-2 text-sm">
          <Expiry on={d.expiresOn} />
        </div>
        {d.expiresOn && (
          <div className="mt-1">
            <Checkbox
              label="Remind me before it expires"
              description={
                d.watchers > (d.watching ? 1 : 0)
                  ? `${d.watchers - (d.watching ? 1 : 0)} other ${d.watchers - (d.watching ? 1 : 0) === 1 ? "person is" : "people are"} reminded too.`
                  : undefined
              }
              checked={remind.isPending ? remind.variables : d.watching}
              onChange={(on) => remind.mutate(on)}
            />
          </div>
        )}
        {d.versions.length > 1 && (
          <details className="mt-1 text-xs text-muted-foreground">
            <summary className="cursor-pointer">
              Earlier versions ({d.versions.length - 1})
            </summary>
            <ul className="mt-1 flex flex-col gap-1 pl-4">
              {d.versions.slice(1).map((v) => (
                <li key={v.id}>
                  <a
                    href={`/api/supplier-documents/${v.id}/file`}
                    className="hover:underline"
                  >
                    Version {v.number}: {v.fileName}
                  </a>{" "}
                  · {v.uploadedByName}, {formatDateTime(v.uploadedUtc)}
                </li>
              ))}
            </ul>
          </details>
        )}
      </div>
      <div className="flex shrink-0 flex-wrap gap-1">
        <a
          href={`/api/supplier-documents/${latest.id}/file`}
          className="inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] px-3 text-sm font-medium hover:bg-muted"
          aria-label={`Download ${d.title}`}
        >
          <Download aria-hidden className="size-4" /> Download
        </a>
        {canChange && (
          <>
            <Button
              variant="ghost"
              icon={<Upload aria-hidden className="size-4" />}
              onClick={() => setDialog("version")}
              aria-label={`New version of ${d.title}`}
            >
              New version
            </Button>
            <Button
              variant="ghost"
              icon={<Pencil aria-hidden className="size-4" />}
              onClick={() => setDialog("edit")}
              aria-label={`Change ${d.title}`}
            />
            <Button
              variant="ghost"
              icon={<Trash2 aria-hidden className="size-4" />}
              onClick={() => remove.mutate()}
              aria-label={`Remove ${d.title}`}
            />
          </>
        )}
      </div>
      {dialog && (
        <DocumentDialog
          supplierId={supplierId}
          document={d}
          mode={dialog}
          onClose={() => setDialog(null)}
        />
      )}
    </li>
  );
}

function Documents({ data }: { data: SupplierPageData }) {
  const [adding, setAdding] = useState(false);
  const s = data.supplier;
  return (
    <div className="flex flex-col gap-4">
      <Card>
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border p-4">
          <div>
            <h2 className="font-display text-lg font-semibold">
              Supplier documents
            </h2>
            <p className="text-sm text-muted-foreground">
              Insurance, certificates, NDAs and the like, kept with the supplier
              rather than a contract.
            </p>
          </div>
          {data.can.manage && (
            <Button
              variant="primary"
              icon={<FilePlus2 aria-hidden className="size-4" />}
              onClick={() => setAdding(true)}
            >
              Add document
            </Button>
          )}
        </div>
        {data.documents.length === 0 ? (
          <EmptyState
            icon={<FileText />}
            title={
              data.can.manage
                ? "No supplier documents yet. Add the first one."
                : "No supplier documents yet."
            }
          />
        ) : (
          <ul className="divide-y divide-border">
            {data.documents.map((d) => (
              <DocumentItem
                key={d.id}
                supplierId={s.id}
                d={d}
                canChange={data.can.manage}
              />
            ))}
          </ul>
        )}
      </Card>
      <Card>
        <div className="border-b border-border p-4">
          <h2 className="font-display text-lg font-semibold">
            From its contracts
          </h2>
          <p className="text-sm text-muted-foreground">
            The latest version of every document on this supplier's contracts
            that you can see.
          </p>
        </div>
        {data.contractDocuments.length === 0 ? (
          <EmptyState icon={<FileText />} title="No contract documents yet." />
        ) : (
          <ul className="divide-y divide-border">
            {data.contractDocuments.map((d) => (
              <li
                key={d.versionId}
                className="flex flex-wrap items-center justify-between gap-3 p-4"
              >
                <div className="min-w-0">
                  <a
                    href={`/api/documents/${d.versionId}/file`}
                    className="font-medium hover:underline"
                  >
                    {d.title}
                  </a>
                  <p className="text-xs text-muted-foreground">
                    {d.kind} · version {d.versionNumber} · {d.uploadedByName},{" "}
                    {formatDateTime(d.uploadedUtc)}
                  </p>
                </div>
                <Link
                  to={`/contracts/${d.contractId}`}
                  className="text-sm text-primary hover:underline"
                >
                  {d.reference} {d.contractTitle}
                </Link>
              </li>
            ))}
          </ul>
        )}
      </Card>
      {adding && (
        <DocumentDialog
          supplierId={s.id}
          document={null}
          mode="add"
          onClose={() => setAdding(false)}
        />
      )}
    </div>
  );
}

// ------------------------------------------------------------------ Business reviews

type ActionDraft = { text: string; ownerId: string; dueOn: string };

function ScorePicker({
  label,
  value,
  onChange,
}: {
  label: string;
  value: number | null;
  onChange: (v: number | null) => void;
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <span className="text-sm font-medium">{label}</span>
      <div>
        <Segmented
          label={label}
          value={value === null ? "none" : String(value)}
          onChange={(v) => onChange(v === "none" ? null : Number(v))}
          options={[
            { value: "none", label: "Not scored" },
            ...[1, 2, 3, 4, 5].map((n) => ({
              value: String(n),
              label: String(n),
            })),
          ]}
        />
      </div>
    </div>
  );
}

function ReviewDialog({
  data,
  review,
  onClose,
}: {
  data: SupplierPageData;
  review: BusinessReview | null;
  onClose: () => void;
}) {
  const s = data.supplier;
  const people = usePeople();
  const refresh = useSave(s.id);
  const [form, setForm] = useState({
    heldOn: review?.heldOn ?? today(),
    summary: review?.summary ?? "",
    supplierAttendees: review?.supplierAttendees ?? "",
    attendeeIds: review?.attendees.map((a) => a.id) ?? [],
    rating: (review?.rating ?? "none") as Rating | "none",
    qualityScore: review?.qualityScore ?? null,
    deliveryScore: review?.deliveryScore ?? null,
    serviceScore: review?.serviceScore ?? null,
    costScore: review?.costScore ?? null,
    nextReviewDue: "",
  });
  const [actions, setActions] = useState<ActionDraft[]>([]);
  const [pick, setPick] = useState("");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const save = useMutation({
    mutationFn: () => {
      const body = {
        ...form,
        rowVersion: review?.rowVersion,
        rating: form.rating === "none" ? null : form.rating,
        nextReviewDue: form.nextReviewDue || null,
        actions: actions.map((a) => ({
          text: a.text,
          ownerId: a.ownerId ? Number(a.ownerId) : null,
          dueOn: a.dueOn || null,
        })),
      };
      return review
        ? api.put(`/api/suppliers/${s.id}/reviews/${review.id}`, body)
        : api.post(`/api/suppliers/${s.id}/reviews`, body);
    },
    onSuccess: () => {
      void refresh();
      toast.success(
        review
          ? "Review saved."
          : "Business review added. Anyone given an action has been emailed.",
      );
      onClose();
    },
    onError: (e) =>
      setErrors(
        e instanceof ApiError
          ? { ...e.fields, _: e.message }
          : { _: errorText(e) },
      ),
  });
  const names = new Map((people.data ?? []).map((p) => [p.id, p.name]));
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={review ? "Change business review" : "Add a business review"}
      description={`With ${s.name}. Add the deck or minutes once it's saved.`}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={save.isPending}
            onClick={() => save.mutate()}
          >
            {review ? "Save review" : "Add review"}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {errors._ && Object.keys(errors).length === 1 && (
          <Alert tone="danger" title={errors._} />
        )}
        <Field label="Held on" required error={errors.heldOn}>
          {(p) => (
            <TextInput
              {...p}
              type="date"
              max={today()}
              value={form.heldOn}
              onChange={(e) => setForm({ ...form, heldOn: e.target.value })}
            />
          )}
        </Field>
        <Field label="JBS people who attended" error={errors.attendeeIds}>
          {(p) => (
            <div className="flex flex-col gap-2">
              {form.attendeeIds.length > 0 && (
                <ul className="flex flex-wrap gap-1.5">
                  {form.attendeeIds.map((id) => (
                    <li key={id}>
                      <button
                        type="button"
                        onClick={() =>
                          setForm({
                            ...form,
                            attendeeIds: form.attendeeIds.filter(
                              (x) => x !== id,
                            ),
                          })
                        }
                        className="inline-flex min-h-8 items-center gap-1 rounded-full bg-primary-soft px-3 text-xs font-medium text-primary-soft-foreground hover:opacity-80"
                        aria-label={`Remove ${names.get(id) ?? "person"}`}
                      >
                        {names.get(id) ?? "…"} ×
                      </button>
                    </li>
                  ))}
                </ul>
              )}
              <Select
                {...p}
                value={pick}
                onChange={(e) => {
                  const id = Number(e.target.value);
                  if (id && !form.attendeeIds.includes(id))
                    setForm({
                      ...form,
                      attendeeIds: [...form.attendeeIds, id],
                    });
                  setPick("");
                }}
              >
                <option value="">Add someone…</option>
                {(people.data ?? [])
                  .filter((x) => !form.attendeeIds.includes(x.id))
                  .map((x) => (
                    <option key={x.id} value={x.id}>
                      {x.name}
                    </option>
                  ))}
              </Select>
            </div>
          )}
        </Field>
        <Field label="Supplier people who attended">
          {(p) => (
            <TextInput
              {...p}
              maxLength={1000}
              placeholder="For example: Dana Brooks (Account manager), Lee Park (Operations)"
              value={form.supplierAttendees}
              onChange={(e) =>
                setForm({ ...form, supplierAttendees: e.target.value })
              }
            />
          )}
        </Field>
        <Field label="Summary" required error={errors.summary}>
          {(p) => (
            <Textarea
              {...p}
              rows={4}
              maxLength={4000}
              placeholder="What was discussed and agreed."
              value={form.summary}
              onChange={(e) => setForm({ ...form, summary: e.target.value })}
            />
          )}
        </Field>
        <div className="flex flex-col gap-1.5">
          <span className="text-sm font-medium">Overall</span>
          <div>
            <Segmented
              label="Overall"
              value={form.rating}
              onChange={(v) => setForm({ ...form, rating: v })}
              options={[
                { value: "none", label: "Not rated" },
                { value: "Good", label: "Good" },
                { value: "NeedsImprovement", label: "Needs improvement" },
                { value: "Poor", label: "Poor" },
              ]}
            />
          </div>
        </div>
        <fieldset className="flex flex-col gap-3">
          <legend className="text-sm font-medium">
            Scores, 1 (poor) to 5 (excellent)
          </legend>
          {scoreNames.map(([key, label]) => (
            <ScorePicker
              key={key}
              label={label}
              value={form[key]}
              onChange={(v) => setForm({ ...form, [key]: v })}
            />
          ))}
        </fieldset>
        {!review && (
          <fieldset className="flex flex-col gap-3">
            <legend className="text-sm font-medium">Actions</legend>
            {actions.map((a, i) => (
              <div
                key={i}
                className="flex flex-col gap-2 rounded-[var(--radius-control)] border border-border p-3"
              >
                <Field
                  label={`Action ${i + 1}`}
                  required
                  error={errors[`actions.${i}.text`]}
                >
                  {(p) => (
                    <TextInput
                      {...p}
                      maxLength={500}
                      value={a.text}
                      onChange={(e) =>
                        setActions(
                          actions.map((x, j) =>
                            j === i ? { ...x, text: e.target.value } : x,
                          ),
                        )
                      }
                    />
                  )}
                </Field>
                <div className="grid gap-2 sm:grid-cols-2">
                  <Field
                    label="Who"
                    required
                    error={errors[`actions.${i}.ownerId`]}
                  >
                    {(p) => (
                      <Select
                        {...p}
                        value={a.ownerId}
                        onChange={(e) =>
                          setActions(
                            actions.map((x, j) =>
                              j === i ? { ...x, ownerId: e.target.value } : x,
                            ),
                          )
                        }
                      >
                        <option value="">Choose…</option>
                        {(people.data ?? []).map((x) => (
                          <option key={x.id} value={x.id}>
                            {x.name}
                          </option>
                        ))}
                      </Select>
                    )}
                  </Field>
                  <Field
                    label="Due"
                    required
                    error={errors[`actions.${i}.dueOn`]}
                  >
                    {(p) => (
                      <TextInput
                        {...p}
                        type="date"
                        value={a.dueOn}
                        onChange={(e) =>
                          setActions(
                            actions.map((x, j) =>
                              j === i ? { ...x, dueOn: e.target.value } : x,
                            ),
                          )
                        }
                      />
                    )}
                  </Field>
                </div>
                <div>
                  <Button
                    variant="ghost"
                    icon={<Trash2 aria-hidden className="size-4" />}
                    onClick={() =>
                      setActions(actions.filter((_, j) => j !== i))
                    }
                  >
                    Remove action
                  </Button>
                </div>
              </div>
            ))}
            <div>
              <Button
                icon={<Plus aria-hidden className="size-4" />}
                onClick={() =>
                  setActions([...actions, { text: "", ownerId: "", dueOn: "" }])
                }
              >
                Add an action
              </Button>
            </div>
          </fieldset>
        )}
        <Field
          label="Next business review due"
          help="Sets the supplier's next review date (the relationship owner is reminded). Leave it empty to keep it as is."
        >
          {(p) => (
            <TextInput
              {...p}
              type="date"
              value={form.nextReviewDue}
              onChange={(e) =>
                setForm({ ...form, nextReviewDue: e.target.value })
              }
            />
          )}
        </Field>
      </div>
    </Dialog>
  );
}

function AddActionDialog({
  supplierId,
  reviewId,
  onClose,
}: {
  supplierId: number;
  reviewId: number;
  onClose: () => void;
}) {
  const people = usePeople();
  const refresh = useSave(supplierId);
  const [a, setA] = useState<ActionDraft>({ text: "", ownerId: "", dueOn: "" });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const save = useMutation({
    mutationFn: () =>
      api.post(`/api/suppliers/${supplierId}/reviews/${reviewId}/actions`, {
        text: a.text,
        ownerId: a.ownerId ? Number(a.ownerId) : null,
        dueOn: a.dueOn || null,
      }),
    onSuccess: () => {
      void refresh();
      toast.success("Action added. Its owner has been emailed.");
      onClose();
    },
    onError: (e) =>
      setErrors(e instanceof ApiError ? e.fields : { text: errorText(e) }),
  });
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Add an action"
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={save.isPending}
            onClick={() => save.mutate()}
          >
            Add action
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <Field label="What needs doing" required error={errors.text}>
          {(p) => (
            <TextInput
              {...p}
              maxLength={500}
              value={a.text}
              onChange={(e) => setA({ ...a, text: e.target.value })}
            />
          )}
        </Field>
        <Field label="Who" required error={errors.ownerId}>
          {(p) => (
            <Select
              {...p}
              value={a.ownerId}
              onChange={(e) => setA({ ...a, ownerId: e.target.value })}
            >
              <option value="">Choose…</option>
              {(people.data ?? []).map((x) => (
                <option key={x.id} value={x.id}>
                  {x.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field label="Due" required error={errors.dueOn}>
          {(p) => (
            <TextInput
              {...p}
              type="date"
              value={a.dueOn}
              onChange={(e) => setA({ ...a, dueOn: e.target.value })}
            />
          )}
        </Field>
      </div>
    </Dialog>
  );
}

function ReviewCard({
  data,
  review,
}: {
  data: SupplierPageData;
  review: BusinessReview;
}) {
  const s = data.supplier;
  const refresh = useSave(s.id);
  const deckInput = useRef<HTMLInputElement>(null);
  const [editing, setEditing] = useState(false);
  const [addingAction, setAddingAction] = useState(false);
  const tick = useMutation({
    mutationFn: (x: { id: number; done: boolean }) =>
      api.post(`/api/review-actions/${x.id}/done`, { done: x.done }),
    onSuccess: (_, x) => {
      void refresh();
      toast.success(x.done ? "Action done." : "Action reopened.");
    },
    onError: (e) => toast.error(errorText(e)),
  });
  const deck = useMutation({
    mutationFn: (file: File) =>
      uploadSupplierDocument(s.id, file, { reviewId: review.id }),
    onSuccess: () => {
      void refresh();
      toast.success("Added to the review.");
    },
    onError: (e) => toast.error(errorText(e)),
  });
  const scores = scoreNames.filter(([key]) => review[key] !== null);
  return (
    <Card className="p-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="font-display text-base font-semibold">
            Business review, {formatDate(review.heldOn)}
          </h2>
          <p className="text-xs text-muted-foreground">
            Added by {review.createdByName}, {formatDateTime(review.createdUtc)}
          </p>
        </div>
        <div className="flex items-center gap-2">
          {review.rating && (
            <Badge
              tone={ratingTone[review.rating]}
              icon={
                review.rating === "Good" ? (
                  <CheckCircle2 aria-hidden className="size-3.5" />
                ) : (
                  <AlertTriangle aria-hidden className="size-3.5" />
                )
              }
            >
              {review.ratingName}
            </Badge>
          )}
          {data.can.review && (
            <Button
              variant="ghost"
              icon={<Pencil aria-hidden className="size-4" />}
              onClick={() => setEditing(true)}
              aria-label={`Change the review of ${formatDate(review.heldOn)}`}
            />
          )}
        </div>
      </div>
      {scores.length > 0 && (
        <dl className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-4">
          {scores.map(([key, label]) => (
            <div
              key={key}
              className="rounded-[var(--radius-control)] bg-muted/60 p-2"
            >
              <dt className="text-xs text-muted-foreground">{label}</dt>
              <dd className="tabular font-semibold">{review[key]} / 5</dd>
            </div>
          ))}
        </dl>
      )}
      <p className="mt-3 text-sm whitespace-pre-line">{review.summary}</p>
      {(review.attendees.length > 0 || review.supplierAttendees) && (
        <p className="mt-3 flex items-start gap-2 text-xs text-muted-foreground">
          <Users aria-hidden className="mt-0.5 size-3.5 shrink-0" />
          <span>
            {[
              review.attendees.map((a) => a.name).join(", "),
              review.supplierAttendees,
            ]
              .filter(Boolean)
              .join("; ")}
          </span>
        </p>
      )}
      <div className="mt-4 flex flex-col gap-2">
        <div className="flex items-center justify-between gap-2">
          <h3 className="text-sm font-semibold">Actions</h3>
          {data.can.review && (
            <Button
              variant="ghost"
              icon={<Plus aria-hidden className="size-4" />}
              onClick={() => setAddingAction(true)}
            >
              Add action
            </Button>
          )}
        </div>
        {review.actions.length === 0 ? (
          <p className="text-sm text-muted-foreground">No actions.</p>
        ) : (
          <ul className="flex flex-col gap-1">
            {review.actions.map((a) => {
              const overdue = !a.done && daysUntil(a.dueOn) < 0;
              return (
                <li
                  key={a.id}
                  className="flex items-start gap-3 rounded-[var(--radius-control)] p-2 hover:bg-muted/40"
                >
                  <input
                    type="checkbox"
                    className="mt-0.5 size-5 shrink-0 accent-[var(--primary)]"
                    checked={a.done}
                    disabled={!a.canTick || tick.isPending}
                    onChange={(e) =>
                      tick.mutate({ id: a.id, done: e.target.checked })
                    }
                    aria-label={`${a.done ? "Done" : "Not done"}: ${a.text}`}
                  />
                  <span className="min-w-0 text-sm">
                    <span
                      className={cn(
                        a.done && "text-muted-foreground line-through",
                      )}
                    >
                      {a.text}
                    </span>
                    <span
                      className={cn(
                        "block text-xs",
                        overdue
                          ? "font-medium text-danger"
                          : "text-muted-foreground",
                      )}
                    >
                      {a.ownerName} · due {formatDate(a.dueOn)}
                      {overdue && " (overdue)"}
                      {a.done &&
                        a.doneUtc &&
                        ` · done ${formatDateTime(a.doneUtc)}${a.doneByName ? ` by ${a.doneByName}` : ""}`}
                    </span>
                  </span>
                </li>
              );
            })}
          </ul>
        )}
      </div>
      <div className="mt-4 flex flex-col gap-2">
        <div className="flex items-center justify-between gap-2">
          <h3 className="text-sm font-semibold">Deck and minutes</h3>
          {data.can.review && (
            <>
              <input
                ref={deckInput}
                type="file"
                hidden
                accept=".pdf,.doc,.docx,.xls,.xlsx,.msg,.eml,.txt,.csv,.png,.jpg,.jpeg"
                onChange={(e) => {
                  const f = e.target.files?.[0];
                  if (f) deck.mutate(f);
                  e.target.value = "";
                }}
              />
              <Button
                variant="ghost"
                loading={deck.isPending}
                icon={<Upload aria-hidden className="size-4" />}
                onClick={() => deckInput.current?.click()}
              >
                Attach a file
              </Button>
            </>
          )}
        </div>
        {review.documents.length === 0 ? (
          <p className="text-sm text-muted-foreground">Nothing attached.</p>
        ) : (
          <ul className="flex flex-col gap-1">
            {review.documents.map((d) => (
              <li key={d.id}>
                <a
                  href={`/api/supplier-documents/${d.versions[0].id}/file`}
                  className="inline-flex min-h-9 items-center gap-2 text-sm font-medium hover:underline"
                >
                  <FileText
                    aria-hidden
                    className="size-4 text-muted-foreground"
                  />{" "}
                  {d.title}
                </a>
              </li>
            ))}
          </ul>
        )}
      </div>
      {editing && (
        <ReviewDialog
          data={data}
          review={review}
          onClose={() => setEditing(false)}
        />
      )}
      {addingAction && (
        <AddActionDialog
          supplierId={s.id}
          reviewId={review.id}
          onClose={() => setAddingAction(false)}
        />
      )}
    </Card>
  );
}

function Reviews({ data }: { data: SupplierPageData }) {
  const [adding, setAdding] = useState(false);
  const open = data.reviews
    .flatMap((r) => r.actions)
    .filter((a) => !a.done).length;
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-muted-foreground">
          {data.reviews.length === 0
            ? "No business reviews yet."
            : `${data.reviews.length} ${data.reviews.length === 1 ? "review" : "reviews"}, newest first. ${open} open ${open === 1 ? "action" : "actions"}.`}
        </p>
        {data.can.review && (
          <Button
            variant="primary"
            icon={<Plus aria-hidden className="size-4" />}
            onClick={() => setAdding(true)}
          >
            Add business review
          </Button>
        )}
      </div>
      {data.reviews.length === 0 ? (
        <Card>
          <EmptyState
            icon={<Users />}
            title={
              data.can.review
                ? "Add the first business review once one has been held."
                : "No business reviews yet."
            }
          />
        </Card>
      ) : (
        data.reviews.map((r) => (
          <ReviewCard key={r.id} data={data} review={r} />
        ))
      )}
      {adding && (
        <ReviewDialog
          data={data}
          review={null}
          onClose={() => setAdding(false)}
        />
      )}
    </div>
  );
}

// ------------------------------------------------------------------ Contracts

function Contracts({ data }: { data: SupplierPageData }) {
  if (data.contracts.length === 0)
    return (
      <Card>
        <EmptyState
          icon={<FileText />}
          title="No contracts with this supplier that you can see."
        />
      </Card>
    );
  return (
    <Card>
      <ul className="divide-y divide-border">
        {data.contracts.map((k) => (
          <li
            key={k.id}
            className="flex flex-wrap items-center justify-between gap-3 p-4"
          >
            <div className="min-w-0">
              <Link
                to={`/contracts/${k.id}`}
                className="font-medium hover:underline"
              >
                {k.title}
              </Link>
              <p className="flex flex-wrap items-center gap-1 text-xs text-muted-foreground">
                {k.reference} · {k.ownerName}
                {k.value !== null &&
                  ` · ${formatMoney(k.value, k.currencyCode, true)}`}
                {k.isEvergreen
                  ? " · Evergreen"
                  : k.endDate
                    ? ` · ends ${formatDate(k.endDate)}`
                    : ""}
                {k.isConfidential && (
                  <>
                    {" · "}
                    <Lock aria-hidden className="size-3" /> Confidential
                  </>
                )}
              </p>
            </div>
            <Badge>{k.stageName}</Badge>
          </li>
        ))}
      </ul>
    </Card>
  );
}

// ------------------------------------------------------------------ Page

export function SupplierPage() {
  const { id } = useParams();
  const supplierId = Number(id);
  const { person } = useMe();
  const [params, setParams] = useSearchParams();
  const tab = (tabs as readonly string[]).includes(params.get("tab") ?? "")
    ? (params.get("tab") as Tab)
    : "overview";
  const [editingBasics, setEditingBasics] = useState(false);
  const data = useQuery({
    queryKey: ["suppliers", supplierId],
    queryFn: () => api.get<SupplierPageData>(`/api/suppliers/${supplierId}`),
    enabled: supplierId > 0,
  });
  const back = (
    <Link
      to="/suppliers"
      className="inline-flex min-h-11 items-center gap-1 self-start text-sm font-medium text-muted-foreground hover:text-foreground"
    >
      <ChevronLeft aria-hidden className="size-4" /> Suppliers
    </Link>
  );
  if (data.isPending) return <Skeleton className="h-96 w-full" />;
  if (data.isError)
    return (
      <>
        <PageHeader title="Supplier" back={back} />
        <Alert tone="danger" title={(data.error as Error).message} />
      </>
    );
  const d = data.data;
  const s = d.supplier;
  const canEditBasics = person.roles.contractOwner || person.roles.admin;
  const openActions = d.reviews
    .flatMap((r) => r.actions)
    .filter((a) => !a.done).length;
  const expiring = d.documents.filter(
    (x) => x.expiresOn && daysUntil(x.expiresOn) <= 60,
  ).length;
  const asSupplier: Supplier = {
    id: s.id,
    name: s.name,
    abn: s.abn,
    vendorNumber: s.vendorNumber,
    notes: s.notes,
    isActive: s.isActive,
    contractCount: d.contracts.length,
    rowVersion: s.rowVersion,
  };
  return (
    <>
      <PageHeader
        title={s.name}
        description={
          [
            s.abn ? `ABN ${formatAbn(s.abn)}` : null,
            s.vendorNumber ? `SAP vendor ${s.vendorNumber}` : null,
            s.isActive ? null : "Hidden from new contracts",
          ]
            .filter(Boolean)
            .join(" · ") || undefined
        }
        help={help.supplier}
        back={back}
        action={
          canEditBasics ? (
            <Button
              icon={<Pencil aria-hidden className="size-4" />}
              onClick={() => setEditingBasics(true)}
            >
              Edit name and numbers
            </Button>
          ) : undefined
        }
      />
      <div className="flex flex-wrap gap-2" aria-label="At a glance">
        {s.tierName && (
          <Badge
            tone="brand"
            icon={<Building2 aria-hidden className="size-3.5" />}
          >
            {s.tierName}
          </Badge>
        )}
        {s.riskRating && (
          <Badge
            tone={riskTone[s.riskRating]}
            icon={<ShieldAlert aria-hidden className="size-3.5" />}
          >
            {s.riskRating} risk
          </Badge>
        )}
        {s.relationshipOwnerName && (
          <Badge icon={<UserRound aria-hidden className="size-3.5" />}>
            {s.relationshipOwnerName}
          </Badge>
        )}
        {expiring > 0 && (
          <Badge
            tone="warning"
            icon={<BellRing aria-hidden className="size-3.5" />}
          >
            {expiring} {expiring === 1 ? "document" : "documents"} expiring or
            expired
          </Badge>
        )}
      </div>
      <div>
        <Segmented
          label="Show"
          value={tab}
          onChange={(v) =>
            setParams(
              (p) => {
                const n = new URLSearchParams(p);
                n.set("tab", v);
                return n;
              },
              { replace: true },
            )
          }
          options={[
            { value: "overview", label: "Overview" },
            {
              value: "documents",
              label: "Documents",
              count: d.documents.length + d.contractDocuments.length,
            },
            {
              value: "reviews",
              label: "Business reviews",
              count: d.reviews.length,
            },
            {
              value: "contracts",
              label: "Contracts",
              count: d.contracts.length,
            },
          ]}
        />
      </div>
      {openActions > 0 && tab !== "reviews" && (
        <p className="text-sm text-muted-foreground">
          {openActions} open {openActions === 1 ? "action" : "actions"} from
          business reviews.{" "}
          <button
            type="button"
            className="font-medium text-primary hover:underline"
            onClick={() => setParams({ tab: "reviews" })}
          >
            See them
          </button>
        </p>
      )}
      {tab === "overview" && <Overview data={d} />}
      {tab === "documents" && <Documents data={d} />}
      {tab === "reviews" && <Reviews data={d} />}
      {tab === "contracts" && <Contracts data={d} />}
      {editingBasics && (
        <SupplierDialog
          supplier={asSupplier}
          onClose={() => setEditingBasics(false)}
          onSaved={() => setEditingBasics(false)}
        />
      )}
    </>
  );
}
