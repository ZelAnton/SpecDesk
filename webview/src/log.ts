/**
 * Structured logging for the webview. Records are shipped to the native host over IPC (`log`) and
 * written to the same rolling log file as native events, so there is one place to look. Keep
 * `data` JSON-serializable; it is stringified for the wire.
 */

import { ipc } from "./ipc.js";
import { Kinds } from "./protocol.js";

type Level = "debug" | "info" | "warn" | "error";

function emit(level: Level, message: string, data?: unknown): void {
  const payload: { level: Level; message: string; data?: string } = { level, message };
  if (data !== undefined) {
    payload.data = JSON.stringify(data);
  }
  ipc.send(Kinds.log, payload);
}

export const log = {
  debug: (message: string, data?: unknown) => emit("debug", message, data),
  info: (message: string, data?: unknown) => emit("info", message, data),
  warn: (message: string, data?: unknown) => emit("warn", message, data),
  error: (message: string, data?: unknown) => emit("error", message, data),
};
