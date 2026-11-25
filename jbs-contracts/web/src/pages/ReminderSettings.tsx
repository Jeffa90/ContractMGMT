// Admin, Emails and reminders: when each reminder is sent, and what every email says (D-13, D-70, D-73). Closed choices only: whole
// days, and plain text with each email's fixed list of {placeholders}. A live preview shows it with a sample contract. Every saved change is kept.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BellRing, ChevronDown, History, Mail } from "lucide-react";
import { useRef, useState } from "react";
import { toast } from "sonner";
import { Textarea } from "@/components/form";
import { PageHeader } from "@/components/Help";
import {
  Alert,
  Button,
  Badge,
  Card,
  Checkbox,
  Field,
  Segmented,
  Skeleton,
  TextInput,
} from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { help } from "@/help";

type Kind = {
  kind: string;
  name: string;
  group: string;
  when: string;
  placeholders: string[];
  button: string;
  days: number[] | null;
  /** Reminders only: false when an admin switched it off (D-99). */
  isOn: boolean | null;
  subject: string;
  body: string;
};
type Data = {
  delivery: string;
  emails: Kind[];
  history: {
    id: number;
    kind: string;
    kindName: string;
    what: "Days" | "Wording" | "OnOff" | "Delivery";
    before: string;
    after: string;
    byName: string;
    atUtc: string;
  }[];
  lastRunUtc: string | null;
};

// Made-up sample values for the preview (no real people).
const sample: Record<string, string> = {
  "First name": "Chris",
  "Contract title": "Linehaul freight Brisbane to Sydney",
  Reference: "JBSAUS-0142",
  Supplier: "Southern Cross Freight",
  Owner: "Chris Taylor",
  "Due date": "Mon 12 Jan 2027",
  Days: "30",
  "Added by": "Alex Admin",
  Roles: "Contract owner, Legal",
  "Windows logon": "DEMO\\ctaylor",
  "Sent by": "Priya Patel",
  "Assigned by": "Sam Nguyen",
  "Legal person": "Leah Brooks",
  Note: "Supplier wants a fuel levy clause.",
  Outcome: "Cleared. Liability cap agreed at $2m.",
  Document: "Public liability certificate 2026",
  "Document type": "insurance certificate",
  "Review date": "Tue 3 Nov 2026",
  Action: "Send the revised delivery schedule",
  Approver: "Grant Wallace",
  Reason: "The signed copy is missing page 4.",
  "Effective date": "Tue 1 Dec 2026",
};

const fill = (text: string) =>
  text.replace(/\{([^{}]*)\}/g, (m, name: string) => sample[name] ?? m);

export function ReminderSettingsPage() {
  const data = useQuery({
    queryKey: ["reminder-settings"],
    queryFn: () => api.get<Data>("/api/admin/reminders"),
  });
  return (
    <>
      <PageHeader
        title="Emails and reminders"
        description="What every email says, and when reminders go out."
        help={help.reminders}
      />
      {data.isPending ? (
        <Skeleton className="h-96 w-full" />
      ) : data.isError ? (
        <Alert tone="danger" title={(data.error as Error).message} />
      ) : (
        <>
          <p className="text-sm text-muted-foreground">
            Reminders are checked every morning at 7:00am Brisbane time.{" "}
            {data.data.lastRunUtc
              ? `Last checked ${formatDateTime(data.data.lastRunUtc)}.`
              : "They haven't run yet."}
          </p>
          <DeliveryCard delivery={data.data.delivery} />
          {[...new Set(data.data.emails.map((e) => e.group))].map((group) => (
            <section
              key={group}
              aria-labelledby={`group-${group}`}
              className="flex flex-col gap-3"
            >
              <h2
                id={`group-${group}`}
                className="font-display text-lg font-semibold"
              >
                {group}
              </h2>
              {data.data.emails
                .filter((e) => e.group === group)
                .map((k) => (
                  <KindCard key={k.kind} kind={k} />
                ))}
            </section>
          ))}
          <Card className="p-5 md:p-6">
            <h2 className="flex items-center gap-2 font-display text-lg font-semibold">
              <History aria-hidden className="size-5" /> History
            </h2>
            {data.data.history.length === 0 ? (
              <p className="mt-2 text-sm text-muted-foreground">
                No changes yet. These are the starting days and wording.
              </p>
            ) : (
              <ul className="mt-3 flex flex-col gap-3">
                {data.data.history.map((h) => (
                  <li
                    key={h.id}
                    className="flex flex-col border-l-2 border-border pl-3 text-sm"
                  >
                    <span>
                      {h.byName} changed the {h.kindName.toLowerCase()}{" "}
                      {h.what === "Days"
                        ? `days from ${h.before} to ${h.after}`
                        : h.what === "Wording"
                          ? "wording"
                          : `from ${h.before.toLowerCase()} to ${h.after.toLowerCase()}`}
                    </span>
                    {h.what === "Wording" && (
                      <details className="text-xs">
                        <summary className="cursor-pointer text-muted-foreground">
                          See before and after
                        </summary>
                        <div className="mt-1 grid gap-2 md:grid-cols-2">
                          <pre className="rounded bg-muted p-2 whitespace-pre-wrap font-sans">
                            {h.before}
                          </pre>
                          <pre className="rounded bg-muted p-2 whitespace-pre-wrap font-sans">
                            {h.after}
                          </pre>
                        </div>
                      </details>
                    )}
                    <span className="text-xs text-muted-foreground">
                      {formatDateTime(h.atUtc)}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </>
      )}
    </>
  );
}

const deliveryOptions = [
  { value: "Each", label: "As they happen" },
  { value: "Daily", label: "One email a day" },
  { value: "Weekly", label: "One email a week" },
];

/** The default for how reminder emails come, for everyone who hasn't chosen their own (D-99). */
function DeliveryCard({ delivery }: { delivery: string }) {
  const queryClient = useQueryClient();
  const save = useMutation({
    mutationFn: (value: string) => api.put("/api/admin/reminders/delivery", { delivery: value }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["reminder-settings"] });
      void queryClient.invalidateQueries({ queryKey: ["me"] });
      toast.success("Saved. It applies from the next morning's reminders.");
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : "That didn't save. Try again."),
  });
  return (
    <Card className="flex flex-col gap-3 p-5 md:p-6">
      <div>
        <h2 className="font-display text-lg font-semibold">How reminder emails come</h2>
        <p className="text-sm text-muted-foreground">
          For everyone who hasn't chosen their own; each person can change theirs by clicking their name and choosing Email reminders.
          A summary puts all of someone's reminders in one email. A weekly one goes on Mondays, and sooner when something is a week or less
          away. Emails that hand over a turn (sent to Legal, waiting for approval) always go straight away.
        </p>
      </div>
      <div>
        <Segmented
          label="How reminder emails come"
          value={save.isPending ? save.variables : delivery}
          onChange={(v) => save.mutate(v)}
          options={deliveryOptions}
        />
      </div>
    </Card>
  );
}

function KindCard({ kind }: { kind: Kind }) {
  const placeholders = kind.placeholders;
  const queryClient = useQueryClient();
  const [days, setDays] = useState((kind.days ?? []).join(", "));
  const [subject, setSubject] = useState(kind.subject);
  const [body, setBody] = useState(kind.body);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const lastField = useRef<"subject" | "body">("body");
  const subjectRef = useRef<HTMLInputElement>(null);
  const bodyRef = useRef<HTMLTextAreaElement>(null);

  const done = (message: string) => () => {
    queryClient.invalidateQueries({ queryKey: ["reminder-settings"] });
    toast.success(message);
  };
  const fail = (e: unknown) =>
    setErrors(
      e instanceof ApiError
        ? { ...e.fields, form: Object.keys(e.fields).length ? "" : e.message }
        : { form: "That didn't save. Try again." },
    );
  const saveDays = useMutation({
    mutationFn: () => {
      const parsed = days
        .split(/[\s,]+/)
        .filter(Boolean)
        .map(Number);
      if (parsed.length === 0 || parsed.some((d) => !Number.isInteger(d)))
        throw new ApiError(400, {
          code: "invalid",
          message: "Use whole numbers.",
          fields: {
            days: "Use whole numbers of days, separated by commas, for example 90, 60, 30.",
          },
        });
      return api.put("/api/admin/reminders/days", {
        kind: kind.kind,
        days: parsed,
      });
    },
    onSuccess: done(`${kind.name} days saved.`),
    onError: fail,
  });
  const saveWording = useMutation({
    mutationFn: () =>
      api.put("/api/admin/reminders/wording", {
        kind: kind.kind,
        subject,
        body,
      }),
    onSuccess: done(`${kind.name} wording saved.`),
    onError: fail,
  });

  // Adds a placeholder where the cursor was (subject or body).
  const insert = (name: string) => {
    const token = `{${name}}`;
    const target =
      lastField.current === "subject" ? subjectRef.current : bodyRef.current;
    const value = lastField.current === "subject" ? subject : body;
    const start = target?.selectionStart ?? value.length;
    const end = target?.selectionEnd ?? value.length;
    const next = value.slice(0, start) + token + value.slice(end);
    if (lastField.current === "subject") setSubject(next);
    else setBody(next);
    requestAnimationFrame(() => {
      target?.focus();
      target?.setSelectionRange(start + token.length, start + token.length);
    });
  };
  const changed = subject !== kind.subject || body !== kind.body;
  const switchOn = useMutation({
    mutationFn: (isOn: boolean) => api.put("/api/admin/reminders/on", { kind: kind.kind, isOn }),
    onSuccess: (_, isOn) => done(isOn ? `${kind.name} switched on.` : `${kind.name} switched off. Nobody gets it until it's switched on again.`)(),
    onError: fail,
  });

  return (
    <Card className="p-0">
      <details
        className="group"
        open={kind.days !== null && kind.kind === "Notice"}
      >
        <summary className="flex cursor-pointer list-none items-start justify-between gap-3 p-5 md:p-6">
          <span>
            <span className="flex items-center gap-2 font-semibold">
              {kind.days ? (
                <BellRing aria-hidden className="size-5" />
              ) : (
                <Mail aria-hidden className="size-5" />
              )}{" "}
              {kind.name}
              {kind.isOn === false && <Badge tone="warning">Off</Badge>}
            </span>
            <span className="block text-sm text-muted-foreground">
              {kind.when}
            </span>
            <span className="mt-1 block text-sm">
              <span className="text-muted-foreground">Subject:</span>{" "}
              {kind.subject}
            </span>
          </span>
          <ChevronDown
            aria-hidden
            className="mt-1 size-5 shrink-0 text-muted-foreground transition-transform group-open:rotate-180"
          />
        </summary>
        <div className="flex flex-col gap-5 px-5 pb-5 md:px-6 md:pb-6">
          {errors.form && <Alert tone="danger" title={errors.form} />}
          {kind.isOn !== null && (
            <Checkbox
              label="Send this reminder"
              description="Untick to stop it for everyone. Its days and wording are kept for when it's switched back on."
              checked={switchOn.isPending ? switchOn.variables : kind.isOn}
              onChange={(v) => switchOn.mutate(v)}
            />
          )}
          {kind.days && (
            <div className="flex flex-wrap items-end gap-3">
              <Field
                label="Days before"
                error={errors.days}
                help="Whole days, largest first. For example 90, 60, 30, 14, 7."
              >
                {(p) => (
                  <TextInput
                    {...p}
                    value={days}
                    onChange={(e) => setDays(e.target.value)}
                    className="max-w-72"
                    inputMode="numeric"
                  />
                )}
              </Field>
              <Button
                loading={saveDays.isPending}
                disabled={days === kind.days.join(", ")}
                onClick={() => saveDays.mutate()}
              >
                Save days
              </Button>
            </div>
          )}

          <div className="grid gap-5 lg:grid-cols-2">
            <div className="flex flex-col gap-3">
              <Field label="Subject" error={errors.subject}>
                {(p) => (
                  <TextInput
                    {...p}
                    ref={subjectRef}
                    value={subject}
                    onFocus={() => (lastField.current = "subject")}
                    onChange={(e) => setSubject(e.target.value)}
                    maxLength={200}
                  />
                )}
              </Field>
              <Field
                label="Email"
                error={errors.body}
                help={`Plain text. Leave a blank line between paragraphs. A paragraph whose placeholder is empty (for example no note) is left out. A button "${kind.button}" is added at the end.`}
              >
                {(p) => (
                  <Textarea
                    {...p}
                    ref={bodyRef}
                    value={body}
                    onFocus={() => (lastField.current = "body")}
                    onChange={(e) => setBody(e.target.value)}
                    className="min-h-48"
                    maxLength={4000}
                  />
                )}
              </Field>
              <div>
                <p className="mb-1 text-sm font-medium">Add a placeholder</p>
                <div className="flex flex-wrap gap-1">
                  {placeholders.map((ph) => (
                    <button
                      key={ph}
                      type="button"
                      onClick={() => insert(ph)}
                      className="min-h-9 rounded-full border border-border px-3 text-xs hover:bg-muted"
                    >
                      {`{${ph}}`}
                    </button>
                  ))}
                </div>
              </div>
              <div className="flex gap-2">
                <Button
                  variant="primary"
                  loading={saveWording.isPending}
                  disabled={!changed}
                  onClick={() => saveWording.mutate()}
                >
                  Save wording
                </Button>
                {changed && (
                  <Button
                    variant="ghost"
                    onClick={() => {
                      setSubject(kind.subject);
                      setBody(kind.body);
                      setErrors({});
                    }}
                  >
                    Undo changes
                  </Button>
                )}
              </div>
            </div>
            <div className="flex flex-col gap-2">
              <p className="text-sm font-medium">
                Preview, with a sample contract
              </p>
              <div className="rounded-[var(--radius-card)] border border-border bg-background p-4 text-sm">
                <p className="font-semibold">{fill(subject)}</p>
                <div className="mt-3 flex flex-col gap-2">
                  {body
                    .split(/\n\s*\n/)
                    .filter((para) => para.trim())
                    .map((para, i) => (
                      <p key={i} className="whitespace-pre-line">
                        {fill(para.trim())}
                      </p>
                    ))}
                </div>
                <span className="mt-3 inline-block rounded-lg bg-primary px-3 py-2 text-xs font-semibold text-primary-foreground">
                  {kind.button}
                </span>
              </div>
            </div>
          </div>
        </div>
      </details>
    </Card>
  );
}
