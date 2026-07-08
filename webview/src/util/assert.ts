/**
 * Compile-time exhaustiveness guard for discriminated unions. Passing a value here forces the compiler to
 * have narrowed it to `never` — i.e. every case was handled — so adding a new union case without a matching
 * branch becomes a type error at the call site rather than a silently-ignored value. Throws if somehow
 * reached at runtime (an unexpected value crossed a boundary the types promised it couldn't).
 */
export function assertNever(value: never): never {
  throw new Error(`Unexpected value: ${JSON.stringify(value)}`);
}
