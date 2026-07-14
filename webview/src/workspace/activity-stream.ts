export interface ActivityEntry {
  readonly when: Date;
  readonly category: "GitHub" | "Context" | "View" | "Action";
  readonly message: string;
  readonly outcome?: "started" | "succeeded" | "failed";
}

type ActivityListener = (entries: readonly ActivityEntry[]) => void;

/** A bounded, in-memory activity feed. It deliberately accepts summaries only, never IPC payloads. */
export class ActivityStream {
  private readonly entries: ActivityEntry[] = [];
  private readonly listeners = new Set<ActivityListener>();

  add(
    category: ActivityEntry["category"],
    message: string,
    outcome?: ActivityEntry["outcome"],
  ): void {
    const entry: ActivityEntry =
      outcome === undefined
        ? { when: new Date(), category, message }
        : { when: new Date(), category, message, outcome };
    this.entries.unshift(entry);
    this.entries.splice(250);
    for (const listener of this.listeners) listener(this.entries);
  }

  clear(): void {
    this.entries.length = 0;
    for (const listener of this.listeners) listener(this.entries);
  }

  subscribe(listener: ActivityListener): () => void {
    this.listeners.add(listener);
    listener(this.entries);
    return () => this.listeners.delete(listener);
  }
}

export const activityStream = new ActivityStream();
