// Step-by-step guides (the help centre, D-89). The words live in guides.json so the screens and the video captions share one source:
// web/help-videos/record.cjs reads the same steps when it records each video.
import type { ReactNode } from "react";
import data from "./guides.json";
import type { Roles } from "@/lib/api";

export type Role = keyof Roles | "everyone";

export type Guide = {
  id: string;
  group: string;
  title: string;
  summary: string;
  who: Role[];
  screens: string[];
  video: boolean;
  phone?: boolean;
  steps: string[];
};

export type Group = { id: string; title: string };

export const groups: Group[] = data.groups;
export const guides: Guide[] = data.guides as Guide[];

export const roleNames: Record<Role, string> = {
  everyone: "Everyone",
  contractOwner: "Contract owners",
  legal: "Legal",
  approver: "Approvers",
  groupHead: "Group Head",
  admin: "Admins",
  auditor: "Auditors",
  procurement: "Procurement",
  reports: "Reports",
};

/** Where the files for a guide's video are (built into the app, so nothing leaves the JBS server). */
export const videoFiles = (id: string) => ({
  video: `/help-videos/${id}.mp4`,
  captions: `/help-videos/${id}.vtt`,
  poster: `/help-videos/${id}.jpg`,
});

/** A step's words without the **bold** marks, for search and captions. */
export const plain = (text: string) => text.replace(/\*\*/g, "");

/** Renders **bold** (button and page names) as strong text. Nothing else is treated as formatting. */
export function rich(text: string): ReactNode[] {
  return text
    .split(/(\*\*[^*]+\*\*)/g)
    .map((part, i) =>
      part.startsWith("**") && part.endsWith("**") ? (
        <strong key={i}>{part.slice(2, -2)}</strong>
      ) : (
        part
      ),
    );
}

/** Guides for this person's roles. "Everyone" guides always count. */
export function isFor(guide: Guide, roles: Roles) {
  return guide.who.some((w) => w === "everyone" || roles[w]);
}

/** Guides matching every word typed, in their title, summary or steps. */
export function search(query: string, list: Guide[] = guides) {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return list;
  return list.filter((g) => {
    const text = [g.title, g.summary, ...g.steps.map(plain)]
      .join(" ")
      .toLowerCase();
    return words.every((w) => text.includes(w));
  });
}

export const guidesForScreen = (screen: string) =>
  guides.filter((g) => g.screens.includes(screen));
