// The "?" beside every page title: plain how-to steps for that screen (design-system.md section 12).
import { HelpCircle, LifeBuoy, PlayCircle } from "lucide-react";
import { Link } from "react-router";
import { guidesForScreen, isFor } from "@/helpcentre/guides";
import { useMe } from "@/lib/me";
import { useState, type ReactNode } from "react";
import { Dialog } from "./Dialog";
import { Button } from "./ui";

export type HelpTopic = { id?: string; title: string; body: ReactNode };

/** Step-by-step guides (with videos) for this screen that fit the person's roles, shown first so they're easy to find. */
function ScreenGuides({ screen, onGo }: { screen?: string; onGo: () => void }) {
  const { person } = useMe();
  const list = screen ? guidesForScreen(screen).filter((g) => isFor(g, person.roles)) : [];
  if (list.length === 0) return null;
  return (
    <div className="mb-4 flex flex-col gap-1 rounded-[var(--radius-control)] bg-muted/60 p-3">
      <h3 className="text-sm font-semibold">Step by step</h3>
      <ul className="flex flex-col">
        {list.map((g) => (
          <li key={g.id}>
            <Link
              to={`/help/${g.id}`}
              onClick={onGo}
              className="inline-flex min-h-9 items-center gap-2 text-sm font-medium text-primary hover:underline"
            >
              {g.video && <PlayCircle aria-hidden className="size-4 shrink-0" />}
              {g.title}
              {g.video && <span className="sr-only"> (video)</span>}
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function HelpButton({ topic }: { topic: HelpTopic }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label={`Help: ${topic.title}`}
        className="inline-flex size-11 items-center justify-center rounded-full text-muted-foreground hover:bg-muted hover:text-foreground"
      >
        <HelpCircle aria-hidden className="size-5" />
      </button>
      <Dialog
        open={open}
        onOpenChange={setOpen}
        title={topic.title}
        footer={
          <Button variant="primary" onClick={() => setOpen(false)}>
            Got it
          </Button>
        }
      >
        <ScreenGuides screen={topic.id} onGo={() => setOpen(false)} />
        <div className="flex max-w-prose flex-col gap-3 text-sm [&_li]:ml-5 [&_ol]:list-decimal [&_ul]:list-disc">
          {topic.body}
        </div>
        <Link
          to="/help"
          onClick={() => setOpen(false)}
          className="mt-4 inline-flex min-h-9 items-center gap-2 text-sm font-medium text-muted-foreground hover:text-foreground"
        >
          <LifeBuoy aria-hidden className="size-4" /> Open the help centre
        </Link>
      </Dialog>
    </>
  );
}

export function PageHeader({
  title,
  description,
  help,
  action,
  back,
}: {
  title: string;
  description?: string;
  help?: HelpTopic;
  action?: ReactNode;
  back?: ReactNode;
}) {
  return (
    <header className="flex flex-col gap-3">
      {back}
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-1">
            <h1 className="font-display text-xl font-semibold md:text-2xl">
              {title}
            </h1>
            {help && <HelpButton topic={help} />}
          </div>
          {description && (
            <p className="mt-1 text-muted-foreground">{description}</p>
          )}
        </div>
        {action}
      </div>
    </header>
  );
}
