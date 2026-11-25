// The help centre (D-89): step-by-step guides grouped by what you're doing, the ones for your roles first, each with a short silent video
// (fictional demo data) whose captions are the steps. "About each screen" holds the same help as the "?" beside each page title.
import {
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  LifeBuoy,
  PlayCircle,
  Search,
  Smartphone,
} from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router";
import { PageHeader } from "@/components/Help";
import { Badge, Button, Card, EmptyState, TextInput } from "@/components/ui";
import { cn } from "@/lib/cn";
import { useMe } from "@/lib/me";
import { help } from "@/help";
import {
  groups,
  guides,
  isFor,
  rich,
  roleNames,
  search,
  videoFiles,
  type Guide,
  type Role,
} from "./guides";

function GuideLink({ guide }: { guide: Guide }) {
  return (
    <li>
      <Link
        to={`/help/${guide.id}`}
        className="flex min-h-14 items-center gap-3 px-4 py-3 hover:bg-muted/60"
      >
        <span className="min-w-0 flex-1">
          <span className="block font-medium">{guide.title}</span>
          <span className="block text-sm text-muted-foreground">
            {guide.summary}
          </span>
        </span>
        {guide.video && (
          <span className="inline-flex shrink-0 items-center gap-1 text-xs text-muted-foreground">
            <PlayCircle aria-hidden className="size-4" />
            <span className="hidden sm:inline">Video</span>
            <span className="sr-only sm:hidden">Has a video</span>
          </span>
        )}
        <ChevronRight
          aria-hidden
          className="size-5 shrink-0 text-muted-foreground"
        />
      </Link>
    </li>
  );
}

function GuideList({ list }: { list: Guide[] }) {
  return (
    <Card>
      <ul className="divide-y divide-border">
        {list.map((g) => (
          <GuideLink key={g.id} guide={g} />
        ))}
      </ul>
    </Card>
  );
}

/** Who each screen's help is for (the screens people without that role never see are left out, unless they ask for everything). */
const screenRoles: Record<string, Role[]> = {
  people: ["admin"],
  fields: ["admin"],
  lists: ["admin"],
  reminders: ["admin"],
  import: ["admin"],
  reports: ["admin", "auditor"],
  team: ["admin", "auditor", "groupHead"],
  approve: ["approver", "groupHead"],
};

export function HelpCentrePage() {
  const { person } = useMe();
  const [query, setQuery] = useState("");
  const [everything, setEverything] = useState(false);
  const yours = (g: Guide) => everything || isFor(g, person.roles);
  const list = guides.filter(yours);
  const hidden =
    guides.length - guides.filter((g) => isFor(g, person.roles)).length;
  const found = search(query, list);
  const screens = Object.values(help).filter(
    (t) =>
      everything ||
      !t.id ||
      !screenRoles[t.id] ||
      screenRoles[t.id].some(
        (r) => r === "everyone" || person.roles[r as keyof typeof person.roles],
      ),
  );

  return (
    <>
      <PageHeader
        title="Help"
        description="Step-by-step guides for what you do, most with a short video. Search, or pick one below."
      />
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="relative w-full max-w-xl">
          <Search
            aria-hidden
            className="pointer-events-none absolute top-1/2 left-3 size-5 -translate-y-1/2 text-muted-foreground"
          />
          <TextInput
            type="search"
            aria-label="Search the help"
            placeholder="What do you want to do? For example: renew, Legal, approve"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            className="pl-10"
          />
        </div>
        {hidden > 0 && (
          <Button
            variant="ghost"
            aria-pressed={everything}
            onClick={() => setEverything((v) => !v)}
          >
            {everything
              ? "Just the guides for me"
              : `Show guides for other roles (${hidden})`}
          </Button>
        )}
      </div>

      {query.trim() ? (
        found.length === 0 ? (
          <Card>
            <EmptyState
              icon={<LifeBuoy />}
              title={`No guides match "${query.trim()}". Try another word${hidden > 0 && !everything ? ", or show the guides for other roles" : ""}.`}
            />
          </Card>
        ) : (
          <section aria-labelledby="results" className="flex flex-col gap-2">
            <h2 id="results" className="font-display text-lg font-semibold">
              {found.length} {found.length === 1 ? "guide" : "guides"}
            </h2>
            <GuideList list={found} />
          </section>
        )
      ) : (
        <>
          {groups.map((group) => {
            const inGroup = list.filter((g) => g.group === group.id);
            if (inGroup.length === 0) return null;
            return (
              <section
                key={group.id}
                aria-labelledby={`group-${group.id}`}
                className="flex flex-col gap-2"
              >
                <h2
                  id={`group-${group.id}`}
                  className="font-display text-lg font-semibold"
                >
                  {group.title}
                </h2>
                <GuideList list={inGroup} />
              </section>
            );
          })}
          <section aria-labelledby="screens" className="flex flex-col gap-2">
            <h2 id="screens" className="font-display text-lg font-semibold">
              About each screen
            </h2>
            <p className="text-sm text-muted-foreground">
              The same help as the ? beside each page title.
            </p>
            <Card>
              <ul className="divide-y divide-border">
                {screens.map((topic) => (
                  <li key={topic.title}>
                    <details className="group">
                      <summary className="flex min-h-12 cursor-pointer list-none items-center justify-between gap-3 px-4 py-3 font-medium hover:bg-muted/60 [&::-webkit-details-marker]:hidden">
                        {topic.title}
                        <ChevronDown
                          aria-hidden
                          className="size-5 text-muted-foreground transition-transform group-open:rotate-180"
                        />
                      </summary>
                      <div className="flex max-w-prose flex-col gap-3 px-4 pb-4 text-sm [&_li]:ml-5 [&_ol]:list-decimal [&_ul]:list-disc">
                        {topic.body}
                      </div>
                    </details>
                  </li>
                ))}
              </ul>
            </Card>
          </section>
        </>
      )}
    </>
  );
}

/** The video, with its steps beside it. The step being shown is highlighted; clicking a step jumps the video to it. */
function GuideVideo({ guide }: { guide: Guide }) {
  const video = useRef<HTMLVideoElement>(null);
  const [current, setCurrent] = useState<number | null>(null);
  const [starts, setStarts] = useState<number[]>([]);
  const files = videoFiles(guide.id);

  useEffect(() => {
    const v = video.current;
    const track = v?.textTracks[0];
    if (!v || !track) return;
    track.mode = "showing";
    const read = () => {
      const cues = track.cues ? Array.from(track.cues) : [];
      if (cues.length) setStarts(cues.map((c) => c.startTime));
    };
    const change = () => {
      const cue = track.activeCues?.[0];
      setCurrent(cue ? Number(cue.id) - 1 : null);
    };
    read();
    track.addEventListener("cuechange", change);
    v.addEventListener("loadedmetadata", read);
    const el = v.querySelector("track");
    el?.addEventListener("load", read);
    return () => {
      track.removeEventListener("cuechange", change);
      v.removeEventListener("loadedmetadata", read);
      el?.removeEventListener("load", read);
    };
  }, []);

  const jump = (i: number) => {
    const v = video.current;
    if (!v || starts[i] === undefined) return;
    v.currentTime = starts[i] + 0.05;
    void v.play().catch(() => undefined);
  };

  return (
    <div
      className={cn(
        "grid gap-4",
        guide.phone
          ? "md:grid-cols-[minmax(0,20rem)_1fr]"
          : "lg:grid-cols-[minmax(0,2fr)_minmax(16rem,1fr)]",
      )}
    >
      <div className={cn(guide.phone && "mx-auto w-full max-w-xs")}>
        <video
          ref={video}
          controls
          muted
          playsInline
          preload="metadata"
          poster={files.poster}
          className="w-full rounded-[var(--radius-card)] border border-border bg-muted"
          aria-label={`Video: ${guide.title}. Silent, with the steps as captions.`}
        >
          <source src={files.video} type="video/mp4" />
          <track
            kind="captions"
            src={files.captions}
            srcLang="en"
            label="Steps"
            default
          />
          Your browser can't play this video. The steps are listed here.
        </video>
        <p className="mt-1 text-xs text-muted-foreground">
          {guide.phone && (
            <Smartphone aria-hidden className="mr-1 inline size-3.5" />
          )}
          Silent, with the steps as captions. Shown with made-up demo data.
        </p>
      </div>
      <Steps
        guide={guide}
        current={current}
        onJump={starts.length ? jump : undefined}
      />
    </div>
  );
}

function Steps({
  guide,
  current,
  onJump,
}: {
  guide: Guide;
  current?: number | null;
  onJump?: (i: number) => void;
}) {
  return (
    <Card className="p-4">
      <h2 className="font-display text-base font-semibold">Steps</h2>
      <ol className="mt-2 flex flex-col gap-1">
        {guide.steps.map((step, i) => (
          <li key={i}>
            <button
              type="button"
              disabled={!onJump}
              onClick={() => onJump?.(i)}
              aria-current={current === i ? "step" : undefined}
              className={cn(
                "flex w-full gap-3 rounded-[var(--radius-control)] p-2 text-left text-sm",
                onJump && "hover:bg-muted/60",
                current === i && "bg-primary-soft text-primary-soft-foreground",
              )}
            >
              <span
                aria-hidden
                className={cn(
                  "tabular flex size-6 shrink-0 items-center justify-center rounded-full border text-xs font-semibold",
                  current === i
                    ? "border-primary bg-primary text-primary-foreground"
                    : "border-border",
                )}
              >
                {i + 1}
              </span>
              <span>
                <span className="sr-only">Step {i + 1}: </span>
                {rich(step)}
              </span>
            </button>
          </li>
        ))}
      </ol>
      {onJump && (
        <p className="mt-2 text-xs text-muted-foreground">
          Click a step to see it in the video.
        </p>
      )}
    </Card>
  );
}

export function GuidePage() {
  const { id } = useParams();
  const guide = guides.find((g) => g.id === id);
  if (!guide) {
    return (
      <>
        <PageHeader title="Help" />
        <Card>
          <EmptyState
            icon={<LifeBuoy />}
            title="We couldn't find that guide."
            action={
              <Link
                to="/help"
                className="font-medium text-primary hover:underline"
              >
                See all guides
              </Link>
            }
          />
        </Card>
      </>
    );
  }
  const group = groups.find((g) => g.id === guide.group);
  const related = guides.filter(
    (g) => g.id !== guide.id && g.group === guide.group,
  );
  return (
    <>
      <PageHeader
        title={guide.title}
        description={guide.summary}
        back={
          <Link
            to="/help"
            className="inline-flex min-h-11 items-center gap-1 self-start text-sm font-medium text-muted-foreground hover:text-foreground"
          >
            <ChevronLeft aria-hidden className="size-4" /> Help
            {group && `: ${group.title}`}
          </Link>
        }
      />
      <div className="flex flex-wrap gap-1.5" aria-label="Who this is for">
        {guide.who.map((w) => (
          <Badge key={w}>{roleNames[w]}</Badge>
        ))}
      </div>
      {guide.video ? <GuideVideo guide={guide} /> : <Steps guide={guide} />}
      {related.length > 0 && (
        <section aria-labelledby="related" className="flex flex-col gap-2">
          <h2 id="related" className="font-display text-lg font-semibold">
            Also in {group?.title ?? "this section"}
          </h2>
          <GuideList list={related} />
        </section>
      )}
    </>
  );
}
