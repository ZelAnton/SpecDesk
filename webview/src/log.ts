/**
 * Structured logging for the webview. Records are shipped to the native host over IPC (`log`) and
 * written to the same rolling log file as native events, so there is one place to look. Keep
 * `data` JSON-serializable; it is stringified for the wire.
 */

import { ipc } from "./ipc.js";
import { Kinds } from "./protocol.js";

type Level = "debug" | "info" | "warn" | "error";

/**
 * `JSON.stringify` with a replacer that renders `BigInt` as a string (the native serializer throws
 * on it outright) and a fallback for anything else it still can't handle (circular references),
 * so a bad log payload never throws in the caller.
 */
function safeStringify(data: unknown): string {
  try {
    return JSON.stringify(data, (_key, value) => (typeof value === "bigint" ? `${value}n` : value));
  } catch {
    // Circular references (or other structures JSON.stringify rejects) land here; log the shape of
    // the failure instead of losing the caller's message.
    return "[unserializable log data]";
  }
}

function emit(level: Level, message: string, data?: unknown): void {
  const payload: { level: Level; message: string; data?: string } = { level, message };
  if (data !== undefined) {
    payload.data = safeStringify(data);
  }
  ipc.send(Kinds.log, payload);
}

export const log = {
  debug: (message: string, data?: unknown) => emit("debug", message, data),
  info: (message: string, data?: unknown) => emit("info", message, data),
  warn: (message: string, data?: unknown) => emit("warn", message, data),
  error: (message: string, data?: unknown) => emit("error", message, data),
};
